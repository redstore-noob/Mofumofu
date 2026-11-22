using Avalonia.Controls;

namespace Mofumofu.ExtensionAPI.UI;

/// <summary>
/// 往组件盒中增加一个自定义的小组件。三个方法都需要 <see cref="PluginPermissionZones.Widget"/> 权限。
/// </summary>
public interface IWidgetAdd
{
    /// <summary>id 会在其他组件相关 API 中用于定位；返回的句柄可撤销。</summary>
    IDisposable WidgetAdd(string id, Control widget);

    /// <summary>小组件由大标题与实际内容组成，标题可单独修改。</summary>
    void WidgetTitleEdit(string id, string newTitle);

    void WidgetEdit(string id, Control newWidget);
}
