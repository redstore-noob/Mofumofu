using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mofumofu.RemoteProtocol;
using Mofumofu.ServerAgent.Security;

namespace Mofumofu.ServerAgent.Events;

public sealed class ProbeEventService
{
    public const string Path = "/internal/probe/events";
    public const string InstanceHeader = "X-Mofumofu-Instance-Id";
    public const string TimestampHeader = "X-Mofumofu-Timestamp";
    public const string NonceHeader = "X-Mofumofu-Nonce";
    public const string SignatureHeader = "X-Mofumofu-Signature";

    private readonly AgentOptions _options;
    private readonly TokenRegistry _registry;
    private readonly EventJournal _journal;
    private readonly IClock _clock;
    private readonly object _sync = new();
    private readonly Dictionary<string, DateTimeOffset> _nonces = new(StringComparer.Ordinal);
    private readonly HashSet<string> _eventIds = new(StringComparer.Ordinal);

    public ProbeEventService(AgentOptions options, TokenRegistry registry, EventJournal journal, IClock clock)
    {
        _options = options;
        _registry = registry;
        _journal = journal;
        _clock = clock;
    }

    public async Task<bool> AcceptAsync(string method, string path, string instanceId, string? timestamp,
        string? nonce, string? signature, byte[] body, CancellationToken cancellationToken = default)
    {
        Authenticate(method, path, instanceId, timestamp, nonce, signature, body);
        ProbeEventRequest request;
        try
        {
            request = JsonSerializer.Deserialize<ProbeEventRequest>(body, ProtocolJson.Options)
                      ?? throw new JsonException("Null event.");
        }
        catch (JsonException)
        {
            throw ApiException.InvalidRequest("The Probe event body is invalid JSON.");
        }

        ProtocolRequest.ValidateVersion(request.ApiVersion);
        ValidateEvent(request);
        lock (_sync)
        {
            PruneNoncesLocked(_clock.UtcNow);
            if (_nonces.ContainsKey(nonce!))
            {
                throw InvalidSignature();
            }

            if (_eventIds.Contains(request.EventId))
            {
                return true;
            }
        }

        if (await _journal.ContainsEventIdAsync(request.EventId, cancellationToken))
        {
            return true;
        }

        lock (_sync)
        {
            if (_nonces.ContainsKey(nonce!))
            {
                throw InvalidSignature();
            }

            if (_eventIds.Contains(request.EventId))
            {
                return true;
            }

            _nonces[nonce!] = _clock.UtcNow.AddMinutes(5);
            _eventIds.Add(request.EventId);
        }

        try
        {
            await _journal.AppendEventAsync(new ServerEvent
            {
                EventId = request.EventId,
                Type = request.Type,
                OccurredAt = request.OccurredAt,
                Message = request.Message,
                Details = request.Details,
            }, cancellationToken);
            return false;
        }
        catch
        {
            lock (_sync)
            {
                _nonces.Remove(nonce!);
                _eventIds.Remove(request.EventId);
            }

            throw;
        }
    }

    public string ComputeCanonicalForTesting(string method, string path, string instanceId, string timestamp,
        string nonce, byte[] body) => Canonical(method, path, instanceId, timestamp, nonce, body);

    public static string CanonicalForContract(string method, string path, string instanceId, string timestamp,
        string nonce, byte[] body) => Canonical(method, path, instanceId, timestamp, nonce, body);

    private void Authenticate(string method, string path, string instanceId, string? timestamp,
        string? nonce, string? signature, byte[] body)
    {
        TokenRegistrySnapshot registry = _registry.LoadSnapshot(instanceId);
        if (!string.Equals(instanceId, _options.InstanceId, StringComparison.Ordinal) ||
            !string.Equals(path, Path, StringComparison.Ordinal) ||
            !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                out DateTimeOffset timestampValue) ||
            Math.Abs((_clock.UtcNow - timestampValue).TotalMinutes) > 5 ||
            !IsValidNonce(nonce))
        {
            throw InvalidSignature();
        }

        string signatureHex = signature ?? string.Empty;
        if (signatureHex.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            signatureHex = signatureHex[7..];
        }
        else if (signatureHex.StartsWith("hmac-sha256=", StringComparison.OrdinalIgnoreCase))
        {
            signatureHex = signatureHex[12..];
        }

        if (!Hex.IsAnyCase(signatureHex, 64))
        {
            throw InvalidSignature();
        }

        byte[] key = Convert.FromHexString(registry.LinkSecret);
        byte[] canonical = Encoding.UTF8.GetBytes(Canonical(method, path, instanceId, timestamp!, nonce!, body));
        byte[] expected = HMACSHA256.HashData(key, canonical);
        byte[] actual = Convert.FromHexString(signatureHex);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            throw InvalidSignature();
        }
    }

    private static string Canonical(string method, string path, string instanceId, string timestamp,
        string nonce, byte[] body) => string.Join('\n',
        method.ToUpperInvariant(), path, instanceId, timestamp, nonce,
        Convert.ToHexStringLower(SHA256.HashData(body)));

    private static bool IsValidNonce(string? nonce) => nonce is { Length: >= 16 and <= 128 } &&
        nonce.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '-' or '_');

    private static void ValidateEvent(ProbeEventRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EventId) || request.EventId.Length > 128 ||
            request.EventId.Any(char.IsControl) || string.IsNullOrWhiteSpace(request.Type) ||
            request.Type.Length > 128 || request.Type.Any(char.IsControl) ||
            request.Message.Length > 65536 || request.OccurredAt.Offset != TimeSpan.Zero ||
            request.Details is null || request.Details.Count > 128 ||
            request.Details.Any(pair => pair.Key.Length > 256 || pair.Value.Length > 4096))
        {
            throw ApiException.InvalidRequest("The Probe event fields are invalid.");
        }
    }

    private void PruneNoncesLocked(DateTimeOffset now)
    {
        foreach ((string nonce, DateTimeOffset expiry) in _nonces.Where(pair => pair.Value < now).ToArray())
        {
            _nonces.Remove(nonce);
        }

        if (_eventIds.Count > 8192)
        {
            _eventIds.Remove(_eventIds.First());
        }
    }

    private static ApiException InvalidSignature() =>
        new(StatusCodes.Status401Unauthorized, ProtocolErrors.Unauthorized,
            "The Probe event signature, timestamp, or nonce is invalid.");
}
