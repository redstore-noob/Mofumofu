using Mofumofu.ExtensionAPI.Data;

namespace Mofumofu.ExtensionAPI.Game;

/// <summary>
/// 插件的游戏启动接口：查询本地实例、启动玩家选中的实例。
/// 出于安全考虑，插件只能启动「玩家在启动器里选中的实例」，不能指定任意实例。
/// </summary>
public interface IGameLaunch
{
    /// <summary>
    /// 获取本地实例列表。
    /// 实例来自玩家添加的所有游戏目录，所以不同目录的实例会混在一起。
    /// </summary>
    /// <returns>全部本地实例（只读快照）；磁盘读取失败等异常直接抛出</returns>
    Task<IReadOnlyList<InstanceInfo>> GetInstancesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取玩家当前选中的实例；还没有选中任何实例时返回 null。
    /// </summary>
    Task<InstanceInfo?> GetSelectedInstanceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 启动玩家当前选中的实例（用户选中的，不是插件选中的）。
    /// 游戏进程成功启动后本方法即返回，不等待游戏退出。
    /// </summary>
    /// <exception cref="InvalidOperationException">没有选中实例，或游戏已经在运行。</exception>
    /// <exception cref="Exception">启动失败（缺少 Java / 版本文件损坏 / 磁盘错误等），异常消息可直接展示给玩家。</exception>
    Task LaunchSelectedAsync(CancellationToken cancellationToken = default);

    /// <summary>游戏是否正在运行（用于避免重复启动）。</summary>
    bool IsGameRunning { get; }

    /// <summary>
    /// 游戏进程成功启动后触发（在 UI 线程回调）。
    /// 订阅需要 readonly 权限；插件停用/卸载时宿主会自动退订。
    /// </summary>
    event Action GameLaunched;

    /// <summary>
    /// 游戏退出后触发（在 UI 线程回调），参数为进程退出码（手动停止时为 -1）。
    /// 订阅需要 readonly 权限；插件停用/卸载时宿主会自动退订。
    /// </summary>
    event Action<int> GameExited;
}
