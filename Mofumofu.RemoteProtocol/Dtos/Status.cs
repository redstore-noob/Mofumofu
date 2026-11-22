namespace Mofumofu.RemoteProtocol;

public sealed record StatusResponse : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public string InstanceId { get; init; } = string.Empty;
    public string State { get; init; } = ProtocolServerStates.Offline;
    public int OnlinePlayers { get; init; }
    public int MaxPlayers { get; init; }
    public double? CpuPercent { get; init; }
    public long? MemoryUsedBytes { get; init; }
    public long? MemoryMaxBytes { get; init; }
    public double? Tps { get; init; }
    public long? UptimeSeconds { get; init; }
    public string? MinecraftVersion { get; init; }
    public string? Motd { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
}
