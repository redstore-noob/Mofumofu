namespace Mofumofu.ExtensionAPI.Tools;

/// <summary>
/// 利用宿主的配置功能存储插件配置.
/// 与启动器分开存储数据，每个插件的存储放在./Mofumofu/plugin/extension_id/config.yaml
/// </summary>
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