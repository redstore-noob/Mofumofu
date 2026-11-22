using Avalonia.Controls;

namespace Mofumofu.ExtensionAPI.Tools;

/// <summary>
/// 查询服务器当前状态。
/// </summary>
/// <remarks>
/// 需要 <see cref="PluginPermissionZones.ServerStatus"/> 权限。
/// <c>serverURL</c> 不能为 null / 空白；本方法是契约层唯一不带 <c>CancellationToken</c> 的异步方法，
/// 保持现有签名以兼容已编译插件。
/// </remarks>
public interface IServerStatus
{
    public Task<ServerStatus> GetServerStatus(string serverURL);
}
/// <summary>
/// <paramref name="Icon"/> 在服务器无图标时由宿主提供占位 Control，永不为 null（无需判空），
/// 与 <c>ResourceProject.IconUrl</c> 的「无图标为 null」是不同概念。
/// </summary>
public record ServerStatus(
    string Motd,
    Control Icon,
    int OnlinePlayers,
    int MaxPlayers,
    int HighestVersion,
    int LowestVersion);