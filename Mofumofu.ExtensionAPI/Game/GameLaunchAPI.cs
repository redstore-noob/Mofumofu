using Mofumofu.ExtensionAPI.Data;

namespace Mofumofu.ExtensionAPI.Game;

/// <summary>
/// 插件只能启动「玩家在启动器里选中的实例」，不能指定任意实例。
/// </summary>
/// <remarks>
/// 权限：实例查询与事件订阅需 <see cref="PluginPermissionZones.Readonly"/>，
/// 启动属写操作，需 <see cref="PluginPermissionZones.Launch"/>。
/// </remarks>
public interface IGameLaunch
{
    /// <summary>实例来自玩家添加的所有游戏目录，不同目录的实例会混在一起。</summary>
    Task<IReadOnlyList<InstanceInfo>> GetInstancesAsync(CancellationToken cancellationToken = default);

    Task<InstanceInfo?> GetSelectedInstanceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 游戏进程成功启动后即返回，不等待游戏退出；没有选中实例或游戏已在运行会抛异常。
    /// </summary>
    /// <exception cref="InvalidOperationException">没有选中实例，或游戏已经在运行。</exception>
    /// <exception cref="Exception">启动失败（缺少 Java / 版本文件损坏 / 磁盘错误等），异常消息可直接展示给玩家。</exception>
    Task LaunchSelectedAsync(CancellationToken cancellationToken = default);

    bool IsGameRunning { get; }

    /// <summary>在 UI 线程回调；插件停用/卸载时宿主会自动退订。</summary>
    event Action GameLaunched;

    /// <summary>在 UI 线程回调，参数为退出码，手动停止时为 -1；插件停用/卸载时宿主会自动退订。</summary>
    event Action<int> GameExited;
}
