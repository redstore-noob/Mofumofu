namespace Mofumofu.RemoteProtocol;

public sealed record BackupMetadata
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string? Description { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public long SizeBytes { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public string? MinecraftVersion { get; init; }
}

public sealed record BackupListResponse : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public List<BackupMetadata> Backups { get; init; } = [];
}

public sealed record BackupCreateRequest : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public string Label { get; init; } = string.Empty;
    public string? Description { get; init; }
}

public sealed record BackupCreateResponse : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public BackupMetadata Backup { get; init; } = new();
    public OperationInfo Operation { get; init; } = new();
}

public sealed record BackupRestoreRequest : ConfirmationPrepareRequest
{
    public string BackupId { get; init; } = string.Empty;
}

public sealed record BackupDeleteRequest : ConfirmationPrepareRequest
{
    public string BackupId { get; init; } = string.Empty;
}
