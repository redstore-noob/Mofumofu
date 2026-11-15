using System;
using System.Collections.Generic;
using System.IO;
using MofuCore.Config;
using MofuCore.Log;
using Mofumofu.Launcher.Services.Agent;
using YamlDotNet.Serialization;

namespace Mofumofu.Launcher;

/// <summary>桌面小组件在格子板上的位置与占格（8 列/行单位）。</summary>
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

/// <summary>单个插件的宿主侧状态：是否启用 + 各权限区开关 + 安装同意记录。</summary>
public sealed class PluginStateEntry
{
    public string Id { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    /// <summary>权限区名 → 是否允许；未记录的权限区按"已声明即允许"处理。</summary>
    public Dictionary<string, bool> Permissions { get; set; } = new();

    /// <summary>同意授权时 extension.dll 的 SHA256（变化 = 插件已更新，需要重新同意）。</summary>
    public string ConsentDllHash { get; set; } = string.Empty;

    /// <summary>首次同意时一次性授权的权限域（清单新增权限时需要重新同意）。</summary>
    public List<string> ConsentPermissions { get; set; } = new();

    /// <summary>最近一次同意时间（ISO 8601）。</summary>
    public string ConsentTime { get; set; } = string.Empty;
}

/// <summary>
/// 本地轻量设置（原生重写阶段替代后端 ConfigAPI）：主题、实例/账户选择等。
/// 与 MofuCore 共用配置目录，保存在 {DataRoot}/Mofumofu/config/settings.yaml。
/// </summary>
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

    /// <summary>整个窗口的不透明度（40-100）。</summary>
    public int WindowOpacity { get; set; } = 100;
    public int BackgroundOpacity { get; set; } = 80;
    public bool AccentFollowBackground { get; set; }
    public string AccentColor { get; set; } = "klein";

    /// <summary>自定义主题色弹窗里「最近使用」的颜色（#RRGGBB，最多 8 个）。</summary>
    public List<string> RecentAccentColors { get; set; } = new();

    /// <summary>任务栏（开始栏左侧）显示的页面 id，可在设置里右键增删。</summary>
    public List<string> TaskbarPages { get; set; } = new() { "home", "download", "instances", "settings" };

    /// <summary>图标配色：theme（跟随主题）/ colorful（多彩）。</summary>
    public string IconPalette { get; set; } = "colorful";

    /// <summary>关闭窗口行为：tray（最小化到托盘）/ exit（退出）/ ask（每次询问）。</summary>
    public string CloseAction { get; set; } = "exit";

    /// <summary>单实例运行：关闭时允许多开（重启后生效）。</summary>
    public bool SingleInstance { get; set; } = true;

    /// <summary>直角模式：窗口与面板圆角归零。</summary>
    public bool SquareCorners { get; set; }

    /// <summary>下载源：bmcl（BMCLAPI 镜像）/ official（Mojang 官方）。</summary>
    public string DownloadSource { get; set; } = "bmcl";
    public string DownloadFallbackSource { get; set; } = "official";
    public string CustomDownloadSourceName { get; set; } = "自定义源";
    public string CustomDownloadSourceUrl { get; set; } = "";

    /// <summary>并行下载线程数（1 - 16，实际生效前会再按 MofuCore 的限制夹取）。</summary>
    public int ParallelDownloads { get; set; } = 32;

    /// <summary>CurseForge API Key（内存中为明文；落盘时由 AgentSecretProtector 字段级加密）。</summary>
    [YamlIgnore]
    public string CurseForgeApiKey { get; set; } = string.Empty;

    /// <summary>settings.yaml 中 CurseForgeApiKey 的加密表示，复用 Agent 凭据加密并兼容旧明文。</summary>
    [YamlMember(Alias = "CurseForgeApiKey")]
    public string CurseForgeApiKeyProtected
    {
        get => AgentSecretProtector.Protect(CurseForgeApiKey);
        set => CurseForgeApiKey = AgentSecretProtector.Unprotect(value);
    }

    /// <summary>安装模组时自动补齐缺失的前置模组。</summary>
    public bool AutoInstallDependencies { get; set; } = true;

    /// <summary>自动更新检查已禁用；兼容旧配置，但忽略其中保存的开启值。</summary>
    public bool AutoCheckUpdates { get => false; set { } }

    /// <summary>是否已完成首次启动引导（OOBE）。</summary>
    public bool OobeCompleted { get; set; }

    /// <summary>插件状态（启用/停用 + 权限开关），按插件 Id 索引。</summary>
    public List<PluginStateEntry> PluginStates { get; set; } = new();

    /// <summary>插件商店已添加的仓库地址（GitHub 仓库 URL）。</summary>
    public List<string> PluginRepos { get; set; } = new();

    /// <summary>服务器小组件里收藏的外部服务器（可添加多台，点击直接进服）。</summary>
    public List<QuickServerEntry> QuickServers { get; set; } = new();

    /// <summary>桌面小组件布局（自由摆放：格子坐标 + 占格）。</summary>
    public List<DesktopWidgetPlacement> DesktopWidgets { get; set; } = new();

    /// <summary>桌面组件分页（每页一组组件摆放）。</summary>
    public List<DesktopWidgetPage> DesktopWidgetPages { get; set; } = new();

    /// <summary>当前选中的桌面组件页索引。</summary>
    public int ActiveDesktopWidgetPage { get; set; }

    /// <summary>桌面布局版本：升级默认版式时用来触发一次重排。</summary>
    public int DesktopLayoutVersion { get; set; }

    public static LocalSettings Current { get; private set; } = new();

    private static readonly object Gate = new();

    public static void Load()
    {
        lock (Gate)
        {
            if (!File.Exists(SettingsPath)) return;
            bool migrate = false;
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
                    migrate = AgentSecretProtector.LegacyPlaintextFound && !AgentSecretProtector.DecryptFailed;
                    decryptFailed = AgentSecretProtector.DecryptFailed;
                }
            }
            catch (Exception ex)
            {
                // 损坏时不再静默重置为默认；先备份原文件，让后续 Save 无法覆盖原始数据。
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
            if (migrate)
                SaveCore(); // 一次性把旧明文凭据回写为加密版本
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
            // 写入失败必须记录，不再静默吞掉，避免用户误以为已保存。
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
                // 临时文件清理失败不影响主流程
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

/// <summary>服务器小组件里的一台收藏服务器（地址支持 host / host:port）。</summary>
public sealed class QuickServerEntry
{
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
}
