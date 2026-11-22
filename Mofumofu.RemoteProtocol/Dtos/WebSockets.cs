using System.Text.Json.Serialization;

namespace Mofumofu.RemoteProtocol;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ConsoleFrame), WebSocketFrameTypes.Console)]
[JsonDerivedType(typeof(EventFrame), WebSocketFrameTypes.Event)]
[JsonDerivedType(typeof(SnapshotFrame), WebSocketFrameTypes.Snapshot)]
[JsonDerivedType(typeof(ResetFrame), WebSocketFrameTypes.Reset)]
[JsonDerivedType(typeof(OperationFrame), WebSocketFrameTypes.Operation)]
public abstract record WebSocketFrame : IProtocolMessage
{
    [JsonPropertyOrder(-100)]
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    [JsonPropertyOrder(-90)]
    public string Cursor { get; init; } = string.Empty;
    [JsonPropertyOrder(-80)]
    public DateTimeOffset SentAt { get; init; }
}

public sealed record ConsoleFrame : WebSocketFrame
{
    public List<ConsoleEntry> Entries { get; init; } = [];
}

public sealed record EventFrame : WebSocketFrame
{
    public ServerEvent Event { get; init; } = new();
}

public sealed record SnapshotFrame : WebSocketFrame
{
    public StatusResponse Status { get; init; } = new();
    public List<PlayerInfo> Players { get; init; } = [];
    public List<ConsoleEntry> RecentConsole { get; init; } = [];
    public List<OperationInfo> ActiveOperations { get; init; } = [];
}

public sealed record ResetFrame : WebSocketFrame
{
    public string Reason { get; init; } = string.Empty;
    public bool RequiresSnapshot { get; init; } = true;
}

public sealed record OperationFrame : WebSocketFrame
{
    public OperationInfo Operation { get; init; } = new();
}
