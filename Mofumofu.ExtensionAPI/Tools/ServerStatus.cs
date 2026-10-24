using Avalonia.Controls;

namespace Mofumofu.ExtensionAPI.Tools;

/// <summary>
/// 查询服务器当前状态
/// </summary>
public interface IServerStatus
{
    /// <summary>
    /// 调用之后返回服务器信息，包括motd与人数等相关信息
    /// </summary>
    /// <param name="serverURL">服务器的地址</param>
    /// <returns>返回服务器当前信息</returns>
    public Task<ServerStatus> GetServerStatus(string serverURL);
}
/// <summary>
/// 关于服务器状态返回的类
/// </summary>
/// <param name="Motd">服务器motd</param>
/// <param name="Icon">服务器图标</param>
/// <param name="OnlinePlayers">服务器当前在线人数</param>
/// <param name="MaxPlayers">服务器最大人数</param>
/// <param name="HighestVersion">服务器支持进入的最高版本</param>
/// <param name="LowestVersion">服务器支持进入的最低版本</param>
public record ServerStatus(
    string Motd,
    Control Icon,
    int OnlinePlayers,
    int MaxPlayers,
    int HighestVersion,
    int LowestVersion);