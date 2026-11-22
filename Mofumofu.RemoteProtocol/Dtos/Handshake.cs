namespace Mofumofu.RemoteProtocol;

public sealed record HandshakeRequest : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public string ClientName { get; init; } = string.Empty;
    public string ClientVersion { get; init; } = string.Empty;
    public List<string> RequestedCapabilities { get; init; } = [];
}

public sealed record HandshakeResponse : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public string InstanceId { get; init; } = string.Empty;
    public string ServerName { get; init; } = string.Empty;
    public string DeploymentMode { get; init; } = string.Empty;
    public string ServerImplementationVersion { get; init; } = string.Empty;
    public List<string> Capabilities { get; init; } = [];
    public List<string> Scopes { get; init; } = [];
    public DateTimeOffset ServerTime { get; init; }
    public string WebSocketPath { get; init; } = ProtocolConstants.WebSocketPath;
    public string? TokenId { get; init; }
}
