namespace Mofumofu.RemoteProtocol;

public sealed record FileEntry
{
    public string Path { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public long? SizeBytes { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
    public string? Revision { get; init; }
    public bool Writable { get; init; }
}

public sealed record FileListResponse : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public string Path { get; init; } = string.Empty;
    public List<FileEntry> Entries { get; init; } = [];
}

public sealed record FileContentResponse : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public string Path { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string Encoding { get; init; } = "utf-8";
    public string Revision { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
}

public sealed record FileWriteRequest : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public string Path { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string ExpectedRevision { get; init; } = string.Empty;
}

public sealed record FileWriteResponse : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public string Path { get; init; } = string.Empty;
    public string Revision { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public DateTimeOffset ModifiedAt { get; init; }
}

public sealed record FileCreateRequest : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public string Path { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
}

public sealed record FileRenameRequest : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public string Path { get; init; } = string.Empty;
    public string NewName { get; init; } = string.Empty;
    public string ExpectedRevision { get; init; } = string.Empty;
}

public sealed record FileMutationResponse : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public string Path { get; init; } = string.Empty;
    public string Revision { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public DateTimeOffset ModifiedAt { get; init; }
}

public sealed record FileDeleteRequest : ConfirmationPrepareRequest
{
    public string Path { get; init; } = string.Empty;
    public string ExpectedRevision { get; init; } = string.Empty;
}
