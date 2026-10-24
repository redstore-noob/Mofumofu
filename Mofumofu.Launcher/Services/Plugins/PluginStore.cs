using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MofuCore.Download;

namespace Mofumofu.Launcher.Services.Plugins;

/// <summary>插件仓库托管平台：决定 Release API 与下载地址的拼法。</summary>
public enum PluginRepoHost
{
    GitHub,
    Gitee,
    GitCode,
}

/// <summary>解析后的仓库引用：平台 + owner/repo，附带各平台的 API 与下载直链拼法。</summary>
public sealed record PluginRepoRef(PluginRepoHost Host, string Owner, string Repo)
{
    /// <summary>owner/repo 形式的仓库标识。</summary>
    public string Slug => $"{Owner}/{Repo}";

    public string HostName => Host switch
    {
        PluginRepoHost.Gitee => "Gitee",
        PluginRepoHost.GitCode => "GitCode",
        _ => "GitHub",
    };

    /// <summary>仓库主页；配置里保存的规范地址也是它（读旧配置时裸 owner/repo 仍按 GitHub 识别）。</summary>
    public string HomeUrl => Host switch
    {
        PluginRepoHost.Gitee => $"https://gitee.com/{Slug}",
        PluginRepoHost.GitCode => $"https://gitcode.com/{Slug}",
        _ => $"https://github.com/{Slug}",
    };

    /// <summary>最新 Release 的 API 地址：GitHub 用 api.github.com，Gitee/GitCode 用各自的 v5 接口。</summary>
    public string LatestReleaseApiUrl => Host switch
    {
        PluginRepoHost.Gitee => $"https://gitee.com/api/v5/repos/{Slug}/releases/latest",
        PluginRepoHost.GitCode => $"https://api.gitcode.com/api/v5/repos/{Slug}/releases/latest",
        _ => $"https://api.github.com/repos/{Slug}/releases/latest",
    };

    /// <summary>API 没给 browser_download_url 时按各平台约定拼附件下载直链。</summary>
    public string AssetDownloadUrl(string tag, string fileName) => Host switch
    {
        PluginRepoHost.Gitee => $"https://gitee.com/{Slug}/releases/download/{tag}/{Uri.EscapeDataString(fileName)}",
        PluginRepoHost.GitCode => $"https://api.gitcode.com/api/v5/repos/{Slug}/releases/{tag}/attach_files/{Uri.EscapeDataString(fileName)}/download",
        _ => $"https://github.com/{Slug}/releases/download/{tag}/{Uri.EscapeDataString(fileName)}",
    };
}

/// <summary>插件商店的一个可安装条目（来自某仓库的最新 Release）。</summary>
public sealed record PluginStoreEntry(
    string RepoUrl,        // 仓库地址（用户添加的原始链接）
    string RepoName,       // owner/repo
    string Name,           // 展示名（仓库名）
    string Version,        // release tag（去掉 v 前缀）
    string Notes,          // release 说明
    string? AssetUrl,      // .nekoex 资产下载地址；null = 该 release 没有可安装资产
    string? AssetName,
    long AssetSize,
    string HostName = "GitHub");

/// <summary>
/// 基于 Release 托管的插件仓库镜像（docs/Extension_Guide/5.基于Github仓库的镜像.md）：
/// 支持 GitHub / Gitee / GitCode 三种源，流程一致：添加仓库 → 读最新 Release → 找 .nekoex 资产 → 下载安装。
/// </summary>
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
            // api.github.com/repos/owner/repo 这类地址去掉中段再取 owner/repo
            if (rest.StartsWith("repos/", StringComparison.OrdinalIgnoreCase))
            {
                rest = rest["repos/".Length..];
            }

            string[] parts = rest.Split('/');
            return parts.Length >= 2 && parts[0].Length > 0 && parts[1].Length > 0
                ? new PluginRepoRef(host, parts[0], parts[1])
                : null;
        }

        // 裸 owner/repo 形式：旧约定按 GitHub
        if (trimmed.Count(c => c == '/') == 1 &&
            trimmed.Contains(' ') == false &&
            trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase) == false)
        {
            string[] parts = trimmed.Split('/');
            return new PluginRepoRef(PluginRepoHost.GitHub, parts[0], parts[1]);
        }

        return null;
    }

    /// <summary>把仓库链接归一化为 owner/repo；无法识别时返回 null。</summary>
    public static string? NormalizeRepo(string url) => ParseRepo(url)?.Slug;

    /// <summary>读取仓库最新 Release 的 .nekoex 条目；仓库无 Release 或无资产时 AssetUrl 为 null。</summary>
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
        long assetSize = 0;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                string current = asset.GetProperty("name").GetString() ?? string.Empty;
                if (current.EndsWith(".nekoex", StringComparison.OrdinalIgnoreCase) == false) continue;

                // Gitee/GitCode 的 v5 响应偶尔缺 browser_download_url，按平台约定拼直链兜底
                string? listed = asset.TryGetProperty("browser_download_url", out var urlEl)
                    ? urlEl.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(listed) && tag.Length > 0)
                {
                    listed = reference.AssetDownloadUrl(tag, current);
                }

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
            reference.HostName);
    }

    /// <summary>下载 .nekoex 到临时目录并安装（进度回报字节数）。</summary>
    public static async Task<string> InstallAsync(
        PluginStoreEntry entry,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (entry.AssetUrl is null)
        {
            throw new InvalidOperationException("该仓库的最新 Release 没有 .nekoex 资产，无法安装。");
        }

        string temporary = Path.Combine(Path.GetTempPath(), $"mofumofu-plugin-{Guid.NewGuid():N}.nekoex");
        try
        {
            var item = new FastDownloadItem(entry.AssetUrl, temporary, entry.AssetSize);
            await FastDownloader.DownloadFileAsync(
                item, 4,
                onBytes: received => progress?.Report(received),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return await NekoexInstaller.InstallAsync(temporary, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { /* 清理失败无所谓 */ }
        }
    }

    /// <summary>已安装版本高于等于商店版本时无需更新。</summary>
    public static bool IsUpToDate(PluginStoreEntry entry)
    {
        PluginRuntime? installed = PluginManager.ById(RepoId(entry));
        if (installed is null) return false;
        if (Version.TryParse(installed.Manifest.Version, out var current) == false) return false;
        if (Version.TryParse(entry.Version, out var latest) == false) return false;

        return current >= latest;
    }

    /// <summary>用插件 id 关联商店条目：约定 manifest id = 仓库名（owner/repo 的 repo 段，小写不敏感）。</summary>
    public static string RepoId(PluginStoreEntry entry) => entry.RepoName.Split('/')[1];
}
