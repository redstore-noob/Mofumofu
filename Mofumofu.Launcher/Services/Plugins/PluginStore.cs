using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MofuCore.Download;

namespace Mofumofu.Launcher.Services.Plugins;

public enum PluginRepoHost
{
    GitHub,
    Gitee,
    GitCode,
}

public sealed record PluginRepoRef(PluginRepoHost Host, string Owner, string Repo)
{
    public string Slug => $"{Owner}/{Repo}";

    public string HostName => Host switch
    {
        PluginRepoHost.Gitee => "Gitee",
        PluginRepoHost.GitCode => "GitCode",
        _ => "GitHub",
    };

    public string HomeUrl => Host switch
    {
        PluginRepoHost.Gitee => $"https://gitee.com/{Slug}",
        PluginRepoHost.GitCode => $"https://gitcode.com/{Slug}",
        _ => $"https://github.com/{Slug}",
    };

    public string LatestReleaseApiUrl => Host switch
    {
        PluginRepoHost.Gitee => $"https://gitee.com/api/v5/repos/{Slug}/releases/latest",
        PluginRepoHost.GitCode => $"https://api.gitcode.com/api/v5/repos/{Slug}/releases/latest",
        _ => $"https://api.github.com/repos/{Slug}/releases/latest",
    };

    public string AssetDownloadUrl(string tag, string fileName) => Host switch
    {
        PluginRepoHost.Gitee => $"https://gitee.com/{Slug}/releases/download/{tag}/{Uri.EscapeDataString(fileName)}",
        PluginRepoHost.GitCode => $"https://api.gitcode.com/api/v5/repos/{Slug}/releases/{tag}/attach_files/{Uri.EscapeDataString(fileName)}/download",
        _ => $"https://github.com/{Slug}/releases/download/{tag}/{Uri.EscapeDataString(fileName)}",
    };
}

public sealed record PluginStoreEntry(
    string RepoUrl,
    string RepoName,
    string Name,
    string Version,
    string Notes,
    string? AssetUrl,
    string? AssetName,
    long AssetSize,
    string HostName = "GitHub")
{
    public string? AssetDigest { get; init; }
}

public static class PluginStore
{
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(20),
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mofumofu-Launcher-PluginStore");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>
    /// 解析仓库链接：github.com / gitee.com / gitcode.com 的 owner/repo（允许带尾部路径），
    /// 也兼容裸 owner/repo（按 GitHub 处理，旧配置如此保存）。
    /// </summary>
    public static PluginRepoRef? ParseRepo(string url)
    {
        string trimmed = (url ?? string.Empty).Trim().TrimEnd('/');
        if (trimmed.Length == 0) return null;

        foreach (var (marker, host) in new[]
                 {
                     ("github.com/", PluginRepoHost.GitHub),
                     ("gitee.com/", PluginRepoHost.Gitee),
                     ("gitcode.com/", PluginRepoHost.GitCode),
                 })
        {
            int at = trimmed.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;

            string rest = trimmed[(at + marker.Length)..];
            if (rest.StartsWith("repos/", StringComparison.OrdinalIgnoreCase))
            {
                rest = rest["repos/".Length..];
            }

            string[] parts = rest.Split('/');
            return parts.Length >= 2 && parts[0].Length > 0 && parts[1].Length > 0
                ? new PluginRepoRef(host, parts[0], parts[1])
                : null;
        }

        if (trimmed.Count(c => c == '/') == 1 &&
            trimmed.Contains(' ') == false &&
            trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase) == false)
        {
            string[] parts = trimmed.Split('/');
            return new PluginRepoRef(PluginRepoHost.GitHub, parts[0], parts[1]);
        }

        return null;
    }

    public static string? NormalizeRepo(string url) => ParseRepo(url)?.Slug;

    public static async Task<PluginStoreEntry> FetchLatestAsync(string repoUrl, CancellationToken cancellationToken = default)
    {
        PluginRepoRef reference = ParseRepo(repoUrl)
            ?? throw new InvalidOperationException("无法识别的仓库地址：" + repoUrl);

        using var response = await Client.GetAsync(reference.LatestReleaseApiUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;

        string tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        string name = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
        string notes = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? string.Empty : string.Empty;

        string? assetUrl = null;
        string? assetName = null;
        string? assetDigest = null;
        long assetSize = 0;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                string current = asset.GetProperty("name").GetString() ?? string.Empty;
                if (current.EndsWith(".nekoex", StringComparison.OrdinalIgnoreCase) == false) continue;

                // browser_download_url 来自远端 JSON，可能被指向外站：先做 HTTPS + 官方域白名单校验；
                // 不可信或缺失时改用平台约定自行拼的可信直链，仍不可信则跳过该资产。
                string? listed = asset.TryGetProperty("browser_download_url", out var urlEl)
                    ? urlEl.GetString()
                    : null;
                if (IsTrustedAssetUrl(listed) == false && tag.Length > 0)
                {
                    listed = reference.AssetDownloadUrl(tag, current);
                }
                if (IsTrustedAssetUrl(listed) == false) continue;

                assetDigest = asset.TryGetProperty("digest", out var digestEl) ? digestEl.GetString() : null;
                assetUrl = listed;
                assetName = current;
                assetSize = asset.TryGetProperty("size", out var sizeEl) && sizeEl.ValueKind == JsonValueKind.Number
                    ? sizeEl.GetInt64()
                    : 0;
                break;
            }
        }

        return new PluginStoreEntry(
            repoUrl, reference.Slug,
            string.IsNullOrWhiteSpace(name) ? reference.Repo : name,
            tag.TrimStart('v', 'V'),
            notes, assetUrl, assetName, assetSize,
            reference.HostName)
        {
            AssetDigest = assetDigest,
        };
    }

    public static async Task<string> InstallAsync(
        PluginStoreEntry entry,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (entry.AssetUrl is null)
        {
            throw new InvalidOperationException("该仓库的最新 Release 没有 .nekoex 资产，无法安装。");
        }

        // 落盘前再校验一次下载地址：必须 HTTPS 且落在官方域白名单内（fail-closed）。
        if (IsTrustedAssetUrl(entry.AssetUrl) == false)
        {
            throw new InvalidOperationException("插件资产的下载地址非法（非官方 HTTPS 源），已中止下载。");
        }

        string temporary = Path.Combine(Path.GetTempPath(), $"mofumofu-plugin-{Guid.NewGuid():N}.nekoex");
        try
        {
            var item = new FastDownloadItem(entry.AssetUrl, temporary, entry.AssetSize);
            await FastDownloader.DownloadFileAsync(
                item, 4,
                onBytes: received => progress?.Report(received),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // 落盘后按 asset 的 size / digest 校验文件，校验通过才交给安装器解压加载。
            VerifyDownloadedAsset(temporary, entry);

            return await NekoexInstaller.InstallAsync(temporary, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch {  }
        }
    }

    /// <summary>
    /// 下载地址校验：强制 HTTPS，且 host 落在 GitHub 自有域白名单；
    /// 同时兼容商店已支持的 Gitee / GitCode 官方域，避免它们被误拒。
    /// </summary>
    private static bool IsTrustedAssetUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) == false) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;

        string host = uri.Host;
        if (host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return host.Equals("gitee.com", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".gitee.com", StringComparison.OrdinalIgnoreCase)
               || host.Equals("gitcode.com", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".gitcode.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeSha256(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        string value = digest.Trim();
        const string prefix = "sha256:";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) value = value[prefix.Length..];
        if (value.Length != 64) return null;
        foreach (char c in value)
        {
            if (!char.IsAsciiHexDigit(c)) return null;
        }
        return value;
    }

    private static void VerifyDownloadedAsset(string path, PluginStoreEntry entry)
    {
        var info = new FileInfo(path);
        if (info.Exists == false)
        {
            throw new InvalidOperationException("商店插件下载失败：文件不存在。");
        }
        if (entry.AssetSize > 0 && info.Length != entry.AssetSize)
        {
            throw new InvalidOperationException("商店插件文件大小与 Release 声明不一致，已中止安装。");
        }
        if (string.IsNullOrWhiteSpace(entry.AssetDigest)) return;

        string? expected = NormalizeSha256(entry.AssetDigest);
        if (expected is null)
        {
            throw new InvalidOperationException("商店插件绑定的 SHA256 摘要格式非法，已中止安装。");
        }
        using FileStream stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        if (!string.Equals(Convert.ToHexString(hash), expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("商店插件 SHA256 校验失败，已中止安装。");
        }
    }

    public static bool IsUpToDate(PluginStoreEntry entry)
    {
        PluginRuntime? installed = PluginManager.ById(RepoId(entry));
        if (installed is null) return false;
        if (Version.TryParse(installed.Manifest.Version, out var current) == false) return false;
        if (Version.TryParse(entry.Version, out var latest) == false) return false;

        return current >= latest;
    }

    public static string RepoId(PluginStoreEntry entry) => entry.RepoName.Split('/')[1];
}
