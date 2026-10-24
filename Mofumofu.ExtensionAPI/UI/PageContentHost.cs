using Avalonia.Controls;
using Mofumofu.ExtensionAPI.Data;

namespace Mofumofu.ExtensionAPI.UI;

/// <summary>
/// 往任意页面插入内容。按页面标识注册，宿主每次进入页面时按注册顺序重建。
/// </summary>
public interface IPageContentHost
{
    /// <summary>
    /// 注册一段内容到指定页面。
    /// </summary>
    /// <param name="pageId">
    /// 页面标识，如 "home"、"download"、"settings"，传 <c>"*"</c> 表示所有页面。
    /// </param>
    /// <param name="slot">插入到页面的哪一层。</param>
    /// <param name="factory">创建内容的工厂；每次进入页面都会调用一次，必须返回新控件。</param>
    /// <param name="order">同一层内的排序，数值小的排前面，默认 0。</param>
    /// <returns>取消这次注册；插件卸载或内容失效时调用。</returns>
    IDisposable Add(string pageId, PageSlot slot, Func<Control> factory, int order = 0);
}
