using Avalonia.Controls;

namespace Mofumofu.ExtensionAPI.UI;

/// <summary>
/// 对启动器壁纸进行修改
/// 视频壁纸原本所需的libvlc过大，不适合随启动器发行，作为可选插件更好
/// </summary>
public interface IWallpaperEdit
{
    /// <summary>
    /// 接管启动器背景
    /// 接管启动器自带壁纸功能停用，注意多个插件打架问题
    /// </summary>
    /// <param name="wallpaperContent">接管壁纸的控件，铺满背景层</param>
    /// <returns>撤销句柄：调用后恢复启动器自带壁纸；插件停用/卸载时宿主也会代为调用</returns>
    IDisposable WallpaperTakeOver(Control wallpaperContent);
}
