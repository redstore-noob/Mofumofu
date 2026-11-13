using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
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
    private const int StreamCopyBufferSize = 81920;

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
        long AssetSize)
    {
        /// <summary>release 资产绑定的 SHA256 摘要（GitHub API 的 digest 字段），无则 null。</summary>
        public string? AssetDigest { get; init; }
    }

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

        (string? url, string? assetName, long size, string? digest) = PickWindowsAsset(root);
        return new ReleaseInfo(tag, name, notes, url, assetName, size) { AssetDigest = digest };
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

    private static (string? Url, string? Name, long Size, string? Digest) PickWindowsAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.GetArrayLength() == 0)
        {
            return (null, null, 0, null);
        }

        (string? Url, string? Name, long Size, string? Digest) best = (null, null, 0, null);
        foreach (var asset in assets.EnumerateArray())
        {
            string assetName = asset.GetProperty("name").GetString() ?? string.Empty;
            // 远端资产名先过白名单：拒绝路径分隔符、.. 及 cmd 元字符，避免越界写文件与命令注入
            if (!IsSafeAssetName(assetName)) continue;
            string? url = asset.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() : null;
            long size = asset.TryGetProperty("size", out var sizeEl) ? sizeEl.GetInt64() : 0;
            // GitHub Releases API 的 digest（形如 "sha256:<hex>"），与资产绑定的官方摘要
            string? digest = asset.TryGetProperty("digest", out var digestEl) ? digestEl.GetString() : null;
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
                best = (url, assetName, size, digest);
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

    private static bool IsSafeAssetName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 255) return false;
        if (name is "." or "..") return false;
        foreach (char c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '.' && c != '-' && c != '_') return false;
        }
        return true;
    }

    private static bool IsGitHubHost(string host)
    {
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeSha256(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        string value = digest.Trim();
        const string prefix = "sha256:";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[prefix.Length..];
        }
        if (value.Length != 64) return null;
        foreach (char c in value)
        {
            if (!char.IsAsciiHexDigit(c)) return null;
        }
        return value;
    }

    /// <summary>
    /// fail-closed 校验将拼进 cmd 脚本的路径：拒绝所有 cmd 元字符与控制字符，
    /// 并去掉尾部反斜杠（否则 "dir\" 会转义收尾引号）。
    /// </summary>
    private static string ValidateCmdPath(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"{description} 为空，已中止更新。");
        }
        foreach (char c in path)
        {
            if (c is '"' or '%' or '!' or '^' or '&' or '|' or '<' or '>' or '(' or ')' or '\r' or '\n'
                || char.IsControl(c))
            {
                throw new InvalidOperationException(
                    $"{description} 包含不允许的字符（U+{(int)c:X4}），已中止更新以避免命令注入。");
            }
        }
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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

        // 下载地址必须 https 且落在 GitHub 自有域：防止 release JSON 被篡改后把更新包指向外站
        if (!Uri.TryCreate(release.AssetUrl, UriKind.Absolute, out Uri? assetUri) ||
            assetUri.Scheme != Uri.UriSchemeHttps ||
            !IsGitHubHost(assetUri.Host))
        {
            throw new InvalidOperationException("更新包下载地址非法（非 GitHub HTTPS 源），已中止下载。");
        }

        // AssetName 来自远端 JSON，先过文件名白名单再拼路径，杜绝 .. / 路径分隔符 / 绝对路径越界
        string assetName = release.AssetName ?? "update.bin";
        if (!IsSafeAssetName(assetName))
        {
            throw new InvalidOperationException("更新包文件名包含非法字符，已中止下载。");
        }

        string directory = UpdateDirectory();
        Directory.CreateDirectory(directory);
        string targetPath = Path.Combine(directory, assetName);

        using var response = await DownloadClient.GetAsync(
            release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long written = 0;
            await using (var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[StreamCopyBufferSize];
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
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // 落盘后校验：写入字节数必须同时匹配 release 绑定大小与响应 Content-Length
            if (release.AssetSize > 0 && written != release.AssetSize)
            {
                throw new InvalidOperationException("更新包大小与 release 记录不一致，已中止下载。");
            }
            if (total is > 0 && written != total.Value)
            {
                throw new InvalidOperationException("更新包下载不完整，已中止下载。");
            }

            // release 绑定了 sha256 摘要时校验落盘文件哈希；摘要存在但格式非法一律中止
            if (!string.IsNullOrWhiteSpace(release.AssetDigest))
            {
                string? expected = NormalizeSha256(release.AssetDigest);
                if (expected is null)
                {
                    throw new InvalidOperationException("release 绑定的 SHA256 摘要格式非法，已中止下载。");
                }
                await using var verify = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                byte[] hash = await SHA256.HashDataAsync(verify, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(Convert.ToHexString(hash), expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("更新包 SHA256 校验失败，已中止下载。");
                }
            }

            return targetPath;
        }
        catch
        {
            // 任何失败都删除未通过校验的临时文件，绝不让损坏包进入安装流程
            try
            {
                if (File.Exists(targetPath)) File.Delete(targetPath);
            }
            catch
            {
                // 清理失败时保留原始异常向上抛出
            }
            throw;
        }
    }

    /// <summary>
    /// 应用已下载的更新包：解压（zip）到暂存区 → 写一个等进程退出后替换文件的 cmd → 重启自身。
    /// 本方法在确认后调用，调用后进程会退出。
    /// </summary>
    public static void Apply(string downloadedPath)
    {
        // 所有将进入 cmd 脚本的路径先过 fail-closed 白名单校验并规范化（含去掉尾反斜杠）
        string appDir = ValidateCmdPath(AppContext.BaseDirectory, "安装目录");
        string? appExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(appExe))
        {
            throw new InvalidOperationException("无法定位当前启动器可执行文件。");
        }
        appExe = ValidateCmdPath(appExe, "启动器可执行文件");

        string updateDir = UpdateDirectory();
        string staging = ValidateCmdPath(Path.Combine(updateDir, "staging"), "暂存目录");
        string script = ValidateCmdPath(Path.Combine(updateDir, "update.cmd"), "更新脚本");
        string backup = ValidateCmdPath(appExe + ".old", "备份文件");
        string safeDownloaded = ValidateCmdPath(downloadedPath, "更新包");

        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);

        bool isZip = safeDownloaded.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        string sourceDir;
        if (isZip)
        {
            // 解压前校验 zip 条目，拒绝 .. 穿越与绝对路径（Zip Slip）
            using (var archive = ZipFile.OpenRead(safeDownloaded))
            {
                foreach (var entry in archive.Entries)
                {
                    string name = entry.FullName.Replace('\\', '/');
                    foreach (var segment in name.Split('/'))
                    {
                        if (segment == "..")
                        {
                            throw new InvalidOperationException("更新包包含非法路径条目，已中止安装。");
                        }
                    }
                    if (name.StartsWith('/') || Path.IsPathRooted(entry.FullName))
                    {
                        throw new InvalidOperationException("更新包包含绝对路径条目，已中止安装。");
                    }
                }
            }
            ZipFile.ExtractToDirectory(safeDownloaded, staging, overwriteFiles: true);
            // zip 若带单一顶层目录则进入它
            var entries = Directory.GetFileSystemEntries(staging);
            sourceDir = entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : staging;
        }
        else
        {
            sourceDir = Path.GetDirectoryName(safeDownloaded)!;
        }
        sourceDir = ValidateCmdPath(sourceDir, "更新源目录");

        // 路径只出现在顶部的 set 行（值已校验），命令体一律用 %VAR% 引用，避免尾反斜杠转义引号
        string overwrite = isZip
            ? "xcopy /e /i /y \"%MFM_SOURCE%\\*\" \"%MFM_APPDIR%\" >nul"
            : "copy /y \"%MFM_DOWNLOAD%\" \"%MFM_APP%\" >nul";
        string lines = string.Join(Environment.NewLine,
            "@echo off",
            "setlocal",
            "timeout /t 2 /nobreak >nul",
            $"set \"MFM_APP={appExe}\"",
            $"set \"MFM_BACKUP={backup}\"",
            $"set \"MFM_SOURCE={sourceDir}\"",
            $"set \"MFM_APPDIR={appDir}\"",
            $"set \"MFM_DOWNLOAD={safeDownloaded}\"",
            $"set \"MFM_STAGING={staging}\"",
            "if not exist \"%MFM_APP%\" goto :fail",
            "copy /y \"%MFM_APP%\" \"%MFM_BACKUP%\" >nul",
            "if errorlevel 1 goto :fail",
            overwrite,
            "if errorlevel 1 goto :rollback",
            "if not exist \"%MFM_APP%\" goto :rollback",
            "if exist \"%MFM_STAGING%\" rmdir /s /q \"%MFM_STAGING%\"",
            "if exist \"%MFM_DOWNLOAD%\" del /f /q \"%MFM_DOWNLOAD%\"",
            "start \"\" \"%MFM_APP%\"",
            "del \"%~f0\" & exit /b 0",
            ":rollback",
            "copy /y \"%MFM_BACKUP%\" \"%MFM_APP%\" >nul",
            "if exist \"%MFM_STAGING%\" rmdir /s /q \"%MFM_STAGING%\"",
            "start \"\" \"%MFM_APP%\"",
            "echo Mofumofu update failed; rolled back to the previous version.",
            "del \"%~f0\" & exit /b 1",
            ":fail",
            "echo Mofumofu update aborted; the current version is unchanged.",
            "del \"%~f0\" & exit /b 1");
        File.WriteAllText(script, lines);

        if (!File.Exists(script))
        {
            throw new InvalidOperationException("更新脚本写入失败，已中止更新，当前版本保持不变。");
        }

        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c \"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("无法启动更新脚本，已中止更新，当前版本保持不变。", ex);
        }
        if (process is null)
        {
            throw new InvalidOperationException("无法启动更新脚本，已中止更新，当前版本保持不变。");
        }
        process.Dispose(); // 脚本要等本进程退出后才替换文件，这里只校验启动成功并释放句柄

        // 仅在脚本确认已启动后才退出；失败路径由调用方捕获异常保持进程存活，脚本内 errorlevel 负责回滚
        Environment.Exit(0);
    }

    private static string UpdateDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mofumofu.Launcher", "update");
}
