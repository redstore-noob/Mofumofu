namespace Mofumofu.ExtensionAPI.Data;

/// <summary>
/// 一次游戏会话的完整数据（游戏退出后由宿主递给插件）。
/// </summary>
/// <param name="InstanceId">实例目录名</param>
/// <param name="InstanceName">实例显示名</param>
/// <param name="GameVersion">游戏版本号</param>
/// <param name="StartedAt">本次游戏启动时刻</param>
/// <param name="PlayDuration">本次游玩时长（会话级，不是累计值；累计值用 context.Game 查实例）</param>
/// <param name="ExitCode">进程退出码；玩家手动停止为 -1</param>
/// <param name="Crashed">是否崩溃退出（exit code 非 0 且非玩家主动停止）</param>
/// <param name="CrashLog">崩溃日志；正常退出为 null</param>
public sealed record GameSessionData(
    string InstanceId,
    string InstanceName,
    string GameVersion,
    DateTime StartedAt,
    TimeSpan PlayDuration,
    int ExitCode,
    bool Crashed,
    string? CrashLog);
