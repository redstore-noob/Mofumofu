using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MofuCore.Config;

namespace Mofumofu.Launcher.Services.Agent;

/// <summary>工具权限：允许调用哪些、写操作是否要先确认。</summary>
public sealed class AgentToolSettings
{
    /// <summary>总开关：关掉后模型拿不到任何工具。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>启动器控制类工具（开页、启动游戏、提示）总开关。</summary>
    public bool LauncherControl { get; set; } = true;

    /// <summary>写操作（启动游戏等）执行前先弹确认。</summary>
    public bool ConfirmWrites { get; set; } = true;

    /// <summary>单次工具执行超时（秒）。</summary>
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>被单独关掉的工具名。</summary>
    public List<string> DisabledTools { get; set; } = new();
}

/// <summary>MofuAgent 的全部可配置项，保存在 {配置目录}/mofuagent/settings.json。</summary>
public sealed class AgentSettings
{
    public List<AgentProviderConfig> Providers { get; set; } = new();

    public string ActiveProviderId { get; set; } = string.Empty;
    public string ActiveModel { get; set; } = string.Empty;

    // ---- 生成参数 ----
    public double Temperature { get; set; } = 0.35;
    public double TopP { get; set; } = 1.0;
    public int MaxOutputTokens { get; set; } = 2048;
    public bool Streaming { get; set; } = true;

    // ---- 上下文 ----
    /// <summary>模型上下文窗口（token），用于用量条与自动压缩阈值。</summary>
    public int ContextWindow { get; set; } = 32768;

    public bool AutoCompact { get; set; } = true;

    /// <summary>占用超过该比例时自动压缩历史。</summary>
    public double CompactAt { get; set; } = 0.78;

    /// <summary>压缩时保留的最近消息条数。</summary>
    public int KeepRecentMessages { get; set; } = 8;

    // ---- 行为 ----
    public string SystemPrompt { get; set; } = DefaultSystemPrompt;

    /// <summary>单轮最多连续工具调用轮次，防止死循环。</summary>
    public int MaxToolRounds { get; set; } = 6;

    public AgentToolSettings Tools { get; set; } = new();

    /// <summary>上次打开的会话 id，启动时恢复。</summary>
    public string LastConversationId { get; set; } = string.Empty;

    /// <summary>对话页右侧的上下文仪表是否展开。窄窗口下可以收起来给正文让位。</summary>
    public bool ShowInspector { get; set; } = true;

    public const string DefaultSystemPrompt =
        "你是 Mofumofu 启动器内置的智能体，名字叫 MofuAgent。\n" +
        "你可以和玩家讨论 Minecraft 相关话题，也可以通过工具直接操作启动器：查看实例、启动游戏、跳转页面、发提示等。\n" +
        "规则：\n" +
        "1. 需要真实信息（实例列表、运行状态）时必须调用工具，不要凭印象编造。\n" +
        "2. 调用工具前先用一句话说明你打算做什么；工具返回后给出简洁结论。\n" +
        "3. 涉及启动游戏、修改配置这类操作，先确认玩家意图，再执行。\n" +
        "4. 回答用中文，短句为主，不要堆砌小标题；代码与命令使用代码块。\n" +
        "5. 不确定的事情直说不确定，不要编造版本号、模组名或文件路径。";

    [JsonIgnore]
    public AgentProviderConfig? ActiveProvider =>
        Providers.FirstOrDefault(p => p.Id == ActiveProviderId) ?? Providers.FirstOrDefault();

    public AgentProviderConfig EnsureProvider()
    {
        if (Providers.Count == 0)
        {
            Providers.AddRange(AgentProviderPresets.CreateDefaults());
        }

        if (string.IsNullOrWhiteSpace(ActiveProviderId) ||
            Providers.All(p => p.Id != ActiveProviderId))
        {
            ActiveProviderId = Providers[0].Id;
        }

        AgentProviderConfig provider = Providers.First(p => p.Id == ActiveProviderId);
        if (provider.Models.Count == 0)
        {
            // 手写的 settings.json 可能把模型列表留空，兜一个占位避免下拉框空白。
            provider.Models.Add("model-id");
        }

        if (string.IsNullOrWhiteSpace(ActiveModel) || !provider.Models.Contains(ActiveModel))
        {
            ActiveModel = provider.Models.FirstOrDefault() ?? string.Empty;
        }

        return provider;
    }

    /// <summary>当前模型生效的上下文窗口：供应商覆盖值优先，否则用全局值。</summary>
    public int ResolveContextWindow()
    {
        AgentProviderConfig? provider = ActiveProvider;
        if (provider is { ContextWindow: > 0 }) return provider.ContextWindow;
        return Math.Max(2048, ContextWindow);
    }
}

/// <summary>设置读写。文件很小，直接整份读写；写失败不抛给 UI。</summary>
public static class AgentSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string RootDirectory =>
        Path.Combine(YamlRead.ConfigDirectory, "mofuagent");

    public static string SettingsPath => Path.Combine(RootDirectory, "settings.json");

    public static string SessionsDirectory => Path.Combine(RootDirectory, "sessions");

    public static string? LastError { get; private set; }

    public static AgentSettings Load()
    {
        LastError = null;
        AgentSecretProtector.ResetFlags();

        try
        {
            if (File.Exists(SettingsPath))
            {
                AgentSettings? loaded = JsonSerializer.Deserialize<AgentSettings>(
                    File.ReadAllText(SettingsPath), Options);
                if (loaded is not null)
                {
                    loaded.Providers ??= new List<AgentProviderConfig>();
                    loaded.Tools ??= new AgentToolSettings();
                    if (loaded.Providers.Count == 0)
                        loaded.Providers.AddRange(AgentProviderPresets.CreateDefaults());
                    NormalizeProviderNames(loaded);

                    // 旧版本把 Key 直接写在 JSON 里：立刻回写一次加密版本，
                    // 避免磁盘上继续留明文。解密失败时绝不回写，否则会把 Key 抹掉。
                    if ((AgentSecretProtector.LegacyPlaintextFound ||
                         AgentSecretProtector.LegacyDpapiFound) &&
                        !AgentSecretProtector.DecryptFailed)
                    {
                        Save(loaded);
                    }

                    if (AgentSecretProtector.DecryptFailed)
                    {
                        LastError = "部分 API Key 无法解密（可能换过系统账户、机器，或主密钥丢失），请在供应商设置里重新填写。";
                    }

                    return loaded;
                }
            }
        }
        catch (Exception error)
        {
            LastError = "读取设置失败：" + error.Message;
        }

        var settings = new AgentSettings();
        settings.Providers.AddRange(AgentProviderPresets.CreateDefaults());
        settings.EnsureProvider();
        return settings;
    }

    /// <summary>
    /// 早期版本的预设名太长，页头的下拉框会被截断。只在名字仍是旧默认值时改成短名，
    /// 玩家自己改过的名字不动。
    /// </summary>
    private static void NormalizeProviderNames(AgentSettings settings)
    {
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["本地模拟（无需 Key）"] = "本地模拟",
            ["月之暗面 Kimi"] = "Kimi",
            ["通义千问（兼容模式）"] = "通义千问",
            ["Ollama（本机）"] = "Ollama",
            ["LM Studio（本机）"] = "LM Studio",
            ["Anthropic Claude"] = "Claude",
            ["Google Gemini"] = "Gemini",
        };

        foreach (AgentProviderConfig provider in settings.Providers)
        {
            if (renamed.TryGetValue(provider.Name, out string? shortName))
                provider.Name = shortName;
        }
    }

    public static bool Save(AgentSettings settings)
    {
        try
        {
            Directory.CreateDirectory(RootDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Options));
            LastError = null;
            return true;
        }
        catch (Exception error)
        {
            LastError = "保存设置失败：" + error.Message;
            return false;
        }
    }
}
