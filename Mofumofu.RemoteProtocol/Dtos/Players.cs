namespace Mofumofu.RemoteProtocol;

public sealed record PlayerInfo
{
    public string Name { get; init; } = string.Empty;
    public Guid? Uuid { get; init; }
    public DateTimeOffset? JoinedAt { get; init; }
    public long? OnlineSeconds { get; init; }
    public int? PingMilliseconds { get; init; }
    public string? GameMode { get; init; }
}

public sealed record OnlinePlayersResponse : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public int OnlineCount { get; init; }
    public int MaxPlayers { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public List<PlayerInfo> Players { get; init; } = [];
}

public sealed record PlayerListEntry
{
    public string Name { get; init; } = string.Empty;
    public Guid? Uuid { get; init; }
    public int? OpLevel { get; init; }
    public bool? BypassesPlayerLimit { get; init; }
    public string? Reason { get; init; }
    public string? Source { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed record PlayerListResponse : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public string Kind { get; init; } = string.Empty;
    public string Revision { get; init; } = string.Empty;
    public List<PlayerListEntry> Entries { get; init; } = [];
}

public sealed record PlayerAddRequest : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public string PlayerName { get; init; } = string.Empty;
    public string? ExpectedRevision { get; init; }
    public int? OpLevel { get; init; }
    public bool? BypassesPlayerLimit { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed record PlayerRemoveRequest : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public string PlayerName { get; init; } = string.Empty;
    public string? ExpectedRevision { get; init; }
}

public sealed record PlayerMutationResponse : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public string Kind { get; init; } = string.Empty;
    public string Revision { get; init; } = string.Empty;
    public PlayerListEntry Entry { get; init; } = new();
    public bool Removed { get; init; }
}
