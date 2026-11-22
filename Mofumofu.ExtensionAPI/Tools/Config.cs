namespace Mofumofu.ExtensionAPI.Tools;

/// <summary>
/// 利用宿主的配置功能存储插件配置，每个插件独立存放在
/// ./Mofumofu/plugin/extension_id/config.yaml。
/// </summary>
/// <remarks>
/// 读写都需要 <see cref="PluginPermissionZones.Storage"/> 权限。
/// <c>key</c> 不能为 null / 空白；返回 false 表示操作失败（含 IO 错误），不区分具体原因。
/// </remarks>
public interface IConfig
{
    public Boolean ConfigAdd(string key, string value);
    public Boolean ConfigRemove(string key);
}