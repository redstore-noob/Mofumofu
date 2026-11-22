using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace Mofumofu.Launcher.Services.Agent;

public enum AgentRole
{
    System,
    User,
    Assistant,
    Tool
}

public sealed class AgentToolCall
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ArgumentsJson { get; set; } = "{}";
}

public sealed class AgentMessage
{
    public AgentRole Role { get; set; }
    public string Text { get; set; } = string.Empty;

    public string? Reasoning { get; set; }

    // Model output and tool results are only replayed to their original recipient.
    public string? ProviderScope { get; set; }
    public bool IsIncomplete { get; set; }

    public List<AgentToolCall> ToolCalls { get; set; } = new();

    public string? ToolCallId { get; set; }

    public string? ToolName { get; set; }
    public bool IsError { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;

    public int Tokens { get; set; }

    public static AgentMessage User(string text) => new() { Role = AgentRole.User, Text = text };

    public static AgentMessage Assistant(string text) => new() { Role = AgentRole.Assistant, Text = text };

    public static AgentMessage ToolResult(AgentToolCall call, string content, bool isError) => new()
    {
        Role = AgentRole.Tool,
        ToolCallId = call.Id,
        ToolName = call.Name,
        Text = content,
        IsError = isError,
    };
}

public sealed class AgentUsage
{
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }

    public int Total => PromptTokens + CompletionTokens;

    public AgentUsage Clone() => new() { PromptTokens = PromptTokens, CompletionTokens = CompletionTokens };
}

public interface IAgentStreamSink
{
    void OnText(string delta);
    void OnReasoning(string delta);
    void OnToolCall(AgentToolCall call);
    void OnUsage(AgentUsage usage);
}

public enum AgentProviderKind
{
    OpenAiCompatible,

    Anthropic,

    Gemini
}

public sealed class AgentProviderConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..8];
    public string Name { get; set; } = string.Empty;
    public AgentProviderKind Kind { get; set; } = AgentProviderKind.OpenAiCompatible;
    public string BaseUrl { get; set; } = string.Empty;

    [JsonConverter(typeof(AgentSecretJsonConverter))]
    public string ApiKey { get; set; } = string.Empty;

    public List<string> Models { get; set; } = new();

    public bool SupportsTools { get; set; } = true;

    [JsonConverter(typeof(AgentSecretJsonConverter))]
    public string ExtraHeaders { get; set; } = string.Empty;

    public int ContextWindow { get; set; }

    public AgentProviderConfig Clone() => new()
    {
        Id = Id,
        Name = Name,
        Kind = Kind,
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        Models = new List<string>(Models),
        SupportsTools = SupportsTools,
        ExtraHeaders = ExtraHeaders,
        ContextWindow = ContextWindow,
    };

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Kind.ToString() : Name;
}

public sealed class AgentConversation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..12];
    public string Title { get; set; } = "新对话";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<AgentMessage> Messages { get; set; } = new();

    // Retained for local display/export of older sessions; never used as model input.
    public string Digest { get; set; } = string.Empty;
    public int ContextStartIndex { get; set; }
    public AgentTaskAttachment? Task { get; set; }

    public string DisplayTime
    {
        get
        {
            TimeSpan age = DateTime.Now - UpdatedAt;
            if (age.TotalMinutes < 1) return "刚刚";
            if (age.TotalHours < 1) return $"{(int)age.TotalMinutes} 分钟前";
            if (age.TotalDays < 1) return $"{(int)age.TotalHours} 小时前";
            if (age.TotalDays < 7) return $"{(int)age.TotalDays} 天前";
            return UpdatedAt.ToString("MM-dd");
        }
    }
}

public static class TokenEstimator
{
    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        double tokens = 0;
        int latinRun = 0;
        foreach (char ch in text)
        {
            if (ch >= 0x2E80)
            {
                if (latinRun > 0)
                {
                    tokens += latinRun / 3.6;
                    latinRun = 0;
                }

                tokens += 1.05;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (latinRun > 0)
                {
                    tokens += latinRun / 3.6;
                    latinRun = 0;
                }

                tokens += 0.25;
            }
            else
            {
                latinRun++;
            }
        }

        if (latinRun > 0) tokens += latinRun / 3.6;
        return Math.Max(1, (int)Math.Ceiling(tokens));
    }

    public static int EstimateMessage(AgentMessage message)
    {
        int tokens = 4 + Estimate(message.Text) + Estimate(message.Reasoning);
        foreach (AgentToolCall call in message.ToolCalls)
        {
            tokens += 6 + Estimate(call.Name) + Estimate(call.ArgumentsJson);
        }

        return tokens;
    }

    public static void Recalculate(IEnumerable<AgentMessage> messages)
    {
        foreach (AgentMessage message in messages)
            message.Tokens = EstimateMessage(message);
    }

    public static string Format(int tokens) => tokens >= 1000
        ? (tokens / 1000.0).ToString("0.#") + "k"
        : tokens.ToString();

    public static string MakeTitle(string text)
    {
        string trimmed = text.Trim().ReplaceLineEndings(" ");
        if (trimmed.Length == 0) return "新对话";
        return trimmed.Length <= 18 ? trimmed : trimmed[..18] + "…";
    }

    public static string Describe(IEnumerable<AgentMessage> messages)
    {
        var builder = new StringBuilder();
        foreach (AgentMessage message in messages)
        {
            builder.Append(message.Role).Append(": ").AppendLine(message.Text);
        }

        return builder.ToString();
    }
}
