using System;
using System.Collections.Generic;
using System.Net;

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
    string BaseUrl);

/// <summary>
/// 内置供应商预设。预设只是"起步模板"，真正生效的是设置里的
/// <see cref="AgentProviderConfig"/>，所以玩家可以随意增删、改地址、改模型。
/// </summary>
public static class AgentProviderPresets
{
    /// <summary>可选预设（不含本地模拟，模拟服务始终内置在默认列表里）。</summary>
    public static IReadOnlyList<AgentProviderPreset> All { get; } = new List<AgentProviderPreset>
    {
        new("openai", "OpenAI", "官方接口",
            AgentProviderKind.OpenAiCompatible, "https://api.openai.com/v1"),

        new("deepseek", "DeepSeek", "官方接口",
            AgentProviderKind.OpenAiCompatible, "https://api.deepseek.com/v1"),

        new("moonshot", "Kimi", "月之暗面 · 长上下文",
            AgentProviderKind.OpenAiCompatible, "https://api.moonshot.cn/v1"),

        new("zhipu", "智谱 GLM", "智谱开放平台",
            AgentProviderKind.OpenAiCompatible, "https://open.bigmodel.cn/api/paas/v4"),

        new("dashscope", "通义千问", "阿里云 · 兼容模式",
            AgentProviderKind.OpenAiCompatible, "https://dashscope.aliyuncs.com/compatible-mode/v1"),

        new("siliconflow", "硅基流动", "聚合推理服务",
            AgentProviderKind.OpenAiCompatible, "https://api.siliconflow.cn/v1"),

        new("openrouter", "OpenRouter", "一个 Key 走多家模型",
            AgentProviderKind.OpenAiCompatible, "https://openrouter.ai/api/v1"),

        new("ollama", "Ollama", "本机 11434 端口，不用填 Key",
            AgentProviderKind.OpenAiCompatible, "http://localhost:11434/v1"),

        new("lmstudio", "LM Studio", "本机 1234 端口，不用填 Key",
            AgentProviderKind.OpenAiCompatible, "http://localhost:1234/v1"),

        new("anthropic", "Claude", "Anthropic Messages",
            AgentProviderKind.Anthropic, "https://api.anthropic.com"),

        new("gemini", "Gemini", "Google AI Studio",
            AgentProviderKind.Gemini, "https://generativelanguage.googleapis.com/v1beta"),
    };

    // 官方 HTTPS 域名白名单：由预设自动推导，明文凭据只发给这些主机。
    private static readonly HashSet<string> OfficialHosts = BuildOfficialHosts();

    private static HashSet<string> BuildOfficialHosts()
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AgentProviderPreset preset in All)
        {
            if (Uri.TryCreate(preset.BaseUrl, UriKind.Absolute, out Uri? uri) &&
                uri.Scheme == Uri.UriSchemeHttps)
            {
                hosts.Add(uri.Host);
            }
        }

        return hosts;
    }

    /// <summary>
    /// 仅当目标为内置预设里的官方 HTTPS 端点时才允许附带明文凭据（ApiKey / ExtraHeaders）。
    /// 本机服务、私网与用户自定义端点一律返回 false，避免密钥外带。
    /// </summary>
    public static bool IsOfficialEndpoint(string? baseUrl)
    {
        string normalized = NormalizeBaseUrl(baseUrl);
        return Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri) &&
               uri.Scheme == Uri.UriSchemeHttps &&
               OfficialHosts.Contains(uri.Host);
    }

    /// <summary>
    /// 出站主机黑名单：云元数据、链路本地、私网与回环里的非本机地址一律拒绝；
    /// 只放行内置的 localhost / 127.0.0.1 / ::1 本机 LLM 预设。
    /// </summary>
    private static bool IsBlockedHost(string host)
    {
        host = host.Trim().Trim('[', ']').ToLowerInvariant();

        // 各家云元数据服务（AWS/GCP/Azure/阿里云/OpenStack）。
        if (host is "metadata" or "metadata.google.internal" or "metadata.azure.internal"
            or "169.254.169.254" or "100.100.100.200")
            return true;

        // 内置 Ollama / LM Studio 预设要能继续用本机服务。
        if (host is "localhost" or "127.0.0.1" or "::1") return false;

        if (!IPAddress.TryParse(host, out IPAddress? address)) return false;

        // IPv4 映射的 IPv6 地址先规整成 IPv4 再判，避免 ::ffff:169.254.169.254 绕过。
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            // IPv6：唯一本地 fc00::/7、链路本地 fe80::/10、未指定与回环均拒绝。
            return bytes[0] == 0xFC || bytes[0] == 0xFD ||
                   (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) ||
                   address.Equals(IPAddress.IPv6None) ||
                   address.Equals(IPAddress.IPv6Loopback);
        }

        int a = bytes[0], b = bytes[1];
        // 0/8、10/8、100.64/10、127/8、169.254/16、172.16/12、192.168/16、224/4 以上。
        return a == 0 || a == 10 || a == 127 ||
               (a == 169 && b == 254) ||
               (a == 172 && b is >= 16 and <= 31) ||
               (a == 192 && b == 168) ||
               (a == 100 && b is >= 64 and <= 127) ||
               a >= 224;
    }

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
        _ => kind.ToString(),
    };

    public static IReadOnlyList<AgentProviderKind> AllKinds { get; } = Enum.GetValues<AgentProviderKind>();

    public static int IndexOfKind(AgentProviderKind kind)
    {
        for (int i = 0; i < AllKinds.Count; i++)
            if (AllKinds[i] == kind) return i;
        return 0;
    }

    public static AgentProviderKind KindAt(int index) =>
        AllKinds[Math.Clamp(index, 0, AllKinds.Count - 1)];

    /// <summary>列表里显示的配置状态：已配置 / 缺 Key / 本机服务 / 模拟。</summary>
    public static (string Text, string Tone) DescribeStatus(AgentProviderConfig provider)
    {
        if (string.IsNullOrWhiteSpace(provider.BaseUrl)) return ("未填地址", "warning");
        if (provider.Models.Count == 0) return ("未填模型", "warning");
        if (IsLocalEndpoint(provider)) return ("本机 · 可用", "success");
        if (string.IsNullOrWhiteSpace(provider.ApiKey)) return ("缺 Key", "warning");
        return ("已配置", "success");
    }

    public static bool IsLocalEndpoint(AgentProviderConfig provider) =>
        provider.BaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
        provider.BaseUrl.Contains("127.0.0.1", StringComparison.Ordinal);

    /// <summary>把玩家输入的地址补成合法 URL：没写协议时本机走 http，其它走 https。</summary>
    public static string NormalizeBaseUrl(string? value)
    {
        string url = (value ?? string.Empty).Trim();
        if (url.Length == 0) return url;

        if (url.Contains("://", StringComparison.Ordinal))
        {
            url = url.TrimEnd('/');
            return Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) &&
                   (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps) &&
                   !IsBlockedHost(parsed.Host)
                ? url
                : string.Empty;
        }

        bool local = url.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                     url.StartsWith("127.0.0.1", StringComparison.Ordinal) ||
                     url.StartsWith("0.0.0.0", StringComparison.Ordinal);
        string normalized = (local ? "http://" : "https://") + url.TrimEnd('/');
        return Uri.TryCreate(normalized, UriKind.Absolute, out Uri? built) && !IsBlockedHost(built.Host)
            ? normalized
            : string.Empty;
    }

    public static string Summarize(AgentProviderConfig provider, string model) =>
        string.IsNullOrWhiteSpace(model) ? provider.Name : $"{provider.Name} · {model}";
}
