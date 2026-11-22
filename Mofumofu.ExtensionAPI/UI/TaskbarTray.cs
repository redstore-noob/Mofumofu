using Avalonia.Controls;

namespace Mofumofu.ExtensionAPI.UI;

/// <summary>
/// 任务栏右侧扩展：图标出现在版本选择框左侧，样式与任务栏按钮一致。
/// </summary>
/// <remarks>
/// 需要 <see cref="PluginPermissionZones.Widget"/> 权限。<c>icon</c> 建议用
/// <c>IPluginContext.CreateIcon</c> 取内置图标并先判空兜底；尺寸过大会被约束到任务栏高度内。
/// </remarks>
public interface ITaskbarTray
{
    /// <summary>id 会自动加插件前缀；click 在 UI 线程回调。返回的句柄可撤销。</summary>
    IDisposable AddWidget(string id, Control icon, Action click);

    /// <summary>可跳内置页（如 "home" / "download"）或插件自己注册的页面。</summary>
    void NavigateTo(string pageId);
}
