using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MofuCore.Server;
using Mofumofu.RemoteProtocol;

namespace Mofumofu.Launcher.Services.Servers;

public sealed class RemoteServerBackend : IServerBackend
{
    private readonly RemoteServerConnection _connection;
    private readonly RemoteApiClient _api;
    private readonly RemoteEventStream _events;
    private readonly Dictionary<string, string> _playerRevisions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _fileRevisions = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> _capabilities;
    private IReadOnlyList<string> _scopes;
    private BackendStatus? _lastStatus;
    private bool _disposed;

    public RemoteServerBackend(RemoteServerConnection connection)
    {
        _connection = connection;
        _capabilities = connection.Capabilities.ToList();
        _scopes = connection.Scopes.ToList();
        _api = new RemoteApiClient(connection.BaseUrl, connection.InstanceId);
        _events = new RemoteEventStream(_api, () => _connection.DailyToken);
        _events.ConsoleLine += (_, args) => ConsoleLine?.Invoke(this, args);
        _events.ConsoleReset += (_, _) => ConsoleReset?.Invoke(this, EventArgs.Empty);
        _events.SnapshotReceived += (_, status) => _lastStatus = MapStatus(status, false);
        CurrentDescription = Describe(connection, null);
    }

    public string BackendKey => _connection.BackendKey;
    public string InstanceId => _connection.InstanceId;
    public bool IsLocal => false;
    public IReadOnlyList<string> Capabilities => _capabilities;
    public IReadOnlyList<string> Scopes => _scopes;
    public ServerDescription CurrentDescription { get; private set; }

    public event EventHandler<ServerConsoleEventArgs>? ConsoleLine;
    public event EventHandler? ConsoleReset;

    public async Task<ServerDescription> GetDescriptionAsync(CancellationToken cancellationToken = default)
    {
        HandshakeResponse handshake = await _api.HandshakeAsync(_connection.DailyToken, cancellationToken);
        _capabilities = handshake.Capabilities;
        var scopes = new HashSet<string>(handshake.Scopes, StringComparer.Ordinal);
        if (handshake.Capabilities.Contains(ProtocolCapabilities.Command) &&
            !string.IsNullOrWhiteSpace(_connection.CommandToken))
        {
            try
            {
                HandshakeResponse commandHandshake = await _api.HandshakeAsync(
                    _connection.CommandToken, cancellationToken);
                scopes.UnionWith(commandHandshake.Scopes);
            }
            catch (RemoteApiException error) when (error.IsAuthenticationFailure)
            {
                // 日常令牌仍可用于其他功能；控制台按实际授权降级。
            }
        }
        _scopes = scopes.ToList();
        StatusResponse? status = null;
        try { status = await _api.GetStatusAsync(_connection.DailyToken, cancellationToken); } catch { }
        CurrentDescription = Describe(_connection, status, handshake);
        return CurrentDescription;
    }

    public async Task<BackendStatus> GetStatusAsync(long cursor, CancellationToken cancellationToken = default)
    {
        try
        {
            StatusResponse status = await _api.GetStatusAsync(_connection.DailyToken, cancellationToken);
            IReadOnlyList<ConsoleEntry> history = cursor == 0 ? _events.RecentConsole() : [];
            BackendStatus mapped = MapStatus(status, false, history);
            _lastStatus = mapped;
            return mapped;
        }
        catch (Exception) when (cursor == long.MaxValue)
        {
            BackendStatus unavailable = new()
            {
                Status = "unreachable",
                Players = _lastStatus?.Players ?? 0,
                MaxPlayers = _lastStatus?.MaxPlayers ?? _connection.MaxPlayers,
                Degraded = true,
            };
            return unavailable;
        }
    }

    public async Task SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        if (!TokenUtilities.IsValidCommand(command)) throw new ArgumentException("指令为空、过长或包含控制字符。");
        await _api.SendAsync<ConsoleCommandResponse>(System.Net.Http.HttpMethod.Post, "/console/commands",
            new ConsoleCommandRequest { Command = command }, _connection.CommandToken, cancellationToken);
    }

    public Task<BackendOperation> StartAsync(CancellationToken cancellationToken = default) =>
        ControlAsync(ProtocolControlActions.Start, cancellationToken);

    public Task<BackendOperation> StopAsync(CancellationToken cancellationToken = default) =>
        ControlAsync(ProtocolControlActions.Stop, cancellationToken);

    public Task<BackendOperation> RestartAsync(CancellationToken cancellationToken = default) =>
        ControlAsync(ProtocolControlActions.Restart, cancellationToken);

    public async Task<BackendPlayers> GetPlayersAsync(CancellationToken cancellationToken = default)
    {
        Task<OnlinePlayersResponse> onlineTask = _api.GetOnlinePlayersAsync(_connection.DailyToken, cancellationToken);
        Task<PlayerListResponse> whitelistTask = GetPlayerListAsync(ProtocolPlayerLists.Whitelist, cancellationToken);
        Task<PlayerListResponse> opsTask = GetPlayerListAsync(ProtocolPlayerLists.Ops, cancellationToken);
        Task<PlayerListResponse> bansTask = GetPlayerListAsync(ProtocolPlayerLists.Bans, cancellationToken);
        await Task.WhenAll(onlineTask, whitelistTask, opsTask, bansTask);
        return new BackendPlayers
        {
            Online = onlineTask.Result.Players.Select(player => new BackendPlayerEntry(player.Name,
                player.Uuid?.ToString() ?? string.Empty, JoinedAt: player.JoinedAt)).ToList(),
            Whitelist = whitelistTask.Result.Entries.Select(MapPlayer).ToList(),
            Ops = opsTask.Result.Entries.Select(MapPlayer).ToList(),
            Banned = bansTask.Result.Entries.Select(MapPlayer).ToList(),
            Running = _lastStatus?.Status is ServerManage.StatusRunning or ServerManage.StatusStarting,
            Revision = string.Join(':', _playerRevisions.OrderBy(pair => pair.Key).Select(pair => pair.Value)),
        };
    }

    public async Task ModifyWhitelistAsync(string name, bool add, CancellationToken cancellationToken = default) =>
        await MutatePlayerAsync(ProtocolPlayerLists.Whitelist, name, add, null, cancellationToken);

    public async Task ModifyOpAsync(string name, bool add, CancellationToken cancellationToken = default) =>
        await MutatePlayerAsync(ProtocolPlayerLists.Ops, name, add, null, cancellationToken);

    public async Task ModifyBanAsync(string name, bool add, string? reason = null,
        CancellationToken cancellationToken = default) =>
        await MutatePlayerAsync(ProtocolPlayerLists.Bans, name, add, reason, cancellationToken);

    public async Task SetWhitelistEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ServerPropertiesResponse current = await _api.GetPropertiesAsync(_connection.DailyToken, cancellationToken);
        await _api.SetPropertiesAsync(new Dictionary<string, string?> { ["white-list"] = enabled ? "true" : "false" },
            current.Revision, _connection.DailyToken, cancellationToken);
    }

    public async Task<IReadOnlyList<BackendBackup>> GetBackupsAsync(CancellationToken cancellationToken = default)
    {
        BackupListResponse response = await _api.GetBackupsAsync(_connection.DailyToken, cancellationToken);
        return response.Backups.Select(MapBackup).ToList();
    }

    public async Task<BackendBackup> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        BackupCreateResponse response = await _api.CreateBackupAsync(_connection.DailyToken, cancellationToken);
        if (!string.IsNullOrWhiteSpace(response.Operation.Id))
            await WaitForOperationAsync(response.Operation, cancellationToken);
        return MapBackup(response.Backup);
    }

    public async Task<BackendOperation> RestoreBackupAsync(string name, CancellationToken cancellationToken = default)
    {
        ConfirmationExecuteResponse response = await _api.RestoreBackupAsync(name, _connection.DailyToken, cancellationToken);
        BackendOperation result = await WaitForOperationAsync(response.Operation, cancellationToken,
            waitForCompletion: _capabilities.Contains(ProtocolCapabilities.Control));
        return result;
    }

    public async Task DeleteBackupAsync(string name, CancellationToken cancellationToken = default)
    {
        ConfirmationExecuteResponse response = await _api.DeleteBackupAsync(name, _connection.DailyToken, cancellationToken);
        await WaitForOperationAsync(response.Operation, cancellationToken);
    }

    public async Task<IReadOnlyList<BackendFile>> GetFilesAsync(string relativePath,
        CancellationToken cancellationToken = default)
    {
        FileListResponse response = await _api.GetFilesAsync(relativePath, _connection.DailyToken, cancellationToken);
        return response.Entries.Select(entry =>
        {
            if (!string.IsNullOrWhiteSpace(entry.Revision)) _fileRevisions[entry.Path] = entry.Revision;
            return new BackendFile(NameOf(entry.Path), entry.Path, entry.IsDirectory, entry.SizeBytes ?? 0,
                entry.ModifiedAt ?? DateTimeOffset.UnixEpoch, entry.Revision ?? string.Empty);
        }).ToList();
    }

    public async Task<BackendFileContent> ReadFileAsync(string relativePath,
        CancellationToken cancellationToken = default)
    {
        FileContentResponse response = await _api.ReadFileAsync(relativePath, _connection.DailyToken, cancellationToken);
        _fileRevisions[relativePath] = response.Revision;
        return new BackendFileContent(response.Path, response.Content, response.Revision);
    }

    public async Task<BackendOperation> WriteFileAsync(string relativePath, string content,
        string? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        FileWriteResponse response = await _api.WriteFileAsync(relativePath, content,
            expectedRevision ?? _fileRevisions.GetValueOrDefault(relativePath), _connection.DailyToken, cancellationToken);
        _fileRevisions[relativePath] = response.Revision;
        return new BackendOperation();
    }

    public async Task CreateTextFileAsync(string relativePath, CancellationToken cancellationToken = default) =>
        await _api.CreateFileAsync(relativePath, false, _connection.DailyToken, cancellationToken);

    public async Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default) =>
        await _api.CreateFileAsync(relativePath, true, _connection.DailyToken, cancellationToken);

    public async Task<string> RenameFileAsync(string relativePath, string newName,
        CancellationToken cancellationToken = default)
    {
        FileMutationResponse response = await _api.RenameFileAsync(relativePath, newName,
            _fileRevisions.GetValueOrDefault(relativePath), _connection.DailyToken, cancellationToken);
        return response.Path;
    }

    public async Task DeleteFileAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        ConfirmationExecuteResponse response = await _api.DeleteFileAsync(relativePath,
            _fileRevisions.GetValueOrDefault(relativePath), _connection.DailyToken, cancellationToken);
        await WaitForOperationAsync(response.Operation, cancellationToken);
    }

    public async Task<BackendProperties> GetPropertiesAsync(CancellationToken cancellationToken = default)
    {
        ServerPropertiesResponse response = await _api.GetPropertiesAsync(_connection.DailyToken, cancellationToken);
        return new BackendProperties(response.Properties.Select(pair => new BackendProperty(pair.Key, pair.Value)).ToList(),
            response.Revision);
    }

    public async Task<BackendOperation> SetPropertiesAsync(IReadOnlyList<BackendProperty> values,
        string? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        ConfirmationExecuteResponse response = await _api.SetPropertiesAsync(
            values.ToDictionary(value => value.Key, value => (string?)value.Value, StringComparer.OrdinalIgnoreCase),
            expectedRevision, _connection.DailyToken, cancellationToken);
        BackendOperation result = await WaitForOperationAsync(response.Operation, cancellationToken);
        return result with { PendingRestart = true };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _events.Dispose();
        _api.Dispose();
    }

    private async Task<PlayerListResponse> GetPlayerListAsync(string kind, CancellationToken cancellationToken)
    {
        PlayerListResponse response = await _api.GetPlayerListAsync(kind, _connection.DailyToken, cancellationToken);
        _playerRevisions[kind] = response.Revision;
        return response;
    }

    private async Task MutatePlayerAsync(string kind, string name, bool add, string? reason,
        CancellationToken cancellationToken)
    {
        if (!TokenUtilities.IsValidPlayerName(name)) throw new ArgumentException("玩家名不合法。");
        string revision = _playerRevisions.GetValueOrDefault(kind) ?? string.Empty;
        PlayerMutationResponse response = add
            ? await _api.AddPlayerAsync(kind, name, _connection.DailyToken, revision, reason, cancellationToken)
            : await _api.RemovePlayerAsync(kind, name, _connection.DailyToken, revision, cancellationToken);
        _playerRevisions[kind] = response.Revision;
    }

    private async Task<BackendOperation> ControlAsync(string action, CancellationToken cancellationToken)
    {
        if (!_capabilities.Contains(ProtocolCapabilities.Control))
            throw new RemoteApiException("该实例由面板接管生命周期，请在面板中启停或重启。",
                code: ProtocolErrors.CapabilityNotSupported);
        ConfirmationExecuteResponse response = await _api.ControlAsync(action, _connection.DailyToken, cancellationToken);
        return await WaitForOperationAsync(response.Operation, cancellationToken);
    }

    private async Task<BackendOperation> WaitForOperationAsync(OperationInfo operation,
        CancellationToken cancellationToken, bool waitForCompletion = true)
    {
        if (operation.State == ProtocolOperationStates.Failed) throw OperationError(operation);
        if (!waitForCompletion || string.IsNullOrWhiteSpace(operation.Id) ||
            operation.State is not (ProtocolOperationStates.Queued or ProtocolOperationStates.Running))
        {
            return MapOperation(operation);
        }

        OperationInfo current = operation;
        for (int attempt = 0; attempt < 600; attempt++)
        {
            await Task.Delay(500, cancellationToken);
            current = (await _api.GetOperationAsync(current.Id, _connection.DailyToken, cancellationToken)).Operation;
            if (current.State == ProtocolOperationStates.Failed) throw OperationError(current);
            if (current.State == ProtocolOperationStates.Succeeded) return MapOperation(current);
        }
        throw new RemoteApiException("远程操作尚未完成，请稍后刷新状态。", code: ProtocolErrors.OperationFailed);
    }

    private static BackendOperation MapOperation(OperationInfo operation) => new(
        operation.State == ProtocolOperationStates.Succeeded,
        operation.Metadata?.GetValueOrDefault("pendingRestart") == "true" ||
        operation.State is ProtocolOperationStates.Queued or ProtocolOperationStates.Running,
        operation.Message);

    private static RemoteApiException OperationError(OperationInfo operation) => new(
        operation.Error?.Message ?? operation.Message ?? "远程操作失败。",
        code: operation.Error?.Code ?? ProtocolErrors.OperationFailed);

    private static BackendPlayerEntry MapPlayer(PlayerListEntry entry) => new(entry.Name,
        entry.Uuid?.ToString() ?? string.Empty, entry.OpLevel ?? 4, entry.BypassesPlayerLimit ?? false,
        entry.Reason ?? string.Empty, entry.ExpiresAt?.ToString("O") ?? "forever");

    private static BackendBackup MapBackup(BackupMetadata backup) => new(backup.Id, backup.SizeBytes,
        backup.CreatedAt, false);

    private BackendStatus MapStatus(StatusResponse status, bool degraded,
        IReadOnlyList<ConsoleEntry>? history = null) => new()
    {
        Status = status.State switch
        {
            ProtocolServerStates.Running => ServerManage.StatusRunning,
            ProtocolServerStates.Starting => ServerManage.StatusStarting,
            ProtocolServerStates.Stopping => ServerManage.StatusStopping,
            ProtocolServerStates.Crashed => "crashed",
            _ => ServerManage.StatusStopped,
        },
        Players = status.OnlinePlayers,
        MaxPlayers = status.MaxPlayers,
        CpuPercent = status.CpuPercent ?? 0,
        MemoryMb = status.MemoryUsedBytes is { } bytes ? bytes / 1024d / 1024d : 0,
        MemoryKind = _connection.DeploymentMode == ProtocolDeploymentModes.Probe ? "jvm-heap" : "process-rss",
        UptimeSeconds = status.UptimeSeconds ?? 0,
        Degraded = degraded,
        NextCursor = history is { Count: > 0 } ? history[^1].Sequence : 0,
        LogLines = history?.Select(entry => entry.Message).ToList() ?? [],
    };

    private static ServerDescription Describe(RemoteServerConnection connection, StatusResponse? status,
        HandshakeResponse? handshake = null) => new(connection.InstanceId,
        handshake?.ServerName is { Length: > 0 } name ? name : connection.Name,
        "remote", handshake?.ServerImplementationVersion ?? connection.ImplementationVersion,
        status?.MinecraftVersion ?? connection.McVersion,
        new Uri(connection.BaseUrl).Host,
        status?.MaxPlayers ?? connection.MaxPlayers,
        string.IsNullOrWhiteSpace(status?.Motd) ? connection.Motd : status.Motd);

    private static string NameOf(string path) => path.Replace('\\', '/').TrimEnd('/').Split('/').Last();
}
