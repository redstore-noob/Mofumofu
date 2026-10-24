using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

using MofuCore;

namespace MofuCore.Download.Minecraft;

/// <summary>
/// 一个完整的下载源定义，包含所有需要替换的基础 URL。
/// </summary>
public sealed class DownloadSource
{
    public string Name { get; init; } = string.Empty;
    public string LauncherMeta { get; init; } = string.Empty;
    public string Meta { get; init; } = string.Empty;
    public string Libraries { get; init; } = string.Empty;
    public string Resources { get; init; } = string.Empty;
    public string Maven { get; init; } = string.Empty;
}

/// <summary>
/// 内置下载源。
/// </summary>
public static class DownloadSources
{
    public static readonly DownloadSource Official = new()
    {
        Name = "Official",
        LauncherMeta = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json",
        Meta = "https://piston-meta.mojang.com",
        Libraries = "https://libraries.minecraft.net",
        Resources = "https://resources.download.minecraft.net",
        Maven = "https://libraries.minecraft.net",
    };

    public static readonly DownloadSource Bmcl = new()
    {
        Name = "BMCL",
        LauncherMeta = "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json",
        Meta = "https://bmclapi2.bangbang93.com",
        Libraries = "https://bmclapi2.bangbang93.com/maven",
        Resources = "https://bmclapi2.bangbang93.com/assets",
        Maven = "https://bmclapi2.bangbang93.com/maven",
    };

    public static readonly IReadOnlyList<DownloadSource> All = [Bmcl, Official];
}

/// <summary>
/// 下载源提供器：所有下载逻辑通过它把 Mojang 官方地址换成活跃源地址。
/// </summary>
public static class DownloadSourceProvider
{
    private static readonly object Gate = new();
    private static readonly HttpClient Client = CreateClient();
    private static DownloadSource _active = DownloadSources.Bmcl;
    private static DownloadSource? _fallback = DownloadSources.Bmcl;

    private static HttpClient CreateClient()
    {
        HttpClient client = new() { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MofuCore/" + MofuCoreInfo.Version);
        // 部分站点（files.minecraftforge.net）缺少 Accept 时会拖到 20 秒才响应
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    internal static HttpClient Http => Client;

    public static DownloadSource Active
    {
        get { lock (Gate) return _active; }
    }

    public static DownloadSource? Fallback
    {
        get { lock (Gate) return _fallback; }
    }

    public static void SetActive(DownloadSource source)
    {
        lock (Gate) _active = source;
    }

    public static void SetFallback(DownloadSource? source)
    {
        lock (Gate) _fallback = source;
    }

    public static string Resolve(string officialUrl)
    {
        DownloadSource active = Active;
        if (string.IsNullOrWhiteSpace(officialUrl) || string.Equals(active.Name, DownloadSources.Official.Name, StringComparison.Ordinal))
        {
            return officialUrl;
        }
        return ReplaceBaseUrl(officialUrl, active);
    }

    public static string? ResolveFallback(string officialUrl)
    {
        DownloadSource? fallback = Fallback;
        if (fallback is null || string.IsNullOrWhiteSpace(officialUrl))
        {
            return null;
        }
        return ReplaceBaseUrl(officialUrl, fallback);
    }

    public static async Task<string> GetStringAsync(string officialUrl, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        byte[] payload = await GetBytesAsync(officialUrl, timeout, cancellationToken).ConfigureAwait(false);
        return System.Text.Encoding.UTF8.GetString(payload);
    }

    public static async Task<byte[]> GetBytesAsync(string officialUrl, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        string primaryUrl = EnsureHttps(Resolve(officialUrl));
        string? fallbackUrl = ResolveFallback(officialUrl);
        if (fallbackUrl is not null)
        {
            fallbackUrl = EnsureHttps(fallbackUrl);
            if (string.Equals(fallbackUrl, primaryUrl, StringComparison.OrdinalIgnoreCase)) fallbackUrl = null;
        }

        Exception? primaryError = null;
        try
        {
            return await GetBytesOnceAsync(primaryUrl, timeout ?? TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            primaryError = e;
        }

        if (fallbackUrl is not null)
        {
            try
            {
                return await GetBytesOnceAsync(fallbackUrl, timeout ?? TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"下载源均不可用（{primaryError?.Message}；回退源：{e.Message}）。", e);
            }
        }

        throw new InvalidOperationException($"获取下载内容失败（{primaryUrl}）：{primaryError?.Message}", primaryError);
    }

    /// <summary>绕过当前下载源，直接访问给定地址（加载器安装器的官方 Maven 兜底）。</summary>
    public static Task<byte[]> GetBytesDirectAsync(
        string url,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        return GetBytesOnceAsync(EnsureHttps(url), timeout ?? TimeSpan.FromSeconds(30), cancellationToken);
    }

    private static async Task<byte[]> GetBytesOnceAsync(string url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using HttpResponseMessage response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
    }

    public static async Task<int> MeasureLatencyAsync(DownloadSource source, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
        long started = Environment.TickCount64;
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Head, source.LauncherMeta);
            using HttpResponseMessage response = await Client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return -1;
            long elapsed = Environment.TickCount64 - started;
            return elapsed < 1 ? 1 : (int)elapsed;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    public static async Task<IReadOnlyList<SourceLatency>> MeasureLatenciesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DownloadSource> sources = DownloadSources.All;
        List<SourceLatency> results = new(sources.Count);
        foreach (DownloadSource source in sources)
        {
            int latency = await MeasureLatencyAsync(source, cancellationToken).ConfigureAwait(false);
            results.Add(new SourceLatency(source.Name, latency));
        }
        return results;
    }

    internal static string EnsureHttps(string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"下载地址不是有效的 HTTPS URL：{url}");
        }
        return url;
    }

    private static string ReplaceBaseUrl(string rawUrl, DownloadSource target)
    {
        string lower = rawUrl.ToLowerInvariant();
        // Quilt 无镜像，直连官方
        if (lower.Contains("meta.quiltmc.org", StringComparison.Ordinal) || lower.Contains("maven.quiltmc.org", StringComparison.Ordinal))
        {
            return rawUrl;
        }
        if (lower.Contains("libraries.minecraft.net", StringComparison.Ordinal))
        {
            return rawUrl.Replace("https://libraries.minecraft.net", target.Libraries, StringComparison.Ordinal);
        }
        if (lower.Contains("resources.download.minecraft.net", StringComparison.Ordinal))
        {
            return rawUrl.Replace("https://resources.download.minecraft.net", target.Resources, StringComparison.Ordinal);
        }
        // files.minecraftforge.net 是官网（promotions 等元数据），不是 Maven 镜像；
        // 只有 maven.minecraftforge.net 才走 BMCL 的 /maven 路径
        if (lower.Contains("maven.minecraftforge.net", StringComparison.Ordinal))
        {
            return rawUrl.Replace("https://maven.minecraftforge.net", target.Maven, StringComparison.Ordinal);
        }
        if (lower.Contains("maven.neoforged.net/releases/net/neoforged", StringComparison.Ordinal))
        {
            return rawUrl.Replace("https://maven.neoforged.net/releases/net/neoforged", target.Maven + "/net/neoforged", StringComparison.Ordinal);
        }
        if (lower.Contains("maven.neoforged.net", StringComparison.Ordinal))
        {
            return rawUrl.Replace("https://maven.neoforged.net", target.Maven, StringComparison.Ordinal);
        }
        if (lower.Contains("meta.fabricmc.net", StringComparison.Ordinal))
        {
            return rawUrl.Replace("https://meta.fabricmc.net", target.Meta + "/fabric-meta", StringComparison.Ordinal);
        }
        if (lower.Contains("maven.fabricmc.net", StringComparison.Ordinal))
        {
            return rawUrl.Replace("https://maven.fabricmc.net", target.Maven, StringComparison.Ordinal);
        }
        if (lower.Contains("piston-meta.mojang.com", StringComparison.Ordinal))
        {
            return rawUrl.Replace("https://piston-meta.mojang.com", target.Meta, StringComparison.Ordinal);
        }
        if (lower.Contains("piston-data.mojang.com", StringComparison.Ordinal))
        {
            return rawUrl.Replace("https://piston-data.mojang.com", target.Meta, StringComparison.Ordinal);
        }
        if (lower.Contains("launchermeta.mojang.com", StringComparison.Ordinal))
        {
            return rawUrl.Replace("https://launchermeta.mojang.com", target.Meta, StringComparison.Ordinal);
        }
        return rawUrl;
    }
}

/// <summary>单个下载源的延迟探测结果。</summary>
public sealed record SourceLatency(string Name, int LatencyMs)
{
    public bool Available => LatencyMs >= 0;
}

/// <summary>
/// 全局下载暂停门：暂停期间读取循环停在门口，连接保持，恢复后从原连接继续。
/// </summary>
public static class DownloadPauseGate
{
    private static readonly object Gate = new();
    private static TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static volatile bool _paused;

    public static event Action? StateChanged;

    public static bool IsPaused => _paused;

    public static void Pause()
    {
        if (_paused) return;
        _paused = true;
        StateChanged?.Invoke();
    }

    public static void Resume()
    {
        if (!_paused) return;
        _paused = false;
        TaskCompletionSource resumed;
        lock (Gate)
        {
            resumed = _resume;
            _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        resumed.TrySetResult();
        StateChanged?.Invoke();
    }

    public static async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        while (_paused)
        {
            Task signal;
            lock (Gate) signal = _resume.Task;
            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// 全局下载限速：按秒记账的令牌桶，所有并发连接共享同一份配额（0 = 不限速）。
/// </summary>
public static class DownloadSpeedLimiter
{
    private const int MinimumKbps = 1;
    private const int MaximumKbps = 1024 * 1024;

    private static readonly object Gate = new();
    private static long _rate;
    private static long _used;
    private static long _windowStart;

    public static int Kbps => (int)(Interlocked.Read(ref _rate) / 1024);

    public static void SetKbps(int kbps)
    {
        if (kbps < 0) kbps = 0;
        if (kbps > 0 && kbps < MinimumKbps) kbps = MinimumKbps;
        if (kbps > MaximumKbps) kbps = MaximumKbps;
        lock (Gate)
        {
            _rate = (long)kbps * 1024;
            _windowStart = 0;
            _used = 0;
        }
    }

    public static int PermitReadSize(int bufferSize)
    {
        long rate = Interlocked.Read(ref _rate);
        if (rate <= 0 || rate >= bufferSize) return bufferSize;
        if (rate < 8 * 1024) return 8 * 1024;
        return (int)rate;
    }

    public static async Task AcquireAsync(long bytes, CancellationToken cancellationToken = default)
    {
        long rate = Interlocked.Read(ref _rate);
        if (rate <= 0 || bytes <= 0) return;

        long delayMs;
        lock (Gate)
        {
            if (_rate <= 0) return;
            long now = Environment.TickCount64;
            if (_windowStart == 0 || now - _windowStart >= 1000)
            {
                _windowStart = now;
                _used = 0;
            }
            if (_used + bytes <= _rate)
            {
                _used += bytes;
                return;
            }
            _windowStart += 1000;
            if (_windowStart < now) _windowStart = now;
            _used = bytes;
            delayMs = _windowStart - now;
        }
        if (delayMs > 0)
        {
            await Task.Delay((int)Math.Min(delayMs, int.MaxValue), cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// 下载设置：并行线程数与全局限速（持久化由配置层负责，这里只保存运行期值）。
/// </summary>
public static class DownloadSettings
{
    public const int DefaultParallelDownloads = 32;
    public const int MinParallelDownloads = 1;
    public const int MaxParallelDownloads = 128;

    private static int _parallelDownloads = DefaultParallelDownloads;

    public static int ParallelDownloads
    {
        get => Volatile.Read(ref _parallelDownloads);
        set => Volatile.Write(ref _parallelDownloads, Math.Clamp(value, MinParallelDownloads, MaxParallelDownloads));
    }

    public static int SpeedLimitKbps
    {
        get => DownloadSpeedLimiter.Kbps;
        set => DownloadSpeedLimiter.SetKbps(value);
    }
}

/// <summary>版本 JSON 中的文件下载声明。</summary>
public sealed class MinecraftDownloadInfo
{
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("sha1")] public string Sha1 { get; set; } = string.Empty;
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
}

/// <summary>版本 JSON 中的 rules 条目。</summary>
public sealed class MinecraftRuleOs
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("arch")] public string Arch { get; set; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
}

/// <summary>版本 JSON 中的单条规则。</summary>
public sealed class MinecraftRule
{
    [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;
    [JsonPropertyName("os")] public MinecraftRuleOs? Os { get; set; }
    [JsonPropertyName("features")] public Dictionary<string, bool>? Features { get; set; }
}

/// <summary>库的 downloads 段。</summary>
public sealed class MinecraftLibraryDownloads
{
    [JsonPropertyName("artifact")] public MinecraftDownloadInfo? Artifact { get; set; }
    [JsonPropertyName("classifiers")] public Dictionary<string, MinecraftDownloadInfo>? Classifiers { get; set; }
}

/// <summary>库的 extract 段（natives 解压排除项）。</summary>
public sealed class MinecraftLibraryExtract
{
    [JsonPropertyName("exclude")] public List<string>? Exclude { get; set; }
}

/// <summary>版本 JSON 中的 libraries 条目。</summary>
public sealed class MinecraftLibrary
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("natives")] public Dictionary<string, string>? Natives { get; set; }
    [JsonPropertyName("rules")] public List<MinecraftRule>? Rules { get; set; }
    [JsonPropertyName("downloads")] public MinecraftLibraryDownloads? Downloads { get; set; }
    [JsonPropertyName("extract")] public MinecraftLibraryExtract? Extract { get; set; }
}

/// <summary>版本 JSON 的 downloads 段。</summary>
public sealed class MinecraftDownloads
{
    [JsonPropertyName("client")] public MinecraftDownloadInfo? Client { get; set; }
    [JsonPropertyName("server")] public MinecraftDownloadInfo? Server { get; set; }
}

/// <summary>日志配置声明。</summary>
public sealed class MinecraftLoggingClient
{
    [JsonPropertyName("argument")] public string Argument { get; set; } = string.Empty;
    [JsonPropertyName("file")] public MinecraftDownloadInfo? File { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
}

/// <summary>版本 JSON 的 logging 段。</summary>
public sealed class MinecraftLogging
{
    [JsonPropertyName("client")] public MinecraftLoggingClient? Client { get; set; }
}

/// <summary>
/// 版本 JSON（安装期所需子集）。继承链通过 <see cref="InheritsFrom"/> 合并为自包含对象。
/// </summary>
public sealed class MinecraftVersionJson
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("inheritsFrom")] public string? InheritsFrom { get; set; }
    [JsonPropertyName("mainClass")] public string? MainClass { get; set; }
    [JsonPropertyName("assets")] public string? Assets { get; set; }
    [JsonPropertyName("minecraftArguments")] public string? MinecraftArguments { get; set; }
    [JsonPropertyName("jar")] public string? Jar { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("time")] public DateTimeOffset? Time { get; set; }
    [JsonPropertyName("releaseTime")] public DateTimeOffset? ReleaseTime { get; set; }
    [JsonPropertyName("libraries")] public List<MinecraftLibrary> Libraries { get; set; } = [];
    [JsonPropertyName("arguments")] public JsonElement? Arguments { get; set; }
    [JsonPropertyName("downloads")] public MinecraftDownloads? Downloads { get; set; }
    [JsonPropertyName("assetIndex")] public MinecraftDownloadInfo? AssetIndex { get; set; }
    [JsonPropertyName("logging")] public MinecraftLogging? Logging { get; set; }
    [JsonPropertyName("clientVersion")] public string? ClientVersion { get; set; }
}

/// <summary>版本 JSON 规则求值（与启动侧保持同一套语义）。</summary>
public static class MinecraftRuleEvaluator
{
    public static Dictionary<string, bool> DefaultFeatures() => new() { ["has_custom_resolution"] = true };

    public static string OsName =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "osx" : "linux";

    public static string OsVersionDescription()
    {
        if (OperatingSystem.IsWindows())
        {
            return $"Windows {Environment.OSVersion.Version}";
        }
        return Environment.OSVersion.VersionString;
    }

    public static bool IsAllowed(IReadOnlyList<MinecraftRule>? rules, IReadOnlyDictionary<string, bool> features)
    {
        if (rules is null || rules.Count == 0) return true;

        bool allowed = false;
        foreach (MinecraftRule rule in rules)
        {
            if (rule.Features is not null)
            {
                bool match = true;
                foreach ((string key, bool value) in rule.Features)
                {
                    if (!features.TryGetValue(key, out bool actual) || actual != value)
                    {
                        match = false;
                        break;
                    }
                }
                if (!match) continue;
            }

            if (rule.Os is not null)
            {
                if (!string.IsNullOrEmpty(rule.Os.Name) && !string.Equals(rule.Os.Name, OsName, StringComparison.Ordinal))
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(rule.Os.Arch))
                {
                    string wanted = rule.Os.Arch.Trim().ToLowerInvariant();
                    string actual = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
                    {
                        System.Runtime.InteropServices.Architecture.X86 => "x86",
                        System.Runtime.InteropServices.Architecture.X64 => "x86_64",
                        System.Runtime.InteropServices.Architecture.Arm => "arm",
                        System.Runtime.InteropServices.Architecture.Arm64 => "aarch64",
                        _ => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                    };
                    if (!string.Equals(wanted, actual, StringComparison.Ordinal) &&
                        !(wanted == "x64" && actual == "x86_64") &&
                        !(wanted == "arm64" && actual == "aarch64"))
                    {
                        continue;
                    }
                }
                if (!string.IsNullOrEmpty(rule.Os.Version))
                {
                    try
                    {
                        if (!System.Text.RegularExpressions.Regex.IsMatch(OsVersionDescription(), rule.Os.Version)) continue;
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }
                }
            }

            if (rule.Action == "allow")
            {
                allowed = true;
            }
            else if (rule.Action == "disallow")
            {
                allowed = false;
            }
        }
        return allowed;
    }
}

/// <summary>安装进度快照（百分比由字节数推导）。</summary>
public sealed record MinecraftInstallProgress(
    int StageIndex,
    string StageName,
    string Detail,
    long CompletedBytes,
    long TotalBytes,
    int CompletedFiles,
    int TotalFiles,
    double BytesPerSecond)
{
    public double Percentage
    {
        get
        {
            if (TotalBytes <= 0) return 0;
            double value = CompletedBytes * 100.0 / TotalBytes;
            return Math.Clamp(value, 0, 100);
        }
    }
}

/// <summary>单个零碎文件的下载作业。</summary>
public sealed record MinecraftSmallFileJob(
    string Name,
    string Url,
    string TargetPath,
    string? ExpectedSha1 = null,
    string? ExpectedSha512 = null);

/// <summary>
/// 文件哈希与校验工具（供下载器与 Modrinth 共用）。
/// </summary>
public static class MinecraftFileHash
{
    public static bool IsSha1(string value) => IsHexDigest(value, 40);

    public static bool IsHexDigest(string? value, int hexLength)
    {
        if (value is null || value.Length != hexLength) return false;
        foreach (char c in value)
        {
            if (!Uri.IsHexDigit(c)) return false;
        }
        return true;
    }

    public static async Task<string> ComputeSha1Async(string path, CancellationToken cancellationToken = default)
        => await ComputeAsync(path, SHA1.Create, cancellationToken).ConfigureAwait(false);

    public static async Task<string> ComputeSha512Async(string path, CancellationToken cancellationToken = default)
        => await ComputeAsync(path, SHA512.Create, cancellationToken).ConfigureAwait(false);

    private static async Task<string> ComputeAsync(string path, Func<HashAlgorithm> factory, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        using HashAlgorithm algorithm = factory();
        byte[] hash = await algorithm.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    public static async Task<bool> MatchesSha1Async(string path, string? expectedSha1, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedSha1)) return true;
        if (!System.IO.File.Exists(path)) return false;
        try
        {
            string actual = await ComputeSha1Async(path, cancellationToken).ConfigureAwait(false);
            return string.Equals(actual, expectedSha1.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static async Task<bool> MatchesSha512Async(string path, string? expectedSha512, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedSha512)) return true;
        if (!System.IO.File.Exists(path)) return false;
        try
        {
            string actual = await ComputeSha512Async(path, cancellationToken).ConfigureAwait(false);
            return string.Equals(actual, expectedSha512.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }
}

/// <summary>
/// Minecraft 版本下载与安装：client.jar / libraries / assets / 资源索引 / 日志配置，
/// 含 natives 解压、规则求值、并行下载、限速、暂停、断点续传、SHA-1 校验与继承链合并。
/// </summary>
public sealed class MinecraftDownload
{
    public const int StageCount = 7;
    public const string LoggingConfigDirectory = "log_configs";

    private const int MaximumInheritanceDepth = 8;
    private const int DownloadBufferSize = 128 * 1024;
    private const string TemporarySuffix = ".mofu-download";
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ProgressReportInterval = TimeSpan.FromMilliseconds(120);

    private static readonly string[] StageNames =
    [
        "获取版本元数据",
        "分析下载清单",
        "下载游戏客户端",
        "下载依赖库",
        "下载资源索引",
        "下载游戏资源",
        "完成校验与安装",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly object ManifestGate = new();
    private static MinecraftManifest? _manifest;
    private static string _manifestUrl = string.Empty;

    private long _lastReportTicks;

    public MinecraftDownload(string minecraftDirectory = "")
    {
        MinecraftDirectory = NormalizeRoot(string.IsNullOrWhiteSpace(minecraftDirectory) ? DefaultMinecraftDirectory() : minecraftDirectory);
    }

    public string MinecraftDirectory { get; set; }

    private static string DefaultMinecraftDirectory() => Path.Combine(AppContext.BaseDirectory, ".minecraft");

    private static string NormalizeRoot(string directory)
    {
        string fullPath = Path.GetFullPath(directory.Trim());
        if (string.Equals(Path.GetFileName(fullPath), "versions", StringComparison.OrdinalIgnoreCase) &&
            Path.GetDirectoryName(fullPath) is { Length: > 0 } parent)
        {
            fullPath = parent;
        }
        return fullPath;
    }

    /// <summary>获取版本清单（使用活跃下载源，10 分钟内复用缓存）。</summary>
    public static Task<IReadOnlyList<MinecraftVersion>> GetVersionsAsync(CancellationToken cancellationToken = default)
    {
        return GetManifest().GetVersionsAsync(cancellationToken);
    }

    private static MinecraftManifest GetManifest()
    {
        string url = DownloadSourceProvider.Active.LauncherMeta;
        lock (ManifestGate)
        {
            if (_manifest is null || !string.Equals(_manifestUrl, url, StringComparison.OrdinalIgnoreCase))
            {
                _manifest = new MinecraftManifest(url);
                _manifestUrl = url;
            }
            return _manifest;
        }
    }

    /// <summary>按版本 ID 从版本清单查元数据地址；找不到返回 null。</summary>
    public static async Task<string?> FindVersionMetadataUrlAsync(string versionId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MinecraftVersion> versions = await GetVersionsAsync(cancellationToken).ConfigureAwait(false);
        foreach (MinecraftVersion version in versions)
        {
            if (string.Equals(version.Id, versionId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(version.Url))
            {
                return version.Url;
            }
        }
        return null;
    }

    /// <summary>下载并安装版本清单中的某个版本。</summary>
    public Task InstallAsync(MinecraftVersion version, IProgress<MinecraftInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        return InstallAsync(version.Id, version.Url, progress, cancellationToken);
    }

    /// <summary>下载指定版本的元数据并完成安装。</summary>
    public async Task InstallAsync(string versionId, string metadataUrl, IProgress<MinecraftInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateVersionId(versionId);
        if (string.IsNullOrWhiteSpace(metadataUrl)) throw new InvalidOperationException("metadataUrl 不能为空。");
        if (string.IsNullOrWhiteSpace(MinecraftDirectory)) throw new InvalidOperationException("minecraftDirectory 不能为空。");

        string versionsDirectory = Path.Combine(MinecraftDirectory, "versions");
        Directory.CreateDirectory(versionsDirectory);

        Report(progress, 1, StageNames[0], $"正在读取 Minecraft {versionId} 的版本描述", 0, 0, 0, 0, 0);
        byte[] metadataBytes = await DownloadSourceProvider.GetBytesAsync(metadataUrl, null, cancellationToken).ConfigureAwait(false);
        await InstallFromMetadataBytesAsync(versionId, metadataBytes, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>用已有的版本 JSON 字节完成安装（供继承链合并后的调用方复用）。</summary>
    public async Task InstallFromMetadataBytesAsync(string versionId, byte[] metadataBytes, IProgress<MinecraftInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateVersionId(versionId);
        Directory.CreateDirectory(Path.Combine(MinecraftDirectory, "versions"));

        MinecraftVersionJson metadata = ParseVersionJson(metadataBytes, versionId);
        string versionDirectory = Path.Combine(MinecraftDirectory, "versions", versionId);
        Directory.CreateDirectory(versionDirectory);

        bool merged = !string.IsNullOrWhiteSpace(metadata.InheritsFrom);
        byte[] versionJsonBytes = metadataBytes;
        if (merged)
        {
            Report(progress, 1, StageNames[0], "正在合并继承的版本配置", 0, 0, 0, 0, 0);
            List<(string Id, MinecraftVersionJson Json)> chain = await LoadVersionChainAsync(versionId, metadata, cancellationToken).ConfigureAwait(false);
            metadata = await MergeVersionChainAsync(versionId, chain, cancellationToken).ConfigureAwait(false);
            versionJsonBytes = JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions);
        }

        Directory.CreateDirectory(Path.Combine(MinecraftDirectory, "versions"));
        Report(progress, 2, StageNames[1], "正在整理客户端、依赖库与资源索引", 0, 0, 0, 0, 0);

        List<MinecraftDownloadFile> clientFiles = CreateClientPlan(metadata, versionId, versionDirectory);
        List<MinecraftDownloadFile> libraryFiles = CreateLibraryPlan(metadata);
        MinecraftDownloadFile? assetIndexFile = CreateAssetIndexPlan(metadata);
        MinecraftDownloadFile? loggingFile = CreateLoggingPlan(metadata);

        long started = Environment.TickCount64;
        InstallCounters counters = new();
        foreach (MinecraftDownloadFile file in clientFiles) counters.AddTotalBytes(Math.Max(0, file.Size));
        foreach (MinecraftDownloadFile file in libraryFiles) counters.AddTotalBytes(Math.Max(0, file.Size));
        counters.AddTotalFiles(clientFiles.Count + libraryFiles.Count);
        if (assetIndexFile is not null)
        {
            counters.AddTotalBytes(Math.Max(0, assetIndexFile.Size));
            counters.AddTotalFiles(1);
        }
        if (loggingFile is not null)
        {
            counters.AddTotalBytes(Math.Max(0, loggingFile.Size));
            counters.AddTotalFiles(1);
        }

        await DownloadStageAsync(3, StageNames[2], clientFiles, progress, counters, started, cancellationToken).ConfigureAwait(false);
        await DownloadStageAsync(4, StageNames[3], libraryFiles, progress, counters, started, cancellationToken).ConfigureAwait(false);

        if (assetIndexFile is not null)
        {
            await DownloadStageAsync(5, StageNames[4], [assetIndexFile], progress, counters, started, cancellationToken).ConfigureAwait(false);
        }
        if (loggingFile is not null)
        {
            await DownloadStageAsync(5, "下载日志配置", [loggingFile], progress, counters, started, cancellationToken).ConfigureAwait(false);
        }

        List<MinecraftDownloadFile> assetFiles = assetIndexFile is null
            ? []
            : await CreateAssetPlanAsync(assetIndexFile.TargetPath, cancellationToken).ConfigureAwait(false);
        foreach (MinecraftDownloadFile file in assetFiles) counters.AddTotalBytes(Math.Max(0, file.Size));
        counters.AddTotalFiles(assetFiles.Count);
        await DownloadStageAsync(6, StageNames[5], assetFiles, progress, counters, started, cancellationToken).ConfigureAwait(false);

        // natives 解压到版本目录，启动时 --natives_directory 直接指向这里
        int extracted = await ExtractNativesAsync(versionId, metadata, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        Report(progress, 7, StageNames[6], extracted > 0 ? $"正在写入版本描述（natives × {extracted}）" : "正在写入版本描述并完成安装",
            counters.CompletedBytes, counters.TotalBytes, (int)counters.CompletedFiles, (int)counters.TotalFiles,
            counters.WindowBytesPerSecond());

        await WriteAllBytesAtomicallyAsync(Path.Combine(versionDirectory, versionId + ".json"), versionJsonBytes, cancellationToken).ConfigureAwait(false);

        Report(progress, 7, StageNames[6], $"Minecraft {versionId} 已安装完成",
            counters.TotalBytes, counters.TotalBytes, (int)counters.TotalFiles, (int)counters.TotalFiles,
            counters.WindowBytesPerSecond());
    }

    /// <summary>校验并补全指定版本的游戏文件（版本 JSON / 客户端 JAR / 库 / 日志配置）。</summary>
    public Task<int> VerifyAndRepairAsync(string versionId, bool hasCustomResolution = true, IProgress<string>? status = null, CancellationToken cancellationToken = default)
    {
        return new MinecraftGameFileVerifier(this).VerifyAndRepairAsync(versionId, hasCustomResolution, status, cancellationToken);
    }

    /// <summary>读取版本的完整配置（自动合并 inheritsFrom 继承链）。</summary>
    public async Task<MinecraftVersionJson> LoadVersionJsonAsync(string versionId, CancellationToken cancellationToken = default)
    {
        ValidateVersionId(versionId);
        List<(string Id, MinecraftVersionJson Json)> chain = await LoadVersionChainAsync(versionId, null, cancellationToken).ConfigureAwait(false);
        return chain.Count == 1 ? chain[0].Json : await MergeVersionChainAsync(versionId, chain, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>解压版本声明的 natives 到 versions/&lt;id&gt;/natives。</summary>
    public async Task<int> ExtractNativesAsync(string versionId, MinecraftVersionJson? metadata = null, CancellationToken cancellationToken = default)
    {
        ValidateVersionId(versionId);
        metadata ??= await LoadVersionJsonAsync(versionId, cancellationToken).ConfigureAwait(false);

        string nativesDirectory = Path.Combine(MinecraftDirectory, "versions", versionId, "natives");
        if (Directory.Exists(nativesDirectory))
        {
            Directory.Delete(nativesDirectory, true);
        }
        Directory.CreateDirectory(nativesDirectory);

        IReadOnlyDictionary<string, bool> features = MinecraftRuleEvaluator.DefaultFeatures();
        int extracted = 0;
        foreach (MinecraftLibrary library in metadata.Libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MinecraftRuleEvaluator.IsAllowed(library.Rules, features)) continue;
            if (library.Natives is null) continue;
            if (!library.Natives.TryGetValue(MinecraftRuleEvaluator.OsName, out string? rawClassifier)) continue;

            string classifier = ExpandArchPlaceholder(rawClassifier);
            string? relativePath = null;
            if (library.Downloads?.Classifiers is not null && library.Downloads.Classifiers.TryGetValue(classifier, out MinecraftDownloadInfo? native))
            {
                relativePath = native.Path;
            }
            relativePath ??= CreateNativeMavenPath(library.Name, classifier);
            if (string.IsNullOrWhiteSpace(relativePath)) continue;

            string jarPath = ResolveRelativePath(Path.Combine(MinecraftDirectory, "libraries"), relativePath);
            if (!File.Exists(jarPath)) continue;

            string[] excludes = library.Extract?.Exclude?.Select(NormalizeArchivePath).ToArray() ?? [];
            extracted += await ExtractNativeArchiveAsync(jarPath, nativesDirectory, excludes, cancellationToken).ConfigureAwait(false);
        }
        return extracted;
    }

    /// <summary>下载单个文件（含断点续传、重试、主备源与 SHA-1 校验）。</summary>
    public static async Task DownloadFileToPathAsync(
        string url,
        string targetPath,
        string? expectedSha1 = null,
        long expectedSize = 0,
        IProgress<long>? bytesProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("下载地址不能为空。");
        if (string.IsNullOrWhiteSpace(targetPath)) throw new InvalidOperationException("下载目标路径不能为空。");

        MinecraftDownloadFile file = new()
        {
            Url = url,
            TargetPath = Path.GetFullPath(targetPath),
            Sha1 = expectedSha1,
            Size = expectedSize,
            DisplayName = Path.GetFileName(targetPath),
        };
        long downloaded = 0;
        await DownloadOneFileAsync(file, bytes =>
        {
            long current = Interlocked.Add(ref downloaded, bytes);
            bytesProgress?.Report(current);
        }, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>并行下载一组小文件，返回与 jobs 等长的错误数组（成功位为 null）。</summary>
    public static async Task<Exception?[]> DownloadSmallFilesAsync(
        IReadOnlyList<MinecraftSmallFileJob> jobs,
        int workers = 0,
        Action<int, long, long>? onFileProgress = null,
        CancellationToken cancellationToken = default)
    {
        Exception?[] errors = new Exception?[jobs.Count];
        if (jobs.Count == 0) return errors;
        if (workers < 1) workers = DownloadSettings.ParallelDownloads;

        using SemaphoreSlim semaphore = new(workers, workers);
        List<Task> tasks = new(jobs.Count);
        for (int index = 0; index < jobs.Count; index++)
        {
            int captured = index;
            if (cancellationToken.IsCancellationRequested)
            {
                errors[captured] = new OperationCanceledException(cancellationToken);
                continue;
            }
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    errors[captured] = new OperationCanceledException(cancellationToken);
                    return;
                }
                try
                {
                    MinecraftSmallFileJob job = jobs[captured];
                    MinecraftDownloadFile file = new()
                    {
                        Url = job.Url,
                        TargetPath = Path.GetFullPath(job.TargetPath),
                        Sha1 = job.ExpectedSha1,
                        DisplayName = job.Name,
                    };
                    await DownloadOneFileAsync(file, _ => { }, null, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(job.ExpectedSha512) &&
                        !await MinecraftFileHash.MatchesSha512Async(file.TargetPath, job.ExpectedSha512, cancellationToken).ConfigureAwait(false))
                    {
                        File.Delete(file.TargetPath);
                        throw new InvalidOperationException($"文件 SHA-512 校验失败：{job.Name}");
                    }
                }
                catch (Exception e)
                {
                    errors[captured] = e;
                }
                finally
                {
                    semaphore.Release();
                }
            }, cancellationToken));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return errors;
    }

    // ---------------------------------------------------------------- 下载清单

    private static List<MinecraftDownloadFile> CreateClientPlan(MinecraftVersionJson metadata, string versionId, string versionDirectory)
    {
        MinecraftDownloadInfo? client = metadata.Downloads?.Client;
        if (client is null || string.IsNullOrWhiteSpace(client.Url)) return [];
        return
        [
            new MinecraftDownloadFile
            {
                Url = client.Url,
                TargetPath = Path.Combine(versionDirectory, versionId + ".jar"),
                Sha1 = client.Sha1,
                Size = client.Size,
                DisplayName = "客户端文件",
            },
        ];
    }

    private List<MinecraftDownloadFile> CreateLibraryPlan(MinecraftVersionJson metadata)
    {
        Dictionary<string, MinecraftDownloadFile> result = new(StringComparer.OrdinalIgnoreCase);
        List<string> order = [];
        IReadOnlyDictionary<string, bool> features = MinecraftRuleEvaluator.DefaultFeatures();

        // 依赖库的下载地址与目标路径统一由 LibraryResolver 补全，启动侧共用同一套坐标解析。
        foreach (LibraryDownloadPlan plan in new LibraryResolver().ResolveDownloads(metadata.Libraries, features))
        {
            if (string.IsNullOrWhiteSpace(plan.Url)) continue;

            string target = ResolveRelativePath(MinecraftDirectoryLibraries, plan.RelativePath);
            SetPlan(result, order, target, new MinecraftDownloadFile
            {
                Url = plan.Url,
                TargetPath = target,
                Sha1 = plan.Sha1 ?? string.Empty,
                Size = plan.Size,
                DisplayName = plan.Native
                    ? "原生依赖库"
                    : Path.GetFileName(target) is { Length: > 0 } name ? name : plan.DisplayName,
            });
        }

        List<MinecraftDownloadFile> files = new(result.Count);
        foreach (string key in order) files.Add(result[key]);
        return files;
    }

    private string MinecraftDirectoryLibraries => Path.Combine(MinecraftDirectory, "libraries");

    private static void SetPlan(Dictionary<string, MinecraftDownloadFile> result, List<string> order, string key, MinecraftDownloadFile file)
    {
        string mapKey = key.Replace('\\', '/').ToLowerInvariant();
        if (!result.ContainsKey(mapKey)) order.Add(mapKey);
        result[mapKey] = file;
    }

    private MinecraftDownloadFile? CreateAssetIndexPlan(MinecraftVersionJson metadata)
    {
        MinecraftDownloadInfo? assetIndex = metadata.AssetIndex;
        if (assetIndex is null) return null;

        string id = string.IsNullOrWhiteSpace(assetIndex.Id) ? metadata.Assets ?? string.Empty : assetIndex.Id;
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("资源索引缺少 ID。");
        if (string.IsNullOrWhiteSpace(assetIndex.Url)) throw new InvalidOperationException("资源索引缺少下载地址。");

        string target = ResolveRelativePath(Path.Combine(MinecraftDirectory, "assets", "indexes"), id + ".json");
        return new MinecraftDownloadFile
        {
            Url = assetIndex.Url,
            TargetPath = target,
            Sha1 = assetIndex.Sha1,
            Size = assetIndex.Size,
            DisplayName = "资源索引",
        };
    }

    private MinecraftDownloadFile? CreateLoggingPlan(MinecraftVersionJson metadata)
    {
        MinecraftDownloadInfo? file = metadata.Logging?.Client?.File;
        if (file is null) return null;
        if (string.IsNullOrWhiteSpace(file.Id) || string.IsNullOrWhiteSpace(file.Url)) return null;
        return new MinecraftDownloadFile
        {
            Url = file.Url,
            TargetPath = Path.Combine(MinecraftDirectory, "assets", LoggingConfigDirectory, file.Id),
            Sha1 = file.Sha1?.Trim(),
            Size = file.Size,
            DisplayName = file.Id,
        };
    }

    private async Task<List<MinecraftDownloadFile>> CreateAssetPlanAsync(string assetIndexPath, CancellationToken cancellationToken)
    {
        List<MinecraftDownloadFile> files = [];
        await using FileStream stream = new(assetIndexPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("objects", out JsonElement objects) || objects.ValueKind != JsonValueKind.Object)
        {
            return files;
        }

        Dictionary<string, MinecraftDownloadFile> result = new(StringComparer.OrdinalIgnoreCase);
        List<string> order = [];
        foreach (JsonProperty property in objects.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!property.Value.TryGetProperty("hash", out JsonElement hashElement)) continue;
            string hash = hashElement.GetString() ?? string.Empty;
            if (!MinecraftFileHash.IsSha1(hash)) continue;
            long size = property.Value.TryGetProperty("size", out JsonElement sizeElement) && sizeElement.TryGetInt64(out long parsed) ? parsed : 0;
            string relativePath = Path.Combine(hash[..2], hash);
            string target = ResolveRelativePath(Path.Combine(MinecraftDirectory, "assets", "objects"), relativePath);
            SetPlan(result, order, target, new MinecraftDownloadFile
            {
                Url = $"https://resources.download.minecraft.net/{hash[..2]}/{hash}",
                TargetPath = target,
                Sha1 = hash,
                Size = size,
                DisplayName = property.Name,
            });
        }
        foreach (string key in order) files.Add(result[key]);
        return files;
    }

    // ---------------------------------------------------------------- 分阶段下载

    private async Task DownloadStageAsync(
        int stageIndex,
        string stageName,
        List<MinecraftDownloadFile> files,
        IProgress<MinecraftInstallProgress>? progress,
        InstallCounters counters,
        long started,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            Report(progress, stageIndex, stageName, "此阶段没有需要下载的文件",
                counters.CompletedBytes, counters.TotalBytes, (int)counters.CompletedFiles, (int)counters.TotalFiles,
                counters.WindowBytesPerSecond());
            return;
        }

        int workers = Math.Clamp(DownloadSettings.ParallelDownloads, 1, Math.Max(1, files.Count));
        using SemaphoreSlim semaphore = new(workers, workers);
        int activeFiles = 0;
        Exception? firstError = null;
        List<Task> tasks = new(files.Count);

        foreach (MinecraftDownloadFile file in files)
        {
            MinecraftDownloadFile captured = file;
            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    int active = Interlocked.Increment(ref activeFiles);
                    long reused = await DownloadOneFileAsync(captured, counters.AddProgressBytes, () =>
                    {
                        ReportThrottled(progress, stageIndex, stageName, captured, active, counters, started);
                    }, cancellationToken).ConfigureAwait(false);
                    if (reused > 0) counters.AddCompleted(reused);
                    counters.IncrementCompletedFiles();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    Interlocked.CompareExchange(ref firstError, e, null);
                }
                finally
                {
                    Interlocked.Decrement(ref activeFiles);
                    semaphore.Release();
                }
            }, cancellationToken));
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        if (firstError is not null) throw new InvalidOperationException(firstError.Message, firstError);
        Report(progress, stageIndex, stageName, stageName + "完成",
            counters.CompletedBytes, counters.TotalBytes, (int)counters.CompletedFiles, (int)counters.TotalFiles,
            counters.WindowBytesPerSecond());
    }

    private void ReportThrottled(
        IProgress<MinecraftInstallProgress>? progress,
        int stageIndex,
        string stageName,
        MinecraftDownloadFile file,
        int activeFileCount,
        InstallCounters counters,
        long started)
    {
        long now = Environment.TickCount64;
        long previous = Interlocked.Read(ref _lastReportTicks);
        if (now - previous < (long)ProgressReportInterval.TotalMilliseconds ||
            Interlocked.CompareExchange(ref _lastReportTicks, now, previous) != previous)
        {
            return;
        }
        Report(progress, stageIndex, stageName, $"正在处理 {activeFileCount} 个文件 · {file.DisplayName}",
            counters.CompletedBytes, counters.TotalBytes, (int)counters.CompletedFiles, (int)counters.TotalFiles,
            counters.WindowBytesPerSecond());
    }

    private static async Task<long> DownloadOneFileAsync(
        MinecraftDownloadFile file,
        Action<long> addProgressBytes,
        Action? reportProgress,
        CancellationToken cancellationToken)
    {
        if (await IsExistingFileValidAsync(file, cancellationToken).ConfigureAwait(false))
        {
            if (file.Size > 0) return file.Size;
            FileInfo info = new(file.TargetPath);
            return info.Exists ? info.Length : 0;
        }

        string directory = Path.GetDirectoryName(file.TargetPath) ?? string.Empty;
        if (directory.Length > 0) Directory.CreateDirectory(directory);
        string temporaryPath = file.TargetPath + TemporarySuffix;

        string resolvedUrl = DownloadSourceProvider.Resolve(file.Url);
        List<string> urls = [resolvedUrl];
        string? fallback = DownloadSourceProvider.ResolveFallback(file.Url);
        if (fallback is not null && !string.Equals(fallback, resolvedUrl, StringComparison.OrdinalIgnoreCase))
        {
            urls.Add(fallback);
        }

        Exception? lastError = null;
        foreach (string url in urls)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                long attemptBytes = 0;
                bool stalled = false;
                using CancellationTokenSource attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                using StallWatchdog watchdog = new(() =>
                {
                    stalled = true;
                    attemptCts.Cancel();
                }, StallTimeout);

                try
                {
                    await DownloadToTemporaryAsync(url, temporaryPath, file.Size, bytes => Interlocked.Add(ref attemptBytes, bytes), reportProgress, watchdog, attemptCts.Token).ConfigureAwait(false);
                    watchdog.Touch();
                    await ValidateTemporaryFileAsync(file, temporaryPath, cancellationToken).ConfigureAwait(false);
                    File.Move(temporaryPath, file.TargetPath, true);
                    return 0;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    DeleteIfExists(temporaryPath);
                    throw;
                }
                catch (Exception e)
                {
                    if (Interlocked.Read(ref attemptBytes) > 0) addProgressBytes(-Interlocked.Read(ref attemptBytes));
                    DeleteIfExists(temporaryPath);
                    lastError = stalled
                        ? new InvalidOperationException($"下载连接停滞（{StallTimeout.TotalSeconds:0} 秒无进度）：{file.DisplayName}", e)
                        : e;
                    if (e is RangeMismatchException) continue;
                }
            }
        }

        throw new InvalidOperationException($"下载失败：{file.DisplayName}", lastError);
    }

    private static async Task DownloadToTemporaryAsync(
        string url,
        string temporaryPath,
        long expectedSize,
        Action<long> addProgressBytes,
        Action? reportProgress,
        StallWatchdog watchdog,
        CancellationToken cancellationToken)
    {
        DownloadSourceProvider.EnsureHttps(url);

        // 大文件（jar / 大 library）走多连接分段下载；未设置限速且没有断点临时文件时启用
        bool canSegment = expectedSize >= 8L * 1024 * 1024
                          && DownloadSpeedLimiter.Kbps <= 0
                          && (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0);
        if (canSegment)
        {
            await DownloadPauseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            await FastDownloader.DownloadFileAsync(
                new FastDownloadItem(url, temporaryPath, expectedSize),
                segments: 4,
                onBytes: bytes =>
                {
                    watchdog.Touch();
                    watchdog.AddBytes(bytes);
                    addProgressBytes(bytes);
                    reportProgress?.Invoke();
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        long existing = 0;
        if (File.Exists(temporaryPath)) existing = new FileInfo(temporaryPath).Length;

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);

        using HttpResponseMessage response = await DownloadSourceProvider.Http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            throw new RangeMismatchException();
        }
        response.EnsureSuccessStatusCode();

        bool append = response.StatusCode == HttpStatusCode.PartialContent && existing > 0;
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using FileStream destination = new(temporaryPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, true);

        byte[] buffer = new byte[DownloadBufferSize];
        while (true)
        {
            await DownloadPauseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            int chunkSize = DownloadSpeedLimiter.PermitReadSize(buffer.Length);
            await DownloadSpeedLimiter.AcquireAsync(chunkSize, cancellationToken).ConfigureAwait(false);

            int read = await stream.ReadAsync(buffer.AsMemory(0, chunkSize), cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            watchdog.Touch();
            watchdog.AddBytes(read);
            addProgressBytes(read);
            reportProgress?.Invoke();
        }
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> IsExistingFileValidAsync(MinecraftDownloadFile file, CancellationToken cancellationToken)
    {
        FileInfo info = new(file.TargetPath);
        if (!info.Exists) return false;
        if (file.Size > 0 && info.Length != file.Size) return false;
        if (file.Size == 0 && string.IsNullOrWhiteSpace(file.Sha1) && info.Length == 0) return false;
        return await MinecraftFileHash.MatchesSha1Async(file.TargetPath, file.Sha1, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ValidateTemporaryFileAsync(MinecraftDownloadFile file, string temporaryPath, CancellationToken cancellationToken)
    {
        if (!await MinecraftFileHash.MatchesSha1Async(temporaryPath, file.Sha1, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"下载文件校验失败：{file.DisplayName}");
        }
        FileInfo info = new(temporaryPath);
        if (file.Size > 0 && info.Length != file.Size)
        {
            throw new InvalidOperationException($"下载文件大小不匹配：{file.DisplayName}");
        }
        if (string.IsNullOrWhiteSpace(file.Sha1) && file.Size == 0 && info.Length == 0)
        {
            throw new InvalidOperationException($"下载文件为空：{file.DisplayName}");
        }
    }

    // ---------------------------------------------------------------- 继承链

    private async Task<List<(string Id, MinecraftVersionJson Json)>> LoadVersionChainAsync(string versionId, MinecraftVersionJson? leafJson, CancellationToken cancellationToken)
    {
        List<(string Id, MinecraftVersionJson Json)> chain = [];
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        string? currentId = versionId;
        MinecraftVersionJson? pendingLeaf = leafJson;

        while (!string.IsNullOrWhiteSpace(currentId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(currentId)) throw new InvalidOperationException($"版本继承出现循环：{currentId}");
            if (chain.Count >= MaximumInheritanceDepth) throw new InvalidOperationException("版本继承层级过深。");

            MinecraftVersionJson json = pendingLeaf ?? await LoadVersionJsonOneAsync(currentId, cancellationToken).ConfigureAwait(false);
            pendingLeaf = null;
            chain.Add((currentId, json));
            currentId = json.InheritsFrom;
        }
        return chain;
    }

    private async Task<MinecraftVersionJson> LoadVersionJsonOneAsync(string versionId, CancellationToken cancellationToken)
    {
        string localPath = Path.Combine(MinecraftDirectory, "versions", versionId, versionId + ".json");
        if (File.Exists(localPath))
        {
            byte[] bytes = await File.ReadAllBytesAsync(localPath, cancellationToken).ConfigureAwait(false);
            return ParseVersionJson(bytes, versionId);
        }

        string? url = await FindVersionMetadataUrlAsync(versionId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException($"找不到版本配置：{localPath}");
        }
        byte[] payload = await DownloadSourceProvider.GetBytesAsync(url, null, cancellationToken).ConfigureAwait(false);
        return ParseVersionJson(payload, versionId);
    }

    private async Task<MinecraftVersionJson> MergeVersionChainAsync(
        string versionId,
        List<(string Id, MinecraftVersionJson Json)> chain,
        CancellationToken cancellationToken)
    {
        MinecraftVersionJson merged = new() { Id = versionId, Libraries = [] };
        Dictionary<string, int> libraryIndexes = new(StringComparer.OrdinalIgnoreCase);
        string? declaredId = null;

        // chain 为「实例 → 父」，倒序合并使子级覆盖父级
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            MinecraftVersionJson current = chain[i].Json;
            if (!string.IsNullOrWhiteSpace(current.Id)) declaredId = current.Id;
            merged.InheritsFrom = null;
            merged.MainClass = current.MainClass ?? merged.MainClass;
            merged.Assets = current.Assets ?? merged.Assets;
            merged.MinecraftArguments = current.MinecraftArguments ?? merged.MinecraftArguments;
            merged.Jar = current.Jar ?? merged.Jar;
            merged.Type = current.Type ?? merged.Type;
            merged.Time = current.Time ?? merged.Time;
            merged.ReleaseTime = current.ReleaseTime ?? merged.ReleaseTime;
            merged.ClientVersion = current.ClientVersion ?? merged.ClientVersion;
            if (current.Arguments is not null) merged.Arguments = current.Arguments;
            if (current.AssetIndex is not null) merged.AssetIndex = current.AssetIndex;
            if (current.Logging is not null) merged.Logging = current.Logging;
            if (current.Downloads?.Client is not null)
            {
                merged.Downloads = new MinecraftDownloads { Client = current.Downloads.Client, Server = merged.Downloads?.Server };
            }
            else if (merged.Downloads is null && current.Downloads is not null)
            {
                merged.Downloads = current.Downloads;
            }

            foreach (MinecraftLibrary library in current.Libraries)
            {
                string key = LibraryCoordinateKey(library.Name);
                if (libraryIndexes.TryGetValue(key, out int existingIndex))
                {
                    merged.Libraries[existingIndex] = library;
                }
                else
                {
                    libraryIndexes[key] = merged.Libraries.Count;
                    merged.Libraries.Add(library);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(declaredId)) merged.Id = declaredId;
        if (merged.Downloads?.Client is null && !string.IsNullOrWhiteSpace(merged.Jar) &&
            !string.Equals(merged.Jar, versionId, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                MinecraftVersionJson jarProvider = await LoadVersionJsonOneAsync(merged.Jar, cancellationToken).ConfigureAwait(false);
                if (jarProvider.Downloads?.Client is not null)
                {
                    merged.Downloads = new MinecraftDownloads { Client = jarProvider.Downloads.Client, Server = merged.Downloads?.Server };
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new InvalidOperationException($"无法解析客户端 JAR 来源版本 {merged.Jar}：{e.Message}", e);
            }
        }
        return merged;
    }

    private static string LibraryCoordinateKey(string? coordinate)
    {
        if (string.IsNullOrWhiteSpace(coordinate)) return string.Empty;
        string name = coordinate;
        string extension = string.Empty;
        int at = name.IndexOf('@');
        if (at >= 0)
        {
            extension = name[at..];
            name = name[..at];
        }
        string[] parts = name.Split(':');
        if (parts.Length < 3) return name.ToLowerInvariant();
        string classifier = parts.Length >= 4 ? parts[3] : string.Empty;
        return (parts[0] + ":" + parts[1] + ":" + classifier + extension).ToLowerInvariant();
    }

    // ---------------------------------------------------------------- natives

    private static async Task<int> ExtractNativeArchiveAsync(string jarPath, string destinationRoot, string[] excludes, CancellationToken cancellationToken)
    {
        int extracted = 0;
        await using FileStream stream = new(jarPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            string entryPath = NormalizeArchivePath(entry.FullName);
            if (entryPath.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) continue;
            bool excluded = false;
            foreach (string prefix in excludes)
            {
                if (entryPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || string.Equals(entryPath, prefix, StringComparison.OrdinalIgnoreCase))
                {
                    excluded = true;
                    break;
                }
            }
            if (excluded) continue;

            string target = ResolveRelativePath(destinationRoot, entryPath);
            string? directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            await using Stream source = entry.Open();
            await using FileStream destination = new(target, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            extracted++;
        }
        return extracted;
    }

    private static string NormalizeArchivePath(string path) => path.Replace('\\', '/').TrimStart('/');

    // ---------------------------------------------------------------- 公共辅助

    /// <summary>校验版本 ID 只能作为 versions/ 下的单个文件夹名使用。</summary>
    public static void ValidateVersionId(string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId)) throw new InvalidOperationException("版本 ID 不能为空。");
        if (versionId is "." or ".." || versionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            versionId.Contains('/') || versionId.Contains('\\'))
        {
            throw new InvalidOperationException($"版本 ID 包含不安全字符：{versionId}");
        }
    }

    /// <summary>Maven 坐标 → 文件系统相对路径（支持 @extension 后缀）。</summary>
    public static string CreateMavenPath(string coordinate)
    {
        if (string.IsNullOrWhiteSpace(coordinate)) return string.Empty;

        string extension = "jar";
        string name = coordinate;
        int at = coordinate.IndexOf('@');
        if (at >= 0)
        {
            extension = coordinate[(at + 1)..];
            name = coordinate[..at];
        }

        string[] parts = name.Split(':');
        if (parts.Length is < 3 or > 4) return string.Empty;
        foreach (string part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) return string.Empty;
        }

        string groupPath = parts[0].Replace('.', Path.DirectorySeparatorChar);
        string classifier = parts.Length == 4 ? "-" + parts[3] : string.Empty;
        return Path.Combine(groupPath, parts[1], parts[2], $"{parts[1]}-{parts[2]}{classifier}.{extension}");
    }

    /// <summary>旧版本 natives 的 Maven 路径回退（name + classifier）。</summary>
    public static string CreateNativeMavenPath(string coordinate, string classifier)
    {
        if (string.IsNullOrWhiteSpace(coordinate) || string.IsNullOrWhiteSpace(classifier)) return string.Empty;
        string[] parts = coordinate.Split(':');
        if (parts.Length is < 3 or > 4) return string.Empty;
        foreach (string part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) return string.Empty;
        }
        return Path.Combine(parts[0].Replace('.', Path.DirectorySeparatorChar), parts[1], parts[2], $"{parts[1]}-{parts[2]}-{classifier}.jar");
    }

    private static string ExpandArchPlaceholder(string classifier)
    {
        string arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture is
            System.Runtime.InteropServices.Architecture.X64 or System.Runtime.InteropServices.Architecture.Arm64 ? "64" : "32";
        return classifier.Replace("${arch}", arch, StringComparison.Ordinal);
    }

    private static string InferMavenRepository(string coordinate)
    {
        if (string.IsNullOrWhiteSpace(coordinate)) return "https://libraries.minecraft.net/";
        string lower = coordinate.ToLowerInvariant();
        if (lower.StartsWith("net.neoforged", StringComparison.Ordinal)) return "https://maven.neoforged.net/releases/";
        if (lower.StartsWith("net.minecraft", StringComparison.Ordinal) && lower.EndsWith(":srg", StringComparison.Ordinal))
        {
            return "https://maven.neoforged.net/releases/";
        }
        if (lower.StartsWith("net.minecraftforge", StringComparison.Ordinal)) return "https://maven.minecraftforge.net/";
        if (lower.StartsWith("cpw.mods", StringComparison.Ordinal)) return "https://maven.minecraftforge.net/";
        if (lower.StartsWith("net.fabricmc", StringComparison.Ordinal)) return "https://maven.fabricmc.net/";
        if (lower.StartsWith("org.quiltmc", StringComparison.Ordinal)) return "https://maven.quiltmc.org/";
        if (lower.StartsWith("io.github.llamalad7", StringComparison.Ordinal)) return "https://maven.neoforged.net/releases/";
        return "https://libraries.minecraft.net/";
    }

    private static string ResolveRelativePath(string root, string relativePath)
    {
        string normalizedRoot = Path.GetFullPath(root);
        string target = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        if (!string.Equals(target, normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
            !target.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"下载路径超出 Minecraft 目录：{relativePath}");
        }
        return target;
    }

    private static MinecraftVersionJson ParseVersionJson(byte[] bytes, string versionId)
    {
        try
        {
            return JsonSerializer.Deserialize<MinecraftVersionJson>(bytes, JsonOptions)
                   ?? throw new InvalidOperationException($"版本 {versionId} 的 JSON 为空。");
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"版本 {versionId} 的 JSON 解析失败：{e.Message}", e);
        }
    }

    private static async Task WriteAllBytesAtomicallyAsync(string path, byte[] data, CancellationToken cancellationToken)
    {
        string temporaryPath = path + TemporarySuffix;
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, data, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            DeleteIfExists(temporaryPath);
        }
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // 清理失败不影响主流程
        }
    }

    private static void Report(
        IProgress<MinecraftInstallProgress>? progress,
        int stageIndex,
        string stageName,
        string detail,
        long completedBytes,
        long totalBytes,
        int completedFiles,
        int totalFiles,
        double bytesPerSecond)
    {
        progress?.Report(new MinecraftInstallProgress(stageIndex, stageName, detail, completedBytes, totalBytes, completedFiles, totalFiles, bytesPerSecond));
    }

    private sealed class MinecraftDownloadFile
    {
        public string Url { get; init; } = string.Empty;
        public string TargetPath { get; init; } = string.Empty;
        public string? Sha1 { get; init; }
        public long Size { get; init; }
        public string DisplayName { get; init; } = string.Empty;
    }

    private sealed class InstallCounters
    {
        private long _completedBytes;
        private long _networkBytes;
        private long _completedFiles;
        private long _totalBytes;
        private long _totalFiles;
        private long _windowBytes;
        private long _windowStartTicks;
        private double _smoothedBytesPerSecond;

        public long CompletedBytes => Interlocked.Read(ref _completedBytes);
        public long NetworkBytes => Interlocked.Read(ref _networkBytes);
        public long CompletedFiles => Interlocked.Read(ref _completedFiles);
        public long TotalBytes => Interlocked.Read(ref _totalBytes);
        public long TotalFiles => Interlocked.Read(ref _totalFiles);

        public void AddProgressBytes(long value)
        {
            Interlocked.Add(ref _networkBytes, value);
            Interlocked.Add(ref _completedBytes, value);
            Interlocked.Add(ref _windowBytes, value);
        }

        public void AddCompleted(long value) => Interlocked.Add(ref _completedBytes, value);
        public void IncrementCompletedFiles() => Interlocked.Increment(ref _completedFiles);
        public void AddTotalBytes(long value) => Interlocked.Add(ref _totalBytes, value);
        public void AddTotalFiles(int value) => Interlocked.Add(ref _totalFiles, value);

        /// <summary>
        /// 最近下载速率（EMA 平滑，时间常数约 1.5 秒）。
        /// 全程平均速率会把"限流前快、限流后涓流"糊成一团，看不出恢复；瞬时值才是用户要的信号。
        /// 只允许进度报告路径调用（ReportThrottled 已用 CAS 串行化，阶段收尾报告是顺序执行）。
        /// </summary>
        public double WindowBytesPerSecond()
        {
            long now = Environment.TickCount64;
            long windowBytes = Interlocked.Exchange(ref _windowBytes, 0);
            long windowStart = Interlocked.CompareExchange(ref _windowStartTicks, 0, 0);
            if (windowStart == 0)
            {
                Interlocked.CompareExchange(ref _windowStartTicks, now, 0);
                return _smoothedBytesPerSecond;
            }

            double seconds = (now - windowStart) / 1000.0;
            Interlocked.Exchange(ref _windowStartTicks, now);
            double instant = seconds > 0.05 ? windowBytes / seconds : 0;
            double alpha = Math.Clamp(seconds / 1.5, 0, 1);
            _smoothedBytesPerSecond += alpha * (instant - _smoothedBytesPerSecond);
            return _smoothedBytesPerSecond;
        }
    }
}

/// <summary>下载停滞看门狗：进度感知，只有连接彻底停滞超过阈值才取消尝试。</summary>
internal sealed class StallWatchdog : IDisposable
{
    // 涓流判定：镜像限速后连接不会完全断流，而是每 1-2 分钟吐一小口数据，
    // 每次 Touch 都会重置零字节超时，任务便无限爬行。低于该速率视为停滞。
    private const long TrickleWindowMs = 60_000;
    private const long TrickleMinimumBytes = 64 * 1024;

    private readonly Action _onStall;
    private readonly TimeSpan _timeout;
    private readonly Timer _timer;
    private long _lastActivityTicks;
    private long _windowBytes;
    private long _windowStartTicks;
    private int _disposed;

    public StallWatchdog(Action onStall, TimeSpan timeout)
    {
        _onStall = onStall;
        _timeout = timeout;
        _lastActivityTicks = Environment.TickCount64;
        _windowStartTicks = _lastActivityTicks;
        _timer = new Timer(_ => CheckStall(), null, 15_000, 15_000);
    }

    public void Touch() => Interlocked.Exchange(ref _lastActivityTicks, Environment.TickCount64);

    /// <summary>记录本次实际收到的字节数（涓流判定的依据）。</summary>
    public void AddBytes(long delta) => Interlocked.Add(ref _windowBytes, delta);

    private void CheckStall()
    {
        if (Volatile.Read(ref _disposed) != 0 || DownloadPauseGate.IsPaused)
        {
            // 暂停期间没有字节是正常的：把停滞与涓流的计时基准顺延到恢复之后，避免恢复即误判
            long pausedAt = Environment.TickCount64;
            Interlocked.Exchange(ref _lastActivityTicks, pausedAt);
            Interlocked.Exchange(ref _windowStartTicks, pausedAt);
            return;
        }

        if (Environment.TickCount64 - Interlocked.Read(ref _lastActivityTicks) >= (long)_timeout.TotalMilliseconds)
        {
            _onStall();
            return;
        }

        // 窗口速率检查：一个窗口结束时分摊字节不足阈值，说明连接被限到涓流，重试比干等更快
        long now = Environment.TickCount64;
        long windowStart = Interlocked.Read(ref _windowStartTicks);
        if (now - windowStart < TrickleWindowMs)
        {
            return;
        }

        long windowBytes = Interlocked.Exchange(ref _windowBytes, 0);
        Interlocked.Exchange(ref _windowStartTicks, now);
        if (windowBytes < TrickleMinimumBytes)
        {
            _onStall();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer.Dispose();
    }
}

/// <summary>断点续传的临时文件与远端内容不一致（416）。</summary>
internal sealed class RangeMismatchException : Exception
{
    public RangeMismatchException() : base("断点信息与远端文件不一致，已重置下载。")
    {
    }
}

/// <summary>
/// 游戏文件完整性校验器：沿 inheritsFrom / jar 链检查版本 JSON、客户端 JAR、库与日志配置，缺失时补全。
/// </summary>
public sealed class MinecraftGameFileVerifier
{
    private readonly MinecraftDownload _download;

    public MinecraftGameFileVerifier(MinecraftDownload download)
    {
        ArgumentNullException.ThrowIfNull(download);
        _download = download;
    }

    /// <summary>校验并补全，返回补全的条目数（0 = 无需补全）。</summary>
    public async Task<int> VerifyAndRepairAsync(
        string versionId,
        bool hasCustomResolution = true,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        MinecraftDownload.ValidateVersionId(versionId);
        string root = _download.MinecraftDirectory;
        int repaired = 0;
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        Queue<string> pending = new();
        pending.Enqueue(versionId);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string currentId = pending.Dequeue();
            if (string.IsNullOrWhiteSpace(currentId) || !visited.Add(currentId)) continue;

            string versionDirectory = Path.Combine(root, "versions", currentId);
            string jsonPath = Path.Combine(versionDirectory, currentId + ".json");

            if (!File.Exists(jsonPath))
            {
                Reporter.Report(status, $"版本描述 {currentId} 缺失，正在重新下载…");
                string? metadataUrl = await MinecraftDownload.FindVersionMetadataUrlAsync(currentId, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(metadataUrl))
                {
                    await _download.InstallAsync(currentId, metadataUrl, null, cancellationToken).ConfigureAwait(false);
                    repaired++;
                }
                continue;
            }

            byte[] jsonBytes;
            try
            {
                jsonBytes = await File.ReadAllBytesAsync(jsonPath, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                Reporter.Report(status, $"版本描述 {currentId} 读取失败，正在重新下载…");
                string? metadataUrl = await MinecraftDownload.FindVersionMetadataUrlAsync(currentId, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(metadataUrl))
                {
                    await _download.InstallAsync(currentId, metadataUrl, null, cancellationToken).ConfigureAwait(false);
                    repaired++;
                }
                continue;
            }

            MinecraftVersionJson metadata;
            try
            {
                metadata = await _download.LoadVersionJsonAsync(currentId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Reporter.Report(status, $"版本描述 {currentId} 解析失败：{e.Message}");
                continue;
            }

            List<string> missing = VerifyLibraries(root, metadata, hasCustomResolution);
            if (missing.Count > 0)
            {
                Reporter.Report(status, $"版本 {currentId} 缺少 {missing.Count} 个库文件，正在补全…");
                try
                {
                    await _download.InstallFromMetadataBytesAsync(currentId, jsonBytes, null, cancellationToken).ConfigureAwait(false);
                    repaired++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    Reporter.Report(status, $"补全失败：{e.Message}");
                }
            }

            if (metadata.Downloads?.Client is not null)
            {
                string jarPath = Path.Combine(versionDirectory, currentId + ".jar");
                if (!File.Exists(jarPath))
                {
                    Reporter.Report(status, $"客户端文件 {currentId} 缺失，正在重新下载…");
                    string? metadataUrl = await MinecraftDownload.FindVersionMetadataUrlAsync(currentId, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(metadataUrl))
                    {
                        await _download.InstallAsync(currentId, metadataUrl, null, cancellationToken).ConfigureAwait(false);
                        repaired++;
                    }
                }
            }

            if (metadata.Logging?.Client?.File is not null)
            {
                string loggingPath = Path.Combine(root, "assets", MinecraftDownload.LoggingConfigDirectory, metadata.Logging.Client.File.Id);
                if (!File.Exists(loggingPath))
                {
                    Reporter.Report(status, "正在补全日志配置…");
                    try
                    {
                        await DownloadLoggingConfigAsync(root, metadata, cancellationToken).ConfigureAwait(false);
                        repaired++;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        Reporter.Report(status, $"补全日志配置失败：{e.Message}");
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(metadata.InheritsFrom)) pending.Enqueue(metadata.InheritsFrom);
            if (!string.IsNullOrWhiteSpace(metadata.Jar)) pending.Enqueue(metadata.Jar);
        }

        return repaired;
    }

    private static async Task DownloadLoggingConfigAsync(string root, MinecraftVersionJson metadata, CancellationToken cancellationToken)
    {
        MinecraftDownloadInfo file = metadata.Logging!.Client!.File!;
        string target = Path.Combine(root, "assets", MinecraftDownload.LoggingConfigDirectory, file.Id);
        await MinecraftDownload.DownloadFileToPathAsync(file.Url, target, file.Sha1, file.Size, null, cancellationToken).ConfigureAwait(false);
    }

    private static List<string> VerifyLibraries(string root, MinecraftVersionJson metadata, bool hasCustomResolution)
    {
        List<string> missing = [];
        string librariesDirectory = Path.Combine(root, "libraries");
        Dictionary<string, bool> features = MinecraftRuleEvaluator.DefaultFeatures();
        features["has_custom_resolution"] = hasCustomResolution;

        foreach (MinecraftLibrary library in metadata.Libraries)
        {
            if (!MinecraftRuleEvaluator.IsAllowed(library.Rules, features)) continue;

            string artifactPath = ArtifactRelativePath(library);
            if (artifactPath.Length > 0)
            {
                string fullPath = Path.Combine(librariesDirectory, artifactPath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(fullPath)) missing.Add(fullPath);
            }

            string nativePath = NativeRelativePath(library);
            if (nativePath.Length > 0)
            {
                string nativeFullPath = Path.Combine(librariesDirectory, nativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(nativeFullPath)) missing.Add(nativeFullPath);
            }
        }
        return missing;
    }

    private static string ArtifactRelativePath(MinecraftLibrary library)
    {
        if (library.Downloads?.Artifact is not null && !string.IsNullOrWhiteSpace(library.Downloads.Artifact.Path))
        {
            return library.Downloads.Artifact.Path;
        }
        if (library.Natives is { Count: > 0 }) return string.Empty;
        return MinecraftDownload.CreateMavenPath(library.Name);
    }

    private static string NativeRelativePath(MinecraftLibrary library)
    {
        if (library.Natives is null) return string.Empty;
        if (!library.Natives.TryGetValue(MinecraftRuleEvaluator.OsName, out string? rawClassifier)) return string.Empty;

        string arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture is
            System.Runtime.InteropServices.Architecture.X64 or System.Runtime.InteropServices.Architecture.Arm64 ? "64" : "32";
        string classifier = rawClassifier.Replace("${arch}", arch, StringComparison.Ordinal);

        if (library.Downloads?.Classifiers is not null &&
            library.Downloads.Classifiers.TryGetValue(classifier, out MinecraftDownloadInfo? native) &&
            !string.IsNullOrWhiteSpace(native.Path))
        {
            return native.Path;
        }
        return MinecraftDownload.CreateNativeMavenPath(library.Name, classifier);
    }

    private static class Reporter
    {
        public static void Report(IProgress<string>? status, string message) => status?.Report(message);
    }
}

/// <summary>下载任务阶段。</summary>
public enum MinecraftDownloadPhase
{
    Idle,
    Preparing,
    Downloading,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>下载任务快照（对外发布的不可变进度状态）。</summary>
public sealed record MinecraftDownloadSnapshot
{
    public long Revision { get; init; }
    public long TaskId { get; init; }
    public MinecraftDownloadPhase Phase { get; init; }
    public string VersionId { get; init; } = string.Empty;
    public int StageIndex { get; init; }
    public string StageName { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public double Percentage { get; init; }
    public long CompletedBytes { get; init; }
    public long TotalBytes { get; init; }
    public int CompletedFiles { get; init; }
    public int TotalFiles { get; init; }
    public double BytesPerSecond { get; init; }

    public bool IsActive => Phase is MinecraftDownloadPhase.Preparing or MinecraftDownloadPhase.Downloading;

    public static MinecraftDownloadSnapshot Idle => new()
    {
        Phase = MinecraftDownloadPhase.Idle,
        StageName = "尚无下载任务",
        Detail = "请在资源下载页选择 Minecraft 版本。",
    };
}

/// <summary>
/// 下载任务状态机：包装 <see cref="MinecraftDownload"/>，用阶段 + 快照对外发布进度，
/// 并提供取消 / 暂停 / 恢复。
/// </summary>
public sealed class MinecraftDownloadTask
{
    private readonly object _gate = new();
    private readonly MinecraftDownload _download;
    private MinecraftDownloadSnapshot _current = MinecraftDownloadSnapshot.Idle;
    private CancellationTokenSource? _active;
    private long _revision;
    private long _taskId;
    private readonly List<string> _warnings = [];

    public MinecraftDownloadTask(MinecraftDownload download)
    {
        ArgumentNullException.ThrowIfNull(download);
        _download = download;
    }

    public event Action<MinecraftDownloadSnapshot>? Changed;

    public MinecraftDownloadSnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>开始下载并安装原版版本；已有活跃任务时返回 false。</summary>
    public async Task<bool> StartAsync(MinecraftVersion version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (string.IsNullOrWhiteSpace(version.Id) || string.IsNullOrWhiteSpace(version.Url)) return false;

        lock (_gate)
        {
            if (_active is not null) return false;
        }

        CancellationTokenSource taskCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        long taskId;
        lock (_gate)
        {
            _active = taskCts;
            _warnings.Clear();
            taskId = ++_taskId;
        }

        Publish(new MinecraftDownloadSnapshot
        {
            Revision = NextRevision(),
            TaskId = taskId,
            Phase = MinecraftDownloadPhase.Preparing,
            VersionId = version.Id,
            StageIndex = 1,
            StageName = "获取版本元数据",
            Detail = $"正在准备下载 Minecraft {version.Id}",
        });

        try
        {
            IProgress<MinecraftInstallProgress> progress = new InlineProgress<MinecraftInstallProgress>(update =>
            {
                if (taskCts.IsCancellationRequested) return;
                Publish(new MinecraftDownloadSnapshot
                {
                    Revision = NextRevision(),
                    TaskId = taskId,
                    Phase = MinecraftDownloadPhase.Downloading,
                    VersionId = version.Id,
                    StageIndex = update.StageIndex,
                    StageName = update.StageName,
                    Detail = update.Detail,
                    Percentage = update.Percentage,
                    CompletedBytes = update.CompletedBytes,
                    TotalBytes = update.TotalBytes,
                    CompletedFiles = update.CompletedFiles,
                    TotalFiles = update.TotalFiles,
                    BytesPerSecond = update.BytesPerSecond,
                });
            });

            await _download.InstallAsync(version, progress, taskCts.Token).ConfigureAwait(false);

            MinecraftDownloadSnapshot completed = Current with
            {
                Revision = NextRevision(),
                Phase = MinecraftDownloadPhase.Completed,
                StageIndex = MinecraftDownload.StageCount,
                StageName = "完成校验与安装",
                Detail = $"Minecraft {version.Id} 下载并安装完成",
                Percentage = 100,
            };
            List<string> warnings = TakeWarnings();
            if (warnings.Count > 0)
            {
                completed = completed with { Detail = completed.Detail + "（警告：" + string.Join("；", warnings) + "）" };
            }
            Publish(completed);
            return true;
        }
        catch (OperationCanceledException) when (taskCts.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            PublishTerminal(taskId, MinecraftDownloadPhase.Cancelled, "下载已取消", $"Minecraft {version.Id} 下载已取消");
            return false;
        }
        catch (Exception e)
        {
            PublishTerminal(taskId, MinecraftDownloadPhase.Failed, "下载失败", e.Message);
            return false;
        }
        finally
        {
            lock (_gate) _active = null;
            taskCts.Dispose();
            DownloadPauseGate.Resume();
        }
    }

    /// <summary>取消当前活跃任务。</summary>
    public bool CancelActive()
    {
        CancellationTokenSource? active;
        lock (_gate) active = _active;
        if (active is null) return false;
        active.Cancel();
        return true;
    }

    /// <summary>暂停当前活跃任务（读取循环停在暂停门）。</summary>
    public bool PauseActive()
    {
        MinecraftDownloadSnapshot previous = Current;
        if (!previous.IsActive || DownloadPauseGate.IsPaused) return false;
        DownloadPauseGate.Pause();
        Publish(previous with { Revision = NextRevision(), Detail = "已暂停（点击「继续下载」恢复）" });
        return true;
    }

    /// <summary>恢复被暂停的下载。</summary>
    public bool ResumeActive()
    {
        if (!DownloadPauseGate.IsPaused) return false;
        MinecraftDownloadSnapshot previous = Current;
        DownloadPauseGate.Resume();
        if (previous.IsActive)
        {
            Publish(previous with { Revision = NextRevision(), Detail = "已恢复下载" });
        }
        return true;
    }

    /// <summary>记录一条任务级非致命警告（随完成快照展示）。</summary>
    public void AddWarning(string text)
    {
        lock (_gate) _warnings.Add(text);
    }

    private List<string> TakeWarnings()
    {
        lock (_gate)
        {
            List<string> warnings = [.. _warnings];
            _warnings.Clear();
            return warnings;
        }
    }

    private void PublishTerminal(long taskId, MinecraftDownloadPhase phase, string stageName, string detail)
    {
        MinecraftDownloadSnapshot previous = Current;
        if (previous.TaskId != taskId) return;
        Publish(previous with
        {
            Revision = NextRevision(),
            Phase = phase,
            StageName = stageName,
            Detail = detail,
        });
    }

    private long NextRevision() => Interlocked.Increment(ref _revision);

    private void Publish(MinecraftDownloadSnapshot snapshot)
    {
        MinecraftDownloadSnapshot effective = snapshot.Revision == 0 ? snapshot with { Revision = NextRevision() } : snapshot;
        Action<MinecraftDownloadSnapshot>? handler;
        lock (_gate)
        {
            if (effective.Revision < _current.Revision) return;
            _current = effective;
            handler = Changed;
        }
        if (handler is null) return;
        try
        {
            handler(effective);
        }
        catch (Exception)
        {
            // 订阅者异常不影响发布方
        }
    }
}

/// <summary>同步转发的进度实现（不捕获 SynchronizationContext）。</summary>
public sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
