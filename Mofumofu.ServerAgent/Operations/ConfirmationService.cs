using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mofumofu.RemoteProtocol;
using Mofumofu.ServerAgent.Security;

namespace Mofumofu.ServerAgent.Operations;

public sealed class ConfirmationService
{
    private readonly IClock _clock;
    private readonly object _sync = new();
    private readonly Dictionary<string, PendingConfirmation> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Tombstone> _tombstones = new(StringComparer.Ordinal);

    public ConfirmationService(IClock clock)
    {
        _clock = clock;
    }

    public ConfirmationPrepareResponse Prepare(string instanceId, AuthenticationContext authentication,
        string requiredScope, string action, string summary, IReadOnlyDictionary<string, string> parameters,
        string? revision, Func<CancellationToken, Task<OperationInfo>> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        DateTimeOffset now = _clock.UtcNow;
        string id = "cfm_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        PendingConfirmation pending = new()
        {
            Id = id,
            InstanceId = instanceId,
            TokenId = authentication.TokenId,
            RequiredScope = requiredScope,
            Action = action,
            ParametersHash = ComputeParametersHash(parameters),
            Revision = revision,
            CreatedAt = now,
            ExpiresAt = now.AddSeconds(ProtocolConstants.ConfirmationLifetimeSeconds),
            Execute = execute,
        };
        lock (_sync)
        {
            PruneExpiredLocked(now);
            _pending.Add(id, pending);
        }

        return new ConfirmationPrepareResponse
        {
            ConfirmationId = id,
            Action = action,
            Summary = summary,
            CreatedAt = pending.CreatedAt,
            ExpiresAt = pending.ExpiresAt,
            ExpiresInSeconds = ProtocolConstants.ConfirmationLifetimeSeconds,
        };
    }

    public Task<OperationInfo> ExecuteAsync(string instanceId, AuthenticationContext authentication,
        string confirmationId, CancellationToken cancellationToken)
    {
        PendingConfirmation pending;
        lock (_sync)
        {
            DateTimeOffset now = _clock.UtcNow;
            PruneExpiredLocked(now);
            if (!_pending.TryGetValue(confirmationId ?? string.Empty, out pending!))
            {
                if (_tombstones.TryGetValue(confirmationId ?? string.Empty, out Tombstone? tombstone))
                {
                    throw Consumed(tombstone.State);
                }

                throw new ApiException(StatusCodes.Status404NotFound, ProtocolErrors.ConfirmationNotFound,
                    "The confirmation does not exist or was invalidated by an Agent restart.");
            }

            if (!string.Equals(pending.InstanceId, instanceId, StringComparison.Ordinal) ||
                !string.Equals(pending.TokenId, authentication.TokenId, StringComparison.Ordinal))
            {
                throw new ApiException(StatusCodes.Status403Forbidden, ProtocolErrors.Forbidden,
                    "The confirmation belongs to a different instance or token.");
            }

            if (!authentication.Scopes.Contains(pending.RequiredScope))
            {
                throw new ApiException(StatusCodes.Status403Forbidden, ProtocolErrors.InsufficientScope,
                    "The confirmation scope is no longer granted.");
            }

            if (now >= pending.ExpiresAt)
            {
                _pending.Remove(pending.Id);
                _tombstones[pending.Id] = new Tombstone(ConfirmationTombstoneState.Expired, now);
                throw Consumed(ConfirmationTombstoneState.Expired);
            }

            _pending.Remove(pending.Id);
            _tombstones[pending.Id] = new Tombstone(ConfirmationTombstoneState.Used, now);
        }

        return pending.Execute(cancellationToken);
    }

    public static string ComputeParametersHash(IReadOnlyDictionary<string, string> parameters)
    {
        SortedDictionary<string, string> normalized = new(StringComparer.Ordinal);
        foreach ((string key, string value) in parameters)
        {
            normalized[key] = value;
        }

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(normalized, ProtocolJson.Options);
        return Convert.ToHexStringLower(SHA256.HashData(json));
    }

    private void PruneExpiredLocked(DateTimeOffset now)
    {
        foreach (PendingConfirmation pending in _pending.Values.Where(value => now >= value.ExpiresAt).ToArray())
        {
            _pending.Remove(pending.Id);
            _tombstones[pending.Id] = new Tombstone(ConfirmationTombstoneState.Expired, now);
        }

        if (_tombstones.Count <= 4096)
        {
            return;
        }

        foreach ((string id, Tombstone tombstone) in _tombstones
            .OrderBy(pair => pair.Value.CreatedAt).Take(_tombstones.Count - 2048).ToArray())
        {
            _tombstones.Remove(id);
        }
    }

    private static ApiException Consumed(ConfirmationTombstoneState state) => new(
        StatusCodes.Status410Gone,
        state == ConfirmationTombstoneState.Used
            ? ProtocolErrors.ConfirmationAlreadyUsed
            : ProtocolErrors.ConfirmationExpired,
        state == ConfirmationTombstoneState.Used
            ? "The confirmation has already been consumed and cannot be replayed."
            : "The confirmation has expired.");

    private sealed class PendingConfirmation
    {
        public required string Id { get; init; }
        public required string InstanceId { get; init; }
        public required string TokenId { get; init; }
        public required string RequiredScope { get; init; }
        public required string Action { get; init; }
        public required string ParametersHash { get; init; }
        public string? Revision { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
        public required Func<CancellationToken, Task<OperationInfo>> Execute { get; init; }
    }

    private enum ConfirmationTombstoneState
    {
        Used,
        Expired,
    }

    private sealed record Tombstone(ConfirmationTombstoneState State, DateTimeOffset CreatedAt);
}
