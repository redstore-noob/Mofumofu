using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MofuCore.Server;
using Mofumofu.RemoteProtocol;
using Mofumofu.ServerAgent.Core;
using Mofumofu.ServerAgent.Probe;
using Mofumofu.ServerAgent.Security;

namespace Mofumofu.ServerAgent.Services;

public sealed class PlayerService
{
    private readonly AgentOptions _options;
    private readonly ICoreGateway _core;
    private readonly IProbeGateway _probe;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _mutationSync = new(1, 1);

    public PlayerService(AgentOptions options, ICoreGateway core, IProbeGateway probe, IClock clock)
    {
        _options = options;
        _core = core;
        _probe = probe;
        _clock = clock;
    }

    public async Task<OnlinePlayersResponse> OnlineAsync(AuthenticationContext authentication,
        CancellationToken cancellationToken)
    {
        ServerInfo? info = RequireInfo();
        if (_core.IsRunning(_options.InstanceId))
        {
            try
            {
                string path = ProtocolConstants.GetServerBasePath(_options.InstanceId) + "/players/online";
                ProbeResponse response = await _probe.SendAsync(HttpMethod.Get, path,
                    authentication.Authorization, query: "apiVersion=" + ProtocolConstants.ApiVersion,
                    cancellationToken: cancellationToken);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw RemoteError(response);
                }

                OnlinePlayersResponse probed = response.Deserialize<OnlinePlayersResponse>();
                ProtocolRequest.ValidateVersion(probed.ApiVersion);
                return probed;
            }
            catch (ProbeUnavailableException)
            {
                // Core/RCON remains authoritative for names; Probe-only fields such as joinedAt stay null.
            }
        }

        ServerPlayerSummary summary = _core.GetPlayers(_options.InstanceId);
        List<PlayerInfo> players = summary.Online.Select(name => new PlayerInfo { Name = name }).ToList();
        return new OnlinePlayersResponse
        {
            OnlineCount = players.Count,
            MaxPlayers = info.MaxPlayers,
            ObservedAt = _clock.UtcNow,
            Players = players,
        };
    }

    public PlayerListResponse GetList(string kind)
    {
        ValidateKind(kind);
        ServerPlayerSummary summary = _core.GetPlayers(_options.InstanceId);
        List<PlayerListEntry> entries = Entries(kind, summary);
        return new PlayerListResponse
        {
            Kind = kind,
            Entries = entries,
            Revision = Revision(entries),
        };
    }

    public async Task<PlayerMutationResponse> AddAsync(string kind, PlayerAddRequest request,
        CancellationToken cancellationToken)
    {
        ValidateKind(kind);
        ValidatePlayerName(request.PlayerName);
        ValidateReason(request.Reason);
        if (request.OpLevel is < 1 or > 4)
        {
            throw ApiException.InvalidRequest("opLevel must be between 1 and 4.");
        }

        await _mutationSync.WaitAsync(cancellationToken);
        try
        {
            PlayerListResponse current = GetList(kind);
            EnsureRevision(current.Revision, request.ExpectedRevision);
            if (current.Entries.Any(entry => entry.Name.Equals(request.PlayerName, StringComparison.OrdinalIgnoreCase)))
            {
                throw ApiException.Conflict("The player is already in this list.");
            }

            ServerPlayerSummary summary = _core.GetPlayers(_options.InstanceId);
            PlayerListEntry added = new()
            {
                Name = request.PlayerName,
                Uuid = Guid.Parse(_core.GetOfflinePlayerUuid(request.PlayerName)),
                OpLevel = kind == ProtocolPlayerLists.Ops ? request.OpLevel ?? 4 : null,
                BypassesPlayerLimit = kind == ProtocolPlayerLists.Ops ? request.BypassesPlayerLimit ?? false : null,
                Reason = kind == ProtocolPlayerLists.Bans ? request.Reason ?? "Banned by an operator." : null,
                Source = kind == ProtocolPlayerLists.Bans ? "Mofumofu.ServerAgent" : null,
                CreatedAt = kind == ProtocolPlayerLists.Bans ? _clock.UtcNow : null,
                ExpiresAt = kind == ProtocolPlayerLists.Bans ? request.ExpiresAt : null,
            };
            if (_core.IsRunning(_options.InstanceId))
            {
                string command = kind switch
                {
                    ProtocolPlayerLists.Whitelist => $"whitelist add {request.PlayerName}",
                    ProtocolPlayerLists.Ops => $"op {request.PlayerName}",
                    ProtocolPlayerLists.Bans => $"ban {request.PlayerName} {added.Reason}",
                    _ => throw new ArgumentOutOfRangeException(nameof(kind)),
                };
                await _core.RunCommandAsync(_options.InstanceId, command);
            }
            else
            {
                AddOffline(kind, request, summary);
            }

            PlayerListResponse updated = GetList(kind);
            PlayerListEntry entry = updated.Entries.FirstOrDefault(item =>
                item.Name.Equals(request.PlayerName, StringComparison.OrdinalIgnoreCase)) ?? added;
            return new PlayerMutationResponse
            {
                Kind = kind,
                Revision = updated.Revision,
                Entry = entry,
                Removed = false,
            };
        }
        finally
        {
            _mutationSync.Release();
        }
    }

    public async Task<PlayerMutationResponse> RemoveAsync(string kind, PlayerRemoveRequest request,
        CancellationToken cancellationToken)
    {
        ValidateKind(kind);
        ValidatePlayerName(request.PlayerName);
        await _mutationSync.WaitAsync(cancellationToken);
        try
        {
            PlayerListResponse current = GetList(kind);
            EnsureRevision(current.Revision, request.ExpectedRevision);
            PlayerListEntry removed = current.Entries.FirstOrDefault(entry =>
                entry.Name.Equals(request.PlayerName, StringComparison.OrdinalIgnoreCase))
                ?? throw ApiException.NotFound("The player is not in this list.");
            ServerPlayerSummary summary = _core.GetPlayers(_options.InstanceId);
            if (_core.IsRunning(_options.InstanceId))
            {
                string command = kind switch
                {
                    ProtocolPlayerLists.Whitelist => $"whitelist remove {request.PlayerName}",
                    ProtocolPlayerLists.Ops => $"deop {request.PlayerName}",
                    ProtocolPlayerLists.Bans => $"pardon {request.PlayerName}",
                    _ => throw new ArgumentOutOfRangeException(nameof(kind)),
                };
                await _core.RunCommandAsync(_options.InstanceId, command);
            }
            else
            {
                RemoveOffline(kind, request.PlayerName, summary);
            }

            return new PlayerMutationResponse
            {
                Kind = kind,
                Revision = Revision(Entries(kind, _core.GetPlayers(_options.InstanceId))),
                Entry = removed,
                Removed = true,
            };
        }
        finally
        {
            _mutationSync.Release();
        }
    }

    private void AddOffline(string kind, PlayerAddRequest request, ServerPlayerSummary summary)
    {
        switch (kind)
        {
            case ProtocolPlayerLists.Whitelist:
                summary.Whitelist.Add(new WhitelistEntry { Name = request.PlayerName });
                _core.SaveWhitelist(_options.InstanceId, summary.Whitelist);
                break;
            case ProtocolPlayerLists.Ops:
                summary.Ops.Add(new OpEntry
                {
                    Name = request.PlayerName,
                    Level = request.OpLevel ?? 4,
                    BypassesPlayerLimit = request.BypassesPlayerLimit ?? false,
                });
                _core.SaveOps(_options.InstanceId, summary.Ops);
                break;
            case ProtocolPlayerLists.Bans:
                summary.Banned.Add(new BannedPlayerEntry
                {
                    Name = request.PlayerName,
                    Reason = request.Reason ?? "Banned by an operator.",
                    Created = FormatMinecraftTime(_clock.UtcNow),
                    Expires = request.ExpiresAt is { } expires ? FormatMinecraftTime(expires) : "forever",
                    Source = "Mofumofu.ServerAgent",
                });
                _core.SaveBanned(_options.InstanceId, summary.Banned);
                break;
        }
    }

    private void RemoveOffline(string kind, string playerName, ServerPlayerSummary summary)
    {
        bool Matches(string name) => name.Equals(playerName, StringComparison.OrdinalIgnoreCase);
        switch (kind)
        {
            case ProtocolPlayerLists.Whitelist:
                _core.SaveWhitelist(_options.InstanceId, summary.Whitelist.Where(entry => !Matches(entry.Name)));
                break;
            case ProtocolPlayerLists.Ops:
                _core.SaveOps(_options.InstanceId, summary.Ops.Where(entry => !Matches(entry.Name)));
                break;
            case ProtocolPlayerLists.Bans:
                _core.SaveBanned(_options.InstanceId, summary.Banned.Where(entry => !Matches(entry.Name)));
                break;
        }
    }

    private ServerInfo RequireInfo() => _core.GetServerInfo(_options.InstanceId)
        ?? throw ApiException.NotFound("The server instance no longer exists.");

    private static List<PlayerListEntry> Entries(string kind, ServerPlayerSummary summary) => kind switch
    {
        ProtocolPlayerLists.Whitelist => summary.Whitelist.Select(entry => new PlayerListEntry
        {
            Name = entry.Name,
            Uuid = Guid.TryParse(entry.Uuid, out Guid uuid) ? uuid : null,
        }).OrderBy(entry => entry.Name, StringComparer.Ordinal).ToList(),
        ProtocolPlayerLists.Ops => summary.Ops.Select(entry => new PlayerListEntry
        {
            Name = entry.Name,
            Uuid = Guid.TryParse(entry.Uuid, out Guid uuid) ? uuid : null,
            OpLevel = entry.Level,
            BypassesPlayerLimit = entry.BypassesPlayerLimit,
        }).OrderBy(entry => entry.Name, StringComparer.Ordinal).ToList(),
        ProtocolPlayerLists.Bans => summary.Banned.Select(entry => new PlayerListEntry
        {
            Name = entry.Name,
            Uuid = Guid.TryParse(entry.Uuid, out Guid uuid) ? uuid : null,
            Reason = entry.Reason,
            Source = entry.Source,
            CreatedAt = ParseMinecraftTime(entry.Created),
            ExpiresAt = ParseMinecraftTime(entry.Expires),
        }).OrderBy(entry => entry.Name, StringComparer.Ordinal).ToList(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string Revision(IEnumerable<PlayerListEntry> entries)
    {
        byte[] json = Encoding.UTF8.GetBytes(ProtocolJson.Serialize(entries.ToArray()));
        return "pl_" + Convert.ToHexStringLower(SHA256.HashData(json));
    }

    private static void EnsureRevision(string actual, string? expected)
    {
        if (!string.IsNullOrEmpty(expected) && !string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw ApiException.RevisionConflict(expected);
        }
    }

    private static void ValidateKind(string kind)
    {
        if (kind is not (ProtocolPlayerLists.Whitelist or ProtocolPlayerLists.Ops or ProtocolPlayerLists.Bans))
        {
            throw ApiException.NotFound("The player list kind was not found.");
        }
    }

    private static void ValidatePlayerName(string? playerName)
    {
        if (!TokenUtilities.IsValidPlayerName(playerName))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, ProtocolErrors.InvalidPlayerName,
                "Player names must be 1-16 ASCII letters, digits, or underscores.");
        }
    }

    private static void ValidateReason(string? reason)
    {
        if (reason is not null && (reason.Length > 256 || reason.Any(char.IsControl)))
        {
            throw ApiException.InvalidRequest("The ban reason is invalid.");
        }
    }

    private static string FormatMinecraftTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss '+0000'", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseMinecraftTime(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "forever")
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed)
            ? parsed : null;
    }

    private static ApiException RemoteError(ProbeResponse response)
    {
        try
        {
            ApiError error = response.Deserialize<ApiError>();
            return new ApiException((int)response.StatusCode, error.Code, error.Message, error.Details);
        }
        catch (JsonException)
        {
            return new ApiException(StatusCodes.Status500InternalServerError, ProtocolErrors.InternalError,
                "Probe returned an invalid error response.");
        }
    }
}
