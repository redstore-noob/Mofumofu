using MofuCore.Server;

namespace Mofumofu.ServerAgent.Core;

public interface ICoreGateway
{
    ServerInfo? GetServerInfo(string instanceId);
    ServerSnapshot Poll(string instanceId, long cursor);
    bool IsRunning(string instanceId);
    bool AutoRestartEnabled(string instanceId);
    Task StartAsync(string instanceId, CancellationToken cancellationToken);
    Task StopAsync(string instanceId, bool force);
    Task RestartAsync(string instanceId, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ShutdownAllAsync(TimeSpan timeout);
    Task RunCommandAsync(string instanceId, string command);
    ServerPlayerSummary GetPlayers(string instanceId);
    void SaveWhitelist(string instanceId, IEnumerable<WhitelistEntry> entries);
    void SaveOps(string instanceId, IEnumerable<OpEntry> entries);
    void SaveBanned(string instanceId, IEnumerable<BannedPlayerEntry> entries);
    string GetOfflinePlayerUuid(string playerName);
    Task<ServerBackupInfo> CreateBackupAsync(string instanceId, CancellationToken cancellationToken);
    IReadOnlyList<ServerBackupInfo> ListBackups(string instanceId);
    void DeleteBackup(string instanceId, string backupId);
    Task RestoreBackupAsync(string instanceId, string backupId, CancellationToken cancellationToken);
}

public sealed class CoreGateway : ICoreGateway
{
    public ServerInfo? GetServerInfo(string instanceId) => ServerManage.GetServerInfo(instanceId);

    public ServerSnapshot Poll(string instanceId, long cursor) => ServerManage.Poll(instanceId, cursor);

    public bool IsRunning(string instanceId) => ServerManage.IsRunning(instanceId);

    public bool AutoRestartEnabled(string instanceId) => ServerManage.AutoRestartEnabled(instanceId);

    public Task StartAsync(string instanceId, CancellationToken cancellationToken) =>
        ServerManage.StartAsync(instanceId, cancellationToken);

    public Task StopAsync(string instanceId, bool force) => ServerManage.StopAsync(instanceId, force);

    public Task RestartAsync(string instanceId, CancellationToken cancellationToken) =>
        ServerManage.RestartAsync(instanceId, cancellationToken);

    public Task<IReadOnlyList<string>> ShutdownAllAsync(TimeSpan timeout) =>
        ServerManage.ShutdownAllAsync(timeout);

    public Task RunCommandAsync(string instanceId, string command) =>
        ServerPlayers.RunCommandAsync(instanceId, command);

    public ServerPlayerSummary GetPlayers(string instanceId) => ServerPlayers.Get(instanceId);

    public void SaveWhitelist(string instanceId, IEnumerable<WhitelistEntry> entries) =>
        ServerPlayers.SaveWhitelist(instanceId, entries);

    public void SaveOps(string instanceId, IEnumerable<OpEntry> entries) =>
        ServerPlayers.SaveOps(instanceId, entries);

    public void SaveBanned(string instanceId, IEnumerable<BannedPlayerEntry> entries) =>
        ServerPlayers.SaveBanned(instanceId, entries);

    public string GetOfflinePlayerUuid(string playerName) => ServerPlayers.OfflinePlayerUuid(playerName);

    public Task<ServerBackupInfo> CreateBackupAsync(string instanceId, CancellationToken cancellationToken) =>
        ServerBackup.CreateAsync(instanceId, cancellationToken);

    public IReadOnlyList<ServerBackupInfo> ListBackups(string instanceId) => ServerBackup.List(instanceId);

    public void DeleteBackup(string instanceId, string backupId) => ServerBackup.Delete(instanceId, backupId);

    public Task RestoreBackupAsync(string instanceId, string backupId, CancellationToken cancellationToken) =>
        ServerBackup.RestoreAsync(instanceId, backupId, cancellationToken);
}
