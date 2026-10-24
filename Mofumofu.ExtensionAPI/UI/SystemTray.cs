namespace Mofumofu.ExtensionAPI.UI;

/// <summary>
/// 系统托盘常驻：往启动器的托盘菜单追加项、发托盘通知。
/// 托盘菜单项在启动器最小化到托盘时依然可达——适合"后台守护"类插件的常驻入口。
/// </summary>
public interface ISystemTray
{
    /// <summary>
    /// 在启动器托盘菜单的「退出」之前追加一个菜单项。
    /// </summary>
    /// <param name="label">菜单文字</param>
    /// <param name="click">点击回调（UI 线程）</param>
    /// <returns>撤销句柄：从托盘菜单移除；插件停用/卸载时宿主代为调用。需要 widget 权限。</returns>
    IDisposable AddMenuItem(string label, Action click);

    /// <summary>
    /// 发一条托盘气泡通知（系统通知）；宿主所在平台不支持气泡时回退为启动器内 Toast。需要 notify 权限。
    /// </summary>
    void ShowNotification(string title, string message);
}
