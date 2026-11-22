using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Mofumofu.Launcher.Services.Servers;

public interface IServerBackend : IDisposable
{
    string BackendKey { get; }
    string InstanceId { get; }
    bool IsLocal { get; }
    IReadOnlyList<string> Capabilities { get; }
    IReadOnlyList<string> Scopes { get; }

    event EventHandler<ServerConsoleEventArgs>? ConsoleLine;
    event EventHandler? ConsoleReset;

    Task<ServerDescription> GetDescriptionAsync(CancellationToken cancellationToken = default);
    Task<BackendStatus> GetStatusAsync(long cursor, CancellationToken cancellationToken = default);
    Task SendCommandAsync(string command, CancellationToken cancellationToken = default);
    Task<BackendOperation> StartAsync(CancellationToken cancellationToken = default);
    Task<BackendOperation> StopAsync(CancellationToken cancellationToken = default);
    Task<BackendOperation> RestartAsync(CancellationToken cancellationToken = default);

    Task<BackendPlayers> GetPlayersAsync(CancellationToken cancellationToken = default);
    Task ModifyWhitelistAsync(string name, bool add, CancellationToken cancellationToken = default);
    Task ModifyOpAsync(string name, bool add, CancellationToken cancellationToken = default);
    Task ModifyBanAsync(string name, bool add, string? reason = null, CancellationToken cancellationToken = default);
    Task SetWhitelistEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackendBackup>> GetBackupsAsync(CancellationToken cancellationToken = default);
    Task<BackendBackup> CreateBackupAsync(CancellationToken cancellationToken = default);
    Task<BackendOperation> RestoreBackupAsync(string name, CancellationToken cancellationToken = default);
    Task DeleteBackupAsync(string name, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackendFile>> GetFilesAsync(string relativePath, CancellationToken cancellationToken = default);
    Task<BackendFileContent> ReadFileAsync(string relativePath, CancellationToken cancellationToken = default);
    Task<BackendOperation> WriteFileAsync(string relativePath, string content, string? expectedRevision = null, CancellationToken cancellationToken = default);
    Task CreateTextFileAsync(string relativePath, CancellationToken cancellationToken = default);
    Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default);
    Task<string> RenameFileAsync(string relativePath, string newName, CancellationToken cancellationToken = default);
    Task DeleteFileAsync(string relativePath, CancellationToken cancellationToken = default);

    Task<BackendProperties> GetPropertiesAsync(CancellationToken cancellationToken = default);
    Task<BackendOperation> SetPropertiesAsync(IReadOnlyList<BackendProperty> values, string? expectedRevision = null, CancellationToken cancellationToken = default);
}
