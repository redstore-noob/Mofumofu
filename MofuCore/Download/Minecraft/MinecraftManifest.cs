using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MofuCore.Download.Minecraft;

/// <summary>
/// Minecraft 版本清单服务：拉取并解析 Mojang 的 version_manifest_v2.json。
/// </summary>
public class MinecraftManifest
{
    public const string DefaultUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    /// <summary>单次拉取版本清单的超时：BMCL 等镜像偶尔会挂起数十秒，超过该预算就换下一个源。</summary>
    private const int ManifestAttemptTimeoutSeconds = 8;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _url;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<MinecraftVersion>? _cachedVersions;
    private DateTimeOffset _cachedAt;

    /// <param name="url">Minecraft 版本清单的 URL，默认 Mojang 官方地址。</param>
    public MinecraftManifest(string url = DefaultUrl)
    {
        _url = string.IsNullOrWhiteSpace(url) ? DefaultUrl : url.Trim();
    }

    public string Url => _url;

    public LatestVersions? Latest { get; private set; }

    /// <summary>
    /// 获取版本列表（按发布时间降序），10 分钟内重复调用直接返回缓存。
    /// </summary>
    public async Task<IReadOnlyList<MinecraftVersion>> GetVersionsAsync(CancellationToken cancellationToken = default)
    {
        if (TryGetCached(out var cached))
        {
            return cached!;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCached(out cached))
            {
                return cached!;
            }

            VersionManifest manifest = await FetchManifestAsync(cancellationToken).ConfigureAwait(false);

            List<MinecraftVersion> versions = manifest.Versions ?? [];
            LatestVersions latest = manifest.Latest ?? new LatestVersions();
            foreach (MinecraftVersion version in versions)
            {
                version.IsLatestRelease = version.Id == latest.Release;
                version.IsLatestSnapshot = version.Id == latest.Snapshot;
            }

            List<MinecraftVersion> sorted = versions.OrderByDescending(v => v.ReleaseTime).ToList();
            Latest = latest;
            _cachedVersions = sorted;
            _cachedAt = DateTimeOffset.UtcNow;
            return sorted;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void InvalidateCache()
    {
        _cachedVersions = null;
        Latest = null;
    }

    private bool TryGetCached(out IReadOnlyList<MinecraftVersion>? versions)
    {
        if (_cachedVersions is not null && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
        {
            versions = _cachedVersions;
            return true;
        }

        versions = null;
        return false;
    }

    private async Task<VersionManifest> FetchManifestAsync(CancellationToken cancellationToken)
    {
        List<string> candidates = BuildFetchCandidates();
        Exception? lastError = null;
        foreach (string candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await FetchManifestOnceAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                lastError = e;
            }
        }

        if (lastError is null)
        {
            throw new InvalidOperationException("获取版本清单失败：没有可用的下载地址。");
        }
        if (candidates.Count == 1)
        {
            throw lastError;
        }

        // 保留每个地址原有的中文错误描述，并说明实际尝试过哪些源
        throw new InvalidOperationException(
            $"获取版本清单失败（已依次尝试 {string.Join("、", candidates)}）：{lastError.Message}", lastError);
    }

    /// <summary>
    /// 候选清单地址：当前配置的源 → 用户配置的回退源 → Mojang 官方。
    /// BMCL 等镜像挂起时直接回退，避免下载页把 20 秒超时耗在单一源上。
    /// </summary>
    private List<string> BuildFetchCandidates()
    {
        List<string> candidates = [];
        AddCandidate(candidates, _url);
        AddCandidate(candidates, DownloadSourceProvider.ResolveFallback(_url));
        AddCandidate(candidates, DownloadSourceProvider.ResolveFallback(DefaultUrl));
        AddCandidate(candidates, DefaultUrl);
        return candidates;
    }

    private static void AddCandidate(List<string> candidates, string? url)
    {
        string trimmed = (url ?? string.Empty).Trim();
        if (trimmed.Length == 0) return;
        foreach (string existing in candidates)
        {
            if (string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase)) return;
        }

        candidates.Add(trimmed);
    }

    private static async Task<VersionManifest> FetchManifestOnceAsync(string url, CancellationToken cancellationToken)
    {
        using CancellationTokenSource attemptCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCts.CancelAfter(TimeSpan.FromSeconds(ManifestAttemptTimeoutSeconds));
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(url, attemptCts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync(attemptCts.Token).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<VersionManifest>(stream, JsonOptions, attemptCts.Token).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("版本清单响应为空或格式错误。");
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException("版本清单响应为空或格式错误：" + e.Message, e);
        }
        catch (HttpRequestException e)
        {
            throw new InvalidOperationException($"获取版本清单失败（{url}）：{e.Message}", e);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"获取版本清单超时（{url}）。", e);
        }
    }
}

/// <summary>版本清单根对象（Mojang API）。</summary>
public class VersionManifest
{
    [JsonPropertyName("latest")] public LatestVersions Latest { get; set; } = new();
    [JsonPropertyName("versions")] public List<MinecraftVersion> Versions { get; set; } = [];
}

/// <summary>最新正式版 / 快照版版本号。</summary>
public class LatestVersions
{
    [JsonPropertyName("release")] public string Release { get; set; } = string.Empty;
    [JsonPropertyName("snapshot")] public string Snapshot { get; set; } = string.Empty;
}

/// <summary>版本清单中的单个版本条目。</summary>
public class MinecraftVersion
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("time")] public DateTimeOffset Time { get; set; }
    [JsonPropertyName("releaseTime")] public DateTimeOffset ReleaseTime { get; set; }
    [JsonIgnore] public bool IsLatestRelease { get; set; }
    [JsonIgnore] public bool IsLatestSnapshot { get; set; }

    public string TypeDisplay => Type switch
    {
        "release" => "正式版",
        "snapshot" => "快照版",
        "old_beta" => "经典 Beta",
        "old_alpha" => "经典 Alpha",
        _ => Type,
    };

    public string DisplayName => $"Minecraft {Id}";
}
