namespace Mofumofu.RemoteProtocol;

public sealed record ControlRequest : ConfirmationPrepareRequest
{
    public string Action { get; init; } = string.Empty;
}

public sealed record ControlResponse : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public string Action { get; init; } = string.Empty;
    public OperationInfo Operation { get; init; } = new();
}
