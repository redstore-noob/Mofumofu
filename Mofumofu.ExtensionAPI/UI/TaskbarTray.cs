using Avalonia.Controls;

namespace Mofumofu.ExtensionAPI.UI;

/// <summary>
/// 任务栏右侧扩展：追加小图标组件，玩家点击时由插件决定弹 Popup 还是跳页面。
/// 图标出现在版本选择框左侧，样式与任务栏按钮一致。
/// </summary>
public interface ITaskbarTray
{
    /// <summary>
    /// 在任务栏右侧追加一个小图标（id 自动加插件前缀）。
    /// </summary>
    /// <param name="id">组件 id（插件内唯一即可）</param>
    /// <param name="icon">
    /// 图标控件，建议 16~20px（用 context.CreateIcon 取内置图标，或自绘控件）。
    /// 尺寸过大会被约束到任务栏高度内。
    /// </param>
    /// <param name="click">
    /// 玩家点击时回调（UI 线程）。
    /// 弹窗：调用 context.Popup.Dialog(...)；跳页面：调用 NavigateTo(...)。
    /// 二选一或组合都行，决定权在插件。
    /// </param>
    /// <returns>撤销句柄：从任务栏移除该图标；插件停用/卸载时宿主代为调用</returns>
    IDisposable AddWidget(string id, Control icon, Action click);

    /// <summary>
    /// 跳转到启动器页面（内置页 id 如 "home" / "download"，或插件自己 RegisterPage 注册的页面）。
    /// </summary>
    void NavigateTo(string pageId);
}
