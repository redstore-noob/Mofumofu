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

    public AgentProviderConfig? EnsureProvider()
    {
        if (Providers.Count == 0)
        {
            ActiveProviderId = string.Empty;
            ActiveModel = string.Empty;
            return null;
        }

        if (string.IsNullOrWhiteSpace(ActiveProviderId) ||
            Providers.All(p => p.Id != ActiveProviderId))
        {
            ActiveProviderId = Providers[0].Id;
        }

        AgentProviderConfig provider = Providers.First(p => p.Id == ActiveProviderId);
        if (string.IsNullOrWhiteSpace(ActiveModel) || !provider.Models.Contains(ActiveModel))
        {
            ActiveModel = provider.Models.FirstOrDefault() ?? string.Empty;
        }

        return provider;
    }

    public void CopyFrom(AgentSettings source)
    {
        Providers = source.Providers;
        ActiveProviderId = source.ActiveProviderId;
        ActiveModel = source.ActiveModel;
        TopP = source.TopP;
        MaxOutputTokens = source.MaxOutputTokens;
        Streaming = source.Streaming;
        ContextWindow = source.ContextWindow;
        AutoCompact = source.AutoCompact;
        CompactAt = source.CompactAt;
        KeepRecentMessages = source.KeepRecentMessages;
        SystemPrompt = source.SystemPrompt;
        MaxToolRounds = source.MaxToolRounds;
        Tools = source.Tools;
        ShowInspector = source.ShowInspector;
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
    private static readonly object WriteGate = new();

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

    public static string BackupPath => SettingsPath + ".bak";

    public static AgentSettings Load()
    {
        LastError = null;

        AgentSettings? fromFile = TryRead(SettingsPath, out string primaryError);
        if (fromFile is not null) return fromFile;

        AgentSettings? fromBackup = TryRead(BackupPath, out _);
        if (fromBackup is not null)
        {
            Save(fromBackup);
            LastError = "设置文件损坏，已从备份自动恢复。";
            return fromBackup;
        }

        if (File.Exists(SettingsPath) || File.Exists(BackupPath))
            LastError = "读取设置失败：" + primaryError + "（已改用默认设置，原文件保留未删）";

        return new AgentSettings();
    }

    private static AgentSettings? TryRead(string path, out string error)
    {
        error = string.Empty;
        if (!File.Exists(path)) return null;

        try
        {
            AgentSecretProtector.ResetFlags();
            string json = File.ReadAllText(path);
            AgentSettings? loaded = JsonSerializer.Deserialize<AgentSettings>(json, Options);
            if (loaded is null)
            {
                error = "文件内容为空";
                return null;
            }

            loaded.Providers ??= new List<AgentProviderConfig>();
            loaded.Tools ??= new AgentToolSettings();

            if (AgentSecretProtector.DecryptFailed)
            {
                LastError = "部分 API Key 无法解密（可能换过账户、机器或主密钥丢失）。" +
                            "请在供应商设置里重新填写；没改动的值不会被覆盖。";
            }

            return loaded;
        }
        catch (Exception exception)
        {
            AgentSecretProtector.ResetFlags();
            error = exception.Message;
            return null;
        }
    }

    public static bool Save(AgentSettings settings)
    {
        try
        {
            lock (WriteGate)
            {
                Directory.CreateDirectory(RootDirectory);
                string json = JsonSerializer.Serialize(settings, Options);

                if (File.Exists(SettingsPath))
                {
                    try { File.Copy(SettingsPath, BackupPath, true); }
                    catch { /* 备份失败不阻断正常保存 */ }
                }

                AtomicWrite(SettingsPath, json);
            }

            LastError = null;
            return true;
        }
        catch (Exception error)
        {
            LastError = "保存设置失败：" + error.Message;
            return false;
        }
    }

    public static void AtomicWrite(string path, string content)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, true);
    }
}
