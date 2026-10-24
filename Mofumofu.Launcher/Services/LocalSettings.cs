using System;
using System.Collections.Generic;
using System.IO;
using MofuCore.Config;
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
    public int WindowOpacity { get; set; } = 100;
    public int BackgroundOpacity { get; set; } = 80;
    public bool AccentFollowBackground { get; set; }
    public string AccentColor { get; set; } = "teal";

    /// <summary>自定义主题色弹窗里「最近使用」的颜色（#RRGGBB，最多 8 个）。</summary>
    public List<string> RecentAccentColors { get; set; } = new();

    /// <summary>任务栏（开始栏左侧）显示的页面 id，可在设置里右键增删。</summary>
    public List<string> TaskbarPages { get; set; } = new() { "home", "download", "instances", "settings" };

    /// <summary>图标配色：theme（跟随主题）/ colorful（多彩）。</summary>
    public string IconPalette { get; set; } = "colorful";

    /// <summary>卓越视效：毛玻璃的高光、阴影、折射、渐进模糊等额外效果。默认关闭。</summary>
    public bool PremiumVisuals { get; set; }

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

    /// <summary>CurseForge API Key（仅保存在本机设置文件）。</summary>
    public string CurseForgeApiKey { get; set; } = string.Empty;

    /// <summary>安装模组时自动补齐缺失的前置模组。</summary>
    public bool AutoInstallDependencies { get; set; } = true;

    /// <summary>启动后联网检查 GitHub Releases 是否有新版本（只在关于页提示，不自动下载）。</summary>
    public bool AutoCheckUpdates { get; set; } = true;

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

    public static void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
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
                    Current = parsed;
                }
            }
        }
        catch
        {
            Current = new LocalSettings();
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, Serializer.Serialize(Current));
        }
        catch
        {
            // 设置写失败不影响运行
        }
    }
}

/// <summary>服务器小组件里的一台收藏服务器（地址支持 host / host:port）。</summary>
public sealed class QuickServerEntry
{
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
}
