using System.Text.Json;

namespace MofuCore.Download.Modrinth;

public sealed record ModFileEntry(string Path, string Sha1)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

public sealed class ModrinthUpdateCandidate
{
    public string FilePath { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    public string CurrentSha1 { get; init; } = string.Empty;

    public string ProjectId { get; init; } = string.Empty;

    public string ProjectTitle { get; init; } = string.Empty;

    public string ProjectIcon { get; init; } = string.Empty;

    public string CurrentVersion { get; init; } = string.Empty;

    public string LatestVersionId { get; init; } = string.Empty;

    public string LatestVersionNumber { get; init; } = string.Empty;

    public string LatestFileName { get; init; } = string.Empty;

    public string DownloadUrl { get; init; } = string.Empty;

    public long DownloadSize { get; init; }

    public string DatePublished { get; init; } = string.Empty;

    public string DisplayTitle => string.IsNullOrWhiteSpace(ProjectTitle) ? ProjectId : ProjectTitle;

    public string VersionTransition => string.IsNullOrWhiteSpace(CurrentVersion)
        ? LatestVersionNumber
        : $"{CurrentVersion} → {LatestVersionNumber}";

    public string SizeDisplay => DownloadSize switch
    {
        >= 1048576 => $"{DownloadSize / 1048576.0:0.0} MB",
        >= 1024 => $"{DownloadSize / 1024.0:0} KB",
        _ => $"{DownloadSize} B",
    };
}

public static class ModrinthUpdateChecker
{
    private const int ProjectBatchSize = 50;

    public static string Sha1OfFile(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            byte[] hash = System.Security.Cryptography.SHA1.HashData(stream);
            return Convert.ToHexStringLower(hash);
        }
        catch
        {
            return string.Empty;
        }
    }

    public static string? NormalizeLoader(string? loader) => (loader ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "fabric" => "fabric",
        "quilt" => "quilt",
        "forge" => "forge",
        "neoforge" => "neoforge",
        "liteloader" => "liteloader",
        _ => null,
    };

    public static async Task<IReadOnlyList<ModrinthUpdateCandidate>> CheckAsync(
        IReadOnlyList<ModFileEntry> files,
        string? gameVersion,
        string? loader,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (files.Count == 0) return [];

        var byHash = files
            .Where(file => !string.IsNullOrWhiteSpace(file.Sha1))
            .GroupBy(file => file.Sha1, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        if (byHash.Count == 0) return [];

        progress?.Report("正在反查文件归属…");

        IReadOnlyDictionary<string, ModrinthVersionFileMatch> matches;
        try
        {
            matches = await ModrinthResourceDownload
                .GetVersionFilesByHashesAsync(byHash.Keys, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new InvalidOperationException("查询 Modrinth 文件信息失败：" + e.Message, e);
        }

        if (matches.Count == 0) return [];

        progress?.Report($"正在检查 {matches.Count} 个文件的更新…");

        string? normalizedLoader = NormalizeLoader(loader);
        string? versionFilter = string.IsNullOrWhiteSpace(gameVersion) ? null : gameVersion.Trim();

        IReadOnlyDictionary<string, ModrinthVersionFileUpdate> updates;
        try
        {
            updates = await ModrinthResourceDownload.GetLatestVersionFilesByHashesAsync(
                    matches.Keys,
                    versionFilter is null ? null : [versionFilter],
                    normalizedLoader is null ? null : [normalizedLoader],
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new InvalidOperationException("查询 Modrinth 最新版本失败：" + e.Message, e);
        }

        var candidates = new List<ModrinthUpdateCandidate>();

        foreach (var (hash, update) in updates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!byHash.TryGetValue(hash, out var localFiles)) continue;
            matches.TryGetValue(hash, out var current);

            if (current is null) continue;

            if (string.Equals(update.VersionId, current.VersionId, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var local in localFiles)
            {
                candidates.Add(new ModrinthUpdateCandidate
                {
                    FilePath = local.Path,
                    FileName = local.FileName,
                    CurrentSha1 = hash,
                    ProjectId = string.IsNullOrWhiteSpace(update.ProjectId)
                        ? current?.ProjectId ?? string.Empty
                        : update.ProjectId,
                    CurrentVersion = current?.VersionNumber ?? string.Empty,
                    LatestVersionId = update.VersionId,
                    LatestVersionNumber = update.VersionNumber,
                    LatestFileName = update.File.Filename,
                    DownloadUrl = update.File.Url,
                    DownloadSize = update.File.Size,
                    DatePublished = update.DatePublished,
                });
            }
        }

        if (candidates.Count == 0) return [];

        progress?.Report("正在读取项目信息…");
        var projects = await LoadProjectsAsync(
                candidates.Select(candidate => candidate.ProjectId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
                cancellationToken)
            .ConfigureAwait(false);

        return candidates
            .Select(candidate => projects.TryGetValue(candidate.ProjectId, out var project)
                ? WithProject(candidate, project)
                : candidate)
            .OrderBy(candidate => candidate.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static async Task<Dictionary<string, ModrinthProject>> LoadProjectsAsync(
        IReadOnlyList<string> projectIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, ModrinthProject>(StringComparer.Ordinal);
        if (projectIds.Count == 0) return result;

        for (int start = 0; start < projectIds.Count; start += ProjectBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = projectIds.Skip(start).Take(ProjectBatchSize).ToList();
            try
            {
                string ids = Uri.EscapeDataString(JsonSerializer.Serialize(batch));
                var projects = await ModrinthApiClient
                    .GetJsonAsync<List<ModrinthProject>>($"/projects?ids={ids}", cancellationToken)
                    .ConfigureAwait(false);

                foreach (var project in projects)
                {
                    if (!string.IsNullOrWhiteSpace(project.ProjectId)) result[project.ProjectId] = project;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }
        }

        return result;
    }

    private static ModrinthUpdateCandidate WithProject(ModrinthUpdateCandidate candidate, ModrinthProject project) =>
        new()
        {
            FilePath = candidate.FilePath,
            FileName = candidate.FileName,
            CurrentSha1 = candidate.CurrentSha1,
            ProjectId = candidate.ProjectId,
            ProjectTitle = project.Title,
            ProjectIcon = project.IconUrl,
            CurrentVersion = candidate.CurrentVersion,
            LatestVersionId = candidate.LatestVersionId,
            LatestVersionNumber = candidate.LatestVersionNumber,
            LatestFileName = candidate.LatestFileName,
            DownloadUrl = candidate.DownloadUrl,
            DownloadSize = candidate.DownloadSize,
            DatePublished = candidate.DatePublished,
        };

    public static async Task<string> ApplyUpdateAsync(
        ModrinthUpdateCandidate candidate,
        string modsDirectory,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (string.IsNullOrWhiteSpace(modsDirectory)) throw new InvalidOperationException("modsDirectory 不能为空。");
        if (string.IsNullOrWhiteSpace(candidate.DownloadUrl)) throw new InvalidOperationException("这条更新没有下载地址。");

        Directory.CreateDirectory(modsDirectory);

        string targetName = SanitizeFileName(
            string.IsNullOrWhiteSpace(candidate.LatestFileName) ? candidate.FileName : candidate.LatestFileName);
        string targetPath = Path.Combine(modsDirectory, targetName);
        string tempPath = targetPath + ".mofu-download";

        string? backupPath = null;
        if (File.Exists(candidate.FilePath))
        {
            string backupDirectory = Path.Combine(
                modsDirectory,
                ".mofu-backup",
                DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));

            Directory.CreateDirectory(backupDirectory);
            backupPath = Path.Combine(backupDirectory, Path.GetFileName(candidate.FilePath));
            File.Move(candidate.FilePath, backupPath, overwrite: true);
        }

        try
        {
            await ModrinthApiClient
                .DownloadToFileAsync(candidate.DownloadUrl, tempPath, progress, cancellationToken)
                .ConfigureAwait(false);

            if (File.Exists(targetPath)) File.Delete(targetPath);
            File.Move(tempPath, targetPath);
            return targetPath;
        }
        catch
        {
            TryDelete(tempPath);

            if (backupPath is not null && File.Exists(backupPath) && !File.Exists(candidate.FilePath))
            {
                try
                {
                    File.Move(backupPath, candidate.FilePath);
                }
                catch
                {
                }
            }

            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    private static string SanitizeFileName(string name)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) return "mod.jar";

        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            trimmed = trimmed.Replace(invalid, '_');
        }

        trimmed = trimmed.Replace('/', '_').Replace('\\', '_');

        return trimmed is "." or ".." ? "mod.jar" : trimmed;
    }
}
