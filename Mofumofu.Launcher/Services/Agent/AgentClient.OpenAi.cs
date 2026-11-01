using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mofumofu.Launcher.Services.Agent;

/// <summary>OpenAI /chat/completions 协议（绝大多数国产与本地推理服务都兼容）。</summary>
internal static class OpenAiClient
{
    public static async Task<AgentTurn> CompleteAsync(
        AgentRequest request, IAgentStreamSink? sink, CancellationToken cancellationToken)
    {
        var turn = new AgentTurn();
        string url = AgentClient.TrimBase(request.Provider.BaseUrl) + "/chat/completions";

        var body = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["messages"] = BuildMessages(request),
            ["temperature"] = request.Temperature,
            ["top_p"] = request.TopP,
            ["max_tokens"] = request.MaxOutputTokens,
            ["stream"] = request.Streaming,
        };

        if (request.Streaming)
            body["stream_options"] = new Dictionary<string, object?> { ["include_usage"] = true };

        if (request.Tools.Count > 0 && request.Provider.SupportsTools)
            body["tools"] = BuildTools(request.Tools);

        using HttpRequestMessage http = AgentClient.Build(HttpMethod.Post, url, request.Provider);
        if (!string.IsNullOrWhiteSpace(request.Provider.ApiKey))
            http.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", request.Provider.ApiKey.Trim());
        http.Content = AgentClient.JsonBody(body);

        using HttpResponseMessage response = await AgentClient.Http.SendAsync(
            http, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new AgentRequestException(await AgentClient.ReadErrorAsync(response, cancellationToken));

        if (!request.Streaming)
        {
            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            ParseComplete(json, turn, sink);
            return turn;
        }

        var toolBuffers = new Dictionary<int, (string Id, string Name, System.Text.StringBuilder Args)>();
        SseReader reader = await SseReader.CreateAsync(response, cancellationToken);

        while (await reader.ReadAsync(cancellationToken) is { } evt)
        {
            string data = evt.Data;
            if (data == "[DONE]") break;

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(data);
            }
            catch (JsonException)
            {
                continue;
            }

            if (node?["error"] is JsonNode errorNode)
                throw new AgentRequestException("供应商返回错误：" + (errorNode["message"]?.GetValue<string>() ?? errorNode.ToJsonString()));

            if (node?["usage"] is JsonNode usageNode)
            {
                turn.Usage.PromptTokens = usageNode["prompt_tokens"]?.GetValue<int>() ?? turn.Usage.PromptTokens;
                turn.Usage.CompletionTokens = usageNode["completion_tokens"]?.GetValue<int>() ?? turn.Usage.CompletionTokens;
                sink?.OnUsage(turn.Usage.Clone());
            }

            JsonNode? choice = node?["choices"]?.AsArray().FirstOrDefault();
            if (choice is null) continue;

            turn.FinishReason = choice["finish_reason"]?.GetValue<string>() ?? turn.FinishReason;
            JsonNode? delta = choice["delta"];
            if (delta is null) continue;

            if (delta["reasoning_content"]?.GetValue<string>() is { Length: > 0 } reasoning)
            {
                turn.Reasoning += reasoning;
                sink?.OnReasoning(reasoning);
            }

            if (delta["content"]?.GetValue<string>() is { Length: > 0 } text)
            {
                turn.Text += text;
                sink?.OnText(text);
            }

            if (delta["tool_calls"] is JsonArray toolCalls)
            {
                foreach (JsonNode? item in toolCalls)
                {
                    if (item is null) continue;
                    int index = item["index"]?.GetValue<int>() ?? toolBuffers.Count;
                    if (!toolBuffers.TryGetValue(index, out var buffer))
                    {
                        buffer = (string.Empty, string.Empty, new System.Text.StringBuilder());
                    }

                    if (item["id"]?.GetValue<string>() is { Length: > 0 } id) buffer.Id = id;
                    if (item["function"]?["name"]?.GetValue<string>() is { Length: > 0 } name) buffer.Name = name;
                    if (item["function"]?["arguments"]?.GetValue<string>() is { } args) buffer.Args.Append(args);
                    toolBuffers[index] = buffer;
                }
            }
        }

        foreach (var (index, buffer) in toolBuffers.OrderBy(pair => pair.Key))
        {
            if (string.IsNullOrWhiteSpace(buffer.Name)) continue;
            var call = new AgentToolCall
            {
                Id = string.IsNullOrWhiteSpace(buffer.Id) ? $"call_{index}_{Guid.NewGuid():n}"[..16] : buffer.Id,
                Name = buffer.Name,
                ArgumentsJson = buffer.Args.Length == 0 ? "{}" : buffer.Args.ToString(),
            };
            turn.ToolCalls.Add(call);
            sink?.OnToolCall(call);
        }

        return turn;
    }

    private static List<object> BuildMessages(AgentRequest request)
    {
        var messages = new List<object>
        {
            new Dictionary<string, object?> { ["role"] = "system", ["content"] = request.SystemPrompt },
        };

        foreach (AgentMessage message in request.Messages)
        {
            switch (message.Role)
            {
                case AgentRole.User:
                    messages.Add(new Dictionary<string, object?> { ["role"] = "user", ["content"] = message.Text });
                    break;

                case AgentRole.Assistant:
                    var assistant = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = string.IsNullOrEmpty(message.Text) ? null : message.Text,
                    };
                    if (message.ToolCalls.Count > 0)
                    {
                        assistant["tool_calls"] = message.ToolCalls.Select(call => new Dictionary<string, object?>
                        {
                            ["id"] = call.Id,
                            ["type"] = "function",
                            ["function"] = new Dictionary<string, object?>
                            {
                                ["name"] = call.Name,
                                ["arguments"] = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson,
                            },
                        }).ToList();
                    }

                    messages.Add(assistant);
                    break;

                case AgentRole.Tool:
                    messages.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = message.ToolCallId,
                        ["content"] = message.Text,
                    });
                    break;
            }
        }

        return messages;
    }

    private static List<object> BuildTools(IReadOnlyList<AgentToolDefinition> tools) =>
        tools.Select(tool => (object)new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?>
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = tool.ParametersNode(),
            },
        }).ToList();

    private static void ParseComplete(string json, AgentTurn turn, IAgentStreamSink? sink)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        if (root.TryGetProperty("error", out JsonElement error))
            throw new AgentRequestException("供应商返回错误：" +
                (error.TryGetProperty("message", out JsonElement msg) ? msg.GetString() : error.ToString()));

        if (root.TryGetProperty("usage", out JsonElement usage))
        {
            turn.Usage.PromptTokens = usage.TryGetProperty("prompt_tokens", out JsonElement pt) ? pt.GetInt32() : 0;
            turn.Usage.CompletionTokens = usage.TryGetProperty("completion_tokens", out JsonElement ct2) ? ct2.GetInt32() : 0;
            sink?.OnUsage(turn.Usage.Clone());
        }

        if (!root.TryGetProperty("choices", out JsonElement choices) || choices.GetArrayLength() == 0) return;

        JsonElement choice = choices[0];
        if (choice.TryGetProperty("finish_reason", out JsonElement finish) && finish.ValueKind == JsonValueKind.String)
            turn.FinishReason = finish.GetString();

        if (!choice.TryGetProperty("message", out JsonElement message)) return;

        if (message.TryGetProperty("reasoning_content", out JsonElement reasoning) && reasoning.ValueKind == JsonValueKind.String)
            turn.Reasoning = reasoning.GetString() ?? string.Empty;

        if (message.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String)
        {
            turn.Text = content.GetString() ?? string.Empty;
            if (turn.Text.Length > 0) sink?.OnText(turn.Text);
        }

        if (message.TryGetProperty("tool_calls", out JsonElement toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement call in toolCalls.EnumerateArray())
            {
                JsonElement function = call.GetProperty("function");
                var parsed = new AgentToolCall
                {
                    Id = call.TryGetProperty("id", out JsonElement id) ? id.GetString() ?? string.Empty : string.Empty,
                    Name = function.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? string.Empty : string.Empty,
                    ArgumentsJson = function.TryGetProperty("arguments", out JsonElement args)
                        ? args.GetString() ?? "{}"
                        : "{}",
                };
                if (parsed.Id.Length == 0) parsed.Id = "call_" + Guid.NewGuid().ToString("n")[..12];
                turn.ToolCalls.Add(parsed);
                sink?.OnToolCall(parsed);
            }
        }
    }
}
