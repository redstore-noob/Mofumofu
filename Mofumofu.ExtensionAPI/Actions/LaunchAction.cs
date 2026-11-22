using Mofumofu.ExtensionAPI.Data;

namespace Mofumofu.ExtensionAPI.Actions;

/// <summary>
/// 启动过程钩子：宿主按注册顺序依次调用；单个钩子抛异常会被宿主吞掉并记日志，不影响其他插件。
/// 全部成员有默认空实现，只需重写关心的钩子。
/// </summary>
public interface ILaunchAction
{
    /// <summary>
    /// 返回 true 放行启动；false 阻止启动（配合 <see cref="ILaunchContext.Cancel"/> 把原因展示给玩家）。
    /// </summary>
    public Task<Boolean> LaunchingAsync(ILaunchContext launch) => Task.FromResult(true);

    public void GameCrashedHook(string crashedLog) { }

    /// <summary>
    /// 崩溃走 <see cref="GameCrashedHook"/>，不会到这里。
    /// </summary>
    public void GameStoppedHook(GameSessionData session) { }
}
