using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NekoSolo.Services;

public sealed record SoloReleaseAsset(
    string Name,
    string DownloadUrl,
    string HtmlUrl,
    long Size,
    string? Digest = null);

public sealed record SoloReleaseInfo(
    string TagName,
    string Name,
    string HtmlUrl,
    string PublishedAt,
    IReadOnlyList<SoloReleaseAsset> Assets);

/// <summary>
/// GitHub Releases 接入：查询最新 Release 并挑出整合包 zip 下载。
/// 仓库可用 NEKOSOLO_RELEASE_REPO（owner/repo）覆盖，默认 redstore-noob/Mofumofu。
/// 尚未发布时 FetchLatestAsync 返回 null。
/// </summary>
public static class SoloReleaseClient
{
    private const string DefaultOwner = "redstore-noob";
    private const string DefaultRepo = "Mofumofu";
    private const int MaxRedirects = 10;
    private const string Sha256DigestPrefix = "sha256:";

    private static readonly string DefaultRepository = DefaultOwner + "/" + DefaultRepo;

    // 查询 API 用常规（自动重定向）客户端；下载单独用禁用自动重定向的客户端，逐跳校验目标主机。
    private static readonly HttpClient Client = CreateClient(autoRedirect: true);
    private static readonly HttpClient DownloadClient = CreateClient(autoRedirect: false);

    public static string Repository
    {
        get
        {
            string? overridden = Environment.GetEnvironmentVariable("NEKOSOLO_RELEASE_REPO");
            string candidate = string.IsNullOrWhiteSpace(overridden) ? DefaultRepository : overridden.Trim();
            // 环境变量会拼进 GitHub API / 页面 URL，必须严格限定为 owner/repo，否则回退默认仓库。
            return IsValidRepository(candidate) ? candidate : DefaultRepository;
        }
    }

    public static string ReleasesPageUrl => "https://github.com/" + Repository + "/releases";

    /// <summary>严格限定 owner/repo，两段非空且不是纯点段。</summary>
    private static bool IsValidRepository(string value)
    {
        string[] parts = value.Split('/');
        if (parts.Length != 2) return false;
        foreach (string part in parts)
        {
            if (part.Length == 0 || part.Length > 100) return false;
            if (part.All(ch => ch == '.')) return false;
            foreach (char ch in part)
            {
                if (!IsRepoChar(ch)) return false;
            }
        }

        return true;
    }

    private static bool IsRepoChar(char ch) =>
        (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') ||
        (ch >= '0' && ch <= '9') || ch is '-' or '_' or '.';

    private static HttpClient CreateClient(bool autoRedirect)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = autoRedirect };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NekoSolo/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>404 表示仓库还没有发布任何 Release。</summary>
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

                // digest（sha256:...）用于下载落盘后的完整性校验。
                string digest = item.TryGetProperty("digest", out JsonElement digestElement)
                    ? digestElement.GetString() ?? string.Empty
                    : string.Empty;

                assets.Add(new SoloReleaseAsset(
                    item.TryGetProperty("name", out JsonElement assetName) ? assetName.GetString() ?? string.Empty : string.Empty,
                    download,
                    item.TryGetProperty("html_url", out JsonElement assetHtml) ? assetHtml.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("size", out JsonElement sizeElement) && sizeElement.TryGetInt64(out long size) ? size : 0,
                    digest));
            }
        }

        return new SoloReleaseInfo(tag, name, html, published, assets);
    }

    /// <summary>只选择明确标记的 NekoSolo 安装包，避免误装普通启动器或其他平台的 zip。</summary>
    public static SoloReleaseAsset? SelectPackageAsset(SoloReleaseInfo? release)
    {
        if (release is null) return null;

        static bool IsPackage(SoloReleaseAsset asset) =>
            asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            asset.Name.Contains("nekosolo", StringComparison.OrdinalIgnoreCase);

        return release.Assets.FirstOrDefault(IsPackage);
    }

    /// <summary>带进度回调、主机白名单与 SHA256 完整性校验。</summary>
    public static async Task<string> DownloadAsync(
        SoloReleaseAsset asset,
        string targetPath,
        Action<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // browser_download_url 来自远程 JSON：初始地址必须是 HTTPS 且落在 github.com。
        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out Uri? initial) ||
            !string.Equals(initial.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("安装包下载地址不是合法的 HTTPS URL。");
        }

        ValidateDownloadUri(initial, isInitial: true);

        // digest 同样来自远程 JSON；缺失或格式非法都无法校验完整性，直接拒绝（fail-closed）。
        byte[] expectedHash = ParseSha256Digest(asset.Digest);

        try
        {
            // 禁用自动重定向，逐跳跟随并校验目标主机，防止被导到非 GitHub 主机。
            using HttpResponseMessage response = await GetWithValidatedRedirectsAsync(initial, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long total = response.Content.Headers.ContentLength ?? asset.Size;
            if (total > SoloPayload.ArchiveMaximumBytes)
            {
                throw new InvalidOperationException("安装包体积超过上限：" + total);
            }

            string? directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using FileStream destination = new(targetPath, FileMode.Create, FileAccess.Write,
                FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            byte[] buffer = new byte[128 * 1024];
            long received = 0;
            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hasher.AppendData(buffer, 0, read);
                received += read;
                if (received > SoloPayload.ArchiveMaximumBytes)
                {
                    throw new InvalidOperationException("安装包下载体积超过上限：" + received);
                }

                progress?.Invoke(total > 0 ? Math.Clamp(received * 1.0 / total, 0, 1) : 0);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (total > 0 && received != total)
            {
                throw new InvalidOperationException($"安装包下载不完整：期望 {total} 字节，实际 {received} 字节");
            }

            // 落盘完成后核对 GitHub 声明的 sha256，不匹配即拒绝并清理临时文件。
            if (!CryptographicOperations.FixedTimeEquals(hasher.GetHashAndReset(), expectedHash))
            {
                throw new InvalidOperationException("安装包 SHA256 校验失败（与 Release digest 不符）。");
            }

            return targetPath;
        }
        catch
        {
            // 失败/取消时清理可能已落盘的半成品，避免被误当成完整安装包。
            TryDelete(targetPath);
            throw;
        }
    }

    private static async Task<HttpResponseMessage> GetWithValidatedRedirectsAsync(
        Uri initial, CancellationToken cancellationToken)
    {
        Uri current = initial;
        HttpResponseMessage response = await DownloadClient
            .GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        int redirects = 0;
        while (IsRedirect(response.StatusCode))
        {
            if (redirects >= MaxRedirects)
            {
                response.Dispose();
                throw new InvalidOperationException("安装包下载重定向次数过多。");
            }

            Uri? location = response.Headers.Location;
            response.Dispose();
            if (location is null)
            {
                throw new InvalidOperationException("安装包下载重定向缺少 Location。");
            }

            Uri next = location.IsAbsoluteUri ? location : new Uri(current, location);
            ValidateDownloadUri(next, isInitial: false);
            current = next;
            redirects++;
            response = await DownloadClient
                .GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }

        return response;
    }

    private static void ValidateDownloadUri(Uri uri, bool isInitial)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("安装包下载地址必须使用 HTTPS：" + uri.Scheme);
        }

        if (!uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("安装包下载地址包含非法的端口或用户信息。");
        }

        string host = uri.Host;
        bool allowed = isInitial
            ? string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase)
            : IsAllowedAssetHost(host);
        if (!allowed)
        {
            throw new InvalidOperationException("安装包下载主机不在 GitHub 白名单内：" + host);
        }
    }

    // 初始地址只认 github.com；重定向后的资产 CDN 还允许 githubusercontent.com。
    private static bool IsAllowedAssetHost(string host) =>
        string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.Moved or
        HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static byte[] ParseSha256Digest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest) ||
            !digest.StartsWith(Sha256DigestPrefix, StringComparison.OrdinalIgnoreCase) ||
            digest.Length != Sha256DigestPrefix.Length + 64)
        {
            throw new InvalidOperationException("资产缺少合法的 sha256 digest，拒绝下载无法校验完整性的安装包。");
        }

        try
        {
            return Convert.FromHexString(digest[Sha256DigestPrefix.Length..]);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("资产 digest 不是合法的 SHA256 十六进制。");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
