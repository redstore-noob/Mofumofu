using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MofuCore.Account;

/// <summary>皮肤站（authlib-injector / Yggdrasil 规范）账号的持久化凭据。</summary>
public sealed class AuthlibCredential
{
    [JsonPropertyName("Username")] public string Username { get; set; } = string.Empty;
    [JsonPropertyName("ProfileName")] public string ProfileName { get; set; } = string.Empty;
    [JsonPropertyName("ProfileUuid")] public string ProfileUuid { get; set; } = string.Empty;
    [JsonPropertyName("AccessToken")] public string AccessToken { get; set; } = string.Empty;
    [JsonPropertyName("ApiRoot")] public string ApiRoot { get; set; } = string.Empty;
    [JsonPropertyName("ServerName")] public string ServerName { get; set; } = string.Empty;

    public AuthlibCredential Clone()
    {
        return (AuthlibCredential)MemberwiseClone();
    }
}



/// <summary>皮肤站认证流程中抛出的错误；Message 已面向用户可读。</summary>
public sealed class AuthlibAuthenticationException : Exception
{
    public AuthlibAuthenticationException(string message, bool captchaRequired = false, Exception? inner = null)
        : base(message, inner)
    {
        CaptchaRequired = captchaRequired;
    }

    /// <summary>皮肤站开启了验证码（Blessing Skin 私有扩展），需引导用户到网页完成验证。</summary>
    public bool CaptchaRequired { get; }
}



/// <summary>解析后的皮肤站信息：归一化的 API 根 + 元数据。</summary>
public sealed class AuthlibServerInfo
{
    public string ApiRoot { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public IReadOnlyList<string> SkinDomains { get; set; } = [];
}

/// <summary>皮肤站上的一个游戏角色档案（Yggdrasil availableProfiles 条目）。</summary>
public sealed class AuthlibProfileInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

/// <summary>皮肤站登录成功结果：访问令牌 + 可选角色列表。</summary>
public sealed class AuthlibLoginResult
{
    public string AccessToken { get; set; } = string.Empty;
    public IReadOnlyList<AuthlibProfileInfo> Profiles { get; set; } = [];
    public string ServerName { get; set; } = string.Empty;
}

/// <summary>
/// authlib-injector（Yggdrasil 规范）皮肤站认证：服务器发现、密码登录、令牌校验与刷新。
/// </summary>
public class AuthlibAccountManage
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly string? _storageDirectory;

    public AuthlibAccountManage(HttpClient? httpClient = null, string? storageDirectory = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _storageDirectory = storageDirectory;
    }

    /// <summary>
    /// 解析用户输入的皮肤站地址为标准 Yggdrasil API 根：输入可以是根域名或完整的 API 根地址。
    /// </summary>
    public async Task<AuthlibServerInfo> ResolveServerAsync(string serverUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            throw new AuthlibAuthenticationException("请输入皮肤站地址。");
        }

        string input = serverUrl.Trim();
        if (!input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            input = "https://" + input;
        }

        input = input.TrimEnd('/');
        string apiRoot = await DiscoverApiRootAsync(input, cancellationToken).ConfigureAwait(false);
        return await RequestMetadataAsync(apiRoot, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> DiscoverApiRootAsync(string input, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(input, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new AuthlibAuthenticationException(
                $"无法连接皮肤站：{HostOf(input)}（请检查地址与网络）", false, e);
        }

        using (response)
        {
            _ = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new AuthlibAuthenticationException(
                    $"皮肤站 {HostOf(input)} 响应异常（HTTP {(int)response.StatusCode}）。");
            }

            // 规范：任意页面可通过该头声明真正的 Yggdrasil API 根（相对或绝对地址）
            if (response.Headers.TryGetValues("X-Authlib-Injector-API-Location", out IEnumerable<string>? values))
            {
                string? location = values.FirstOrDefault()?.Trim();
                if (!string.IsNullOrEmpty(location))
                {
                    string resolved = ResolveLocation(input, response.RequestMessage?.RequestUri, location);
                    return resolved.TrimEnd('/');
                }
            }
        }

        return TrimToPath(input);
    }

    private static string ResolveLocation(string baseUrl, Uri? finalUri, string location)
    {
        if (location.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            location.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return location;
        }

        Uri baseUri = finalUri ?? new Uri(baseUrl);
        if (location.StartsWith('/'))
        {
            return baseUri.GetLeftPart(UriPartial.Authority) + location;
        }

        string current = baseUri.GetLeftPart(UriPartial.Path);
        int index = current.LastIndexOf('/');
        if (index > current.IndexOf("://", StringComparison.Ordinal) + 2)
        {
            current = current[..index];
        }

        return current + "/" + location;
    }

    private static string TrimToPath(string rawUrl)
    {
        int index = rawUrl.IndexOfAny(['#', '?']);
        return (index >= 0 ? rawUrl[..index] : rawUrl).TrimEnd('/');
    }

    private static string HostOf(string rawUrl)
    {
        string rest = rawUrl;
        foreach (string prefix in new[] { "https://", "http://" })
        {
            if (rest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                rest = rest[prefix.Length..];
                break;
            }
        }

        int index = rest.IndexOfAny(['/', '?', '#']);
        return index >= 0 ? rest[..index] : rest;
    }

    private async Task<AuthlibServerInfo> RequestMetadataAsync(string apiRoot, CancellationToken cancellationToken)
    {
        (HttpStatusCode statusCode, string body) = await GetAsync(apiRoot, cancellationToken).ConfigureAwait(false);
        if ((int)statusCode is < 200 or >= 300)
        {
            throw new AuthlibAuthenticationException($"读取皮肤站元数据失败（HTTP {(int)statusCode}）。");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            string serverName = root.TryGetProperty("meta", out JsonElement meta) &&
                                meta.TryGetProperty("serverName", out JsonElement name)
                ? name.GetString() ?? string.Empty
                : string.Empty;
            List<string> skinDomains = [];
            if (root.TryGetProperty("skinDomains", out JsonElement domains) && domains.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement domain in domains.EnumerateArray())
                {
                    if (domain.GetString() is { Length: > 0 } value)
                    {
                        skinDomains.Add(value);
                    }
                }
            }

            return new AuthlibServerInfo { ApiRoot = apiRoot, ServerName = serverName, SkinDomains = skinDomains };
        }
        catch (JsonException e)
        {
            throw new AuthlibAuthenticationException(
                "该地址不是有效的 authlib-injector 皮肤站（元数据解析失败）。", false, e);
        }
    }

    /// <summary>使用账号密码登录皮肤站，返回访问令牌与可选角色列表。</summary>
    public async Task<AuthlibLoginResult> AuthenticateAsync(
        string apiRoot,
        string username,
        string password,
        string clientToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiRoot) || string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(password))
        {
            throw new AuthlibAuthenticationException("皮肤站地址、用户名与密码均不能为空。");
        }

        if (string.IsNullOrWhiteSpace(clientToken))
        {
            clientToken = Guid.NewGuid().ToString("D");
        }

        object payload = new
        {
            agent = new { name = "MinecraftLauncher", version = 1 },
            username = username.Trim(),
            password,
            clientToken,
            requestUser = true,
        };
        string endpoint = apiRoot.TrimEnd('/') + "/authserver/authenticate";
        (HttpStatusCode statusCode, string body) = await PostJsonAsync(endpoint, payload, cancellationToken)
            .ConfigureAwait(false);
        if ((int)statusCode is < 200 or >= 300)
        {
            throw CreateLoginFailure((int)statusCode, body);
        }

        AuthlibAuthenticatePayload? parsed = TryDeserialize<AuthlibAuthenticatePayload>(body);
        if (parsed is null || string.IsNullOrWhiteSpace(parsed.AccessToken))
        {
            throw new AuthlibAuthenticationException("皮肤站未返回有效的访问令牌。");
        }

        List<AuthlibProfileInfo> profiles = [];
        foreach (AuthlibProfileEntry profile in parsed.AvailableProfiles ?? [])
        {
            if (!string.IsNullOrWhiteSpace(profile.Id) && !string.IsNullOrWhiteSpace(profile.Name))
            {
                profiles.Add(new AuthlibProfileInfo { Id = profile.Id, Name = profile.Name });
            }
        }

        return new AuthlibLoginResult { AccessToken = parsed.AccessToken, Profiles = profiles };
    }

    private static AuthlibAuthenticationException CreateLoginFailure(int statusCode, string body)
    {
        string errorCode = string.Empty;
        string errorMessage = string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out JsonElement error))
            {
                errorCode = error.GetString() ?? string.Empty;
            }

            if (document.RootElement.TryGetProperty("errorMessage", out JsonElement message))
            {
                errorMessage = message.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // 非 JSON 错误体：走默认文案
        }

        // 皮肤站开了验证码（Blessing Skin 私有行为）：引导用户到网页完成验证
        if (errorMessage.Contains("验证码", StringComparison.Ordinal) ||
            errorMessage.Contains("captcha", StringComparison.OrdinalIgnoreCase))
        {
            return new AuthlibAuthenticationException(
                "该皮肤站开启了验证码，请在浏览器中打开皮肤站完成一次登录验证，然后回来重试。", true);
        }

        if (errorCode.Contains("ForbiddenOperationException", StringComparison.Ordinal) &&
            (errorMessage.Contains("credentials", StringComparison.OrdinalIgnoreCase) ||
             errorMessage.Contains("password", StringComparison.OrdinalIgnoreCase) ||
             errorMessage.Contains("用户", StringComparison.Ordinal)))
        {
            return new AuthlibAuthenticationException("用户名或密码错误。");
        }

        return string.IsNullOrWhiteSpace(errorMessage)
            ? new AuthlibAuthenticationException($"登录失败（HTTP {statusCode}）。")
            : new AuthlibAuthenticationException($"登录失败：{errorMessage}");
    }

    /// <summary>
    /// 启动前的令牌保活：validate 通过则原样返回；失效则 refresh 换新令牌；
    /// 全部失败时提示重新登录。返回可用的访问令牌。
    /// </summary>
    public async Task<string> ValidateOrRefreshAsync(
        AuthlibCredential credential,
        string clientToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (string.IsNullOrWhiteSpace(credential.ApiRoot) || string.IsNullOrWhiteSpace(credential.AccessToken))
        {
            throw new AuthlibAuthenticationException("皮肤站凭据缺少 ApiRoot 或 AccessToken。");
        }

        if (string.IsNullOrWhiteSpace(clientToken))
        {
            clientToken = Guid.NewGuid().ToString("D");
        }

        string root = credential.ApiRoot.TrimEnd('/');
        (HttpStatusCode statusCode, _) = await PostJsonAsync(
            root + "/authserver/validate",
            new { accessToken = credential.AccessToken, clientToken },
            cancellationToken).ConfigureAwait(false);
        if ((int)statusCode is >= 200 and < 300)
        {
            return credential.AccessToken;
        }

        (HttpStatusCode refreshStatusCode, string body) = await PostJsonAsync(
            root + "/authserver/refresh",
            new { accessToken = credential.AccessToken, clientToken, requestUser = true },
            cancellationToken).ConfigureAwait(false);
        if ((int)refreshStatusCode is < 200 or >= 300)
        {
            throw new AuthlibAuthenticationException("皮肤站登录状态已失效，请重新登录该账号。");
        }

        AuthlibRefreshPayload? parsed = TryDeserialize<AuthlibRefreshPayload>(body);
        if (parsed is null || string.IsNullOrWhiteSpace(parsed.AccessToken))
        {
            throw new AuthlibAuthenticationException("皮肤站登录状态已失效，请重新登录该账号。");
        }

        return parsed.AccessToken;
    }

    private async Task<(HttpStatusCode StatusCode, string Body)> GetAsync(string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, body);
    }

    private async Task<(HttpStatusCode StatusCode, string Body)> PostJsonAsync(
        string url,
        object payload,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            // 皮肤站载荷本身就是 camelCase 属性名，直接按原样序列化
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"),
        };
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, body);
    }

    private static T? TryDeserialize<T>(string body) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class AuthlibAuthenticatePayload
    {
        [JsonPropertyName("accessToken")] public string AccessToken { get; set; } = string.Empty;
        [JsonPropertyName("clientToken")] public string ClientToken { get; set; } = string.Empty;
        [JsonPropertyName("availableProfiles")] public List<AuthlibProfileEntry>? AvailableProfiles { get; set; }
    }

    private sealed class AuthlibProfileEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    }

    private sealed class AuthlibRefreshPayload
    {
        [JsonPropertyName("accessToken")] public string AccessToken { get; set; } = string.Empty;
    }
}

/// <summary>authlib-injector latest.json 的产物描述（兼容新旧两种字段布局）。</summary>
public sealed class AuthlibArtifact
{
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    [JsonPropertyName("download_url")] public string DownloadUrl { get; set; } = string.Empty;
    [JsonPropertyName("checksums")] public AuthlibChecksums? Checksums { get; set; }
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = string.Empty;
    [JsonPropertyName("sha1")] public string Sha1 { get; set; } = string.Empty;

    public string ArtifactUrl => !string.IsNullOrWhiteSpace(DownloadUrl) ? DownloadUrl.Trim() : Url.Trim();
    public string ArtifactSha256 => !string.IsNullOrWhiteSpace(Checksums?.Sha256) ? Checksums!.Sha256.Trim() : Sha256.Trim();
    public string ArtifactSha1 => !string.IsNullOrWhiteSpace(Checksums?.Sha1) ? Checksums!.Sha1.Trim() : Sha1.Trim();

    public sealed class AuthlibChecksums
    {
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = string.Empty;
        [JsonPropertyName("sha1")] public string Sha1 { get; set; } = string.Empty;
    }
}

/// <summary>
/// authlib-injector 注入器的下载与缓存：官方源优先，BMCLAPI 镜像兜底，全源失败时退回本地旧版 jar。
/// </summary>
public static class AuthlibInjector
{
    private const string OfficialLatestJsonUrl = "https://authlib-injector.yushi.moe/artifact/latest.json";
    private const string MirrorLatestJsonUrl = "https://bmclapi2.bangbang93.com/mirrors/authlib-injector/artifact/latest.json";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    /// <summary>确保 minecraftDirectory 下存在可用的注入器 jar，返回其绝对路径。</summary>
    public static async Task<string> EnsureAsync(
        string minecraftDirectory,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(minecraftDirectory))
        {
            throw new ArgumentException("minecraftDirectory 不能为空。", nameof(minecraftDirectory));
        }

        string installDirectory = Path.Combine(minecraftDirectory, "authlib-injector");
        Directory.CreateDirectory(installDirectory);

        Exception? lastError = null;
        foreach (string latestJsonUrl in new[] { OfficialLatestJsonUrl, MirrorLatestJsonUrl })
        {
            try
            {
                return await EnsureFromSourceAsync(latestJsonUrl, installDirectory, log, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or IOException or InvalidOperationException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lastError = e;
                log?.Invoke($"注入器下载源 {HostOf(latestJsonUrl)} 不可用：{e.Message}");
            }
        }

        // 全部下载源失败：回退到本地任意旧版注入器（外置登录多数场景下仍可用）
        foreach (string candidate in Directory.EnumerateFiles(installDirectory, "authlib-injector-*.jar"))
        {
            string name = Path.GetFileName(candidate);
            log?.Invoke($"警告：无法获取最新注入器，使用本地已有的 {name}。");
            return candidate;
        }

        throw new InvalidOperationException($"获取 authlib-injector 注入器失败：{lastError?.Message}");
    }

    private static async Task<string> EnsureFromSourceAsync(
        string latestJsonUrl,
        string installDirectory,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        AuthlibArtifact artifact = await FetchLatestArtifactAsync(latestJsonUrl, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(artifact.ArtifactUrl) || string.IsNullOrWhiteSpace(artifact.Version))
        {
            throw new InvalidOperationException("latest.json 缺少下载地址或 version 字段。");
        }

        string targetPath = Path.Combine(installDirectory, $"authlib-injector-{artifact.Version}.jar");
        if (File.Exists(targetPath))
        {
            if (!HasKnownHash(artifact) || await MatchesHashAsync(targetPath, artifact, cancellationToken).ConfigureAwait(false))
            {
                log?.Invoke($"authlib-injector {artifact.Version} 已就绪。");
                return targetPath;
            }

            log?.Invoke("本地注入器缓存校验失败，正在重新下载。");
            File.Delete(targetPath);
        }

        string downloadUrl = ResolveDownloadUrl(latestJsonUrl, artifact.ArtifactUrl);
        log?.Invoke($"正在下载 authlib-injector {artifact.Version}…");
        await DownloadFileAsync(downloadUrl, targetPath, cancellationToken).ConfigureAwait(false);

        if (HasKnownHash(artifact) && !await MatchesHashAsync(targetPath, artifact, cancellationToken).ConfigureAwait(false))
        {
            File.Delete(targetPath);
            throw new InvalidOperationException("注入器哈希校验失败。");
        }

        log?.Invoke("authlib-injector 下载完成。");
        return targetPath;
    }

    private static async Task<AuthlibArtifact> FetchLatestArtifactAsync(
        string latestJsonUrl,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await Http.GetAsync(latestJsonUrl, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"latest.json 请求失败（HTTP {(int)response.StatusCode}）");
        }

        AuthlibArtifact? artifact = await response.Content.ReadFromJsonAsync<AuthlibArtifact>(cancellationToken)
            .ConfigureAwait(false);
        return artifact ?? throw new JsonException("latest.json 解析结果为空。");
    }

    private static string ResolveDownloadUrl(string latestJsonUrl, string artifactUrl)
    {
        if (latestJsonUrl != OfficialLatestJsonUrl ||
            !(artifactUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
              artifactUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            return artifactUrl;
        }

        // 镜像与官方目录结构一致，仅替换主机与前缀
        string mirrorRoot = MirrorLatestJsonUrl[..(MirrorLatestJsonUrl.LastIndexOf('/') + 1)];
        mirrorRoot = mirrorRoot[..(mirrorRoot.TrimEnd('/').LastIndexOf('/') + 1)];
        int index = artifactUrl[8..].IndexOf('/');
        return index < 0 ? artifactUrl : mirrorRoot + artifactUrl[(8 + index + 1)..];
    }

    private static bool HasKnownHash(AuthlibArtifact artifact)
    {
        return !string.IsNullOrWhiteSpace(artifact.ArtifactSha256) || !string.IsNullOrWhiteSpace(artifact.ArtifactSha1);
    }

    private static async Task<bool> MatchesHashAsync(
        string filePath,
        AuthlibArtifact artifact,
        CancellationToken cancellationToken)
    {
        byte[] data = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(artifact.ArtifactSha256))
        {
            return string.Equals(Convert.ToHexString(SHA256.HashData(data)), artifact.ArtifactSha256.Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(artifact.ArtifactSha1))
        {
            return string.Equals(Convert.ToHexString(SHA1.HashData(data)), artifact.ArtifactSha1.Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static async Task DownloadFileAsync(string url, string targetPath, CancellationToken cancellationToken)
    {
        string temporaryPath = targetPath + ".download";
        using HttpResponseMessage response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"下载失败（HTTP {(int)response.StatusCode}）");
        }

        await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (FileStream target = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, targetPath, true);
    }

    private static string HostOf(string rawUrl)
    {
        string rest = rawUrl;
        foreach (string prefix in new[] { "https://", "http://" })
        {
            if (rest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                rest = rest[prefix.Length..];
                break;
            }
        }

        int index = rest.IndexOfAny(['/', '?', '#']);
        return index >= 0 ? rest[..index] : rest;
    }
}


