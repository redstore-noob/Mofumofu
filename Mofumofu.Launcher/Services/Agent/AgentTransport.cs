using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mofumofu.Launcher.Services.Agent;

/// <summary>供应商返回的错误（消息可直接展示给玩家）。</summary>
public sealed class AgentRequestException(string message) : Exception(message);

/// <summary>一次完整请求的参数。</summary>
public sealed class AgentRequest
{
    public required AgentProviderConfig Provider { get; init; }
    public required string Model { get; init; }
    public required string SystemPrompt { get; init; }
    public required IReadOnlyList<AgentMessage> Messages { get; init; }
    public IReadOnlyList<AgentToolDefinition> Tools { get; init; } = Array.Empty<AgentToolDefinition>();
    public double Temperature { get; init; } = 0.35;
    public double TopP { get; init; } = 1.0;
    public int MaxOutputTokens { get; init; } = 2048;
    public bool Streaming { get; init; } = true;
}

/// <summary>一次模型调用的结果。</summary>
public sealed class AgentTurn
{
    public string Text { get; set; } = string.Empty;
    public string Reasoning { get; set; } = string.Empty;
    public List<AgentToolCall> ToolCalls { get; } = new();
    public AgentUsage Usage { get; set; } = new();
    public string? FinishReason { get; set; }
    public TimeSpan Elapsed { get; set; }
}

/// <summary>
/// 供应商协议适配层：把统一的 AgentRequest 翻译成各家请求体，并把 SSE 增量还原成
/// 文本 / 思考 / 工具调用。所有分支共享同一条 <see cref="SseReader"/>。
/// </summary>
public static partial class AgentClient
{
    internal static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public static async Task<AgentTurn> CompleteAsync(
        AgentRequest request, IAgentStreamSink? sink, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        AgentTurn turn = request.Provider.Kind switch
        {
            AgentProviderKind.Mock => await MockClient.CompleteAsync(request, sink, cancellationToken),
            AgentProviderKind.Anthropic => await AnthropicClient.CompleteAsync(request, sink, cancellationToken),
            AgentProviderKind.Gemini => await GeminiClient.CompleteAsync(request, sink, cancellationToken),
            _ => await OpenAiClient.CompleteAsync(request, sink, cancellationToken),
        };

        turn.Elapsed = stopwatch.Elapsed;
        return turn;
    }

    /// <summary>连通性测试：发一次最短请求，返回耗时与首段回复。</summary>
    public static async Task<(bool Ok, string Message)> TestAsync(
        AgentProviderConfig provider, string model, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var request = new AgentRequest
            {
                Provider = provider,
                Model = model,
                SystemPrompt = "你是连通性测试助手，只回复 OK。",
                Messages = new[] { AgentMessage.User("ping") },
                MaxOutputTokens = 16,
                Streaming = false,
            };

            AgentTurn turn = await CompleteAsync(request, null, cancellationToken);
            stopwatch.Stop();

            string text = string.IsNullOrWhiteSpace(turn.Text) ? "（空回复）" : turn.Text.Trim();
            if (text.Length > 60) text = text[..60] + "…";
            return (true, $"连接成功 · {(int)stopwatch.ElapsedMilliseconds} ms · {text.ReplaceLineEndings(" ")}");
        }
        catch (OperationCanceledException)
        {
            return (false, "测试已取消或超时");
        }
        catch (Exception error)
        {
            return (false, error.Message);
        }
    }

    // ==================================================================
    // 公共小工具
    // ==================================================================

    internal static HttpRequestMessage Build(HttpMethod method, string url, AgentProviderConfig provider)
    {
        var message = new HttpRequestMessage(method, url);
        foreach (string line in provider.ExtraHeaders.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            message.Headers.TryAddWithoutValidation(line[..colon].Trim(), line[(colon + 1)..].Trim());
        }

        return message;
    }

    internal static StringContent JsonBody(object body) =>
        new(JsonSerializer.Serialize(body, AgentJson.Options), Encoding.UTF8, "application/json");

    internal static string TrimBase(string baseUrl) => baseUrl.TrimEnd('/');

    internal static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            body = string.Empty;
        }

        string detail = body;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("error", out JsonElement error))
            {
                detail = error.ValueKind == JsonValueKind.String
                    ? error.GetString() ?? body
                    : error.TryGetProperty("message", out JsonElement inner) ? inner.GetString() ?? body : body;
            }
        }
        catch
        {
            // 非 JSON 错误体，原样展示
        }

        if (string.IsNullOrWhiteSpace(detail)) detail = response.ReasonPhrase ?? "无响应内容";
        if (detail.Length > 400) detail = detail[..400] + "…";

        return $"HTTP {(int)response.StatusCode} · {detail}";
    }
}

/// <summary>共享的 JSON 序列化设置。</summary>
internal static class AgentJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>SSE 行读取器：把 "data: ..." 与 "event: ..." 组合成一条条事件。</summary>
internal sealed class SseReader
{
    private readonly StreamReader _reader;

    private SseReader(StreamReader reader) => _reader = reader;

    public static async Task<SseReader> CreateAsync(HttpResponseMessage response, CancellationToken ct)
    {
        Stream stream = await response.Content.ReadAsStreamAsync(ct);
        return new SseReader(new StreamReader(stream, Encoding.UTF8));
    }

    /// <summary>读取下一条事件；流结束时返回 null。</summary>
    public async Task<(string? Event, string Data)?> ReadAsync(CancellationToken ct)
    {
        string? eventName = null;
        var data = new StringBuilder();

        while (true)
        {
            string? line = await _reader.ReadLineAsync(ct);
            if (line is null)
            {
                return data.Length > 0 ? (eventName, data.ToString()) : null;
            }

            if (line.Length == 0)
            {
                if (data.Length > 0) return (eventName, data.ToString());
                eventName = null;
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                string payload = line[5..].TrimStart();
                if (data.Length > 0) data.Append('\n');
                data.Append(payload);
            }
            // 忽略 id: / retry: / 注释行
        }
    }
}
