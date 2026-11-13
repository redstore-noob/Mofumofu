using Mofumofu.ExtensionAPI.Data;
using Mofumofu.ExtensionAPI.UI;

namespace Mofumofu.ExtensionAPI;
/// <summary>
/// 插件入口，记得实现。
/// 注意填好，这里的内容会给玩家看。
/// </summary>
/// <remarks>
/// 本接口的 5 个成员都必须实现，新增任何成员都会让已发布插件编译 / 加载失败。
/// 未来扩展（卸载钩子、能力声明、最低宿主版本等）请以「带默认实现」的新接口、或往
/// <c>IPluginContext</c> 增加带默认实现的属性方式提供，不要改动本接口既有成员签名。
/// </remarks>
public interface IMofuPlugin
{
    string Name{get;}
    Version Version{get;}
    PluginTypeEnum ExtensionType{get;}
    string Description{get;}
    /// <summary>
    /// 当插件被宿主启动器加载时，该函数自动被宿主调用一次。
    /// 通过 context 调用宿主能力（弹提示：context.Popup.Toast(...)；注册小组件：context.Widget.WidgetAdd(...)）。
    /// </summary>
    /// <param name="context">宿主能力集合，插件入口的唯一通道</param>
    /// <returns>当加载失败/拒绝加载时，请返回一次false，否则返回true.</returns>
    Boolean Init(IPluginContext context);
}
