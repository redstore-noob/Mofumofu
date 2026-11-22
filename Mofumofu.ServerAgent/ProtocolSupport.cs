using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Mofumofu.RemoteProtocol;

namespace Mofumofu.ServerAgent;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class ApiException : Exception
{
    public ApiException(int statusCode, string code, string message,
        Dictionary<string, string>? details = null, string? operationId = null)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        Details = details;
        OperationId = operationId;
    }

    public int StatusCode { get; }
    public string Code { get; }
    public Dictionary<string, string>? Details { get; }
    public string? OperationId { get; }

    public ApiError ToError(string requestId) => new()
    {
        Code = Code,
        Message = Message,
        Details = Details,
        RequestId = requestId,
        OperationId = OperationId,
    };

    public static ApiException InvalidRequest(string message) =>
        new(StatusCodes.Status400BadRequest, ProtocolErrors.InvalidRequest, message);

    public static ApiException NotFound(string message = "The requested resource was not found.") =>
        new(StatusCodes.Status404NotFound, ProtocolErrors.NotFound, message);

    public static ApiException Conflict(string message) =>
        new(StatusCodes.Status409Conflict, ProtocolErrors.Conflict, message);

    public static ApiException RevisionConflict(string expectedRevision) =>
        new(StatusCodes.Status409Conflict, ProtocolErrors.RevisionConflict,
            "The resource revision does not match.",
            new Dictionary<string, string> { ["expectedRevision"] = expectedRevision });
}

public static class AgentErrorCodes
{
    public const string ProbeUnavailable = "PROBE_UNAVAILABLE";
    public const string AuditUnavailable = "AUDIT_UNAVAILABLE";
}

public static class ProtocolRequest
{
    public static void ValidateQueryVersion(HttpContext context)
    {
        string? value = context.Request.Query[ProtocolVersionParameter].ToString();
        ValidateVersion(value);
    }

    public static void ValidateVersion(string? version)
    {
        if (ProtocolVersion.GetCompatibilityError(version) is { } error)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, error.Code, error.Message, error.Details);
        }
    }

    public static async Task<T> ReadBodyAsync<T>(HttpContext context, CancellationToken cancellationToken = default)
        where T : class, IProtocolMessage
    {
        if (context.Request.ContentLength > AgentOptions.MaxRequestBodyBytes)
        {
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, ProtocolErrors.PayloadTooLarge,
                "The request body is too large.");
        }

        try
        {
            await using MemoryStream buffer = new();
            byte[] chunk = new byte[81920];
            while (true)
            {
                int read = await context.Request.Body.ReadAsync(chunk, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > AgentOptions.MaxRequestBodyBytes)
                {
                    throw new ApiException(StatusCodes.Status413PayloadTooLarge, ProtocolErrors.PayloadTooLarge,
                        "The request body is too large.");
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            }

            T value = JsonSerializer.Deserialize<T>(buffer.ToArray(), ProtocolJson.Options)
                      ?? throw new JsonException("The JSON value was null.");
            ValidateVersion(value.ApiVersion);
            return value;
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException)
        {
            throw ApiException.InvalidRequest("The request body must be a valid protocol JSON object.");
        }
    }

    public static IResult Json<T>(T value, int statusCode = StatusCodes.Status200OK) =>
        Results.Json(value, ProtocolJson.Options, statusCode: statusCode);

    public static IResult Error(ApiException error, string requestId) =>
        Json(error.ToError(requestId), error.StatusCode);

    private const string ProtocolVersionParameter = "apiVersion";
}

public static class Hex
{
    public static bool IsLower(string? value, int length)
    {
        if (value is null || value.Length != length)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsAnyCase(string? value, int length)
    {
        if (value is null || value.Length != length)
        {
            return false;
        }

        return value.All(Uri.IsHexDigit);
    }

    public static string Sha256(string value) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed record ProbeEventRequest : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public string EventId { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public DateTimeOffset OccurredAt { get; init; }
    public string Message { get; init; } = string.Empty;
    public Dictionary<string, string> Details { get; init; } = [];
}

public sealed record AgentAcknowledgementResponse : IProtocolMessage
{
    public string ApiVersion { get; init; } = ProtocolConstants.ApiVersion;
    public bool Accepted { get; init; }
    public bool Duplicate { get; init; }
}
