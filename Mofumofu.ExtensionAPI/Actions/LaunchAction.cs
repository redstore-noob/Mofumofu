using Mofumofu.ExtensionAPI.Data;

namespace Mofumofu.ExtensionAPI.Actions;

/// <summary>
/// 启动过程钩子：参与/感知启动流程的插件实现此接口。
/// 宿主按注册顺序依次调用；单个钩子抛异常会被宿主吞掉并记日志，不影响其他插件。
/// </summary>
/// <summary>
/// 全部成员都有默认空实现：插件只需重写自己关心的钩子。
/// </summary>
public interface ILaunchAction
{
    /// <summary>
    /// 启动前执行的钩子：可做前置检查、注入 JVM 参数。
    /// </summary>
    /// <param name="launch">
    /// 本次启动的上下文：要启动哪个实例、游戏目录；
    /// <see cref="ILaunchContext.JvmArguments"/> 是可变列表，Add 进去的参数会被宿主采用；
    /// 调用 <see cref="ILaunchContext.Cancel"/> 可阻止本次启动。
    /// </param>
    /// <returns>true = 放行本次启动；false = 阻止本次启动（配合 Cancel(reason) 把原因展示给玩家）</returns>
    public Task<Boolean> LaunchingAsync(ILaunchContext launch) => Task.FromResult(true);

    /// <summary>
    /// 游戏崩溃时执行的钩子。
    /// </summary>
    /// <param name="crashedLog">游戏崩溃时返回的日志</param>
    public void GameCrashedHook(string crashedLog) { }

    /// <summary>
    /// 玩家正常退出游戏时的钩子（崩溃走 <see cref="GameCrashedHook"/>，不会到这里）。
    /// 数据只读快照：本会话时长、退出码等都在参数里。
    /// </summary>
    /// <param name="session">本次游戏会话的数据</param>
    public void GameStoppedHook(GameSessionData session) { }
}
