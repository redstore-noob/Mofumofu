namespace Mofumofu.ExtensionAPI.UI;

/// <summary>
/// 系统托盘常驻：往启动器的托盘菜单追加项、发托盘通知。
/// 菜单项在启动器最小化到托盘时依然可达，适合"后台守护"类插件。
/// </summary>
public interface ISystemTray
{
    /// <summary>菜单项追加在「退出」之前；需要 <see cref="PluginPermissionZones.Widget"/> 权限，回调在 UI 线程。</summary>
    IDisposable AddMenuItem(string label, Action click);

    /// <summary>平台不支持气泡时回退为启动器内 Toast；需要 <see cref="PluginPermissionZones.Notify"/> 权限。</summary>
    void ShowNotification(string title, string message);
}
