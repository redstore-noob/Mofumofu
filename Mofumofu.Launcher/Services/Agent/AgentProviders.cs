using System;
using System.Collections.Generic;
using System.Net;

namespace Mofumofu.Launcher.Services.Agent;

public sealed record AgentProviderPreset(
    string Id,
    string Name,
    string Note,
    AgentProviderKind Kind,
    string BaseUrl);

public static class AgentProviderPresets
{
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

    private static bool IsBlockedHost(string host)
    {
        host = host.Trim().Trim('[', ']').ToLowerInvariant();

        if (host is "metadata" or "metadata.google.internal" or "metadata.azure.internal"
            or "169.254.169.254" or "100.100.100.200")
            return true;

        if (host is "localhost" or "127.0.0.1" or "::1") return false;

        if (!IPAddress.TryParse(host, out IPAddress? address)) return false;

        // IPv4 映射的 IPv6 地址先规整成 IPv4 再判，避免 ::ffff:169.254.169.254 绕过。
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return bytes[0] == 0xFC || bytes[0] == 0xFD ||
                   (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) ||
                   address.Equals(IPAddress.IPv6None) ||
                   address.Equals(IPAddress.IPv6Loopback);
        }

        int a = bytes[0], b = bytes[1];
        return a == 0 || a == 10 || a == 127 ||
               (a == 169 && b == 254) ||
               (a == 172 && b is >= 16 and <= 31) ||
               (a == 192 && b == 168) ||
               (a == 100 && b is >= 64 and <= 127) ||
               a >= 224;
    }

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
