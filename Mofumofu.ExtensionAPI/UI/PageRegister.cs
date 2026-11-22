using Avalonia.Controls;

namespace Mofumofu.ExtensionAPI.UI;

/// <summary>
/// 注册一个新的导航页面（启动器左下角的开始菜单里的条目）。
/// </summary>
/// <remarks>
/// 需要 <see cref="PluginPermissionZones.Widget"/> 权限。<c>createPage</c> 每次进入页面都会调用一次
/// 且必须返回新控件（不要缓存），<c>icon</c> 按非 null 处理（用 <c>IPluginContext.CreateIcon</c> 时请先判空）。
/// </remarks>
public interface IPageRegister
{
    /// <summary>isTool 为 true 时注册到工具下拉栏。返回的句柄可撤销，插件停用/卸载时宿主也会代为调用。</summary>
    IDisposable RegisterPage(string pageName, Func<Control> createPage, Control icon, Boolean isTool);
}
