namespace Mofumofu.ExtensionAPI.Tools;

/// <summary>
/// 插件写的日志会在消息最前面加上 (extension:extension_id) 前缀。
/// </summary>
public interface ILogWrite
{
    public Boolean LogAdd(string message);
}