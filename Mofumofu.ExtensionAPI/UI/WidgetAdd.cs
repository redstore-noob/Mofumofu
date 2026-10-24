using Avalonia.Controls;

namespace Mofumofu.ExtensionAPI.UI;

/// <summary>
/// 往组件盒中增加一个自定义的小组件
/// 我觉得这功能很牛逼
/// </summary>
public interface IWidgetAdd
{
    /// <summary>
    /// 注册一个小组件
    /// </summary>
    /// <param name="id">组件注册id，注意该id将在其他组件相关API中起作用</param>
    /// <param name="widget">组件的控件</param>
    /// <returns>撤销句柄：调用后从组件盒移除该组件；插件停用/卸载时宿主也会代为调用</returns>
    IDisposable WidgetAdd(string id, Control widget);
    /// <summary>
    /// 修改组件的标题
    /// 因为小组件设计为一个大标题+小组件实际内容，对title修改能力需求高
    /// </summary>
    /// <param name="id">需修改的小组件id</param>
    /// <param name="newTitle">修改的新小组件标题</param>
    void WidgetTitleEdit(string id, string newTitle);
    /// <summary>
    /// 直接修改小组件的content
    /// </summary>
    /// <param name="id">需修改的小组件的注册id</param>
    /// <param name="newWidget">新的小组件content</param>
    void WidgetEdit(string id, Control newWidget);
}
