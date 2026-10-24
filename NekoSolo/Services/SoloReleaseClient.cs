using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NekoSolo.Services;

public sealed record SoloReleaseAsset(
    string Name,
    string DownloadUrl,
    string HtmlUrl,
    long Size);

public sealed record SoloReleaseInfo(
    string TagName,
    string Name,
    string HtmlUrl,
    string PublishedAt,
    IReadOnlyList<SoloReleaseAsset> Assets);

/// <summary>
/// NekoSolo 的 GitHub Releases 接入：查询最新 Release，挑出整合包 zip 下载。
/// 仓库可通过环境变量 NEKOSOLO_RELEASE_REPO（owner/repo）覆盖；默认 redstore-noob/Mofumofu。
/// 尚未发布时 FetchLatestAsync 返回 null，由 UI 显示「暂未发布」。
/// </summary>
public static class SoloReleaseClient
{
    private const string DefaultOwner = "redstore-noob";
    private const string DefaultRepo = "Mofumofu";

    private static readonly HttpClient Client = CreateClient();

    public static string Repository
    {
        get
        {
            string? overridden = Environment.GetEnvironmentVariable("NEKOSOLO_RELEASE_REPO");
            return string.IsNullOrWhiteSpace(overridden)
                ? DefaultOwner + "/" + DefaultRepo
                : overridden.Trim();
        }
    }

    public static string ReleasesPageUrl => "https://github.com/" + Repository + "/releases";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NekoSolo/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>查询最新 Release；404 表示仓库还没有发布任何 Release，返回 null。</summary>
    public static async Task<SoloReleaseInfo?> FetchLatestAsync(CancellationToken cancellationToken = default)
    {
        string url = $"https://api.github.com/repos/{Repository}/releases/latest";
        using HttpResponseMessage response = await Client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub Releases 请求失败：HTTP {(int)response.StatusCode}");
        }

        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseRelease(json);
    }

    /// <summary>解析 GitHub Release JSON，并挑出 NekoSolo 安装包资产。</summary>
    public static SoloReleaseInfo? ParseRelease(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        string tag = root.TryGetProperty("tag_name", out JsonElement tagElement)
            ? tagElement.GetString() ?? string.Empty
            : string.Empty;
        string name = root.TryGetProperty("name", out JsonElement nameElement)
            ? nameElement.GetString() ?? tag
            : tag;
        string html = root.TryGetProperty("html_url", out JsonElement htmlElement)
            ? htmlElement.GetString() ?? string.Empty
            : string.Empty;
        string published = root.TryGetProperty("published_at", out JsonElement publishedElement)
            ? publishedElement.GetString() ?? string.Empty
            : string.Empty;

        List<SoloReleaseAsset> assets = [];
        if (root.TryGetProperty("assets", out JsonElement assetArray) &&
            assetArray.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in assetArray.EnumerateArray())
            {
                string download = item.TryGetProperty("browser_download_url", out JsonElement downloadElement)
                    ? downloadElement.GetString() ?? string.Empty
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(download)) continue;

                assets.Add(new SoloReleaseAsset(
                    item.TryGetProperty("name", out JsonElement assetName) ? assetName.GetString() ?? string.Empty : string.Empty,
                    download,
                    item.TryGetProperty("html_url", out JsonElement assetHtml) ? assetHtml.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("size", out JsonElement sizeElement) && sizeElement.TryGetInt64(out long size) ? size : 0));
            }
        }

        return new SoloReleaseInfo(tag, name, html, published, assets);
    }

    /// <summary>从 Release 资产里挑 NekoSolo 安装包：优先带 nekosolo 的 zip，其次任意 zip。</summary>
    public static SoloReleaseAsset? SelectPackageAsset(SoloReleaseInfo? release)
    {
        if (release is null) return null;

        static bool IsPackage(SoloReleaseAsset asset, bool requireName) =>
            asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            (!requireName || asset.Name.Contains("nekosolo", StringComparison.OrdinalIgnoreCase));

        return release.Assets.FirstOrDefault(asset => IsPackage(asset, requireName: true))
               ?? release.Assets.FirstOrDefault(asset => IsPackage(asset, requireName: false));
    }

    /// <summary>下载资产到本地临时文件，返回路径；带进度回调。</summary>
    public static async Task<string> DownloadAsync(
        SoloReleaseAsset asset,
        string targetPath,
        Action<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await Client
            .GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? asset.Size;
        string? directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using FileStream destination = new(targetPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

        byte[] buffer = new byte[128 * 1024];
        long received = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            progress?.Invoke(total > 0 ? Math.Clamp(received * 1.0 / total, 0, 1) : 0);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        return targetPath;
    }
}
