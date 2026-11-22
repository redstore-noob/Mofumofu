using Avalonia.Controls;
using Mofumofu.ExtensionAPI.Actions;
using Mofumofu.ExtensionAPI.Downloads;
using Mofumofu.ExtensionAPI.Game;
using Mofumofu.ExtensionAPI.Java;
using Mofumofu.ExtensionAPI.Tools;

namespace Mofumofu.ExtensionAPI.UI;

/// <summary>
/// 宿主提供给插件的能力集合（组合根）：每项能力在宿主侧都按权限区校验，
/// 读取类需 <see cref="PluginPermissionZones.Readonly"/>，UI 扩展需
/// <see cref="PluginPermissionZones.Widget"/>，通知需 <see cref="PluginPermissionZones.Notify"/>，
/// 网络写操作需 <see cref="PluginPermissionZones.Network"/>，配置读写需
/// <see cref="PluginPermissionZones.Storage"/>，实例写操作需 <see cref="PluginPermissionZones.InstanceWrite"/>。
/// 为保证已编译插件兼容，扩展能力请新增「带默认实现」的接口 / 属性，不要改动既有成员签名。
/// </summary>
public interface IPluginContext
{
    IPopup Popup { get; }

    IPageContentHost Pages { get; }

    IPageRegister PageRegister { get; }

    IWidgetAdd Widget { get; }

    IWallpaperEdit Wallpaper { get; }

    IStyleLoad Style { get; }

    IGameLaunch Game { get; }

    IInstanceDownload Downloads { get; }

    IDownloadAction DownloadAction { get; }

    IResourceDownload ResourceDownload { get; }

    IJavaDownload Java { get; }

    IInstanceAction InstanceAction { get; }

    ITaskbarTray Taskbar { get; }

    ITheme Theme { get; }

    ISystemTray SystemTray { get; }

    IConfig Config { get; }

    void Log(string message);

    /// <summary>
    /// 图标颜色跟随宿主主题，无需插件处理。
    /// kind 不存在时返回 null：要求非空 <c>Control</c> 的入口（RegisterPage / Dialog / AddWidget /
    /// WallpaperTakeOver）不能直接透传返回值，否则启用 Nullable 时会得到 CS8604 警告或运行时 NRE。
    /// </summary>
    Control? CreateIcon(string kind, double size = 16);

    bool HasIcon(string kind);

    /// <summary>只读快照，插件不要强转修改。</summary>
    IReadOnlyList<string> IconKinds { get; }

    /// <summary>
    /// 带默认实现，保证已编译插件引用本接口时不会因新增成员而二进制不兼容；
    /// 未接线该成员的宿主调用时抛 <see cref="NotSupportedException"/>。
    /// </summary>
    IServerStatus ServerStatus => throw new NotSupportedException(
        "当前宿主未接线 IServerStatus（serverStatus 能力不可用）。");

    ILogWrite LogWrite => throw new NotSupportedException(
        "当前宿主未接线 ILogWrite。");

    IMemoryInfo MemoryInfo => throw new NotSupportedException(
        "当前宿主未接线 IMemoryInfo。");
}
