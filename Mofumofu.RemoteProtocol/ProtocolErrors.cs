namespace Mofumofu.RemoteProtocol;

public static class ProtocolErrors
{
    public const string InvalidRequest = "invalid_request";
    public const string InvalidApiVersion = "invalid_api_version";
    public const string UnsupportedApiVersion = "unsupported_api_version";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string InsufficientScope = "insufficient_scope";
    public const string CapabilityNotSupported = "capability_not_supported";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string RevisionConflict = "revision_conflict";
    public const string InvalidToken = "invalid_token";
    public const string InvalidPlayerName = "invalid_player_name";
    public const string InvalidCommand = "invalid_command";
    public const string InvalidPath = "invalid_path";
    public const string PayloadTooLarge = "payload_too_large";
    public const string RateLimited = "rate_limited";
    public const string ServerOffline = "server_offline";
    public const string ServerRunning = "server_running";
    public const string ConfirmationExpired = "confirmation_expired";
    public const string ConfirmationAlreadyUsed = "confirmation_already_used";
    public const string ConfirmationNotFound = "confirmation_not_found";
    public const string OperationNotFound = "operation_not_found";
    public const string OperationFailed = "operation_failed";
    public const string InternalError = "internal_error";

    public static ApiError CreateInvalidApiVersion(string? requestedVersion) => new()
    {
        Code = InvalidApiVersion,
        Message = $"Invalid apiVersion '{requestedVersion ?? "<null>"}'. Expected a version in 'major.minor' form, such as '{ProtocolConstants.ApiVersion}'.",
        Details = new Dictionary<string, string>
        {
            ["requestedVersion"] = requestedVersion ?? string.Empty,
            ["supportedVersion"] = ProtocolConstants.ApiVersion
        }
    };

    public static ApiError CreateUnsupportedApiVersion(string requestedVersion)
    {
        string major = requestedVersion.Split('.')[0];
        return new ApiError
        {
            Code = UnsupportedApiVersion,
            Message = $"Protocol major version '{major}' is not compatible with supported major version '{ProtocolConstants.ApiMajorVersion}' (apiVersion '{ProtocolConstants.ApiVersion}'). Use a compatible client and server.",
            Details = new Dictionary<string, string>
            {
                ["requestedVersion"] = requestedVersion,
                ["requestedMajor"] = major,
                ["supportedMajor"] = ProtocolConstants.ApiMajorVersion.ToString(),
                ["supportedVersion"] = ProtocolConstants.ApiVersion
            }
        };
    }
}
