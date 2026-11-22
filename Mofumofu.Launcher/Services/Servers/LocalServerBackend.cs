using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MofuCore.Server;

namespace Mofumofu.Launcher.Services.Servers;

public sealed class LocalServerBackend : IServerBackend
{
    private static readonly string[] LocalCapabilities =
    [
        ServerCapability.Read, ServerCapability.Console, ServerCapability.Players,
        ServerCapability.Files, ServerCapability.Properties, ServerCapability.Backup,
        ServerCapability.Control, ServerCapability.Command, ServerCapability.Events,
    ];
    private static readonly string[] LocalScopes =
    [
        ServerScope.Read, ServerScope.Players, ServerScope.Files,
        ServerScope.Backup, ServerScope.Control, ServerScope.Command,
    ];

    private readonly string _id;
    private readonly string _name;
    private bool _disposed;

    public LocalServerBackend(ServerInfo info)
    {
        _id = info.Id;
        _name = info.Name;
        CurrentDescription = Describe(info, ReadMotd(info.Id));
        ServerManage.ConsoleLine += OnCoreConsoleLine;
    }

    public string BackendKey => "local:" + _id;
    public string InstanceId => _id;
    public bool IsLocal => true;
    public IReadOnlyList<string> Capabilities => LocalCapabilities;
    public IReadOnlyList<string> Scopes => LocalScopes;
    public ServerDescription CurrentDescription { get; private set; }

    public event EventHandler<ServerConsoleEventArgs>? ConsoleLine;
    public event EventHandler? ConsoleReset;

    public Task<ServerDescription> GetDescriptionAsync(CancellationToken cancellationToken = default)
    {
        ServerInfo info = ServerManage.GetServerInfo(_id)
            ?? throw new InvalidOperationException("服务器不存在");
        CurrentDescription = Describe(info, ReadMotd(_id));
        return Task.FromResult(CurrentDescription);
    }

    public Task<BackendStatus> GetStatusAsync(long cursor, CancellationToken cancellationToken = default)
    {
        ServerSnapshot snapshot = ServerManage.Poll(_id, cursor);
        ServerInfo? info = ServerManage.GetServerInfo(_id);
        return Task.FromResult(new BackendStatus
        {
            Status = snapshot.Status,
            Players = snapshot.Players,
            MaxPlayers = info?.MaxPlayers ?? 0,
            CpuPercent = snapshot.CpuPercent,
            MemoryMb = snapshot.MemoryMb,
            NextCursor = snapshot.NextCursor,
            LogLines = snapshot.LogLines,
        });
    }

    public async Task SendCommandAsync(string command, CancellationToken cancellationToken = default) =>
        await ServerPlayers.RunCommandAsync(_id, command).ConfigureAwait(false);

    public async Task<BackendOperation> StartAsync(CancellationToken cancellationToken = default)
    {
        await ServerManage.StartAsync(_id, cancellationToken).ConfigureAwait(false);
        return new BackendOperation();
    }

    public async Task<BackendOperation> StopAsync(CancellationToken cancellationToken = default)
    {
        await ServerManage.StopAsync(_id).ConfigureAwait(false);
        return new BackendOperation();
    }

    public async Task<BackendOperation> RestartAsync(CancellationToken cancellationToken = default)
    {
        await ServerManage.RestartAsync(_id, cancellationToken).ConfigureAwait(false);
        return new BackendOperation();
    }

    public async Task<BackendPlayers> GetPlayersAsync(CancellationToken cancellationToken = default) =>
        await Task.Run(() => MapPlayers(ServerPlayers.Get(_id)), cancellationToken);

    public async Task ModifyWhitelistAsync(string name, bool add, CancellationToken cancellationToken = default)
    {
        if (ServerManage.IsRunning(_id))
        {
            await ServerPlayers.RunCommandAsync(_id, $"whitelist {(add ? "add" : "remove")} {name}");
            return;
        }

        await Task.Run(() =>
        {
            List<WhitelistEntry> entries = ServerPlayers.Get(_id).Whitelist.ToList();
            WhitelistEntry? existing = entries.FirstOrDefault(entry =>
                string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
            if (add && existing is null) entries.Add(new WhitelistEntry { Name = name });
            if (!add && existing is not null) entries.Remove(existing);
            ServerPlayers.SaveWhitelist(_id, entries);
        }, cancellationToken);
    }

    public async Task ModifyOpAsync(string name, bool add, CancellationToken cancellationToken = default)
    {
        if (ServerManage.IsRunning(_id))
        {
            await ServerPlayers.RunCommandAsync(_id, add ? $"op {name}" : $"deop {name}");
            return;
        }

        await Task.Run(() =>
        {
            List<OpEntry> entries = ServerPlayers.Get(_id).Ops.ToList();
            OpEntry? existing = entries.FirstOrDefault(entry =>
                string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
            if (add && existing is null) entries.Add(new OpEntry { Name = name, Level = 4 });
            if (!add && existing is not null) entries.Remove(existing);
            ServerPlayers.SaveOps(_id, entries);
        }, cancellationToken);
    }

    public async Task ModifyBanAsync(string name, bool add, string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (ServerManage.IsRunning(_id))
        {
            string suffix = add && !string.IsNullOrWhiteSpace(reason) ? " " + reason : string.Empty;
            await ServerPlayers.RunCommandAsync(_id, $"{(add ? "ban" : "pardon")} {name}{suffix}");
            return;
        }

        await Task.Run(() =>
        {
            List<BannedPlayerEntry> entries = ServerPlayers.Get(_id).Banned.ToList();
            BannedPlayerEntry? existing = entries.FirstOrDefault(entry =>
                string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
            if (add && existing is null)
                entries.Add(new BannedPlayerEntry { Name = name, Reason = reason ?? "Banned by an operator." });
            if (!add && existing is not null) entries.Remove(existing);
            ServerPlayers.SaveBanned(_id, entries);
        }, cancellationToken);
    }

    public Task SetWhitelistEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ServerPlayers.SetWhitelistEnabled(_id, enabled);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BackendBackup>> GetBackupsAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => (IReadOnlyList<BackendBackup>)ServerBackup.List(_id).Select(MapBackup).ToList(), cancellationToken);

    public async Task<BackendBackup> CreateBackupAsync(CancellationToken cancellationToken = default) =>
        MapBackup(await ServerBackup.CreateAsync(_id, cancellationToken).ConfigureAwait(false));

    public async Task<BackendOperation> RestoreBackupAsync(string name, CancellationToken cancellationToken = default)
    {
        await ServerBackup.RestoreAsync(_id, name, cancellationToken).ConfigureAwait(false);
        return new BackendOperation();
    }

    public Task DeleteBackupAsync(string name, CancellationToken cancellationToken = default) =>
        Task.Run(() => ServerBackup.Delete(_id, name), cancellationToken);

    public Task<IReadOnlyList<BackendFile>> GetFilesAsync(string relativePath,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        string parent = NormalizeRelative(relativePath);
        var rows = ServerFiles.List(_id, parent).Select(entry =>
        {
            string relative = string.IsNullOrEmpty(parent) ? entry.Name : parent + "/" + entry.Name;
            return new BackendFile(entry.Name, relative, entry.IsDir, entry.Size,
                new DateTimeOffset(entry.ModTime, TimeSpan.Zero));
        }).ToList();
        return (IReadOnlyList<BackendFile>)rows;
    }, cancellationToken);

    public Task<BackendFileContent> ReadFileAsync(string relativePath,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        string normalized = NormalizeRelative(relativePath);
        string content = ServerFiles.ReadText(_id, normalized);
        return new BackendFileContent(normalized, content, Revision(content));
    }, cancellationToken);

    public Task<BackendOperation> WriteFileAsync(string relativePath, string content,
        string? expectedRevision = null, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        ServerFiles.WriteText(_id, NormalizeRelative(relativePath), content);
        return new BackendOperation();
    }, cancellationToken);

    public Task CreateTextFileAsync(string relativePath, CancellationToken cancellationToken = default) =>
        Task.Run(() => ServerFiles.CreateTextFile(_id, NormalizeRelative(relativePath)), cancellationToken);

    public Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default) =>
        Task.Run(() => ServerFiles.CreateDirectory(_id, NormalizeRelative(relativePath)), cancellationToken);

    public async Task<string> RenameFileAsync(string relativePath, string newName,
        CancellationToken cancellationToken = default) => NormalizeRelative(await Task.Run(
        () => ServerFiles.Rename(_id, NormalizeRelative(relativePath), newName), cancellationToken));

    public Task DeleteFileAsync(string relativePath, CancellationToken cancellationToken = default) =>
        Task.Run(() => ServerFiles.Delete(_id, NormalizeRelative(relativePath)), cancellationToken);

    public Task<BackendProperties> GetPropertiesAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var values = ServerProperties.Get(_id).Select(p => new BackendProperty(p.Key, p.Value)).ToList();
        string path = Path.Combine(ServerManage.ServerDirectory(_id), "server.properties");
        string revision = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks.ToString() : string.Empty;
        return new BackendProperties(values, revision);
    }, cancellationToken);

    public Task<BackendOperation> SetPropertiesAsync(IReadOnlyList<BackendProperty> values,
        string? expectedRevision = null, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        ServerProperties.Set(_id, values.Select(value => new ServerProperty
        {
            Key = value.Key,
            Value = value.Value,
        }));
        return new BackendOperation();
    }, cancellationToken);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ServerManage.ConsoleLine -= OnCoreConsoleLine;
    }

    private void OnCoreConsoleLine(string serverId, string line)
    {
        if (!string.Equals(serverId, _id, StringComparison.Ordinal)) return;
        long cursor = ServerManage.Poll(_id, long.MaxValue).NextCursor;
        ConsoleLine?.Invoke(this, new ServerConsoleEventArgs(line, cursor));
    }

    private static BackendPlayers MapPlayers(ServerPlayerSummary summary) => new()
    {
        Online = summary.Online.Select(name => new BackendPlayerEntry(name)).ToList(),
        Whitelist = summary.Whitelist.Select(entry => new BackendPlayerEntry(entry.Name, entry.Uuid)).ToList(),
        Ops = summary.Ops.Select(entry => new BackendPlayerEntry(entry.Name, entry.Uuid,
            entry.Level, entry.BypassesPlayerLimit)).ToList(),
        Banned = summary.Banned.Select(entry => new BackendPlayerEntry(entry.Name, entry.Uuid,
            Reason: entry.Reason, Expires: entry.Expires)).ToList(),
        WhitelistEnabled = summary.WhitelistEnabled,
        Running = summary.Running,
    };

    private static BackendBackup MapBackup(ServerBackupInfo info) => new(info.Name, info.SizeBytes,
        DateTimeOffset.FromUnixTimeSeconds(info.CreatedAt), info.Hot);

    private static ServerDescription Describe(ServerInfo info, string motd) => new(
        info.Id, info.Name, info.Core, info.CoreVersion, info.McVersion,
        $"127.0.0.1:{info.Port}", info.MaxPlayers, motd);

    private static string ReadMotd(string id)
    {
        try
        {
            return ServerProperties.Get(id).FirstOrDefault(property =>
                string.Equals(property.Key, "motd", StringComparison.OrdinalIgnoreCase))?.Value is { } value
                && !string.IsNullOrWhiteSpace(value) ? value : "—";
        }
        catch
        {
            return "—";
        }
    }

    private static string NormalizeRelative(string path) => path.Replace('\\', '/').Trim('/');

    private static string Revision(string content) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
