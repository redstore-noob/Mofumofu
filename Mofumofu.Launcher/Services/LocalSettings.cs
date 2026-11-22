using System;
using System.Collections.Generic;
using System.IO;
using MofuCore.Config;
using MofuCore.Log;
using Mofumofu.Launcher.Services.Agent;
using YamlDotNet.Serialization;

namespace Mofumofu.Launcher;

public sealed class DesktopWidgetPage
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<DesktopWidgetPlacement> Widgets { get; set; } = new();
}

public sealed class DesktopWidgetPlacement
{
    public string Id { get; set; } = string.Empty;
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
}

public sealed class PluginStateEntry
{
    public string Id { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public Dictionary<string, bool> Permissions { get; set; } = new();

    public string ConsentDllHash { get; set; } = string.Empty;

    public List<string> ConsentPermissions { get; set; } = new();

    public string ConsentTime { get; set; } = string.Empty;
}

public sealed class LocalSettings
{
    private static string SettingsPath => Path.Combine(YamlRead.ConfigDirectory, "settings.yaml");

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithIndentedSequences()
        .Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();


    public string Theme { get; set; } = "dark";
    public string Language { get; set; } = "zh-CN";
    public string SelectedInstanceId { get; set; } = string.Empty;
    public string SelectedAccountName { get; set; } = string.Empty;
    public string StartBarPlacement { get; set; } = "bottom";
    public bool StartBarAutoHide { get; set; }
    public bool PanelBlur { get; set; } = true;

    public int WindowOpacity { get; set; } = 100;
    public int BackgroundOpacity { get; set; } = 80;
    public bool AccentFollowBackground { get; set; }
    public string AccentColor { get; set; } = "klein";

    public List<string> RecentAccentColors { get; set; } = new();

    public List<string> TaskbarPages { get; set; } = new() { "home", "download", "instances", "settings" };

    public string IconPalette { get; set; } = "colorful";

    public string CloseAction { get; set; } = "exit";

    public bool SingleInstance { get; set; } = true;

    public bool SquareCorners { get; set; }

    public string DownloadSource { get; set; } = "bmcl";
    public string DownloadFallbackSource { get; set; } = "official";
    public string CustomDownloadSourceName { get; set; } = "自定义源";
    public string CustomDownloadSourceUrl { get; set; } = "";

    public int ParallelDownloads { get; set; } = 32;

    [YamlIgnore]
    public string CurseForgeApiKey { get; set; } = string.Empty;

    [YamlMember(Alias = "CurseForgeApiKey")]
    public string CurseForgeApiKeyProtected
    {
        get => AgentSecretProtector.Protect(CurseForgeApiKey);
        set => CurseForgeApiKey = AgentSecretProtector.Unprotect(value);
    }

    public bool AutoInstallDependencies { get; set; } = true;

    public bool AutoCheckUpdates { get; set; } = false;
    public string? LastUpdateCheckAt { get; set; }

    public bool OobeCompleted { get; set; }

    public List<PluginStateEntry> PluginStates { get; set; } = new();

    public List<string> PluginRepos { get; set; } = new();

    public List<QuickServerEntry> QuickServers { get; set; } = new();

    public List<DesktopWidgetPlacement> DesktopWidgets { get; set; } = new();

    public List<DesktopWidgetPage> DesktopWidgetPages { get; set; } = new();

    public int ActiveDesktopWidgetPage { get; set; }

    public int DesktopLayoutVersion { get; set; }

    public static LocalSettings Current { get; private set; } = new();

    private static readonly object Gate = new();

    public static void Load()
    {
        lock (Gate)
        {
            if (!File.Exists(SettingsPath)) return;
            bool decryptFailed = false;
            try
            {
                AgentSecretProtector.ResetFlags();
                var parsed = Deserializer.Deserialize<LocalSettings>(File.ReadAllText(SettingsPath));
                if (parsed is not null)
                {
                    parsed.DesktopWidgets ??= new();
                    parsed.DesktopWidgetPages ??= new();
                    parsed.PluginStates ??= new();
                    foreach (PluginStateEntry entry in parsed.PluginStates)
                    {
                        entry.Permissions ??= new();
                        entry.ConsentPermissions ??= new();
                    }
                    parsed.PluginRepos ??= new();
                    parsed.QuickServers ??= new();
                    parsed.RecentAccentColors ??= new();
                    parsed.TaskbarPages ??= new() { "home", "download", "instances", "settings" };
                    parsed.IconPalette ??= "colorful";
                    parsed.DownloadFallbackSource ??= "official";
                    parsed.CustomDownloadSourceName ??= "自定义源";
                    parsed.CustomDownloadSourceUrl ??= "";
                    // 兼容反序列化直接命中明文属性的情形：仍带密文前缀时补做解密。
                    if (AgentSecretProtector.LooksEncrypted(parsed.CurseForgeApiKey))
                        parsed.CurseForgeApiKey = AgentSecretProtector.Unprotect(parsed.CurseForgeApiKey);
                    Current = parsed;
                    decryptFailed = AgentSecretProtector.DecryptFailed;
                }
            }
            catch (Exception ex)
            {
                LogWrite.Write("ERROR", "[LocalSettings] 设置文件读取/解析失败，已改用默认设置：" + ex.Message);
                BackupCorruptedFile();
            }
            finally
            {
                // 清除与 Agent 子系统共享的全局标志，避免串扰。
                AgentSecretProtector.ResetFlags();
            }

            if (decryptFailed)
                LogWrite.Write("ERROR", "[LocalSettings] 部分加密凭据无法解密（可能换过账户/机器或主密钥丢失），未改动的值不会被覆盖。");
        }
    }

    /// <summary>把损坏的设置文件重命名备份，避免后续 Save 覆盖原始内容。</summary>
    private static void BackupCorruptedFile()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            string backup = SettingsPath + ".bad-" + stamp;
            int index = 0;
            while (File.Exists(backup)) backup = SettingsPath + ".bad-" + stamp + "-" + (++index);
            File.Move(SettingsPath, backup);
            LogWrite.Write("WARN", "[LocalSettings] 已将损坏的设置文件备份为 " + Path.GetFileName(backup) + "，原数据可手动恢复。");
        }
        catch (Exception ex)
        {
            LogWrite.Write("ERROR", "[LocalSettings] 损坏文件备份失败，原文件仍保留在磁盘：" + ex.Message);
        }
    }

    public static void Save()
    {
        lock (Gate)
        {
            SaveCore();
        }
    }

    /// <summary>
    /// 在 Gate 内只读访问 PluginStates：与 SaveCore 的序列化互斥，避免并发枚举时抛「集合已修改」。
    /// 所有对插件状态的读取（FirstOrDefault / 遍历等）都应走这里。
    /// </summary>
    public static T WithPluginStates<T>(Func<List<PluginStateEntry>, T> read)
    {
        lock (Gate) return read(Current.PluginStates);
    }

    /// <summary>
    /// 在 Gate 内修改 PluginStates（新增条目 / 改权限字典等），与 SaveCore 的序列化互斥；
    /// 回调里可安全调用 <see cref="Save"/>（Monitor 可重入）。
    /// </summary>
    public static void MutatePluginStates(Action<List<PluginStateEntry>> mutate)
    {
        lock (Gate) mutate(Current.PluginStates);
    }

    private static void SaveCore()
    {
        // 先写临时文件再原子替换，避免写一半把原文件损坏。
        string temp = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(temp, Serializer.Serialize(Current));
            File.Move(temp, SettingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            LogWrite.Write("ERROR", "[LocalSettings] 设置保存失败：" + ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// 重置启动器偏好与个性化配置；保留当前账号/实例选择，OOBE 完成状态恢复默认，
    /// 让首次设置向导在下次启动时重新弹出。不删除账号、实例、游戏文件或插件文件。
    /// </summary>
    public static void ResetToDefaults()
    {
        lock (Gate)
        {
            Current = new LocalSettings
            {
                SelectedInstanceId = Current.SelectedInstanceId,
                SelectedAccountName = Current.SelectedAccountName,
            };
            SaveCore();
        }
    }
}

public sealed class QuickServerEntry
{
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
}
