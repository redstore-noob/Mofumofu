using Avalonia.Controls;

namespace Mofumofu.ExtensionAPI.UI;

/// <summary>
/// 控制启动器弹出信息的接口，需要 <see cref="PluginPermissionZones.Notify"/> 权限。
/// </summary>
public interface IPopup
{
    public void Dialog(Control content, string title);
    public void Toast(string message);
}