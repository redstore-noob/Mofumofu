using System.Security.Cryptography;
using System.Text.Json;
using MofuCore.Server;
using Mofumofu.RemoteProtocol;
using Mofumofu.ServerAgent.Core;
using Mofumofu.ServerAgent.Operations;

namespace Mofumofu.ServerAgent.Services;

public sealed class BackupService
{
    private readonly AgentOptions _options;
    private readonly ICoreGateway _core;
    private readonly OperationService _operations;
    private readonly Dictionary<string, HashCacheEntry> _hashCache = new(StringComparer.Ordinal);

    public BackupService(AgentOptions options, ICoreGateway core, OperationService operations)
    {
        _options = options;
        _core = core;
        _operations = operations;
    }

    public async Task<BackupListResponse> ListAsync(CancellationToken cancellationToken)
    {
        List<BackupMetadata> backups = [];
        foreach (ServerBackupInfo backup in _core.ListBackups(_options.InstanceId))
        {
            try
            {
                backups.Add(await MetadataAsync(backup, cancellationToken));
            }
            catch (IOException)
            {
                // A concurrent prune may remove an item after Core lists it.
            }
        }

        return new BackupListResponse { Backups = backups };
    }

    public async Task<BackupCreateResponse> CreateAsync(BackupCreateRequest request,
        CancellationToken cancellationToken)
    {
        ValidateText(request.Label, "label", 128, required: true);
        ValidateText(request.Description, "description", 1024, required: false);
        BackupMetadata? created = null;
        OperationInfo operation = await _operations.RunAsync("backup.create", ProtocolScopes.Backup,
            async token =>
            {
                ServerBackupInfo backup = await _core.CreateBackupAsync(_options.InstanceId, token);
                WriteDescription(backup.Path, request.Label, request.Description);
                created = await MetadataAsync(backup, token);
                return new OperationCompletion("Backup created.", new Dictionary<string, string>
                {
                    ["backupId"] = created.Id,
                    ["sizeBytes"] = created.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["sha256"] = created.Sha256,
                });
            });
        if (operation.State == ProtocolOperationStates.Failed || created is null)
        {
            throw new ApiException(StatusCodes.Status500InternalServerError, ProtocolErrors.OperationFailed,
                "The backup operation failed.", operationId: operation.Id);
        }

        return new BackupCreateResponse { Backup = created, Operation = operation };
    }

    public BackupMetadata Get(string backupId)
    {
        ValidateBackupId(backupId);
        ServerBackupInfo backup = _core.ListBackups(_options.InstanceId).FirstOrDefault(item =>
            string.Equals(item.Name, backupId, StringComparison.Ordinal))
            ?? throw ApiException.NotFound("The backup was not found.");
        return MetadataAsync(backup, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Delete(string backupId, string expectedSha256)
    {
        BackupMetadata backup = Get(backupId);
        EnsureRevision(backup, expectedSha256);
        _core.DeleteBackup(_options.InstanceId, backup.Id);
        string sidecar = SidecarPath(backupId);
        if (File.Exists(sidecar))
        {
            File.Delete(sidecar);
        }
    }

    public async Task<OperationInfo> RestoreAsync(string backupId, string expectedSha256,
        CancellationToken cancellationToken)
    {
        return await _operations.StartAsync("backup.restore", ProtocolScopes.Backup, async token =>
        {
            BackupMetadata backup = Get(backupId);
            EnsureRevision(backup, expectedSha256);
            if (_core.IsRunning(_options.InstanceId))
            {
                throw new ApiException(StatusCodes.Status409Conflict, ProtocolErrors.ServerRunning,
                    "The server must remain stopped throughout backup restoration.");
            }

            await _core.RestoreBackupAsync(_options.InstanceId, backup.Id, token);
            return new OperationCompletion("Backup restored; Core created a safety backup first.");
        });
    }

    private static void EnsureRevision(BackupMetadata backup, string expectedSha256)
    {
        if (!string.Equals(backup.Sha256, expectedSha256, StringComparison.Ordinal))
        {
            throw ApiException.RevisionConflict(expectedSha256);
        }
    }

    private async Task<BackupMetadata> MetadataAsync(ServerBackupInfo backup, CancellationToken cancellationToken)
    {
        FileInfo file = new(backup.Path);
        if (!file.Exists)
        {
            throw new IOException("The backup no longer exists.");
        }

        string hash;
        HashCacheEntry cacheKey = new(file.Length, file.LastWriteTimeUtc.Ticks, string.Empty);
        lock (_hashCache)
        {
            if (_hashCache.TryGetValue(backup.Name, out HashCacheEntry? cached) &&
                cached.Size == cacheKey.Size && cached.TimestampTicks == cacheKey.TimestampTicks)
            {
                cacheKey = cached;
            }
        }

        if (cacheKey.Hash.Length > 0)
        {
            hash = cacheKey.Hash;
        }
        else
        {
            await using FileStream stream = new(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
            lock (_hashCache)
            {
                _hashCache[backup.Name] = new HashCacheEntry(file.Length, file.LastWriteTimeUtc.Ticks, hash);
            }
        }

        BackupDescription? description = ReadDescription(backup.Name);
        ServerInfo? info = _core.GetServerInfo(_options.InstanceId);
        return new BackupMetadata
        {
            Id = backup.Name,
            Label = description?.Label ?? Path.GetFileNameWithoutExtension(backup.Name),
            Description = description?.Description,
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(backup.CreatedAt),
            SizeBytes = backup.SizeBytes,
            Sha256 = hash,
            MinecraftVersion = info?.McVersion,
        };
    }

    private void WriteDescription(string backupPath, string label, string? description)
    {
        string path = backupPath + ".agent.json";
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new BackupDescription(label, description),
            ProtocolJson.Options);
        File.WriteAllBytes(temporary, payload);
        File.Move(temporary, path, overwrite: true);
    }

    private BackupDescription? ReadDescription(string backupId)
    {
        try
        {
            string path = SidecarPath(backupId);
            if (!File.Exists(path) || new FileInfo(path).Length > 4096)
            {
                return null;
            }

            return JsonSerializer.Deserialize<BackupDescription>(File.ReadAllText(path), ProtocolJson.Options);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string SidecarPath(string backupId) => Path.Combine(_options.InstanceDirectory, "backups",
        Path.GetFileName(backupId) + ".agent.json");

    private static void ValidateBackupId(string? backupId)
    {
        if (string.IsNullOrWhiteSpace(backupId) || backupId.Length > 255 ||
            backupId.Contains("..", StringComparison.Ordinal) || backupId.Contains('/') ||
            backupId.Contains('\\') || !backupId.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(backupId) != backupId)
        {
            throw ApiException.InvalidRequest("The backup ID is invalid.");
        }
    }

    private static void ValidateText(string? value, string name, int maximum, bool required)
    {
        if (required && string.IsNullOrWhiteSpace(value) || value is not null &&
            (value.Length > maximum || value.Any(char.IsControl)))
        {
            throw ApiException.InvalidRequest($"The backup {name} is invalid.");
        }
    }

    private sealed record BackupDescription(string Label, string? Description);
    private sealed record HashCacheEntry(long Size, long TimestampTicks, string Hash);
}
