using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Mofumofu.Launcher.Services;

/// <summary>
/// GitHub Releases 自动更新：查询最新版本 → 下载资产 → 重启安装。
/// 资产约定：优先挑选名字含 "win" 的 .zip（整包更新），其次任意 .zip / .exe（单文件替换）。
/// </summary>
public static class GitHubUpdater
{
    public const string Repo = "redstore-noob/Mofumofu";
    private const string ApiLatest = "https://api.github.com/repos/" + Repo + "/releases/latest";

    private static readonly HttpClient ApiClient = CreateClient(TimeSpan.FromSeconds(30));
    private static readonly HttpClient DownloadClient = CreateClient(Timeout.InfiniteTimeSpan);

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            // 下载大文件时不允许连接级超时杀掉慢速网络
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = timeout,
        };
        // GitHub API 强制要求 User-Agent
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mofumofu-Launcher-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>一次版本检查的结果。</summary>
    public sealed record ReleaseInfo(
        string Tag,
        string Name,
        string Notes,
        string? AssetUrl,
        string? AssetName,
        long AssetSize);

    /// <summary>
    /// 查询最新 release；没有可下载资产时 AssetUrl 为 null（只能提示用户手动去下载）。
    /// 网络/解析失败抛异常，由调用方展示。
    /// </summary>
    public static async Task<ReleaseInfo> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        using var response = await ApiClient.GetAsync(ApiLatest, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;

        string tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        string name = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
        string notes = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? string.Empty : string.Empty;

        (string? url, string? assetName, long size) = PickWindowsAsset(root);
        return new ReleaseInfo(tag, name, notes, url, assetName, size);
    }

    /// <summary>release 的 tag 是否比当前启动器版本新（支持 v 前缀；无法解析时视为不更新）。</summary>
    public static bool IsNewer(string tag)
    {
        if (!TryParseVersion(tag, out var latest)) return false;
        return latest > CurrentVersion();
    }

    public static Version CurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (TryParseVersion(informational, out var version)) return version;
        return assembly.GetName().Version ?? new Version(0, 0, 0);
    }

    private static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;

        // 兼容各种写法：v1.2.3 / 1.2.3 / "public beta 0.1.0" / 1.2.3-beta.1 —— 取第一段数字版本
        var match = System.Text.RegularExpressions.Regex.Match(text, @"\d+(?:\.\d+)*");
        if (match.Success == false) return false;
        return Version.TryParse(match.Value, out version!);
    }

    private static (string? Url, string? Name, long Size) PickWindowsAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.GetArrayLength() == 0)
        {
            return (null, null, 0);
        }

        (string? Url, string? Name, long Size) best = (null, null, 0);
        foreach (var asset in assets.EnumerateArray())
        {
            string assetName = asset.GetProperty("name").GetString() ?? string.Empty;
            string? url = asset.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() : null;
            long size = asset.TryGetProperty("size", out var sizeEl) ? sizeEl.GetInt64() : 0;
            if (string.IsNullOrEmpty(url)) continue;

            bool isZip = assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            bool isExe = assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            bool isWin = assetName.Contains("win", StringComparison.OrdinalIgnoreCase);
            if (!isZip && !isExe) continue;

            // 优先 win + zip，其次任意 zip，再其次 exe
            int score = (isWin, isZip) switch
            {
                (true, true) => 3,
                (_, true) => 2,
                (_, false) => 1, // exe 单文件替换
            };
            if (best.Url is null || score > ScoreOf(best.Name))
            {
                best = (url, assetName, size);
            }
        }

        return best;

        static int ScoreOf(string? name) => (name?.Contains("win", StringComparison.OrdinalIgnoreCase) ?? false,
                name?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ?? false) switch
        {
            (true, true) => 3,
            (_, true) => 2,
            _ => 1,
        };
    }

    /// <summary>
    /// 下载更新包到 %LOCALAPPDATA%\Mofumofu.Launcher\update\，返回本地文件路径。
    /// </summary>
    public static async Task<string> DownloadAsync(
        ReleaseInfo release,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        if (release.AssetUrl is null)
        {
            throw new InvalidOperationException("该 release 没有可下载的更新包。");
        }

        string directory = UpdateDirectory();
        Directory.CreateDirectory(directory);
        string targetPath = Path.Combine(directory, release.AssetName ?? "update.bin");

        using var response = await DownloadClient.GetAsync(
            release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;
            if (progress is not null && total is > 0)
            {
                progress.Report(Math.Clamp(written * 100.0 / total.Value, 0, 100));
            }
        }

        return targetPath;
    }

    /// <summary>
    /// 应用已下载的更新包：解压（zip）到暂存区 → 写一个等进程退出后替换文件的 cmd → 重启自身。
    /// 本方法在确认后调用，调用后进程会退出。
    /// </summary>
    public static void Apply(string downloadedPath)
    {
        string appDir = AppContext.BaseDirectory;
        string? appExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(appExe))
        {
            throw new InvalidOperationException("无法定位当前启动器可执行文件。");
        }

        string staging = Path.Combine(UpdateDirectory(), "staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);

        bool isZip = downloadedPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        string sourceDir;
        if (isZip)
        {
            ZipFile.ExtractToDirectory(downloadedPath, staging, overwriteFiles: true);
            // zip 若带单一顶层目录则进入它
            var entries = Directory.GetFileSystemEntries(staging);
            sourceDir = entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : staging;
        }
        else
        {
            sourceDir = Path.GetDirectoryName(downloadedPath)!;
        }

        string script = Path.Combine(UpdateDirectory(), "update.cmd");
        string backup = appExe + ".old";
        string lines = string.Join(Environment.NewLine,
            "@echo off",
            "timeout /t 2 /nobreak >nul",
            $"if exist \"{backup}\" del /f /q \"{backup}\"",
            $"copy /y \"{appExe}\" \"{backup}\" >nul",
            isZip
                ? $"xcopy /e /i /y \"{sourceDir}\\*\" \"{appDir}\\\" >nul"
                : $"copy /y \"{downloadedPath}\" \"{appExe}\" >nul",
            $"if exist \"{staging}\" rmdir /s /q \"{staging}\"",
            $"if exist \"{downloadedPath}\" del /f /q \"{downloadedPath}\"",
            $"start \"\" \"{appExe}\"",
            "del \"%~f0\"");
        File.WriteAllText(script, lines);

        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        _ = process;
        Environment.Exit(0);
    }

    private static string UpdateDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mofumofu.Launcher", "update");
}
