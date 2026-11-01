using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mofumofu.Launcher.Services.Agent;

/// <summary>Anthropic Messages API（x-api-key + anthropic-version）。</summary>
internal static class AnthropicClient
{
    private const string ApiVersion = "2023-06-01";

    public static async Task<AgentTurn> CompleteAsync(
        AgentRequest request, IAgentStreamSink? sink, CancellationToken cancellationToken)
    {
        var turn = new AgentTurn();
        string url = AgentClient.TrimBase(request.Provider.BaseUrl) + "/v1/messages";

        var body = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxOutputTokens,
            ["temperature"] = request.Temperature,
            ["system"] = request.SystemPrompt,
            ["messages"] = BuildMessages(request.Messages),
            ["stream"] = request.Streaming,
        };

        if (request.TopP is > 0 and < 1) body["top_p"] = request.TopP;

        if (request.Tools.Count > 0 && request.Provider.SupportsTools)
        {
            body["tools"] = request.Tools.Select(tool => new Dictionary<string, object?>
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = tool.ParametersNode(),
            }).ToList();
        }

        using HttpRequestMessage http = AgentClient.Build(HttpMethod.Post, url, request.Provider);
        http.Headers.TryAddWithoutValidation("x-api-key", request.Provider.ApiKey.Trim());
        http.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        http.Content = AgentClient.JsonBody(body);

        using HttpResponseMessage response = await AgentClient.Http.SendAsync(
            http, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new AgentRequestException(await AgentClient.ReadErrorAsync(response, cancellationToken));

        if (!request.Streaming)
        {
            ParseComplete(await response.Content.ReadAsStringAsync(cancellationToken), turn, sink);
            return turn;
        }

        // index → 正在累积的工具调用（tool_use 的 input 是分片 JSON 字符串）
        var toolBuffers = new Dictionary<int, (string Id, string Name, StringBuilder Json)>();
        SseReader reader = await SseReader.CreateAsync(response, cancellationToken);

        while (await reader.ReadAsync(cancellationToken) is { } evt)
        {
            string type = evt.Event ?? string.Empty;
            if (type.Length == 0) continue;

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(evt.Data);
            }
            catch (JsonException)
            {
                continue;
            }

            switch (type)
            {
                case "message_start":
                    if (node?["message"]?["usage"]?["input_tokens"]?.GetValue<int>() is { } input)
                        turn.Usage.PromptTokens = input;
                    break;

                case "content_block_start":
                {
                    int index = node?["index"]?.GetValue<int>() ?? 0;
                    JsonNode? block = node?["content_block"];
                    if (block?["type"]?.GetValue<string>() == "tool_use")
                    {
                        toolBuffers[index] = (
                            block["id"]?.GetValue<string>() ?? "tool_" + Guid.NewGuid().ToString("n")[..10],
                            block["name"]?.GetValue<string>() ?? string.Empty,
                            new StringBuilder());
                    }

                    break;
                }

                case "content_block_delta":
                {
                    int index = node?["index"]?.GetValue<int>() ?? 0;
                    JsonNode? delta = node?["delta"];
                    string deltaType = delta?["type"]?.GetValue<string>() ?? string.Empty;
                    switch (deltaType)
                    {
                        case "text_delta":
                            if (delta?["text"]?.GetValue<string>() is { Length: > 0 } text)
                            {
                                turn.Text += text;
                                sink?.OnText(text);
                            }

                            break;

                        case "thinking_delta":
                            if (delta?["thinking"]?.GetValue<string>() is { Length: > 0 } thinking)
                            {
                                turn.Reasoning += thinking;
                                sink?.OnReasoning(thinking);
                            }

                            break;

                        case "input_json_delta":
                            if (toolBuffers.TryGetValue(index, out var buffer) &&
                                delta?["partial_json"]?.GetValue<string>() is { } partial)
                            {
                                buffer.Json.Append(partial);
                            }

                            break;
                    }

                    break;
                }

                case "message_delta":
                    if (node?["usage"]?["output_tokens"]?.GetValue<int>() is { } output)
                        turn.Usage.CompletionTokens = output;
                    turn.FinishReason = node?["delta"]?["stop_reason"]?.GetValue<string>() ?? turn.FinishReason;
                    sink?.OnUsage(turn.Usage.Clone());
                    break;

                case "error":
                    throw new AgentRequestException("供应商返回错误：" +
                        (node?["error"]?["message"]?.GetValue<string>() ?? "未知错误"));
            }
        }

        foreach (var (index, buffer) in toolBuffers.OrderBy(pair => pair.Key))
        {
            if (string.IsNullOrWhiteSpace(buffer.Name)) continue;
            var call = new AgentToolCall
            {
                Id = buffer.Id,
                Name = buffer.Name,
                ArgumentsJson = buffer.Json.Length == 0 ? "{}" : buffer.Json.ToString(),
            };
            turn.ToolCalls.Add(call);
            sink?.OnToolCall(call);
        }

        return turn;
    }

    /// <summary>把统一消息转成 Anthropic 的 user/assistant 交替结构，工具结果放在 user 的 tool_result 块里。</summary>
    private static List<object> BuildMessages(IReadOnlyList<AgentMessage> messages)
    {
        var result = new List<(string Role, List<object> Blocks)>();

        void Append(string role, object block)
        {
            if (result.Count > 0 && result[^1].Role == role)
            {
                result[^1].Blocks.Add(block);
            }
            else
            {
                result.Add((role, new List<object> { block }));
            }
        }

        foreach (AgentMessage message in messages)
        {
            switch (message.Role)
            {
                case AgentRole.User:
                    Append("user", new Dictionary<string, object?> { ["type"] = "text", ["text"] = message.Text });
                    break;

                case AgentRole.Assistant:
                    if (!string.IsNullOrWhiteSpace(message.Text))
                        Append("assistant", new Dictionary<string, object?> { ["type"] = "text", ["text"] = message.Text });
                    foreach (AgentToolCall call in message.ToolCalls)
                    {
                        JsonNode? input = null;
                        try
                        {
                            input = JsonNode.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                        }
                        catch (JsonException)
                        {
                            input = new JsonObject();
                        }

                        Append("assistant", new Dictionary<string, object?>
                        {
                            ["type"] = "tool_use",
                            ["id"] = call.Id,
                            ["name"] = call.Name,
                            ["input"] = input,
                        });
                    }

                    break;

                case AgentRole.Tool:
                    Append("user", new Dictionary<string, object?>
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = message.ToolCallId,
                        ["content"] = message.Text,
                        ["is_error"] = message.IsError,
                    });
                    break;
            }
        }

        return result.Select(entry => (object)new Dictionary<string, object?>
        {
            ["role"] = entry.Role,
            ["content"] = entry.Blocks,
        }).ToList();
    }

    private static void ParseComplete(string json, AgentTurn turn, IAgentStreamSink? sink)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        if (root.TryGetProperty("usage", out JsonElement usage))
        {
            turn.Usage.PromptTokens = usage.TryGetProperty("input_tokens", out JsonElement input) ? input.GetInt32() : 0;
            turn.Usage.CompletionTokens = usage.TryGetProperty("output_tokens", out JsonElement output) ? output.GetInt32() : 0;
            sink?.OnUsage(turn.Usage.Clone());
        }

        if (root.TryGetProperty("stop_reason", out JsonElement stop) && stop.ValueKind == JsonValueKind.String)
            turn.FinishReason = stop.GetString();

        if (!root.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array) return;

        foreach (JsonElement block in content.EnumerateArray())
        {
            string type = block.TryGetProperty("type", out JsonElement typeElement) ? typeElement.GetString() ?? "" : "";
            if (type == "text" && block.TryGetProperty("text", out JsonElement text))
            {
                string value = text.GetString() ?? string.Empty;
                turn.Text += value;
                if (value.Length > 0) sink?.OnText(value);
            }
            else if (type == "tool_use")
            {
                var call = new AgentToolCall
                {
                    Id = block.TryGetProperty("id", out JsonElement id) ? id.GetString() ?? "" : "",
                    Name = block.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? "" : "",
                    ArgumentsJson = block.TryGetProperty("input", out JsonElement inputNode)
                        ? inputNode.GetRawText()
                        : "{}",
                };
                turn.ToolCalls.Add(call);
                sink?.OnToolCall(call);
            }
        }
    }
}
