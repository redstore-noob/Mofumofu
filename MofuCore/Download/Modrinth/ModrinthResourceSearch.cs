using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using MofuCore;

namespace MofuCore.Download.Modrinth;

/// <summary>Modrinth search 接口的命中项。</summary>
public sealed class ModrinthProject
{
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
    [JsonPropertyName("icon_url")] public string IconUrl { get; set; } = string.Empty;
    [JsonPropertyName("project_type")] public string ProjectType { get; set; } = string.Empty;
    [JsonPropertyName("downloads")] public long Downloads { get; set; }
    [JsonPropertyName("follows")] public long Follows { get; set; }
    [JsonPropertyName("slug")] public string Slug { get; set; } = string.Empty;
    [JsonPropertyName("versions")] public List<string> Versions { get; set; } = [];
    [JsonPropertyName("date_created")] public string DateCreated { get; set; } = string.Empty;

    public string DownloadsDisplay => Downloads switch
    {
        >= 1_000_000 => $"{Downloads / 1_000_000.0:0.0}M 下载",
        >= 1_000 => $"{Downloads / 1_000.0:0.0}K 下载",
        _ => $"{Downloads} 下载",
    };

    public string FollowsDisplay => Follows >= 1_000 ? $"{Follows / 1_000.0:0.0}K ⭐" : $"{Follows} ⭐";

    public string TypeDisplay => ProjectType switch
    {
        "mod" => "模组",
        "modpack" => "整合包",
        "resourcepack" => "资源包",
        "shader" => "光影",
        "datapack" => "数据包",
        "plugin" => "插件",
        _ => ProjectType,
    };
}

/// <summary>Modrinth search 响应（含分页信息）。</summary>
public sealed class ModrinthSearchResult
{
    [JsonPropertyName("hits")] public List<ModrinthProject> Hits { get; set; } = [];
    [JsonPropertyName("offset")] public int Offset { get; set; }
    [JsonPropertyName("limit")] public int Limit { get; set; }
    [JsonPropertyName("total_hits")] public int TotalHits { get; set; }
}

/// <summary>Modrinth 版本中的单个文件。</summary>
public sealed class ModrinthVersionFile
{
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("filename")] public string Filename { get; set; } = string.Empty;
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("primary")] public bool Primary { get; set; }
    [JsonPropertyName("hashes")] public Dictionary<string, string> Hashes { get; set; } = [];

    public string Sha1 => Hashes.TryGetValue("sha1", out string? value) ? value.Trim().ToLowerInvariant() : string.Empty;
    public string Sha512 => Hashes.TryGetValue("sha512", out string? value) ? value.Trim().ToLowerInvariant() : string.Empty;

    public string SizeDisplay => Size switch
    {
        >= 1048576 => $"{Size / 1048576.0:0.0} MB",
        >= 1024 => $"{Size / 1024.0:0} KB",
        _ => $"{Size} B",
    };
}

/// <summary>Modrinth 版本的依赖条目。</summary>
public sealed class ModrinthDependency
{
    [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
    [JsonPropertyName("version_id")] public string? VersionId { get; set; }
    [JsonPropertyName("file_name")] public string? FileName { get; set; }
    [JsonPropertyName("dependency_type")] public string DependencyType { get; set; } = "required";

    public bool IsRequired => DependencyType is "" or "required";

    public string DependencyTypeDisplay => DependencyType switch
    {
        "required" => "必需",
        "optional" => "可选",
        "incompatible" => "不兼容",
        "embedded" => "内嵌",
        _ => DependencyType,
    };
}

/// <summary>Modrinth 版本条目。</summary>
public sealed class ModrinthVersion
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("version_number")] public string VersionNumber { get; set; } = string.Empty;
    [JsonPropertyName("changelog")] public string? Changelog { get; set; }
    [JsonPropertyName("game_versions")] public List<string> GameVersions { get; set; } = [];
    [JsonPropertyName("loaders")] public List<string> Loaders { get; set; } = [];
    [JsonPropertyName("date_published")] public string DatePublished { get; set; } = string.Empty;
    [JsonPropertyName("version_type")] public string VersionType { get; set; } = string.Empty;
    [JsonPropertyName("files")] public List<ModrinthVersionFile> Files { get; set; } = [];
    [JsonPropertyName("dependencies")] public List<ModrinthDependency> Dependencies { get; set; } = [];

    public ModrinthVersionFile? PrimaryFile()
    {
        foreach (ModrinthVersionFile file in Files)
        {
            if (file.Primary) return file;
        }
        return Files.Count > 0 ? Files[0] : null;
    }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? VersionNumber : Name;

    public string VersionTypeDisplay => VersionType switch
    {
        "release" => "正式版",
        "beta" => "测试版",
        "alpha" => "内测版",
        _ => string.Empty,
    };

    public string DateDisplay => DateTimeOffset.TryParse(DatePublished, out DateTimeOffset parsed) ? parsed.ToString("yyyy-MM-dd") : DatePublished;

    public string GameVersionsDisplay => GameVersions.Count <= 3 ? string.Join(", ", GameVersions) : string.Join(", ", GameVersions.Take(3));

    public string LoaderDisplay => string.Join(", ", Loaders.Select(loader => loader switch
    {
        "fabric" => "Fabric",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        "quilt" => "Quilt",
        _ => loader,
    }));

    public string Summary
    {
        get
        {
            List<string> parts = [];
            if (LoaderDisplay.Length > 0) parts.Add(LoaderDisplay);
            if (DateDisplay.Length > 0) parts.Add(DateDisplay);
            if (PrimaryFile() is { } primary) parts.Add(primary.SizeDisplay);
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// Modrinth API v2 客户端：官方地址 + 国内镜像回退，8 秒请求超时，失败给出可读错误。
/// </summary>
internal static class ModrinthApiClient
{
    public const string OfficialApiRoot = "https://api.modrinth.com/v2";
    public const string MirrorApiRoot = "https://mod.mcimirror.top/modrinth/v2";
    public const string SiteUrl = "https://modrinth.com";

    private const int MaxResponseBytes = 32 << 20;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly HttpClient Client = CreateClient();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly object Gate = new();

    private static string _apiRoot = OfficialApiRoot;
    private static string _mirrorRoot = MirrorApiRoot;
    private static volatile bool _preferMirror;
    private static volatile bool _lastUsedMirror;

    private static HttpClient CreateClient()
    {
        HttpClient client = new() { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MofuCore/" + MofuCoreInfo.Version);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    public static bool UsedMirror => _lastUsedMirror;

    public static string HostOf(string root)
    {
        string trimmed = root.Trim();
        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[8..];
        else if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[7..];
        int index = trimmed.IndexOfAny(['/', '?', '#']);
        return index >= 0 ? trimmed[..index] : trimmed;
    }

    public static void SetEndpoints(string official, string? mirror)
    {
        lock (Gate)
        {
            _apiRoot = official.TrimEnd('/');
            _mirrorRoot = (mirror ?? string.Empty).TrimEnd('/');
        }
        _preferMirror = false;
        _lastUsedMirror = false;
    }

    public static void ResetEndpoints() => SetEndpoints(OfficialApiRoot, MirrorApiRoot);

    public static async Task<T> GetJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        byte[] payload = await RequestAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        return Deserialize<T>(payload);
    }

    public static async Task<byte[]> RequestAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        string apiRoot;
        string mirrorRoot;
        lock (Gate)
        {
            apiRoot = _apiRoot;
            mirrorRoot = _mirrorRoot;
        }

        string primary = apiRoot + path;
        string? mirror = null;
        if (!string.IsNullOrWhiteSpace(mirrorRoot))
        {
            string candidate = mirrorRoot + path;
            if (!string.Equals(candidate, primary, StringComparison.OrdinalIgnoreCase)) mirror = candidate;
        }

        string first = primary;
        string? second = mirror;
        if (mirror is not null && _preferMirror)
        {
            first = mirror;
            second = primary;
        }

        byte[]? encodedBody = body is null ? null : JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
        Exception firstError;
        try
        {
            byte[] payload = await RequestOnceAsync(method, first, encodedBody, cancellationToken).ConfigureAwait(false);
            RecordSuccess(mirror is not null && string.Equals(first, mirror, StringComparison.Ordinal));
            return payload;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            firstError = e;
        }

        Exception? secondError = null;
        if (second is not null && IsRetryable(firstError))
        {
            try
            {
                byte[] payload = await RequestOnceAsync(method, second, encodedBody, cancellationToken).ConfigureAwait(false);
                RecordSuccess(mirror is not null && string.Equals(second, mirror, StringComparison.Ordinal));
                return payload;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                secondError = e;
            }
        }

        if (IsRetryable(firstError))
        {
            try
            {
                byte[] payload = await RequestOnceAsync(method, first, encodedBody, cancellationToken).ConfigureAwait(false);
                RecordSuccess(mirror is not null && string.Equals(first, mirror, StringComparison.Ordinal));
                return payload;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // 重试仍失败：走统一错误描述
            }
        }

        throw DescribeFailure(firstError, secondError, second is not null);
    }

    private static async Task<byte[]> RequestOnceAsync(HttpMethod method, string endpoint, byte[]? body, CancellationToken cancellationToken)
    {
        using CancellationTokenSource attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCts.CancelAfter(RequestTimeout);
        using HttpRequestMessage request = new(method, endpoint);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }
        using HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ModrinthStatusException((int)response.StatusCode);
        }
        return await response.Content.ReadAsByteArrayAsync(attemptCts.Token).ConfigureAwait(false);
    }

    private static T Deserialize<T>(byte[] payload)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload, JsonOptions)
                   ?? throw new InvalidOperationException("Modrinth 响应为空。");
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException("Modrinth 响应解析失败：" + e.Message, e);
        }
    }

    private static void RecordSuccess(bool usedMirror)
    {
        _preferMirror = usedMirror;
        _lastUsedMirror = usedMirror;
    }

    private static bool IsRetryable(Exception error) => error switch
    {
        ModrinthStatusException status => status.StatusCode is (int)HttpStatusCode.RequestTimeout or (int)HttpStatusCode.TooManyRequests or >= 500,
        InvalidOperationException => false,
        _ => true,
    };

    private static InvalidOperationException DescribeFailure(Exception primaryError, Exception? mirrorError, bool mirrorTried)
    {
        if (mirrorTried && mirrorError is not null)
        {
            return new InvalidOperationException(
                $"Modrinth 接口访问失败：官方（{HostOf(OfficialApiRoot)}）与国内镜像（{HostOf(MirrorApiRoot)}）均未返回数据。" +
                $"请检查网络或代理后重试（原始错误：{primaryError.Message}；镜像错误：{mirrorError.Message}）", primaryError);
        }
        return new InvalidOperationException(
            $"Modrinth 接口访问失败（{HostOf(OfficialApiRoot)}）：{primaryError.Message}。请检查网络或代理后重试", primaryError);
    }

    public static async Task DownloadToFileAsync(string url, string targetPath, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"下载地址不是有效的 HTTPS URL：{url}");
        }
        string? directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using HttpResponseMessage response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using FileStream destination = new(targetPath + ".mofu-download", FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true);
        byte[] buffer = new byte[128 * 1024];
        long downloaded = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            downloaded += read;
            progress?.Report(downloaded);
        }
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Close();
        File.Move(targetPath + ".mofu-download", targetPath, true);
    }
}

/// <summary>Modrinth 接口返回非 2xx 状态码。</summary>
internal sealed class ModrinthStatusException : Exception
{
    public ModrinthStatusException(int statusCode) : base($"接口返回 HTTP {statusCode}")
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

/// <summary>
/// Modrinth 项目搜索：关键词、项目类型、MC 版本、加载器过滤，支持分页与排序。
/// </summary>
public sealed class ModrinthResourceSearch
{
    public const string SiteUrl = ModrinthApiClient.SiteUrl;
    public const string OfficialApiRoot = ModrinthApiClient.OfficialApiRoot;

    public static bool UsedMirror => ModrinthApiClient.UsedMirror;

    public static void SetEndpoints(string official, string? mirror = null) => ModrinthApiClient.SetEndpoints(official, mirror);

    public static void ResetEndpoints() => ModrinthApiClient.ResetEndpoints();

    /// <summary>搜索项目（单页）。projectType: mod / modpack / resourcepack / shader / plugin。</summary>
    public static async Task<IReadOnlyList<ModrinthProject>> SearchAsync(
        string query,
        string projectType = "mod",
        string? gameVersion = null,
        IEnumerable<string>? loaders = null,
        int limit = 20,
        int offset = 0,
        string index = "relevance",
        CancellationToken cancellationToken = default)
    {
        ModrinthSearchResult page = await SearchPageAsync(query, projectType, gameVersion, loaders, limit, offset, index, cancellationToken).ConfigureAwait(false);
        return page.Hits;
    }

    /// <summary>搜索项目并返回分页信息。</summary>
    public static async Task<ModrinthSearchResult> SearchPageAsync(
        string query,
        string projectType = "mod",
        string? gameVersion = null,
        IEnumerable<string>? loaders = null,
        int limit = 20,
        int offset = 0,
        string index = "relevance",
        CancellationToken cancellationToken = default)
    {
        // Modrinth facets：内层数组 = OR，外层数组 = AND。
        // 因此 project_type / versions / loaders 各占一组；加载器之间是 OR（同组）。
        List<string> groups = ["[" + Quote("project_type:" + projectType) + "]"];
        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            groups.Add("[" + Quote("versions:" + gameVersion.Trim()) + "]");
        }
        List<string> loaderList = UniqueNonEmpty(loaders);
        if (loaderList.Count > 0)
        {
            groups.Add("[" + string.Join(",", loaderList.Select(loader => Quote("categories:" + loader))) + "]");
        }

        StringBuilder endpoint = new("/search?");
        endpoint.Append("query=").Append(Uri.EscapeDataString(query ?? string.Empty));
        endpoint.Append("&facets=").Append(Uri.EscapeDataString("[" + string.Join(",", groups) + "]"));
        endpoint.Append("&limit=").Append(Math.Clamp(limit, 1, 100));
        endpoint.Append("&offset=").Append(Math.Max(0, offset));
        if (!string.IsNullOrWhiteSpace(index))
        {
            endpoint.Append("&index=").Append(Uri.EscapeDataString(index.Trim()));
        }
        return await ModrinthApiClient.GetJsonAsync<ModrinthSearchResult>(endpoint.ToString(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>无关键词的浏览列表（下载页默认列表）。</summary>
    public static Task<ModrinthSearchResult> BrowseAsync(
        string projectType = "mod",
        string? gameVersion = null,
        IEnumerable<string>? loaders = null,
        int limit = 20,
        int offset = 0,
        string index = "downloads",
        CancellationToken cancellationToken = default)
    {
        return SearchPageAsync(string.Empty, projectType, gameVersion, loaders, limit, offset, index, cancellationToken);
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static List<string> UniqueNonEmpty(IEnumerable<string>? values)
    {
        List<string> result = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? value in values ?? [])
        {
            string trimmed = value?.Trim() ?? string.Empty;
            if (trimmed.Length == 0 || !seen.Add(trimmed)) continue;
            result.Add(trimmed);
        }
        return result;
    }
}
