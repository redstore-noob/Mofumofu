using Avalonia.Controls;

namespace Mofumofu.ExtensionAPI.UI;

/// <summary>
/// 对启动器壁纸进行修改。视频壁纸所需的 libvlc 过大，不适合随启动器发行，所以做成可选插件。
/// </summary>
/// <remarks>
/// 需要 <see cref="PluginPermissionZones.Widget"/> 权限。接管后启动器自带壁纸功能停用；
/// 多个插件同时接管会互相覆盖。
/// </remarks>
public interface IWallpaperEdit
{
    /// <summary>控件铺满背景层；返回的句柄可恢复启动器自带壁纸。</summary>
    IDisposable WallpaperTakeOver(Control wallpaperContent);
}
