using Mofumofu.ExtensionAPI.Data;
using Mofumofu.ExtensionAPI.UI;

namespace Mofumofu.ExtensionAPI;
/// <summary>插件入口。这里的内容会原样展示给玩家。</summary>
/// <remarks>
/// 5 个成员都必须实现，新增成员会让已发布插件编译 / 加载失败。未来扩展请用「带默认实现」的
/// 新接口，或在 <c>IPluginContext</c> 上增加带默认实现的属性，不要改动本接口既有成员签名。
/// </remarks>
public interface IMofuPlugin
{
    string Name{get;}
    Version Version{get;}
    PluginTypeEnum ExtensionType{get;}
    string Description{get;}
    /// <summary>宿主加载插件时调用一次；加载失败/拒绝加载返回 false，否则返回 true。</summary>
    Boolean Init(IPluginContext context);
}
