namespace Mofumofu.ExtensionAPI.Actions;

/// <summary>
/// 一次启动的上下文：宿主在启动前装好递给钩子，插件据此做检查与注入。
/// </summary>
public interface ILaunchContext
{
    /// <summary>即将启动的实例目录名。</summary>
    string InstanceId { get; }

    /// <summary>游戏版本号。</summary>
    string VersionId { get; }

    /// <summary>实例所在游戏目录。</summary>
    string GameDirectory { get; }

    /// <summary>
    /// JVM 参数（可变列表）：插件 Add 进去的参数会被宿主在装配启动命令时采用。
    /// 这是钩子唯一的可写面，其余成员均为只读事实。
    /// </summary>
    IList<string> JvmArguments { get; }

    /// <summary>阻止本次启动；reason 会展示给玩家。调用后宿主短路后续钩子。</summary>
    void Cancel(string reason);
}
