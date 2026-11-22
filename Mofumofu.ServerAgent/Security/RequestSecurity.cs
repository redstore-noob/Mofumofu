using System.Text.Json;
using Mofumofu.RemoteProtocol;

namespace Mofumofu.ServerAgent.Security;

public enum RateCategory
{
    Read,
    Write,
    Sensitive,
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class AgentSecurityMetadata : Attribute
{
    public AgentSecurityMetadata(string scope, RateCategory category, bool root = false)
    {
        Scope = scope;
        Category = category;
        Root = root;
    }

    public string Scope { get; }
    public RateCategory Category { get; }
    public bool Root { get; }
}

public sealed class TokenRateLimiter
{
    private readonly IClock _clock;
    private readonly object _sync = new();
    private readonly Dictionary<string, Window> _windows = new(StringComparer.Ordinal);

    public TokenRateLimiter(IClock clock)
    {
        _clock = clock;
    }

    public void Check(string tokenId, RateCategory category)
    {
        int limit = category switch
        {
            RateCategory.Read => 20,
            RateCategory.Write => 5,
            RateCategory.Sensitive => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(category)),
        };
        long second = _clock.UtcNow.ToUnixTimeSeconds();
        string key = $"{tokenId}\n{category}";
        lock (_sync)
        {
            if (!_windows.TryGetValue(key, out Window? window) || window.Second != second)
            {
                window = new Window { Second = second };
                _windows[key] = window;
            }

            if (window.Count >= limit)
            {
                throw new ApiException(StatusCodes.Status429TooManyRequests, ProtocolErrors.RateLimited,
                    "The per-token request rate limit was exceeded.");
            }

            window.Count++;
            if (_windows.Count > 4096)
            {
                foreach (KeyValuePair<string, Window> item in _windows
                    .Where(pair => pair.Value.Second < second - 2).ToArray())
                {
                    _windows.Remove(item.Key);
                }
            }
        }
    }

    private sealed class Window
    {
        public long Second { get; init; }
        public int Count { get; set; }
    }
}

public sealed record AuditEntry
{
    public DateTimeOffset Timestamp { get; init; }
    public string TokenId { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Parameters { get; init; } =
        new Dictionary<string, string>();
    public string Result { get; init; } = string.Empty;
}

public interface IAuditSink
{
    Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}

public sealed class JsonlAuditSink : IAuditSink
{
    private static readonly JsonSerializerOptions SerializerOptions = new(ProtocolJson.Options);
    private readonly string _path;
    private readonly SemaphoreSlim _sync = new(1, 1);

    public JsonlAuditSink(string path)
    {
        _path = path;
    }

    public async Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            byte[] json = JsonSerializer.SerializeToUtf8Bytes(entry, SerializerOptions);
            await using FileStream stream = new(_path, FileMode.Append, FileAccess.Write, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(json, cancellationToken);
            await stream.WriteAsync(new byte[] { (byte)'\n' }, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            _sync.Release();
        }
    }
}

public sealed class AuditExecutor
{
    private readonly IAuditSink _sink;
    private readonly IClock _clock;

    public AuditExecutor(IAuditSink sink, IClock clock)
    {
        _sink = sink;
        _clock = clock;
    }

    public async Task<T> ExecuteAsync<T>(AuthenticationContext authentication, string action,
        IReadOnlyDictionary<string, string> parameters, Func<CancellationToken, Task<T>> write,
        CancellationToken cancellationToken = default)
    {
        await AppendRequiredAsync(new AuditEntry
        {
            Timestamp = _clock.UtcNow,
            TokenId = authentication.TokenId,
            Action = action,
            Parameters = parameters,
            Result = "pending",
        }, cancellationToken);

        try
        {
            T result = await write(cancellationToken);
            await AppendRequiredAsync(new AuditEntry
            {
                Timestamp = _clock.UtcNow,
                TokenId = authentication.TokenId,
                Action = action,
                Parameters = parameters,
                Result = "succeeded",
            }, cancellationToken);
            return result;
        }
        catch (Exception error)
        {
            AuditEntry failure = new()
            {
                Timestamp = _clock.UtcNow,
                TokenId = authentication.TokenId,
                Action = action,
                Parameters = parameters,
                Result = error is ApiException api ? $"failed:{api.Code}" : "failed",
            };
            try
            {
                await _sink.AppendAsync(failure, CancellationToken.None);
            }
            catch
            {
                throw AuditUnavailable();
            }

            throw;
        }
    }

    public Task ExecuteAsync(AuthenticationContext authentication, string action,
        IReadOnlyDictionary<string, string> parameters, Func<CancellationToken, Task> write,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(authentication, action, parameters, async token =>
        {
            await write(token);
            return true;
        }, cancellationToken);

    private async Task AppendRequiredAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            await _sink.AppendAsync(entry, cancellationToken);
        }
        catch (Exception error) when (error is not ApiException)
        {
            throw AuditUnavailable();
        }
    }

    private static ApiException AuditUnavailable() =>
        new(StatusCodes.Status500InternalServerError, AgentErrorCodes.AuditUnavailable,
            "Audit storage is unavailable; the write was not accepted.");
}
