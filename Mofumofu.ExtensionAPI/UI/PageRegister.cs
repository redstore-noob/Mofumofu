using Avalonia.Controls;

namespace Mofumofu.ExtensionAPI.UI;

/// <summary>
/// 注册一个新的标签页(就是启动器左下角长得跟开始菜单一样的东西里面的一个一个项那个)
/// </summary>
public interface IPageRegister
{
    /// <summary>
    /// 在左下角那个长得像开始菜单的地方注册一个新页面
    /// </summary>
    /// <param name="pageName">页面在开始菜单的名字</param>
    /// <param name="createPage">
    /// 页面内容工厂：宿主每次进入该页面都会调用一次，必须返回新控件
    /// （同一个控件实例不能同时挂在两个地方，所以不要缓存返回值）
    /// </param>
    /// <param name="icon">在“开始菜单显示的图标”</param>
    /// <param name="isTool">当值为true时，新页面注册到工具下拉栏中🤔(你为什么要这么做?)</param>
    /// <returns>撤销句柄：调用后从开始菜单移除该页面；插件停用/卸载时宿主也会代为调用</returns>
    IDisposable RegisterPage(string pageName, Func<Control> createPage, Control icon, Boolean isTool);
}
