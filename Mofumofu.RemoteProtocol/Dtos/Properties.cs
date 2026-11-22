namespace Mofumofu.RemoteProtocol;

public sealed record ServerPropertiesResponse : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public string Revision { get; init; } = string.Empty;
    public bool RestartRequired { get; init; }
    public Dictionary<string, string> Properties { get; init; } = [];
}

public sealed record ServerPropertiesPatchRequest : ConfirmationPrepareRequest
{
    public string ExpectedRevision { get; init; } = string.Empty;
    /// <summary>A null value removes a custom key; known server keys may only be set to valid values.</summary>
    public Dictionary<string, string?> Changes { get; init; } = [];
}

public sealed record ServerPropertiesPatchResponse : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public string Revision { get; init; } = string.Empty;
    public bool RestartRequired { get; init; } = true;
    public DateTimeOffset AppliedAt { get; init; }
    public Dictionary<string, string> Properties { get; init; } = [];
}
