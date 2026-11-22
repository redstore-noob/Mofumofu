namespace Mofumofu.ExtensionAPI.Data;

/// <summary>
/// 一次游戏会话的完整数据。PlayDuration 是本次会话时长而非累计值；
/// 玩家手动停止时 ExitCode 为 -1，正常退出时 CrashLog 为 null。
/// </summary>
public sealed record GameSessionData(
    string InstanceId,
    string InstanceName,
    string GameVersion,
    DateTime StartedAt,
    TimeSpan PlayDuration,
    int ExitCode,
    bool Crashed,
    string? CrashLog);
