using Avalonia.Controls;
using Mofumofu.ExtensionAPI.Data;

namespace Mofumofu.ExtensionAPI.UI;

/// <summary>
/// 往任意页面插入内容。宿主每次进入页面时按注册顺序重建。
/// </summary>
/// <remarks>
/// 需要 <see cref="PluginPermissionZones.Widget"/> 权限。<c>factory</c> 每次进入页面都会调用一次，
/// 必须返回新控件（同一控件实例不能同时挂在两个地方）。
/// </remarks>
public interface IPageContentHost
{
    /// <summary>
    /// pageId 如 "home"、"download"、"settings"，传 <c>"*"</c> 表示所有页面；
    /// order 数值小的排前面。返回的句柄可撤销注册。
    /// </summary>
    IDisposable Add(string pageId, PageSlot slot, Func<Control> factory, int order = 0);
}
