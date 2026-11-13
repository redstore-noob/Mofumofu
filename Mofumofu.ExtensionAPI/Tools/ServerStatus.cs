using Avalonia.Controls;

namespace Mofumofu.ExtensionAPI.Tools;

/// <summary>
/// 查询服务器当前状态
/// </summary>
/// <remarks>
/// 查询服务器状态需要 <see cref="PluginPermissionZones.ServerStatus"/> 权限。
/// <c>serverURL</c> 不能为 null / 空白；本方法是契约层唯一不带 <c>CancellationToken</c> 的异步方法，
/// 真正可取消的重载需宿主侧实现后补充（保持现有签名以兼容已编译插件）。
/// </remarks>
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
/// <param name="Icon">服务器图标；服务器无图标时宿主提供占位 Control，本字段永不为 null（插件无需判空），与 <c>ResourceProject.IconUrl</c> 的「无图标为 null」是不同概念</param>
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