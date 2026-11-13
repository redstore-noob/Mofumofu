namespace Mofumofu.ExtensionAPI.Tools;

/// <summary>
/// 利用宿主的配置功能存储插件配置.
/// 与启动器分开存储数据，每个插件的存储放在./Mofumofu/plugin/extension_id/config.yaml
/// </summary>
/// <remarks>
/// 读写插件配置属于写操作，需要 <see cref="PluginPermissionZones.Storage"/> 权限
/// （与只读查询一样受权限约束，避免「读要权限、写不要」的倒挂）。
/// <c>key</c> 不能为 null / 空白；返回 false 表示操作失败（含 IO 错误），不区分具体原因。
/// </remarks>
public interface IConfig
{
    /// <summary>
    /// 在配置文件中增加/修改一条配置
    /// 当该条配置不存在时增加，存在时修改
    /// </summary>
    /// <param name="key">配置存储的key</param>
    /// <param name="value">配置存储key的对应值</param>
    /// <returns>本次操作是否成功</returns>
    public Boolean ConfigAdd(string key, string value);
    /// <summary>
    /// 移除一条配置
    /// </summary>
    /// <param name="key">将被移出的配置项的key</param>
    /// <returns>本次操作是否成功</returns>
    public Boolean ConfigRemove(string key);
}