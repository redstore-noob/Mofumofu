namespace Mofumofu.ExtensionAPI.Tools;

/// <summary>
/// 利用宿主的日志系统，插件往里面也写一条东西.
/// 插件写的日志会在日志消息最前面加上(extension:extension_id)
/// </summary>
public interface ILogWrite
{
    /// <summary>
    /// 函数，在日志文件中追加一条消息。
    /// </summary>
    /// <param name="message"></param>
    /// <returns></returns>
    public Boolean LogAdd(string message);
}