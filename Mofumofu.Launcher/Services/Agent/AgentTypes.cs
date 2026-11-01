using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace Mofumofu.Launcher.Services.Agent;

/// <summary>对话里的角色。Tool 表示一条工具执行结果。</summary>
public enum AgentRole
{
    System,
    User,
    Assistant,
    Tool
}

/// <summary>一次模型返回的工具调用请求。</summary>
public sealed class AgentToolCall
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ArgumentsJson { get; set; } = "{}";
}

/// <summary>会话中的一条消息。</summary>
public sealed class AgentMessage
{
    public AgentRole Role { get; set; }
    public string Text { get; set; } = string.Empty;

    /// <summary>推理模型的思考内容（DeepSeek-R1 / o 系列等），仅展示不参与上下文请求。</summary>
    public string? Reasoning { get; set; }

    /// <summary>助手消息携带的工具调用。</summary>
    public List<AgentToolCall> ToolCalls { get; set; } = new();

    /// <summary>工具结果对应的调用 id。</summary>
    public string? ToolCallId { get; set; }

    public string? ToolName { get; set; }
    public bool IsError { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;

    /// <summary>估算 token 数，用于上下文占用显示。</summary>
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

/// <summary>一次模型调用的用量。</summary>
public sealed class AgentUsage
{
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }

    public int Total => PromptTokens + CompletionTokens;

    public AgentUsage Clone() => new() { PromptTokens = PromptTokens, CompletionTokens = CompletionTokens };
}

/// <summary>流式回调：文本增量、思考增量、工具调用增量、用量、结束原因。</summary>
public interface IAgentStreamSink
{
    void OnText(string delta);
    void OnReasoning(string delta);
    void OnToolCall(AgentToolCall call);
    void OnUsage(AgentUsage usage);
}

/// <summary>单个供应商的配置。</summary>
public enum AgentProviderKind
{
    /// <summary>OpenAI 兼容协议（OpenAI / DeepSeek / Kimi / GLM / Ollama / LM Studio …）。</summary>
    OpenAiCompatible,

    /// <summary>Anthropic Messages API。</summary>
    Anthropic,

    /// <summary>Google Gemini generateContent。</summary>
    Gemini,

    /// <summary>本地模拟：不联网，用脚本化回复演示完整 Agent 流程。</summary>
    Mock
}

public sealed class AgentProviderConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..8];
    public string Name { get; set; } = string.Empty;
    public AgentProviderKind Kind { get; set; } = AgentProviderKind.OpenAiCompatible;
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>API Key。本地模拟不需要。落盘时由 DPAPI 加密。</summary>
    [JsonConverter(typeof(AgentSecretJsonConverter))]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>该供应商下可选的模型 id 列表。</summary>
    public List<string> Models { get; set; } = new();

    public bool SupportsTools { get; set; } = true;

    /// <summary>额外的请求头，按 "Name: Value" 每行一条。可能含鉴权信息，落盘时一并加密。</summary>
    [JsonConverter(typeof(AgentSecretJsonConverter))]
    public string ExtraHeaders { get; set; } = string.Empty;

    /// <summary>该供应商的上下文窗口覆盖值；0 表示按模型名自动推断。</summary>
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

/// <summary>对话会话（持久化单位）。</summary>
public sealed class AgentConversation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..12];
    public string Title { get; set; } = "新对话";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<AgentMessage> Messages { get; set; } = new();

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

/// <summary>粗糙但稳定的 token 估算：中文按字计，拉丁按 3.6 字符计。</summary>
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

    /// <summary>把消息列表重新算一遍 token（导入/压缩后调用）。</summary>
    public static void Recalculate(IEnumerable<AgentMessage> messages)
    {
        foreach (AgentMessage message in messages)
            message.Tokens = EstimateMessage(message);
    }

    /// <summary>把 12345 显示成 12.3k。</summary>
    public static string Format(int tokens) => tokens >= 1000
        ? (tokens / 1000.0).ToString("0.#") + "k"
        : tokens.ToString();

    /// <summary>给会话起个短标题：取第一句用户消息。</summary>
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
