using System.Text.Json;
using System.Text.RegularExpressions;

namespace MofuCore.Download.Minecraft;



/// <summary>
/// Forge：版本列表三级回退——BMCL 全量列表（国内快）→ 官方 maven-metadata.xml（直连）→
/// promotions_slim.json（只有 latest / recommended，最后兜底）。
/// 安装必须运行官方 installer（SRG 重映射客户端只由安装器生成）。
/// </summary>
public static class ForgeDownload
{
    public const string PromotionsUrl = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
    public const string MetadataUrl = "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml";
    public const string MavenRoot = "https://maven.minecraftforge.net/net/minecraftforge/forge/";
    public const string BmclForgeListUrl = "https://bmclapi2.bangbang93.com/forge/minecraft/";

    private static readonly SemaphoreSlim PromotionsGate = new(1, 1);
    private static readonly object PromotionsLock = new();
    private static string? _promotionsJson;
    private static DateTimeOffset _promotionsAt;

    public static async Task<IReadOnlyList<ModLoaderVersionInfo>> GetVersionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        string mc = FabricDownload.RequireMinecraftVersion(minecraftVersion);

        // 全量列表优先：BMCL（快）→ 官方 maven-metadata（直连，绕开镜像重写）
        IReadOnlyList<ModLoaderVersionInfo> full = await TryBmclFullListAsync(mc, cancellationToken)
            .ConfigureAwait(false);
        if (full.Count > 0) return full;

        full = await TryMavenMetadataAsync(mc, cancellationToken).ConfigureAwait(false);
        if (full.Count > 0) return full;

        // 两条全量源都不可用时退回 promotions（仅 latest / recommended）
        string json = await GetPromotionsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ModLoaderVersionInfo> result = ParsePromotions(json, mc);
        if (result.Count > 0) return result;

        // BMCL 的 Forge promotions 镜像经常落后（只含远古版本），列表为空时直连官方再取一次
        string official = await GetStringDirectAsync(
            PromotionsUrl, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        return ParsePromotions(official, mc);
    }

    /// <summary>BMCL 全量列表：/{mc} 返回该版本下所有 Forge 构建（version 为裸构建号）。</summary>
    private static async Task<IReadOnlyList<ModLoaderVersionInfo>> TryBmclFullListAsync(
        string minecraftVersion, CancellationToken cancellationToken)
    {
        try
        {
            string json = await DownloadSourceProvider.GetStringAsync(
                BmclForgeListUrl + Uri.EscapeDataString(minecraftVersion),
                TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

            return ParseBmclForgeList(json, minecraftVersion);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return [];
        }
    }

    internal static IReadOnlyList<ModLoaderVersionInfo> ParseBmclForgeList(string json, string minecraftVersion)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<ModLoaderVersionInfo> result = [];
        foreach (JsonElement entry in document.RootElement.EnumerateArray())
        {
            // 防御：BMCL 数据偶有脏行，mcversion 字段存在且不匹配时跳过
            if (entry.TryGetProperty("mcversion", out JsonElement mcElement) &&
                !string.Equals(mcElement.GetString(), minecraftVersion, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!entry.TryGetProperty("version", out JsonElement versionElement)) continue;
            string bare = versionElement.GetString()?.Trim() ?? string.Empty;
            if (bare.Length == 0) continue;

            string fullVersion = minecraftVersion + "-" + bare;
            if (!seen.Add(fullVersion) || IsPrerelease(fullVersion)) continue;

            result.Add(new ModLoaderVersionInfo(
                ModLoaderKind.Forge,
                minecraftVersion,
                fullVersion,
                $"Forge {fullVersion}",
                true,
                InstallerUrl(fullVersion, minecraftVersion)));
        }

        return ModLoaderInstaller.SortLoaderVersions(result);
    }

    /// <summary>官方 Maven 全量元数据：解析 maven-metadata.xml 的 version 列表（直连，不走镜像重写）。</summary>
    private static async Task<IReadOnlyList<ModLoaderVersionInfo>> TryMavenMetadataAsync(
        string minecraftVersion, CancellationToken cancellationToken)
    {
        try
        {
            byte[] metadataBytes = await DownloadSourceProvider.GetBytesDirectAsync(
                MetadataUrl, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            string metadata = System.Text.Encoding.UTF8.GetString(metadataBytes);

            return ParseMavenMetadata(metadata, minecraftVersion);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return [];
        }
    }

    internal static IReadOnlyList<ModLoaderVersionInfo> ParseMavenMetadata(string xml, string minecraftVersion)
    {
        string prefix = minecraftVersion + "-";
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<ModLoaderVersionInfo> result = [];

        foreach (Match match in VersionTag.Matches(xml))
        {
            string fullVersion = match.Groups[1].Value.Trim();
            if (!fullVersion.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(fullVersion) || IsPrerelease(fullVersion)) continue;

            result.Add(new ModLoaderVersionInfo(
                ModLoaderKind.Forge,
                minecraftVersion,
                fullVersion,
                $"Forge {fullVersion}",
                true,
                InstallerUrl(fullVersion, minecraftVersion)));
        }

        return ModLoaderInstaller.SortLoaderVersions(result);
    }

    /// <summary>预发布 / 非安装目标过滤：shim 是 ModDevGradle 兼容包（maven 用下划线、BMCL 用连字符），beta/pre/rc 不是正式构建。</summary>
    private static bool IsPrerelease(string version)
    {
        string lower = version.ToLowerInvariant();

        return lower.Contains("shim", StringComparison.Ordinal) ||
               lower.Contains("-beta", StringComparison.Ordinal) ||
               lower.Contains("-pre", StringComparison.Ordinal) ||
               lower.Contains("-rc", StringComparison.Ordinal);
    }

    private static readonly Regex VersionTag = new(
        "<version>([^<]+)</version>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static async Task<string> GetPromotionsAsync(CancellationToken cancellationToken)
    {
        lock (PromotionsLock)
        {
            if (_promotionsJson is not null && DateTimeOffset.UtcNow - _promotionsAt < TimeSpan.FromMinutes(10))
            {
                return _promotionsJson;
            }
        }

        await PromotionsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_promotionsJson is not null && DateTimeOffset.UtcNow - _promotionsAt < TimeSpan.FromMinutes(10))
            {
                return _promotionsJson;
            }

            string json;
            try
            {
                // files.minecraftforge.net 偶尔要 20 秒才响应（尤其没有 Accept 时），超时放宽
                json = await DownloadSourceProvider.GetStringAsync(
                    PromotionsUrl, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                json = await GetStringDirectAsync(
                    PromotionsUrl, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            }

            _promotionsJson = json;
            _promotionsAt = DateTimeOffset.UtcNow;
            return json;
        }
        finally
        {
            PromotionsGate.Release();
        }
    }

    private static IReadOnlyList<ModLoaderVersionInfo> ParsePromotions(string json, string minecraftVersion)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("promos", out JsonElement promos) ||
            promos.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        Dictionary<string, ModLoaderVersionInfo> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in promos.EnumerateObject())
        {
            string key = property.Name;
            bool recommended = key.EndsWith("-recommended", StringComparison.OrdinalIgnoreCase);
            bool latest = key.EndsWith("-latest", StringComparison.OrdinalIgnoreCase);
            if (!recommended && !latest) continue;

            string suffix = recommended ? "-recommended" : "-latest";
            string keyVersion = key[..^suffix.Length];
            if (!string.Equals(keyVersion, minecraftVersion, StringComparison.OrdinalIgnoreCase)) continue;

            string forgeVersion = property.Value.GetString()?.Trim() ?? string.Empty;
            if (forgeVersion.Length == 0) continue;

            string fullVersion = minecraftVersion + "-" + forgeVersion;
            if (result.TryGetValue(fullVersion, out ModLoaderVersionInfo? existing) && existing.IsStable)
            {
                continue;
            }

            result[fullVersion] = new ModLoaderVersionInfo(
                ModLoaderKind.Forge,
                minecraftVersion,
                fullVersion,
                $"Forge {fullVersion}",
                recommended,
                InstallerUrl(fullVersion, minecraftVersion));
        }

        return ModLoaderInstaller.SortLoaderVersions(result.Values);
    }

    private static async Task<string> GetStringDirectAsync(
        string url,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using HttpResponseMessage response = await DownloadSourceProvider.Http
            .GetAsync(url, timeoutCts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
    }

    /// <summary>Forge 安装器地址；loaderVersion 允许是裸构建号或完整 "mc-build"。</summary>
    public static string InstallerUrl(string loaderVersion, string minecraftVersion)
    {
        string mc = (minecraftVersion ?? string.Empty).Trim();
        string version = (loaderVersion ?? string.Empty).Trim();
        string fullVersion = version.StartsWith(mc + "-", StringComparison.OrdinalIgnoreCase)
            ? version
            : mc + "-" + version;
        return MavenRoot + Uri.EscapeDataString(fullVersion) +
               "/forge-" + Uri.EscapeDataString(fullVersion) + "-installer.jar";
    }

    public static Task<string> InstallAsync(
        ModLoaderVersionInfo version,
        string gameDirectory,
        string instanceName,
        IProgress<ModLoaderInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return ModLoaderInstaller.InstallWithInstallerAsync(
            version, gameDirectory, instanceName, progress, cancellationToken,
            directInstallSources: [version.InstallSource]);
    }
}



/// <summary>
/// 加载器安装共用步骤：补装原版、查找/自动安装 Java、下载并运行安装器、把安装器生成的
/// versions/&lt;id&gt; 目录对齐到用户指定的实例名。只被同目录下的加载器类使用。
/// </summary>
