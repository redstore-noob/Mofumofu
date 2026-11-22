using System.Security.Cryptography;
using System.Text;
using Mofumofu.RemoteProtocol;

namespace Mofumofu.ServerAgent.Files;

public sealed record SafeFileContent(
    string Path,
    string Content,
    string Revision,
    long SizeBytes,
    DateTimeOffset ModifiedAt);

public sealed record PropertyDocument(string Content, string Revision);

public sealed record SafeFileMutation(
    string Path,
    string Revision,
    bool IsDirectory,
    DateTimeOffset ModifiedAt);

public sealed class SafeFileService
{
    public const string MissingRevision = "0";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> BlockedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "auth", "audit", "backups", "logs", ".mofumofu-agent",
    };
    private static readonly HashSet<string> BlockedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "server.json", "server.properties", "eula.txt", "whitelist.json", "ops.json",
        "banned-players.json", "banned-ips.json", "usercache.json", "usernamecache.json",
    };
    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mca", ".mcc", ".dat", ".dat_old", ".db", ".zip", ".jar", ".dll", ".exe",
    };

    private readonly AgentOptions _options;
    private readonly string _root;
    private readonly SemaphoreSlim _sync = new(1, 1);

    public SafeFileService(AgentOptions options)
    {
        _options = options;
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.InstanceDirectory));
    }

    public string NormalizeRelativePath(string? path, bool allowEmpty = false)
    {
        if (string.IsNullOrEmpty(path))
        {
            if (allowEmpty)
            {
                return string.Empty;
            }

            throw InvalidPath();
        }

        if (path.IndexOf('\0') >= 0 || path.Any(char.IsControl) || path.Contains('\\') ||
            path.StartsWith('/') || Path.IsPathRooted(path) || path.Contains(':') ||
            path.StartsWith("//", StringComparison.Ordinal))
        {
            throw InvalidPath();
        }

        string[] segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." ||
            segment.EndsWith(' ') || segment.EndsWith('.')))
        {
            throw InvalidPath();
        }

        string normalized = string.Join('/', segments);
        if (!allowEmpty && normalized.Length == 0)
        {
            throw InvalidPath();
        }

        return normalized;
    }

    public async Task<FileListResponse> ListAsync(string? path, CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            string relative = NormalizeRelativePath(path, allowEmpty: true);
            string full = Resolve(relative, allowProtected: false);
            if (!Directory.Exists(full))
            {
                throw ApiException.NotFound("The directory was not found.");
            }

            List<FileEntry> entries = [];
            foreach (string child in Directory.EnumerateFileSystemEntries(full)
                .OrderBy(value => value, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = Path.GetFileName(child);
                string childRelative = relative.Length == 0 ? name : $"{relative}/{name}";
                FileSystemInfo info = File.Exists(child) ? new FileInfo(child) : new DirectoryInfo(child);
                if (!info.Exists || IsReparsePoint(info))
                {
                    continue;
                }

                try
                {
                    _ = Resolve(childRelative, allowProtected: false);
                }
                catch (ApiException)
                {
                    continue;
                }

                if (info is FileInfo file)
                {
                    if (file.Length > AgentOptions.MaxTextBytes)
                    {
                        continue;
                    }

                    entries.Add(new FileEntry
                    {
                        Path = childRelative,
                        IsDirectory = false,
                        SizeBytes = file.Length,
                        ModifiedAt = file.LastWriteTimeUtc,
                        Revision = await ComputeRevisionAsync(file.FullName, file.Length,
                            file.LastWriteTimeUtc, cancellationToken),
                        Writable = true,
                    });
                }
                else
                {
                    entries.Add(new FileEntry
                    {
                        Path = childRelative,
                        IsDirectory = true,
                        ModifiedAt = info.LastWriteTimeUtc,
                        Writable = false,
                    });
                }

                if (entries.Count >= 1000)
                {
                    break;
                }
            }

            return new FileListResponse { Path = relative, Entries = entries };
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<SafeFileMutation> GetItemAsync(string path, CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            string relative = NormalizeRelativePath(path);
            return await GetItemCoreAsync(relative, cancellationToken);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<SafeFileMutation> CreateAsync(string path, bool isDirectory,
        CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            string relative = NormalizeRelativePath(path);
            string full = Resolve(relative, allowProtected: false);
            if (File.Exists(full) || Directory.Exists(full))
            {
                throw ApiException.Conflict("The file or directory already exists.");
            }

            string? parent = Path.GetDirectoryName(full);
            if (parent is null || !Directory.Exists(parent))
            {
                throw ApiException.NotFound("The parent directory was not found.");
            }

            RecheckPath(relative, full);
            if (isDirectory)
            {
                Directory.CreateDirectory(full);
            }
            else
            {
                await using FileStream stream = new(full, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            return await GetItemCoreAsync(relative, cancellationToken);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<SafeFileMutation> RenameAsync(string path, string newName, string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            string sourceRelative = NormalizeRelativePath(path);
            if (string.IsNullOrEmpty(newName) || newName.Contains('/') || newName.Contains('\\'))
            {
                throw InvalidPath();
            }

            string leaf = NormalizeRelativePath(newName);
            string? parent = sourceRelative.Contains('/') ? Path.GetDirectoryName(sourceRelative)?.Replace('\\', '/') : string.Empty;
            string destinationRelative = string.IsNullOrEmpty(parent) ? leaf : $"{parent}/{leaf}";
            string source = Resolve(sourceRelative, allowProtected: false);
            string destination = Resolve(destinationRelative, allowProtected: false);
            SafeFileMutation current = await GetItemCoreAsync(sourceRelative, cancellationToken);
            EnsureRevision(current.Revision, expectedRevision);
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                throw ApiException.Conflict("The rename destination already exists.");
            }

            RecheckPath(sourceRelative, source);
            RecheckPath(destinationRelative, destination);
            if (current.IsDirectory)
            {
                Directory.Move(source, destination);
            }
            else
            {
                File.Move(source, destination);
            }

            return await GetItemCoreAsync(destinationRelative, cancellationToken);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<SafeFileContent> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            string relative = NormalizeRelativePath(path);
            return await ReadCoreAsync(relative, allowProtected: false, cancellationToken);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<SafeFileContent> WriteAsync(string path, string content, string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            string relative = NormalizeRelativePath(path);
            return await WriteCoreAsync(relative, content, expectedRevision, allowProtected: false,
                cancellationToken);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task DeleteAsync(string path, string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            string relative = NormalizeRelativePath(path);
            string full = Resolve(relative, allowProtected: false);
            SafeFileMutation current = await GetItemCoreAsync(relative, cancellationToken);
            EnsureRevision(current.Revision, expectedRevision);
            RecheckPath(relative, full);
            if (!current.IsDirectory)
            {
                File.Delete(full);
                return;
            }

            string latestRevision = ComputeDirectoryRevision(relative, full, CancellationToken.None);
            EnsureRevision(latestRevision, expectedRevision);
            DeleteDirectoryTree(relative, full, cancellationToken);
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task<SafeFileMutation> GetItemCoreAsync(string relative, CancellationToken cancellationToken)
    {
        string full = Resolve(relative, allowProtected: false);
        if (File.Exists(full))
        {
            SafeFileContent content = await ReadCoreAsync(relative, allowProtected: false, cancellationToken);
            return new SafeFileMutation(relative, content.Revision, false, content.ModifiedAt);
        }

        if (Directory.Exists(full))
        {
            RecheckPath(relative, full);
            return new SafeFileMutation(relative, ComputeDirectoryRevision(relative, full, cancellationToken),
                true, Directory.GetLastWriteTimeUtc(full));
        }

        throw ApiException.NotFound("The file or directory was not found.");
    }

    private string ComputeDirectoryRevision(string relative, string full, CancellationToken cancellationToken)
    {
        StringBuilder fingerprint = new();
        int count = 0;
        Visit(relative, full, isRoot: true);
        return "d_" + Convert.ToHexStringLower(SHA256.HashData(StrictUtf8.GetBytes(fingerprint.ToString())));

        void Visit(string currentRelative, string currentFull, bool isRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > 10_000 || IsReparsePoint(new DirectoryInfo(currentFull)))
            {
                throw isRoot && count <= 10_000 ? InvalidPath() : PayloadTooLarge();
            }

            DirectoryInfo directory = new(currentFull);
            fingerprint.Append(currentRelative).Append('|').Append(directory.LastWriteTimeUtc.Ticks).Append('\n');
            foreach (string child in Directory.EnumerateFileSystemEntries(currentFull).OrderBy(value => value,
                StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = Path.GetFileName(child);
                string childRelative = $"{currentRelative}/{name}";
                FileSystemInfo info = Directory.Exists(child) ? new DirectoryInfo(child) : new FileInfo(child);
                if (IsReparsePoint(info) || IsProtectedPath(childRelative, child))
                {
                    throw InvalidPath();
                }

                if (++count > 10_000)
                {
                    throw PayloadTooLarge();
                }

                fingerprint.Append(childRelative).Append('|');
                if (info is FileInfo file)
                {
                    fingerprint.Append('f').Append('|').Append(file.Length).Append('|')
                        .Append(file.LastWriteTimeUtc.Ticks).Append('\n');
                }
                else
                {
                    fingerprint.Append('d').Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
                    Visit(childRelative, child, isRoot: false);
                }
            }
        }
    }

    private void DeleteDirectoryTree(string relative, string full, CancellationToken cancellationToken)
    {
        RecheckPath(relative, full);
        foreach (string child in Directory.EnumerateFileSystemEntries(full))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileName(child);
            string childRelative = $"{relative}/{name}";
            FileSystemInfo info = Directory.Exists(child) ? new DirectoryInfo(child) : new FileInfo(child);
            if (IsReparsePoint(info) || IsProtectedPath(childRelative, child))
            {
                throw InvalidPath();
            }

            if (info is DirectoryInfo)
            {
                DeleteDirectoryTree(childRelative, child, cancellationToken);
            }
            else
            {
                File.Delete(child);
            }
        }

        RecheckPath(relative, full);
        Directory.Delete(full, recursive: false);
    }

    internal async Task<PropertyDocument> ReadPropertiesAsync(CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            SafeFileContent content = await ReadCoreAsync("server.properties", allowProtected: true,
                cancellationToken);
            return new PropertyDocument(content.Content, content.Revision);
        }
        finally
        {
            _sync.Release();
        }
    }

    internal async Task<string> WritePropertiesAsync(string content, string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            SafeFileContent result = await WriteCoreAsync("server.properties", content, expectedRevision,
                allowProtected: true, cancellationToken);
            return result.Revision;
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task<SafeFileContent> ReadCoreAsync(string relative, bool allowProtected,
        CancellationToken cancellationToken)
    {
        string full = Resolve(relative, allowProtected);
        if (!File.Exists(full))
        {
            throw ApiException.NotFound("The file was not found.");
        }

        RecheckPath(relative, full);
        await using FileStream stream = new(full, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > AgentOptions.MaxTextBytes)
        {
            throw PayloadTooLarge();
        }

        byte[] bytes = new byte[stream.Length];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                throw new IOException("The file changed while it was being read.");
            }

            offset += read;
        }

        RecheckPath(relative, full);
        DateTimeOffset modified = File.GetLastWriteTimeUtc(full);
        string content;
        try
        {
            content = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw ApiException.InvalidRequest("The file is not valid UTF-8 text.");
        }

        return new SafeFileContent(relative, content, ComputeRevision(bytes, modified), bytes.LongLength, modified);
    }

    private async Task<SafeFileContent> WriteCoreAsync(string relative, string content, string expectedRevision,
        bool allowProtected, CancellationToken cancellationToken)
    {
        string full = Resolve(relative, allowProtected);
        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(content ?? string.Empty);
        }
        catch (EncoderFallbackException)
        {
            throw ApiException.InvalidRequest("The content is not valid UTF-8 text.");
        }

        if (bytes.LongLength > AgentOptions.MaxTextBytes)
        {
            throw PayloadTooLarge();
        }

        string currentRevision = MissingRevision;
        if (File.Exists(full))
        {
            SafeFileContent current = await ReadCoreAsync(relative, allowProtected, cancellationToken);
            currentRevision = current.Revision;
        }
        else if (Directory.Exists(full))
        {
            throw InvalidPath();
        }

        EnsureRevision(currentRevision, expectedRevision);
        string? directory = Path.GetDirectoryName(full);
        if (directory is null || !Directory.Exists(directory))
        {
            throw ApiException.NotFound("The parent directory was not found.");
        }

        string temporary = Path.Combine(directory, $".mofu-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            RecheckPath(relative, full);
            string latestRevision = File.Exists(full)
                ? (await ReadCoreAsync(relative, allowProtected, cancellationToken)).Revision
                : MissingRevision;
            EnsureRevision(latestRevision, expectedRevision);
            File.Move(temporary, full, overwrite: true);
            RecheckPath(relative, full);
            return await ReadCoreAsync(relative, allowProtected, cancellationToken);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch
            {
                // The original write error is more useful than a temporary-file cleanup error.
            }
        }
    }

    private string Resolve(string relative, bool allowProtected)
    {
        if (!Directory.Exists(_root))
        {
            throw ApiException.NotFound("The server instance no longer exists.");
        }

        DirectoryInfo rootInfo = new(_root);
        if (IsReparsePoint(rootInfo))
        {
            throw InvalidPath();
        }

        string full = relative.Length == 0 ? _root : Path.GetFullPath(Path.Combine(_root,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        string rootPrefix = _root + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!full.Equals(_root, comparison) && !full.StartsWith(rootPrefix, comparison))
        {
            throw InvalidPath();
        }

        if (!allowProtected && IsProtectedPath(relative, full))
        {
            throw InvalidPath();
        }

        RecheckPath(relative, full);
        return full;
    }

    private void RecheckPath(string relative, string full)
    {
        if (IsReparsePoint(new DirectoryInfo(_root)))
        {
            throw InvalidPath();
        }

        string current = _root;
        foreach (string segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo? info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current) ? new FileInfo(current) : null;
            if (info is not null && info.Exists && IsReparsePoint(info))
            {
                throw InvalidPath();
            }
        }

        string expected = relative.Length == 0 ? _root : Path.GetFullPath(Path.Combine(_root,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!expected.Equals(full, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw InvalidPath();
        }
    }

    private bool IsProtectedPath(string relative, string full)
    {
        if (relative.Length == 0)
        {
            return false;
        }

        string[] segments = relative.Split('/');
        if (segments.Any(segment => BlockedDirectories.Contains(segment) ||
            segment.Equals("world", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("world_", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        string name = segments[^1];
        if (BlockedFiles.Contains(name) || BlockedExtensions.Contains(Path.GetExtension(name)) ||
            name.Equals("level.dat_old", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return full.Equals(_options.ProbeRegistryPath, comparison) ||
               full.Equals(_options.AuditPath, comparison) ||
               full.Equals(_options.EventJournalPath, comparison);
    }

    private static bool IsReparsePoint(FileSystemInfo info)
    {
        try
        {
            return info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return true;
        }
    }

    private static async Task<string> ComputeRevisionAsync(string path, long length, DateTimeOffset modified,
        CancellationToken cancellationToken)
    {
        if (length > AgentOptions.MaxTextBytes)
        {
            throw PayloadTooLarge();
        }

        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return ComputeRevision(bytes, modified);
    }

    private static string ComputeRevision(byte[] bytes, DateTimeOffset modified)
    {
        byte[] timestamp = BitConverter.GetBytes(modified.UtcTicks);
        byte[] combined = new byte[bytes.Length + timestamp.Length];
        Buffer.BlockCopy(bytes, 0, combined, 0, bytes.Length);
        Buffer.BlockCopy(timestamp, 0, combined, bytes.Length, timestamp.Length);
        return "r_" + Convert.ToHexStringLower(SHA256.HashData(combined));
    }

    private static void EnsureRevision(string actual, string expected)
    {
        string wanted = string.IsNullOrEmpty(expected) ? MissingRevision : expected;
        if (!string.Equals(actual, wanted, StringComparison.Ordinal))
        {
            throw ApiException.RevisionConflict(wanted);
        }
    }

    private static ApiException InvalidPath() =>
        new(StatusCodes.Status400BadRequest, ProtocolErrors.InvalidPath,
            "The path is outside the safe text-file allowlist or contains an unsafe segment.");

    private static ApiException PayloadTooLarge() =>
        new(StatusCodes.Status413PayloadTooLarge, ProtocolErrors.PayloadTooLarge,
            "The UTF-8 text file exceeds the 2 MiB limit.");
}
