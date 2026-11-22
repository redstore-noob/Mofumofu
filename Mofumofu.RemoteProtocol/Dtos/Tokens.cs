namespace Mofumofu.RemoteProtocol;

public sealed record TokenInfo
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsRoot { get; init; }
    public List<string> Scopes { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
}

public sealed record TokenListResponse : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public List<TokenInfo> Tokens { get; init; } = [];
}

public sealed record TokenDeriveRequest : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public string Name { get; init; } = string.Empty;
    public List<string> Scopes { get; init; } = [];
    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed record TokenIssuedResponse : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    /// <summary>Plaintext is returned only once by derive/rotate and is never included in list responses.</summary>
    public string Token { get; init; } = string.Empty;
    public TokenInfo TokenInfo { get; init; } = new();
}

public sealed record TokenRevokeRequest : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public string TokenId { get; init; } = string.Empty;
}

public sealed record TokenRevokeResponse : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public string TokenId { get; init; } = string.Empty;
    public DateTimeOffset RevokedAt { get; init; }
}

public sealed record TokenRotateRequest : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public string TokenId { get; init; } = string.Empty;
    public int GracePeriodSeconds { get; init; } = ProtocolConstants.DefaultTokenRotationGraceSeconds;
}

public sealed record TokenRotateResponse : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public string Token { get; init; } = string.Empty;
    public TokenInfo TokenInfo { get; init; } = new();
    public DateTimeOffset PreviousTokenExpiresAt { get; init; }
}
