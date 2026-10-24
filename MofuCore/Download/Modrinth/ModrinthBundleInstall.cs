using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using MofuCore.Download.Minecraft;

namespace MofuCore.Download.Modrinth;

/// <summary>整合包声明的 Mod Loader 类型。</summary>
public enum ModpackLoader
{
    Vanilla,
    Fabric,
    Quilt,
    NeoForge,
    Forge,
    OptiFine,
}

/// <summary>整合包清单声明的运行要求（来自 modrinth.index.json 的 dependencies）。</summary>
public class ModpackRequirements
{
    public string MinecraftVersion { get; set; } = string.Empty;

    public ModpackLoader Loader { get; set; }

    public string LoaderVersion { get; set; } = string.Empty;

    /// <summary>原始加载器依赖键（如 fabric-loader / quilt-loader / neoforge / forge）。</summary>
    public string RawLoaderKey { get; set; } = string.Empty;

    public string LoaderName => ModpackLoaders.DisplayName(Loader);

    public bool LoaderSupported => Loader is ModpackLoader.Fabric or ModpackLoader.Quilt
        or ModpackLoader.Forge or ModpackLoader.NeoForge;
}

/// <summary>加载器展示名。</summary>
public static class ModpackLoaders
{
    public static string DisplayName(ModpackLoader loader) => loader switch
    {
        ModpackLoader.Fabric => "Fabric",
        ModpackLoader.Quilt => "Quilt",
        ModpackLoader.NeoForge => "NeoForge",
        ModpackLoader.Forge => "Forge",
        ModpackLoader.OptiFine => "OptiFine",
        _ => "Vanilla",
    };
}

/// <summary>整合包安装选项。</summary>
public class ModpackInstallOptions
{
    /// <summary>解压完成后是否自动下载并安装清单声明的加载器。</summary>
    public bool InstallLoader { get; set; } = true;

    /// <summary>依赖文件并发下载数。</summary>
    public int ParallelDownloads { get; set; } = 16;

    /// <summary>单个文件下载失败后的最大重试次数。</summary>
    public int MaximumRetries { get; set; } = 2;

    /// <summary>运行 Forge/NeoForge 安装器用的 Java（为空时自动探测）。</summary>
    public string JavaExecutable { get; set; } = string.Empty;
}

/// <summary>整合包安装进度（阶段 + 字节 / 文件计数）。</summary>
public sealed record ModpackInstallProgress(
    string Phase,
    long CompletedBytes,
    long TotalBytes,
    int CompletedFiles,
    int TotalFiles)
{
    public double Percentage
    {
        get
        {
            if (TotalBytes <= 0) return 0;
            return Math.Clamp(CompletedBytes * 100.0 / TotalBytes, 0, 100);
        }
    }
}

/// <summary>整合包安装结果。</summary>
public class ModpackInstallResult
{
    /// <summary>识别出的清单格式（当前恒为 modrinth）。</summary>
    public string DetectedFormat { get; set; } = "modrinth";

    /// <summary>从包内解压到实例目录的文件数。</summary>
    public int InstalledFiles { get; set; }

    /// <summary>按清单声明下载的依赖文件数。</summary>
    public int DownloadedFiles { get; set; }

    /// <summary>本地已存在且哈希相符而跳过的依赖数。</summary>
    public int ReusedFiles { get; set; }

    public List<string> Errors { get; } = [];

    public List<string> Warnings { get; } = [];

    public string DeclaredMinecraftVersion { get; set; } = string.Empty;

    public string DeclaredLoaderName { get; set; } = string.Empty;

    public string DeclaredLoaderVersion { get; set; } = string.Empty;

    public bool DeclaredLoaderSupported { get; set; } = true;

    /// <summary>自动安装成功后的版本目录名（失败或未安装为空）。</summary>
    public string InstalledLoaderVersionId { get; set; } = string.Empty;

    public bool Success => Errors.Count == 0;
}

/// <summary>
/// .mrpack 整合包安装：读取 modrinth.index.json、按层叠顺序解压 overrides、
/// 下载并校验清单声明的依赖文件，可选自动安装 Fabric/Quilt/Forge/NeoForge 加载器。
/// </summary>
public class ModrinthBundleInstall
{
    private const int MaximumArchiveEntries = 20000;
    private const long MaximumEntryBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumExtractedBytes = 8L * 1024 * 1024 * 1024;
    private const long MaximumIndexBytes = 8L * 1024 * 1024;
    private const int CopyBufferSize = 128 * 1024;
    private const string ServerOverridesPrefix = "server-overrides/";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly (string Prefix, int Layer)[] ContentRoots =
    [
        ("overrides/", 1),
        ("client-overrides/", 2),
        (".minecraft/", 1),
    ];

    private static readonly HashSet<string> LauncherMetadataNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "modrinth.index.json", "index.json", "manifest.json", "mmc-pack.json",
        "instance.cfg", ".packignore", "icon.png",
    };

    /// <summary>
    /// 把 .mrpack 安装到实例游戏目录（mods/config/saves 等的父目录）。
    /// 清单声明的直链依赖会被下载并按 sha1/sha512 校验；overrides 按 mrpack 规范层叠解压。
    /// </summary>
    public static async Task<ModpackInstallResult> InstallAsync(
        string mrpackPath,
        string gameDirectory,
        ModpackInstallOptions? options = null,
        IProgress<ModpackInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mrpackPath))
        {
            throw new ArgumentException("mrpackPath 不能为空。", nameof(mrpackPath));
        }

        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            throw new ArgumentException("gameDirectory 不能为空。", nameof(gameDirectory));
        }

        options ??= new ModpackInstallOptions();
        string archivePath = Path.GetFullPath(mrpackPath.Trim());
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("整合包不存在：" + archivePath, archivePath);
        }

        string gameRoot = Path.GetFullPath(gameDirectory.Trim());
        Directory.CreateDirectory(gameRoot);

        ModpackInstallResult result = new();
        await using FileStream stream = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count > MaximumArchiveEntries)
        {
            throw new InvalidOperationException($"整合包含 {archive.Entries.Count} 个条目，超过安全上限（{MaximumArchiveEntries}）。");
        }

        ZipArchiveEntry? indexEntry = FindIndexEntry(archive);
        if (indexEntry is null)
        {
            throw new InvalidOperationException("不是有效的 Modrinth 整合包（缺少 modrinth.index.json / index.json）。");
        }

        byte[] indexBytes = await ReadEntryBytesAsync(indexEntry, MaximumIndexBytes, cancellationToken).ConfigureAwait(false);
        ModrinthIndex index;
        try
        {
            index = JsonSerializer.Deserialize<ModrinthIndex>(indexBytes, JsonOptions)
                    ?? throw new InvalidOperationException("整合包索引为空。");
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException("整合包索引不是有效 JSON：" + e.Message, e);
        }

        ModpackRequirements? requirements = BuildRequirements(index.Dependencies);
        result.DeclaredMinecraftVersion = requirements?.MinecraftVersion ?? string.Empty;
        result.DeclaredLoaderName = requirements?.LoaderName ?? string.Empty;
        result.DeclaredLoaderVersion = requirements?.LoaderVersion ?? string.Empty;
        result.DeclaredLoaderSupported = requirements is null || requirements.LoaderSupported;

        long totalArchiveBytes = 0;
        HashSet<string> archiveNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string normalized = NormalizeEntryName(entry.FullName);
            archiveNames.Add(normalized);
            if (!normalized.EndsWith('/') && normalized.Length > 0)
            {
                totalArchiveBytes += entry.Length;
            }
        }

        long extractedBytes = 0;
        await ExtractLayeredAsync(
            archive, indexEntry, gameRoot, result, archiveNames,
            totalArchiveBytes, progress, cancellationToken).ConfigureAwait(false);

        long declaredDownloadBytes = (index.Files ?? []).Where(file => file.FileSize > 0).Sum(file => file.FileSize);
        List<DependencyJob> jobs = CollectDependencyJobs(index, archiveNames, gameRoot, result);
        if (jobs.Count > 0)
        {
            Report(progress, "下载依赖文件…", extractedBytes, totalArchiveBytes + declaredDownloadBytes, 0, jobs.Count);
            await DownloadDependenciesAsync(
                jobs, options, extractedBytes, totalArchiveBytes + declaredDownloadBytes,
                result, progress, cancellationToken).ConfigureAwait(false);
        }

        if (options.InstallLoader && requirements is not null && requirements.Loader != ModpackLoader.Vanilla)
        {
            if (requirements.LoaderSupported)
            {
                try
                {
                    Report(progress, "安装加载器…", 0, 0, 0, 0);
                    result.InstalledLoaderVersionId = await InstallLoaderAsync(
                        gameRoot, requirements, options, null, cancellationToken).ConfigureAwait(false);
                    result.Warnings.Add(
                        $"已安装 {requirements.LoaderName} {requirements.LoaderVersion}（版本目录 {result.InstalledLoaderVersionId}）。");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    result.Errors.Add($"安装加载器 {requirements.LoaderName} {requirements.LoaderVersion} 失败：{e.Message}");
                }
            }
            else
            {
                result.Warnings.Add(
                    $"整合包声明的加载器 {requirements.RawLoaderKey} {requirements.LoaderVersion} 暂不支持自动安装，" +
                    "文件已解压，需要手动装好加载器后再启动。");
            }
        }
        else if (options.InstallLoader && requirements is null)
        {
            result.Warnings.Add("清单没有声明运行要求（dependencies.minecraft），未自动安装加载器。");
        }

        Report(progress, "完成", extractedBytes, totalArchiveBytes, result.InstalledFiles, archive.Entries.Count);
        return result;
    }

    /// <summary>读取 .mrpack 里的 modrinth.index.json。</summary>
    public static async Task<ModrinthIndex> ReadIndexAsync(string mrpackPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mrpackPath))
        {
            throw new ArgumentException("mrpackPath 不能为空。", nameof(mrpackPath));
        }

        string path = Path.GetFullPath(mrpackPath.Trim());
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("整合包不存在：" + path, path);
        }

        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read);
        ZipArchiveEntry? indexEntry = FindIndexEntry(archive);
        if (indexEntry is null)
        {
            throw new InvalidOperationException("不是有效的 Modrinth 整合包（缺少 modrinth.index.json / index.json）。");
        }

        byte[] bytes = await ReadEntryBytesAsync(indexEntry, MaximumIndexBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<ModrinthIndex>(bytes, JsonOptions)
                   ?? throw new InvalidOperationException("整合包索引为空。");
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException("整合包索引不是有效 JSON：" + e.Message, e);
        }
    }

    /// <summary>解析 .mrpack 声明的 Minecraft 版本与加载器；未声明时返回 null。</summary>
    public static async Task<ModpackRequirements?> ReadRequirementsAsync(
        string mrpackPath,
        CancellationToken cancellationToken = default)
    {
        ModrinthIndex index = await ReadIndexAsync(mrpackPath, cancellationToken).ConfigureAwait(false);
        return BuildRequirements(index.Dependencies);
    }

    /// <summary>
    /// 按清单要求安装加载器，返回生成的版本目录名（versions 下的 ID）。
    /// Fabric/Quilt 直接写 meta profile JSON；Forge/NeoForge 下载安装器并运行
    /// <c>java -jar installer --installClient</c>。
    /// </summary>
    public static async Task<string> InstallLoaderAsync(
        string gameDirectory,
        ModpackRequirements requirements,
        ModpackInstallOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            throw new ArgumentException("gameDirectory 不能为空。", nameof(gameDirectory));
        }

        string minecraftVersion = requirements.MinecraftVersion.Trim();
        string loaderVersion = requirements.LoaderVersion.Trim();
        if (minecraftVersion.Length == 0 || loaderVersion.Length == 0)
        {
            throw new InvalidOperationException("清单未声明 Minecraft 版本或加载器版本。");
        }

        string gameRoot = Path.GetFullPath(gameDirectory.Trim());
        ModLoaderKind kind = requirements.Loader switch
        {
            ModpackLoader.Fabric => ModLoaderKind.Fabric,
            ModpackLoader.Quilt => ModLoaderKind.Quilt,
            ModpackLoader.Forge => ModLoaderKind.Forge,
            ModpackLoader.NeoForge => ModLoaderKind.NeoForge,
            ModpackLoader.OptiFine => ModLoaderKind.OptiFine,
            _ => throw new InvalidOperationException($"不支持的加载器：{requirements.LoaderName}"),
        };
        if (kind == ModLoaderKind.OptiFine)
        {
            throw new InvalidOperationException("整合包暂不支持自动安装 OptiFine。");
        }

        // 加载器安装统一交给 MofuCore.Download.Minecraft，避免和下载页各写一套
        ModLoaderVersionInfo version = ModLoaderInstaller.CreateVersion(kind, minecraftVersion, loaderVersion);
        IProgress<ModLoaderInstallProgress>? loaderProgress = progress is null
            ? null
            : new Progress<ModLoaderInstallProgress>(update =>
                progress.Report($"{update.StageName} · {update.Detail}"));

        return await ModLoaderInstaller.InstallAsync(
            version, gameRoot, null, loaderProgress, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExtractLayeredAsync(
        ZipArchive archive,
        ZipArchiveEntry indexEntry,
        string gameRoot,
        ModpackInstallResult result,
        HashSet<string> archiveNames,
        long totalArchiveBytes,
        IProgress<ModpackInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        long extractedBytes = 0;
        for (int layer = 0; layer <= 2; layer++)
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = NormalizeEntryName(entry.FullName);
                if (name.Length == 0 || name.EndsWith('/'))
                {
                    continue;
                }

                if (string.Equals(name, NormalizeEntryName(indexEntry.FullName), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!name.Contains('/') && LauncherMetadataNames.Contains(name))
                {
                    continue;
                }

                (string relative, int entryLayer, bool install) = ResolveEntry(name);
                if (!install || entryLayer != layer || relative.Length == 0)
                {
                    continue;
                }

                if (entry.Length > MaximumEntryBytes)
                {
                    result.Errors.Add($"跳过 {name}：单文件超过上限（{MaximumEntryBytes >> 20} MB）。");
                    continue;
                }

                try
                {
                    string target = SafeCombine(gameRoot, relative);
                    string? directory = Path.GetDirectoryName(target);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    long written = await CopyEntryAsync(entry, target, cancellationToken).ConfigureAwait(false);
                    extractedBytes += written;
                    if (extractedBytes > MaximumExtractedBytes)
                    {
                        throw new InvalidOperationException($"累计解压字节超过安全上限（{MaximumExtractedBytes >> 20} MB），疑似压缩炸弹。");
                    }

                    result.InstalledFiles++;
                    Report(progress, "解压整合包文件…", extractedBytes, totalArchiveBytes, result.InstalledFiles, archiveNames.Count);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    result.Errors.Add($"解压 {name} 失败：{e.Message}");
                }
            }
        }
    }

    private static List<DependencyJob> CollectDependencyJobs(
        ModrinthIndex index,
        HashSet<string> archiveNames,
        string gameRoot,
        ModpackInstallResult result)
    {
        List<DependencyJob> jobs = [];
        HashSet<string> targets = new(StringComparer.OrdinalIgnoreCase);
        foreach (ModrinthIndexFile file in index.Files ?? [])
        {
            string declaredPath = (file.Path ?? string.Empty).Replace('\\', '/').Trim();
            if (declaredPath.Length == 0)
            {
                continue;
            }

            if (file.Env is not null
                && string.Equals(file.Env.Client, "unsupported", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsInArchive(archiveNames, declaredPath))
            {
                continue;
            }

            string? url = (file.Downloads ?? []).FirstOrDefault(IsHttpUrl);
            if (string.IsNullOrWhiteSpace(url))
            {
                result.Errors.Add($"跳过 {declaredPath}：清单没有可用的下载地址。");
                continue;
            }

            string target;
            try
            {
                target = SafeCombine(gameRoot, CleanRelativePath(declaredPath));
            }
            catch (Exception e)
            {
                result.Errors.Add($"跳过 {declaredPath}：{e.Message}");
                continue;
            }

            if (!targets.Add(target))
            {
                continue;
            }

            string sha1 = GetHash(file.Hashes, "sha1");
            string sha512 = GetHash(file.Hashes, "sha512");
            bool hasDeclaredHash = IsDeclaredHashWellFormed(sha1, 40) || IsDeclaredHashWellFormed(sha512, 128);
            if (File.Exists(target) && new FileInfo(target).Length > 0)
            {
                // 清单没声明哈希时按"非空即认"复用；声明了就按哈希校验
                if (!hasDeclaredHash || FileMatchesHashes(target, sha1, sha512))
                {
                    result.ReusedFiles++;
                    continue;
                }
            }

            jobs.Add(new DependencyJob(declaredPath, url, target, sha1, sha512, file.FileSize));
        }

        return jobs;
    }

    private static async Task DownloadDependenciesAsync(
        List<DependencyJob> jobs,
        ModpackInstallOptions options,
        long extractedBytes,
        long totalBytes,
        ModpackInstallResult result,
        IProgress<ModpackInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        object gate = new();
        long completedBytes = 0;
        int completedFiles = 0;
        int parallelism = Math.Clamp(options.ParallelDownloads, 1, 128);
        using SemaphoreSlim semaphore = new(parallelism, parallelism);

        Task[] tasks = jobs.Select(job => Task.Run(async () =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string? directory = Path.GetDirectoryName(job.TargetPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                Exception? lastError = null;
                for (int attempt = 0; attempt <= Math.Max(0, options.MaximumRetries); attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string temporary = job.TargetPath + ".tmp-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        await DownloadToFileAsync(job.Url, temporary, null, cancellationToken).ConfigureAwait(false);
                        if (!FileMatchesHashes(temporary, job.Sha1, job.Sha512))
                        {
                            throw new InvalidOperationException("哈希校验失败。");
                        }

                        File.Move(temporary, job.TargetPath, overwrite: true);
                        lock (gate)
                        {
                            completedFiles++;
                            completedBytes += job.DeclaredSize > 0 ? job.DeclaredSize : new FileInfo(job.TargetPath).Length;
                            result.DownloadedFiles++;
                            Report(progress, "下载依赖文件…",
                                extractedBytes + completedBytes,
                                totalBytes,
                                completedFiles, jobs.Count);
                        }

                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        lastError = e;
                        TryDelete(temporary);
                    }
                }

                lock (gate)
                {
                    result.Errors.Add($"下载依赖 {job.Label} 失败：{lastError?.Message}");
                }
            }
            finally
            {
                semaphore.Release();
            }
        }, cancellationToken)).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task DownloadToFileAsync(
        string url,
        string targetPath,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await Http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using FileStream destination = new(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true);
        byte[] buffer = new byte[CopyBufferSize];
        long total = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
            progress?.Report(total);
        }
    }

    private static async Task<long> CopyEntryAsync(ZipArchiveEntry entry, string targetPath, CancellationToken cancellationToken)
    {
        await using Stream source = entry.Open();
        await using FileStream destination = new(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true);
        byte[] buffer = new byte[CopyBufferSize];
        long written = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            written += read;
            if (written > MaximumEntryBytes)
            {
                throw new InvalidOperationException($"条目 {entry.FullName} 实际解压大小超过单文件上限，疑似压缩炸弹。");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return written;
    }

    private static async Task<byte[]> ReadEntryBytesAsync(
        ZipArchiveEntry entry,
        long limit,
        CancellationToken cancellationToken)
    {
        if (entry.Length > limit)
        {
            throw new InvalidOperationException("整合包索引体积超过上限。");
        }

        await using Stream stream = entry.Open();
        using MemoryStream buffer = new();
        byte[] chunk = new byte[CopyBufferSize];
        long total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            total += read;
            if (total > limit)
            {
                throw new InvalidOperationException("整合包索引体积超过上限。");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static ZipArchiveEntry? FindIndexEntry(ZipArchive archive)
    {
        foreach (string name in new[] { "modrinth.index.json", "index.json" })
        {
            ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(candidate =>
            {
                string candidateName = NormalizeEntryName(candidate.FullName);
                return !candidateName.Contains('/')
                       && string.Equals(candidateName, name, StringComparison.OrdinalIgnoreCase);
            });
            if (entry is not null)
            {
                return entry;
            }
        }

        return null;
    }

    private static ModpackRequirements? BuildRequirements(Dictionary<string, string>? dependencies)
    {
        if (dependencies is null)
        {
            return null;
        }

        string minecraft = dependencies
            .FirstOrDefault(pair => string.Equals(pair.Key, "minecraft", StringComparison.OrdinalIgnoreCase))
            .Value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(minecraft))
        {
            return null;
        }

        ModpackRequirements requirements = new() { MinecraftVersion = minecraft.Trim() };
        foreach (string key in new[] { "fabric-loader", "quilt-loader", "neoforge", "forge" })
        {
            if (!dependencies.TryGetValue(key, out string? value))
            {
                continue;
            }

            requirements.RawLoaderKey = key;
            requirements.Loader = MapLoader(key);
            requirements.LoaderVersion = (value ?? string.Empty).Trim();
            return requirements;
        }

        // 大小写变体 / 未知加载器：保留原始键与版本号，供上层提示"声明了但装不了"
        foreach ((string key, string value) in dependencies)
        {
            if (string.Equals(key, "minecraft", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            requirements.RawLoaderKey = key;
            requirements.LoaderVersion = (value ?? string.Empty).Trim();
            requirements.Loader = MapLoader(key);
            break;
        }

        return requirements;
    }

    private static ModpackLoader MapLoader(string key)
    {
        return key.Trim().ToLowerInvariant() switch
        {
            "fabric-loader" or "fabric" => ModpackLoader.Fabric,
            "quilt-loader" or "quilt" => ModpackLoader.Quilt,
            "neoforge" => ModpackLoader.NeoForge,
            "forge" => ModpackLoader.Forge,
            "optifine" => ModpackLoader.OptiFine,
            _ => ModpackLoader.Vanilla,
        };
    }

    private static (string Relative, int Layer, bool Install) ResolveEntry(string name)
    {
        string normalized = NormalizeEntryName(name);
        if (normalized.StartsWith(ServerOverridesPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return (string.Empty, 0, false);
        }

        foreach ((string prefix, int layer) in ContentRoots)
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return (normalized[prefix.Length..], layer, true);
            }
        }

        return (normalized, 0, true);
    }

    private static bool IsInArchive(HashSet<string> archiveNames, string declaredPath)
    {
        if (archiveNames.Contains(CleanRelativePath(declaredPath)))
        {
            return true;
        }

        foreach ((string prefix, _) in ContentRoots)
        {
            if (archiveNames.Contains(CleanRelativePath(prefix + declaredPath)))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeEntryName(string name) => (name ?? string.Empty).Replace('\\', '/');

    private static string CleanRelativePath(string path)
    {
        string normalized = NormalizeEntryName(path).Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        if (normalized.Length > 0 && normalized[0] == '/')
        {
            throw new InvalidOperationException("路径不能用绝对路径：" + path);
        }

        return normalized;
    }

    private static string SafeCombine(string root, string relative)
    {
        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalized = relative.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized))
        {
            throw new InvalidOperationException("路径不能用绝对路径：" + relative);
        }

        string combined = Path.GetFullPath(Path.Combine(rootFull, normalized));
        if (!string.Equals(combined, rootFull, StringComparison.OrdinalIgnoreCase)
            && !combined.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("路径越出实例目录：" + relative);
        }

        return combined;
    }

    private static bool IsHttpUrl(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static string GetHash(Dictionary<string, string>? hashes, string algorithm)
    {
        if (hashes is null)
        {
            return string.Empty;
        }

        return hashes.TryGetValue(algorithm, out string? value) ? (value ?? string.Empty).Trim() : string.Empty;
    }

    private static bool IsDeclaredHashWellFormed(string hash, int length)
    {
        return hash.Length == length && hash.All(Uri.IsHexDigit);
    }

    private static bool FileMatchesHashes(string path, string sha1, string sha512)
    {
        bool wantSha1 = IsDeclaredHashWellFormed(sha1, 40);
        bool wantSha512 = IsDeclaredHashWellFormed(sha512, 128);
        if (!wantSha1 && !wantSha512)
        {
            return true;
        }

        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using IncrementalHash sha1Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            using IncrementalHash sha512Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            byte[] buffer = new byte[CopyBufferSize];
            while (true)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                sha1Hash.AppendData(buffer, 0, read);
                sha512Hash.AppendData(buffer, 0, read);
            }

            if (wantSha1 && !string.Equals(Convert.ToHexString(sha1Hash.GetHashAndReset()), sha1, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !wantSha512
                   || string.Equals(Convert.ToHexString(sha512Hash.GetHashAndReset()), sha512, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 清理失败不影响结果
        }
    }

    private static void Report(
        IProgress<ModpackInstallProgress>? progress,
        string phase,
        long completedBytes,
        long totalBytes,
        int completedFiles,
        int totalFiles)
    {
        progress?.Report(new ModpackInstallProgress(phase, completedBytes, totalBytes, completedFiles, totalFiles));
    }

    private sealed record DependencyJob(
        string Label,
        string Url,
        string TargetPath,
        string Sha1,
        string Sha512,
        long DeclaredSize);
}
