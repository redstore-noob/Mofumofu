using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mofumofu.Launcher.Services.Agent;

/// <summary>Google Gemini generateContent（streamGenerateContent + alt=sse）。</summary>
internal static class GeminiClient
{
    public static async Task<AgentTurn> CompleteAsync(
        AgentRequest request, IAgentStreamSink? sink, CancellationToken cancellationToken)
    {
        var turn = new AgentTurn();
        string action = request.Streaming ? ":streamGenerateContent?alt=sse" : ":generateContent";
        string url = $"{AgentClient.TrimBase(request.Provider.BaseUrl)}/models/{request.Model}{action}";
        if (!string.IsNullOrWhiteSpace(request.Provider.ApiKey))
            url += (url.Contains('?') ? "&" : "?") + "key=" + Uri.EscapeDataString(request.Provider.ApiKey.Trim());

        var body = new Dictionary<string, object?>
        {
            ["systemInstruction"] = new Dictionary<string, object?>
            {
                ["parts"] = new[] { new Dictionary<string, object?> { ["text"] = request.SystemPrompt } },
            },
            ["contents"] = BuildContents(request.Messages),
            ["generationConfig"] = new Dictionary<string, object?>
            {
                ["temperature"] = request.Temperature,
                ["topP"] = request.TopP,
                ["maxOutputTokens"] = request.MaxOutputTokens,
            },
        };

        if (request.Tools.Count > 0 && request.Provider.SupportsTools)
        {
            body["tools"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["functionDeclarations"] = request.Tools.Select(tool => new Dictionary<string, object?>
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = tool.ParametersNode(),
                    }).ToList(),
                },
            };
        }

        using HttpRequestMessage http = AgentClient.Build(HttpMethod.Post, url, request.Provider);
        http.Content = AgentClient.JsonBody(body);

        using HttpResponseMessage response = await AgentClient.Http.SendAsync(
            http, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new AgentRequestException(await AgentClient.ReadErrorAsync(response, cancellationToken));

        if (!request.Streaming)
        {
            ParseChunk(await response.Content.ReadAsStringAsync(cancellationToken), turn, sink, false);
            return turn;
        }

        SseReader reader = await SseReader.CreateAsync(response, cancellationToken);
        while (await reader.ReadAsync(cancellationToken) is { } evt)
        {
            if (string.IsNullOrWhiteSpace(evt.Data)) continue;
            ParseChunk(evt.Data, turn, sink, true);
        }

        return turn;
    }

    private static List<object> BuildContents(IReadOnlyList<AgentMessage> messages)
    {
        var contents = new List<(string Role, List<object> Parts)>();

        void Append(string role, object part)
        {
            if (contents.Count > 0 && contents[^1].Role == role) contents[^1].Parts.Add(part);
            else contents.Add((role, new List<object> { part }));
        }

        foreach (AgentMessage message in messages)
        {
            switch (message.Role)
            {
                case AgentRole.User:
                    Append("user", new Dictionary<string, object?> { ["text"] = message.Text });
                    break;

                case AgentRole.Assistant:
                    if (!string.IsNullOrWhiteSpace(message.Text))
                        Append("model", new Dictionary<string, object?> { ["text"] = message.Text });
                    foreach (AgentToolCall call in message.ToolCalls)
                    {
                        JsonNode? args = null;
                        try
                        {
                            args = JsonNode.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                        }
                        catch (JsonException)
                        {
                            args = new JsonObject();
                        }

                        Append("model", new Dictionary<string, object?>
                        {
                            ["functionCall"] = new Dictionary<string, object?>
                            {
                                ["name"] = call.Name,
                                ["args"] = args,
                            },
                        });
                    }

                    break;

                case AgentRole.Tool:
                    Append("function", new Dictionary<string, object?>
                    {
                        ["functionResponse"] = new Dictionary<string, object?>
                        {
                            ["name"] = message.ToolName ?? "tool",
                            ["response"] = new Dictionary<string, object?>
                            {
                                ["result"] = message.Text,
                                ["isError"] = message.IsError,
                            },
                        },
                    });
                    break;
            }
        }

        return contents.Select(entry => (object)new Dictionary<string, object?>
        {
            ["role"] = entry.Role,
            ["parts"] = entry.Parts,
        }).ToList();
    }

    private static void ParseChunk(string json, AgentTurn turn, IAgentStreamSink? sink, bool streaming)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        if (root.TryGetProperty("error", out JsonElement error))
            throw new AgentRequestException("供应商返回错误：" +
                (error.TryGetProperty("message", out JsonElement msg) ? msg.GetString() : error.ToString()));

        if (root.TryGetProperty("usageMetadata", out JsonElement usage))
        {
            turn.Usage.PromptTokens = usage.TryGetProperty("promptTokenCount", out JsonElement prompt)
                ? prompt.GetInt32()
                : turn.Usage.PromptTokens;
            turn.Usage.CompletionTokens = usage.TryGetProperty("candidatesTokenCount", out JsonElement candidates)
                ? candidates.GetInt32()
                : turn.Usage.CompletionTokens;
            sink?.OnUsage(turn.Usage.Clone());
        }

        if (!root.TryGetProperty("candidates", out JsonElement list) || list.GetArrayLength() == 0) return;

        JsonElement candidate = list[0];
        if (candidate.TryGetProperty("finishReason", out JsonElement finish) && finish.ValueKind == JsonValueKind.String)
            turn.FinishReason = finish.GetString();

        if (!candidate.TryGetProperty("content", out JsonElement content) ||
            !content.TryGetProperty("parts", out JsonElement parts) ||
            parts.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("text", out JsonElement textElement) && textElement.ValueKind == JsonValueKind.String)
            {
                string text = textElement.GetString() ?? string.Empty;
                if (part.TryGetProperty("thought", out JsonElement thought) && thought.ValueKind == JsonValueKind.True)
                {
                    turn.Reasoning += text;
                    sink?.OnReasoning(text);
                }
                else if (text.Length > 0)
                {
                    turn.Text += text;
                    sink?.OnText(text);
                }
            }
            else if (part.TryGetProperty("functionCall", out JsonElement call))
            {
                var parsed = new AgentToolCall
                {
                    Id = "call_" + Guid.NewGuid().ToString("n")[..12],
                    Name = call.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? "" : "",
                    ArgumentsJson = call.TryGetProperty("args", out JsonElement args) ? args.GetRawText() : "{}",
                };
                if (string.IsNullOrWhiteSpace(parsed.Name)) continue;
                turn.ToolCalls.Add(parsed);
                sink?.OnToolCall(parsed);
            }
        }
    }
}
