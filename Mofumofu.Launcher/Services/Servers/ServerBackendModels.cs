using System;
using System.Collections.Generic;

namespace Mofumofu.Launcher.Services.Servers;

public static class ServerCapability
{
    public const string Read = "read";
    public const string Console = "console";
    public const string Players = "players";
    public const string Files = "files";
    public const string Properties = "properties";
    public const string Backup = "backup";
    public const string Control = "control";
    public const string Command = "command";
    public const string Events = "events";
}

public static class ServerScope
{
    public const string Read = "read";
    public const string Players = "players";
    public const string Files = "files";
    public const string Backup = "backup";
    public const string Control = "control";
    public const string Command = "command";
}

public sealed record ServerDescription(
    string Id,
    string Name,
    string Core,
    string CoreVersion,
    string McVersion,
    string Address,
    int MaxPlayers,
    string Motd);

public sealed class BackendStatus
{
    public string Status { get; init; } = MofuCore.Server.ServerManage.StatusStopped;
    public int Players { get; init; }
    public int MaxPlayers { get; init; }
    public double CpuPercent { get; init; }
    public double MemoryMb { get; init; }
    public string MemoryKind { get; init; } = "process-rss";
    public long UptimeSeconds { get; init; }
    public bool Degraded { get; init; }
    public long NextCursor { get; init; }
    public IReadOnlyList<string> LogLines { get; init; } = [];
}

public sealed record BackendPlayerEntry(
    string Name,
    string Uuid = "",
    int Level = 4,
    bool BypassesPlayerLimit = false,
    string Reason = "",
    string Expires = "forever",
    DateTimeOffset? JoinedAt = null);

public sealed class BackendPlayers
{
    public IReadOnlyList<BackendPlayerEntry> Online { get; init; } = [];
    public IReadOnlyList<BackendPlayerEntry> Whitelist { get; init; } = [];
    public IReadOnlyList<BackendPlayerEntry> Ops { get; init; } = [];
    public IReadOnlyList<BackendPlayerEntry> Banned { get; init; } = [];
    public bool WhitelistEnabled { get; init; }
    public bool Running { get; init; }
    public string Revision { get; init; } = string.Empty;
}

public sealed record BackendBackup(
    string Name,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    bool Hot,
    string State = "ready");

public sealed record BackendFile(
    string Name,
    string RelativePath,
    bool IsDirectory,
    long SizeBytes,
    DateTimeOffset ModifiedAt,
    string Revision = "");

public sealed record BackendFileContent(string RelativePath, string Content, string Revision);

public sealed record BackendProperty(string Key, string Value);

public sealed record BackendProperties(IReadOnlyList<BackendProperty> Values, string Revision);

public sealed record BackendOperation(bool Completed = true, bool PendingRestart = false, string? Message = null);

public sealed class ServerConsoleEventArgs(string line, long cursor) : EventArgs
{
    public string Line { get; } = line;
    public long Cursor { get; } = cursor;
}
