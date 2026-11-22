using System.Globalization;

namespace Mofumofu.RemoteProtocol;

public static class ProtocolVersion
{
    public static bool IsCompatible(string? apiVersion) => GetCompatibilityError(apiVersion) is null;

    public static ApiError? GetCompatibilityError(string? apiVersion)
    {
        if (!TryGetMajor(apiVersion, out int major))
        {
            return ProtocolErrors.CreateInvalidApiVersion(apiVersion);
        }

        return major == ProtocolConstants.ApiMajorVersion
            ? null
            : ProtocolErrors.CreateUnsupportedApiVersion(apiVersion!);
    }

    public static void EnsureCompatible(string? apiVersion)
    {
        if (GetCompatibilityError(apiVersion) is { } error)
        {
            throw new ProtocolVersionException(error);
        }
    }

    private static bool TryGetMajor(string? apiVersion, out int major)
    {
        major = 0;
        if (string.IsNullOrWhiteSpace(apiVersion))
        {
            return false;
        }

        string[] parts = apiVersion.Split('.');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out major)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }
}

public sealed class ProtocolVersionException : Exception
{
    public ProtocolVersionException(ApiError error)
        : base((error ?? throw new ArgumentNullException(nameof(error))).Message)
    {
        Error = error;
    }

    public ApiError Error { get; }
}
