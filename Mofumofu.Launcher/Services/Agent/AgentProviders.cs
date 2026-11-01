using System;
using System.Collections.Generic;
using System.Linq;

namespace Mofumofu.Launcher.Services.Agent;

/// <summary>
/// 一条供应商预设：只描述默认值，不持有运行时状态。
/// 新增供应商时从这里拷一份，玩家之后随便改。
/// </summary>
public sealed record AgentProviderPreset(
    string Id,
    string Name,
    string Note,
    AgentProviderKind Kind,
    string BaseUrl,
    string[] Models,
    string ApiKey = "");

/// <summary>
/// 内置供应商预设。预设只是"起步模板"，真正生效的是设置里的
/// <see cref="AgentProviderConfig"/>，所以玩家可以随意增删、改地址、改模型。
/// </summary>
public static class AgentProviderPresets
{
    /// <summary>可选预设（不含本地模拟，模拟服务始终内置在默认列表里）。</summary>
    public static IReadOnlyList<AgentProviderPreset> All { get; } = new List<AgentProviderPreset>
    {
        new("openai", "OpenAI", "GPT-4o / 4.1 官方接口",
            AgentProviderKind.OpenAiCompatible, "https://api.openai.com/v1",
            new[] { "gpt-4o-mini", "gpt-4o", "gpt-4.1-mini" }),

        new("deepseek", "DeepSeek", "deepseek-chat / reasoner",
            AgentProviderKind.OpenAiCompatible, "https://api.deepseek.com/v1",
            new[] { "deepseek-chat", "deepseek-reasoner" }),

        new("moonshot", "Kimi", "月之暗面 · 长上下文",
            AgentProviderKind.OpenAiCompatible, "https://api.moonshot.cn/v1",
            new[] { "moonshot-v1-32k", "moonshot-v1-8k", "kimi-k2-0905-preview" }),

        new("zhipu", "智谱 GLM", "GLM-4 系列",
            AgentProviderKind.OpenAiCompatible, "https://open.bigmodel.cn/api/paas/v4",
            new[] { "glm-4-flash", "glm-4-plus" }),

        new("dashscope", "通义千问", "阿里云 · 兼容模式",
            AgentProviderKind.OpenAiCompatible, "https://dashscope.aliyuncs.com/compatible-mode/v1",
            new[] { "qwen-plus", "qwen-max", "qwen-turbo" }),

        new("siliconflow", "硅基流动", "聚合推理服务",
            AgentProviderKind.OpenAiCompatible, "https://api.siliconflow.cn/v1",
            new[] { "Qwen/Qwen2.5-72B-Instruct", "deepseek-ai/DeepSeek-V3" }),

        new("openrouter", "OpenRouter", "一个 Key 走多家模型",
            AgentProviderKind.OpenAiCompatible, "https://openrouter.ai/api/v1",
            new[] { "anthropic/claude-3.5-sonnet", "openai/gpt-4o-mini", "google/gemini-2.0-flash-001" }),

        new("ollama", "Ollama", "本机 11434 端口",
            AgentProviderKind.OpenAiCompatible, "http://localhost:11434/v1",
            new[] { "qwen2.5:7b", "llama3.1:8b" }, "ollama"),

        new("lmstudio", "LM Studio", "本机 1234 端口",
            AgentProviderKind.OpenAiCompatible, "http://localhost:1234/v1",
            new[] { "local-model" }, "lm-studio"),

        new("anthropic", "Claude", "Anthropic Messages",
            AgentProviderKind.Anthropic, "https://api.anthropic.com",
            new[] { "claude-3-5-sonnet-latest", "claude-3-7-sonnet-latest" }),

        new("gemini", "Gemini", "Google AI Studio",
            AgentProviderKind.Gemini, "https://generativelanguage.googleapis.com/v1beta",
            new[] { "gemini-2.0-flash", "gemini-2.5-flash" }),
    };

    public static IReadOnlyList<AgentProviderConfig> CreateDefaults()
    {
        var providers = new List<AgentProviderConfig>
        {
            new()
            {
                Id = "mock",
                Name = "本地模拟",
                Kind = AgentProviderKind.Mock,
                BaseUrl = "local://mock",
                Models = { "mofu-sim-1" },
                SupportsTools = true,
            },
        };

        providers.AddRange(All.Select(preset => ToConfig(preset, preset.Id)));
        return providers;
    }

    /// <summary>按预设新建一条供应商。运行期新增用唯一 id，避免和默认项撞车。</summary>
    public static AgentProviderConfig CreateFromPreset(AgentProviderPreset preset) =>
        ToConfig(preset, preset.Id + "-" + Guid.NewGuid().ToString("n")[..4]);

    private static AgentProviderConfig ToConfig(AgentProviderPreset preset, string id) => new()
    {
        Id = id,
        Name = preset.Name,
        Kind = preset.Kind,
        BaseUrl = preset.BaseUrl,
        ApiKey = preset.ApiKey,
        Models = preset.Models.ToList(),
        SupportsTools = true,
    };

    /// <summary>新建空白供应商时的骨架。</summary>
    public static AgentProviderConfig CreateBlank(string name = "自定义供应商") => new()
    {
        Id = Guid.NewGuid().ToString("n")[..8],
        Name = name,
        Kind = AgentProviderKind.OpenAiCompatible,
        BaseUrl = "https://api.example.com/v1",
        Models = { "model-id" },
    };

    /// <summary>按模型名猜一个上下文窗口，用于新建供应商时给个合理默认值。</summary>
    public static int GuessContextWindow(string model)
    {
        string id = model.ToLowerInvariant();
        if (id.Contains("128k")) return 131072;
        if (id.Contains("32k")) return 32768;
        if (id.Contains("8k")) return 8192;
        if (id.Contains("16k")) return 16384;
        if (id.Contains("nano") || id.Contains("mini")) return 128000;
        if (id.Contains("deepseek")) return 65536;
        if (id.Contains("claude")) return 200000;
        if (id.Contains("gemini")) return 1000000;
        if (id.Contains("qwen") || id.Contains("glm") || id.Contains("moonshot") ||
            id.Contains("kimi") || id.Contains("llama") || id.Contains("mistral"))
            return 131072;
        return 32768;
    }

    /// <summary>供应商协议的中文名，设置界面下拉用。</summary>
    public static string DescribeKind(AgentProviderKind kind) => kind switch
    {
        AgentProviderKind.OpenAiCompatible => "OpenAI 兼容",
        AgentProviderKind.Anthropic => "Anthropic Messages",
        AgentProviderKind.Gemini => "Gemini generateContent",
        AgentProviderKind.Mock => "本地模拟",
        _ => kind.ToString(),
    };

    public static IReadOnlyList<AgentProviderKind> AllKinds { get; } = Enum.GetValues<AgentProviderKind>();

    /// <summary>列表里显示的配置状态：已配置 / 缺 Key / 本机服务 / 模拟。</summary>
    public static (string Text, string Tone) DescribeStatus(AgentProviderConfig provider)
    {
        if (provider.Kind == AgentProviderKind.Mock) return ("模拟", "info");
        if (IsLocalEndpoint(provider)) return ("本机", "info");
        if (string.IsNullOrWhiteSpace(provider.ApiKey)) return ("缺 Key", "warning");
        return ("已配置", "success");
    }

    public static bool IsLocalEndpoint(AgentProviderConfig provider) =>
        provider.Kind == AgentProviderKind.Mock ||
        provider.BaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
        provider.BaseUrl.Contains("127.0.0.1", StringComparison.Ordinal);

    /// <summary>把玩家输入的地址补成合法 URL：没写协议时本机走 http，其它走 https。</summary>
    public static string NormalizeBaseUrl(string? value)
    {
        string url = (value ?? string.Empty).Trim();
        if (url.Length == 0) return url;
        if (url.Contains("://", StringComparison.Ordinal)) return url.TrimEnd('/');

        bool local = url.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                     url.StartsWith("127.0.0.1", StringComparison.Ordinal) ||
                     url.StartsWith("0.0.0.0", StringComparison.Ordinal);
        return (local ? "http://" : "https://") + url.TrimEnd('/');
    }

    public static string Summarize(AgentProviderConfig provider, string model) =>
        string.IsNullOrWhiteSpace(model) ? provider.Name : $"{provider.Name} · {model}";
}
