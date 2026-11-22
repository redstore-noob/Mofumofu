namespace Mofumofu.RemoteProtocol;

public sealed record ConsoleCommandRequest : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public string Command { get; init; } = string.Empty;
}

public sealed record ConsoleCommandResponse : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public bool Accepted { get; init; }
    public DateTimeOffset SentAt { get; init; }
    public string? OperationId { get; init; }
}

public sealed record ConsoleEntry
{
    public long Sequence { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string Level { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}
