using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace MofuCore.Java;

/// <summary>
/// 从 Mojang 的 java runtime 清单（launchermeta products/java-runtime）下载并安装 Java 运行时。
/// 安装目录为 {游戏目录}/runtime/{组件}/{平台}，与官方启动器一致。
/// </summary>
public class JavaDownload
{
    public const string DefaultAllManifestUrl =
        "https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    private const int MaximumParallelDownloads = 8;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly object __cacheLock = new();
    private static IReadOnlyList<MojangJavaRuntime>? __cachedRuntimes;
    private static DateTimeOffset __cachedAt;

    /// <summary>
    /// 当前平台的清单键，如 windows-x64 / windows-arm64 / mac-os / linux。
    /// </summary>
    public static string PlatformKey
    {
        get
        {
            string architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
            bool arm64 = architecture.Contains("arm64", StringComparison.Ordinal);
            bool x86 = architecture is "x86" or "x86_32" or "x86_64";

            if (OperatingSystem.IsWindows())
            {
                return arm64 ? "windows-arm64" : x86 ? "windows-x86" : "windows-x64";
            }

            if (OperatingSystem.IsMacOS())
            {
                return arm64 ? "mac-os-arm64" : "mac-os";
            }

            return x86 ? "linux-i386" : "linux";
        }
    }

    /// <summary>
    /// 当前平台的说明文字（如 "Windows x64"）。
    /// </summary>
    public static string PlatformDisplayName
    {
        get
        {
            string osName = OperatingSystem.IsWindows()
                ? "Windows"
                : OperatingSystem.IsMacOS() ? "macOS" : "Linux";
            string architecture = PlatformKey switch
            {
                "windows-arm64" or "mac-os-arm64" => OperatingSystem.IsMacOS() ? "Apple Silicon" : "ARM64",
                "windows-x86" or "linux-i386" => "x86",
                _ => "x64",
            };
            return osName + " " + architecture;
        }
    }

    /// <summary>
    /// Java 运行时根目录：{游戏目录}/runtime。
    /// </summary>
    public static string GetRuntimeDirectory(string gameDirectory)
    {
        return Path.Combine(gameDirectory, "runtime");
    }

    /// <summary>
    /// 查询当前平台全部可下载的 Mojang Java 运行时（按主版本降序），10 分钟内重复调用使用缓存。
    /// </summary>
    public static async Task<IReadOnlyList<MojangJavaRuntime>> GetAvailableAsync(
        string? allManifestUrl = null,
        CancellationToken cancellationToken = default)
    {
        string url = string.IsNullOrWhiteSpace(allManifestUrl) ? DefaultAllManifestUrl : allManifestUrl.Trim();
        if (string.Equals(url, DefaultAllManifestUrl, StringComparison.Ordinal) && TryGetCached(out IReadOnlyList<MojangJavaRuntime>? cached))
        {
            return cached!;
        }

        string json;
        try
        {
            json = await Http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new InvalidOperationException("获取 Java 运行时清单失败：" + e.Message, e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("获取 Java 运行时清单超时。", e);
        }

        Dictionary<string, Dictionary<string, List<AllManifestEntry>>>? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, List<AllManifestEntry>>>>(json, JsonOptions);
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException("Java 运行时清单格式错误：" + e.Message, e);
        }

        List<MojangJavaRuntime> result = [];
        if (manifest is not null && manifest.TryGetValue(PlatformKey, out Dictionary<string, List<AllManifestEntry>>? components))
        {
            foreach (KeyValuePair<string, List<AllManifestEntry>> component in components)
            {
                AllManifestEntry? entry = component.Value.FirstOrDefault();
                if (entry is null || string.IsNullOrWhiteSpace(entry.Manifest.Url))
                {
                    continue;
                }

                int? majorVersion = ParseMajorVersion(entry.Version.Name);
                result.Add(new MojangJavaRuntime
                {
                    Component = component.Key,
                    VersionName = entry.Version.Name,
                    Released = entry.Version.Released,
                    ManifestUrl = entry.Manifest.Url,
                    ManifestSha1 = entry.Manifest.Sha1,
                    MajorVersion = majorVersion,
                });
            }
        }

        List<MojangJavaRuntime> sorted = result
            .OrderByDescending(runtime => runtime.MajorVersion ?? 0)
            .ThenBy(runtime => runtime.Component, StringComparer.Ordinal)
            .ToList();

        if (string.Equals(url, DefaultAllManifestUrl, StringComparison.Ordinal))
        {
            lock (__cacheLock)
            {
                __cachedRuntimes = sorted;
                __cachedAt = DateTimeOffset.UtcNow;
            }
        }

        return sorted;
    }

    /// <summary>
    /// 清空可用版本缓存。
    /// </summary>
    public static void InvalidateCache()
    {
        lock (__cacheLock)
        {
            __cachedRuntimes = null;
        }
    }

    /// <summary>
    /// 下载并安装指定组件的 Java 运行时，返回安装结果。
    /// </summary>
    public static async Task<InstalledJavaRuntime> InstallAsync(
        string component,
        string gameDirectory,
        IProgress<JavaRuntimeInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(component))
        {
            throw new ArgumentException("组件名不能为空。", nameof(component));
        }

        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            throw new ArgumentException("游戏目录不能为空。", nameof(gameDirectory));
        }

        IReadOnlyList<MojangJavaRuntime> available = await GetAvailableAsync(null, cancellationToken).ConfigureAwait(false);
        MojangJavaRuntime? selected = available.FirstOrDefault(runtime =>
            string.Equals(runtime.Component, component.Trim(), StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            throw new InvalidOperationException($"当前平台没有名为 {component} 的 Java 运行时。");
        }

        RuntimeManifest manifest = await FetchManifestAsync(selected.ManifestUrl, cancellationToken).ConfigureAwait(false);
        string runtimeRoot = Path.Combine(GetRuntimeDirectory(Path.GetFullPath(gameDirectory.Trim())), selected.Component, PlatformKey);
        Directory.CreateDirectory(runtimeRoot);

        List<KeyValuePair<string, RuntimeFile>> files = manifest.Files
            .Where(pair => string.Equals(pair.Value.Type, "file", StringComparison.OrdinalIgnoreCase))
            .ToList();
        long totalBytes = files.Sum(pair => pair.Value.Downloads?.Raw?.Size ?? 0);
        int totalFiles = files.Count;

        int completedFiles = 0;
        long completedBytes = 0;
        long lastReportTicks = -ProgressInterval.Ticks;
        Stopwatch stopwatch = Stopwatch.StartNew();
        object reportLock = new();

        ReportProgress(progress, "正在准备 Java 运行时", 0, totalBytes, stopwatch, 0, totalFiles);

        using SemaphoreSlim gate = new(MaximumParallelDownloads, MaximumParallelDownloads);
        Task[] tasks = files.Select(pair => Task.Run(async () =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                RuntimeFile file = pair.Value;
                RuntimeDownload? download = file.Downloads?.Raw;
                if (download is null || string.IsNullOrWhiteSpace(download.Url))
                {
                    throw new InvalidOperationException("Java 运行时清单缺少下载地址：" + pair.Key);
                }

                string targetPath = ResolveTargetPath(runtimeRoot, pair.Key);
                await DownloadFileAsync(download, targetPath, cancellationToken).ConfigureAwait(false);
                if (file.Executable)
                {
                    MarkExecutable(targetPath);
                }

                Interlocked.Add(ref completedBytes, download.Size);
                int done = Interlocked.Increment(ref completedFiles);
                lock (reportLock)
                {
                    long nowTicks = stopwatch.Elapsed.Ticks;
                    if (nowTicks - lastReportTicks >= ProgressInterval.Ticks || done == totalFiles)
                    {
                        lastReportTicks = nowTicks;
                        ReportProgress(progress, "正在下载 Java 运行时", Interlocked.Read(ref completedBytes), totalBytes, stopwatch, done, totalFiles);
                    }
                }
            }
            finally
            {
                gate.Release();
            }
        }, cancellationToken)).ToArray();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 任一下载失败时终止整次安装，半成品目录可被下次安装覆盖
            throw;
        }

        string? javaPath = JavaPathSearch.FindJavaExecutableInDirectory(runtimeRoot);
        if (javaPath is null)
        {
            throw new InvalidOperationException("安装完成后未找到 java 可执行文件。");
        }

        JavaRuntime? runtime = await JavaPathSearch.DetectAsync(javaPath, cancellationToken).ConfigureAwait(false);
        ReportProgress(progress, "安装完成", totalBytes, totalBytes, stopwatch, totalFiles, totalFiles);

        return new InstalledJavaRuntime
        {
            DirectoryPath = runtimeRoot,
            JavaExecutablePath = javaPath,
            Component = selected.Component,
            VersionName = runtime?.VersionString ?? selected.VersionName,
            MajorVersion = runtime?.MajorVersion ?? selected.MajorVersion,
            Vendor = runtime?.Vendor ?? JavaVendor.Unknown,
        };
    }

    /// <summary>
    /// 安装满足指定主版本的最新 Java 运行时；没有匹配的组件时返回 null。
    /// </summary>
    public static async Task<InstalledJavaRuntime?> InstallForMajorVersionAsync(
        int majorVersion,
        string gameDirectory,
        IProgress<JavaRuntimeInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MojangJavaRuntime> available = await GetAvailableAsync(null, cancellationToken).ConfigureAwait(false);
        MojangJavaRuntime? selected = available
            .Where(runtime => runtime.MajorVersion == majorVersion)
            .OrderByDescending(runtime => runtime.Released)
            .FirstOrDefault();
        return selected is null
            ? null
            : await InstallAsync(selected.Component, gameDirectory, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 扫描已安装的 Java 运行时（{游戏目录}/runtime 下的全部组件与平台）。
    /// </summary>
    public static async Task<IReadOnlyList<InstalledJavaRuntime>> GetInstalledRuntimesAsync(
        string gameDirectory,
        CancellationToken cancellationToken = default)
    {
        List<InstalledJavaRuntime> result = [];
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return result;
        }

        string runtimeRoot = GetRuntimeDirectory(Path.GetFullPath(gameDirectory.Trim()));
        if (!Directory.Exists(runtimeRoot))
        {
            return result;
        }

        foreach (string componentDirectory in Directory.GetDirectories(runtimeRoot))
        {
            foreach (string platformDirectory in Directory.GetDirectories(componentDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? javaPath = JavaPathSearch.FindJavaExecutableInDirectory(platformDirectory);
                if (javaPath is null)
                {
                    continue;
                }

                JavaRuntime? runtime = await JavaPathSearch.DetectAsync(javaPath, cancellationToken).ConfigureAwait(false);
                result.Add(new InstalledJavaRuntime
                {
                    DirectoryPath = platformDirectory,
                    JavaExecutablePath = javaPath,
                    Component = Path.GetFileName(componentDirectory),
                    Platform = Path.GetFileName(platformDirectory),
                    VersionName = runtime?.VersionString ?? string.Empty,
                    MajorVersion = runtime?.MajorVersion,
                    Vendor = runtime?.Vendor ?? JavaVendor.Unknown,
                });
            }
        }

        return result
            .OrderByDescending(runtime => runtime.MajorVersion ?? 0)
            .ThenBy(runtime => runtime.Component, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 删除已安装的 Java 运行时目录（仅允许 runtime 目录内的路径）。
    /// </summary>
    public static bool DeleteRuntime(string gameDirectory, string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory) || string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }

        try
        {
            string runtimeRoot = Path.GetFullPath(GetRuntimeDirectory(Path.GetFullPath(gameDirectory.Trim())))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullPath = Path.GetFullPath(directoryPath.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!fullPath.StartsWith(runtimeRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }

            return true;
        }
        catch (Exception e)
        {
            Debug.WriteLine("[Java] 删除 Java 运行时失败: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// 从版本名推导 Java 主版本（如 17.0.8 → 17，1.8.0_74 → 8）。
    /// </summary>
    public static int? ParseMajorVersion(string? versionName)
    {
        if (string.IsNullOrWhiteSpace(versionName))
        {
            return null;
        }

        Match match = Regex.Match(versionName.Trim(), @"^(\d+)(?:\.(\d+))?");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out int major))
        {
            return null;
        }

        if (major == 1 && match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out int legacyMajor))
        {
            return legacyMajor;
        }

        return major;
    }

    private static bool TryGetCached(out IReadOnlyList<MojangJavaRuntime>? runtimes)
    {
        lock (__cacheLock)
        {
            if (__cachedRuntimes is not null && DateTimeOffset.UtcNow - __cachedAt < CacheTtl)
            {
                runtimes = __cachedRuntimes;
                return true;
            }
        }

        runtimes = null;
        return false;
    }

    private static async Task<RuntimeManifest> FetchManifestAsync(string manifestUrl, CancellationToken cancellationToken)
    {
        try
        {
            string json = await Http.GetStringAsync(manifestUrl, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<RuntimeManifest>(json, JsonOptions)
                   ?? throw new InvalidOperationException("Java 运行时清单为空或格式错误。");
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException("Java 运行时清单格式错误：" + e.Message, e);
        }
        catch (HttpRequestException e)
        {
            throw new InvalidOperationException("获取 Java 运行时清单失败：" + e.Message, e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("获取 Java 运行时清单超时。", e);
        }
    }

    private static async Task DownloadFileAsync(
        RuntimeDownload download,
        string targetPath,
        CancellationToken cancellationToken)
    {
        // 已存在且大小一致的文件直接跳过（官方启动器同样按清单跳过）
        if (File.Exists(targetPath) && new FileInfo(targetPath).Length == download.Size)
        {
            return;
        }

        string? directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using HttpResponseMessage response = await Http
                .GetAsync(download.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (FileStream destination = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
            {
                await source.CopyToAsync(destination, 128 * 1024, cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(download.Sha1))
            {
                await using FileStream stream = File.OpenRead(temporaryPath);
                string hash = Convert.ToHexString(await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
                if (!string.Equals(hash, download.Sha1.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Java 运行时文件校验失败（SHA-1 不匹配）：" + download.Url);
                }
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
            temporaryPath = string.Empty;
        }
        finally
        {
            if (temporaryPath.Length > 0)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // 临时文件清理失败不影响结果
                }
            }
        }
    }

    private static string ResolveTargetPath(string root, string relativePath)
    {
        string normalizedRoot = Path.GetFullPath(root);
        string combined = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!combined.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("非法的运行时文件路径：" + relativePath);
        }

        return combined;
    }

    private static void MarkExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch
        {
            // 权限设置失败时保持原样
        }
    }

    private static void ReportProgress(
        IProgress<JavaRuntimeInstallProgress>? progress,
        string phase,
        long completedBytes,
        long totalBytes,
        Stopwatch stopwatch,
        int completedFiles,
        int totalFiles)
    {
        if (progress is null)
        {
            return;
        }

        double elapsed = stopwatch.Elapsed.TotalSeconds;
        progress.Report(new JavaRuntimeInstallProgress
        {
            Phase = phase,
            CompletedBytes = completedBytes,
            TotalBytes = totalBytes,
            FilesDone = completedFiles,
            FilesTotal = totalFiles,
            BytesPerSecond = elapsed > 0 ? (long)(completedBytes / elapsed) : 0,
        });
    }

    private sealed class AllManifestEntry
    {
        [JsonPropertyName("manifest")] public ManifestReference Manifest { get; set; } = new();
        [JsonPropertyName("version")] public VersionReference Version { get; set; } = new();
    }

    private sealed class ManifestReference
    {
        [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
        [JsonPropertyName("sha1")] public string Sha1 { get; set; } = string.Empty;
        [JsonPropertyName("size")] public long Size { get; set; }
    }

    private sealed class VersionReference
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("released")] public DateTimeOffset Released { get; set; }
    }

    private sealed class RuntimeManifest
    {
        [JsonPropertyName("files")] public Dictionary<string, RuntimeFile> Files { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class RuntimeFile
    {
        [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
        [JsonPropertyName("executable")] public bool Executable { get; set; }
        [JsonPropertyName("downloads")] public RuntimeDownloads? Downloads { get; set; }
    }

    private sealed class RuntimeDownloads
    {
        [JsonPropertyName("raw")] public RuntimeDownload? Raw { get; set; }
    }

    private sealed class RuntimeDownload
    {
        [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
        [JsonPropertyName("sha1")] public string Sha1 { get; set; } = string.Empty;
        [JsonPropertyName("size")] public long Size { get; set; }
    }
}

/// <summary>
/// Mojang Java 运行时清单中的一条候选。
/// </summary>
public class MojangJavaRuntime
{
    /// <summary>组件名，如 java-runtime-gamma。</summary>
    public string Component { get; set; } = string.Empty;

    /// <summary>版本名，如 17.0.8。</summary>
    public string VersionName { get; set; } = string.Empty;

    public DateTimeOffset Released { get; set; }

    public string ManifestUrl { get; set; } = string.Empty;

    public string ManifestSha1 { get; set; } = string.Empty;

    /// <summary>从版本名推导的 Java 主版本。</summary>
    public int? MajorVersion { get; set; }

    public string DisplayName => MajorVersion is null ? Component : $"Java {MajorVersion}";

    public string DetailText => string.IsNullOrWhiteSpace(VersionName) ? Component : $"{VersionName} · {Component}";
}

/// <summary>
/// Java 运行时安装进度。
/// </summary>
public class JavaRuntimeInstallProgress
{
    public string Phase { get; set; } = string.Empty;

    public long CompletedBytes { get; set; }

    public long TotalBytes { get; set; }

    public long BytesPerSecond { get; set; }

    public int FilesDone { get; set; }

    public int FilesTotal { get; set; }

    public double Percent => TotalBytes > 0 ? Math.Clamp((double)CompletedBytes / TotalBytes * 100d, 0, 100) : 0;
}

/// <summary>
/// 已安装的 Java 运行时。
/// </summary>
public class InstalledJavaRuntime
{
    /// <summary>安装目录（平台目录）。</summary>
    public string DirectoryPath { get; set; } = string.Empty;

    /// <summary>java 可执行文件路径。</summary>
    public string JavaExecutablePath { get; set; } = string.Empty;

    /// <summary>Mojang 组件名，如 java-runtime-gamma。</summary>
    public string Component { get; set; } = string.Empty;

    /// <summary>平台键，如 windows-x64。</summary>
    public string Platform { get; set; } = string.Empty;

    /// <summary>java 版本号。</summary>
    public string VersionName { get; set; } = string.Empty;

    public int? MajorVersion { get; set; }

    public JavaVendor Vendor { get; set; } = JavaVendor.Unknown;

    public string DisplayName => MajorVersion is null ? Component : $"Java {MajorVersion}";
}
