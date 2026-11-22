using System.Net;

namespace Mofumofu.ServerAgent;

public sealed class AgentOptions
{
    public const int MaxTextBytes = 2 * 1024 * 1024;
    public const int MaxRequestBodyBytes = 4 * 1024 * 1024;

    public string ServerRootDirectory { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public string ProbeRegistryPath { get; set; } = string.Empty;
    public string ProbeBaseUrl { get; set; } = "http://127.0.0.1:17871";
    public string BindUrl { get; set; } = "http://127.0.0.1:17870";
    public string EventJournalPath { get; set; } = string.Empty;
    public string AuditPath { get; set; } = string.Empty;
    public int EventJournalCapacity { get; set; } = 4096;

    public string InstanceDirectory => Path.Combine(ServerRootDirectory, InstanceId);

    public void NormalizeAndValidate()
    {
        ServerRootDirectory = NormalizeDirectory(ServerRootDirectory, nameof(ServerRootDirectory));
        ProbeRegistryPath = NormalizeFile(ProbeRegistryPath, nameof(ProbeRegistryPath));
        EventJournalPath = NormalizeFile(EventJournalPath, nameof(EventJournalPath));
        AuditPath = NormalizeFile(AuditPath, nameof(AuditPath));
        InstanceId = InstanceId.Trim();
        if (string.IsNullOrEmpty(InstanceId) || InstanceId is "." or ".." ||
            InstanceId.Contains("..", StringComparison.Ordinal) ||
            InstanceId.Contains('/') || InstanceId.Contains('\\') || InstanceId.IndexOf('\0') >= 0)
        {
            throw new InvalidOperationException("InstanceId must be a single, valid server directory name.");
        }

        if (EventJournalCapacity is < 16 or > 100_000)
        {
            throw new InvalidOperationException("EventJournalCapacity must be between 16 and 100000.");
        }

        if (PathsOverlap(EventJournalPath, AuditPath) ||
            PathsOverlap(ProbeRegistryPath, AuditPath) ||
            PathsOverlap(ProbeRegistryPath, EventJournalPath))
        {
            throw new InvalidOperationException("Registry, journal, and audit paths must be different files.");
        }

        _ = ParseLoopbackHttpUrl(BindUrl, nameof(BindUrl), requireLoopbackAddress: true);
        _ = ParseLoopbackHttpUrl(ProbeBaseUrl, nameof(ProbeBaseUrl), requireLoopbackAddress: false);
        string instance = Path.GetFullPath(InstanceDirectory);
        string root = Path.TrimEndingDirectorySeparator(ServerRootDirectory) + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!instance.StartsWith(root, comparison))
        {
            throw new InvalidOperationException("The instance directory escapes ServerRootDirectory.");
        }
    }

    public int GetBindPort() => ParseLoopbackHttpUrl(BindUrl, nameof(BindUrl), true).Port;

    public static Uri ParseLoopbackHttpUrl(string value, string name, bool requireLoopbackAddress)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.IsDefaultPort && uri.Port <= 0)
        {
            throw new InvalidOperationException($"{name} must be an absolute HTTP(S) URL without credentials, query, or fragment.");
        }

        bool loopback = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out IPAddress? address))
        {
            loopback = IPAddress.IsLoopback(address);
        }

        if (!loopback || (requireLoopbackAddress &&
            (!IPAddress.TryParse(uri.Host, out IPAddress? bindAddress) || !IPAddress.Loopback.Equals(bindAddress))))
        {
            throw new InvalidOperationException($"{name} must use a loopback address.");
        }

        if (requireLoopbackAddress && uri.AbsolutePath != "/")
        {
            throw new InvalidOperationException($"{name} must not contain a URL path.");
        }

        return uri;
    }

    private static string NormalizeDirectory(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0)
        {
            throw new InvalidOperationException($"{name} is required.");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    private static string NormalizeFile(string value, string name)
    {
        string path = NormalizeDirectory(value, name);
        if (string.IsNullOrEmpty(Path.GetFileName(path)))
        {
            throw new InvalidOperationException($"{name} must name a file.");
        }

        return path;
    }

    private static bool PathsOverlap(string left, string right) =>
        Path.GetFullPath(left).Equals(Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
