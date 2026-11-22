using System.Text.Json.Serialization;

namespace Mofumofu.RemoteProtocol;

public interface IProtocolMessage
{
    string ApiVersion { get; }
}

public sealed record ApiError : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public Dictionary<string, string>? Details { get; init; }
    public string? RequestId { get; init; }
    public string? OperationId { get; init; }
}

public sealed record OperationInfo
{
    public string Id { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string State { get; init; } = ProtocolOperationStates.Queued;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public double? ProgressPercent { get; init; }
    public string? Message { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
    public ApiError? Error { get; init; }
}

public sealed record OperationResponse : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public OperationInfo Operation { get; init; } = new();
}

public abstract record ConfirmationPrepareRequest : IProtocolMessage
{
    [JsonPropertyOrder(-100)]
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
}

public sealed record ConfirmationPrepareResponse : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public string ConfirmationId { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public int ExpiresInSeconds { get; init; } = ProtocolConstants.ConfirmationLifetimeSeconds;
}

public sealed record ConfirmationExecuteRequest : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public string ConfirmationId { get; init; } = string.Empty;
}

public sealed record ConfirmationExecuteResponse : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public OperationInfo Operation { get; init; } = new();
}

public sealed record ServerEvent
{
    public string EventId { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public DateTimeOffset OccurredAt { get; init; }
    public string Message { get; init; } = string.Empty;
    public Dictionary<string, string> Details { get; init; } = [];
}
