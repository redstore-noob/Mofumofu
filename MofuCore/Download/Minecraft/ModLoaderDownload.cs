using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MofuCore.Config;
using MofuCore.Java;

namespace MofuCore.Download.Minecraft;

/// <summary>
/// 五种加载器的版本查询与安装：Fabric / Quilt 走各自的 meta 与 maven；
/// Forge / NeoForge 读 maven-metadata 与官方安装器；OptiFine 解析官网下载页。
/// </summary>
public enum ModLoaderKind
{
    Vanilla,
    Fabric,
    Quilt,
    Forge,
    NeoForge,
    OptiFine,
}

public sealed record ModLoaderVersionInfo(
    ModLoaderKind Kind,
    string MinecraftVersion,
    string LoaderVersion,
    string DisplayName,
    bool IsStable,
    string InstallSource)
{
    public bool RequiresInstaller => Kind is ModLoaderKind.Forge or ModLoaderKind.NeoForge or ModLoaderKind.OptiFine;
}

public sealed record ModLoaderInstallProgress(
    int StageIndex,
    string StageName,
    string Detail,
    double Percentage,
    double BytesPerSecond = 0,
    long CompletedBytes = 0,
    long TotalBytes = 0);

public static class FabricDownload
{
    public const string MetaRoot = ApiEndpoints.FabricMeta + "/versions/loader/";

    public static async Task<IReadOnlyList<ModLoaderVersionInfo>> GetVersionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        string mc = RequireMinecraftVersion(minecraftVersion);
        string url = MetaRoot + Uri.EscapeDataString(mc);
        string json = await DownloadSourceProvider.GetStringAsync(url, TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);

        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

        List<ModLoaderVersionInfo> result = [];
        foreach (JsonElement entry in document.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("loader", out JsonElement loader) ||
                !loader.TryGetProperty("version", out JsonElement versionElement))
            {
                continue;
            }

            string? version = versionElement.GetString();
            if (string.IsNullOrWhiteSpace(version)) continue;

            bool stable = loader.TryGetProperty("stable", out JsonElement stableElement) &&
                          stableElement.ValueKind == JsonValueKind.True;
            result.Add(new ModLoaderVersionInfo(
                ModLoaderKind.Fabric,
                mc,
                version.Trim(),
                $"Fabric Loader {version.Trim()}",
                stable,
                $"{MetaRoot}{Uri.EscapeDataString(mc)}/{Uri.EscapeDataString(version.Trim())}/profile/json"));
        }

        return ModLoaderInstaller.SortLoaderVersions(result);
    }

    public static async Task<string> InstallAsync(
        ModLoaderVersionInfo version,
        string gameDirectory,
        string instanceName,
        IProgress<ModLoaderInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await ModLoaderInstaller.EnsureVanillaAsync(
            gameDirectory, version.MinecraftVersion, progress, cancellationToken).ConfigureAwait(false);

        ModLoaderInstaller.Report(progress, 60, "获取 Fabric 配置", "正在读取 Fabric Loader 的版本 JSON…");
        string json = await DownloadSourceProvider.GetStringAsync(
            version.InstallSource, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

        await ModLoaderInstaller.WriteProfileVersionAsync(
            gameDirectory, instanceName, json, progress, cancellationToken).ConfigureAwait(false);
        return instanceName;
    }

    internal static string RequireMinecraftVersion(string minecraftVersion)
    {
        string mc = (minecraftVersion ?? string.Empty).Trim();
        if (mc.Length == 0)
        {
            throw new InvalidOperationException("Minecraft 版本不能为空。");
        }
        return mc;
    }
}



public static class QuiltLoaderDownload
{
    public const string MetaRoot = ApiEndpoints.QuiltMeta + "/versions/loader/";

    public static async Task<IReadOnlyList<ModLoaderVersionInfo>> GetVersionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        string mc = FabricDownload.RequireMinecraftVersion(minecraftVersion);
        string url = MetaRoot + Uri.EscapeDataString(mc);
        string json = await DownloadSourceProvider.GetStringAsync(url, TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);

        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

        List<ModLoaderVersionInfo> result = [];
        foreach (JsonElement entry in document.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("loader", out JsonElement loader) ||
                !loader.TryGetProperty("version", out JsonElement versionElement))
            {
                continue;
            }

            string? version = versionElement.GetString();
            if (string.IsNullOrWhiteSpace(version)) continue;

            bool stable = loader.TryGetProperty("stable", out JsonElement stableElement)
                ? stableElement.ValueKind == JsonValueKind.True
                : !version.Contains("-beta", StringComparison.OrdinalIgnoreCase) &&
                  !version.Contains("-pre", StringComparison.OrdinalIgnoreCase) &&
                  !version.Contains("-rc", StringComparison.OrdinalIgnoreCase);
            result.Add(new ModLoaderVersionInfo(
                ModLoaderKind.Quilt,
                mc,
                version.Trim(),
                $"Quilt Loader {version.Trim()}",
                stable,
                $"{MetaRoot}{Uri.EscapeDataString(mc)}/{Uri.EscapeDataString(version.Trim())}/profile/json"));
        }

        return ModLoaderInstaller.SortLoaderVersions(result);
    }

    public static async Task<string> InstallAsync(
        ModLoaderVersionInfo version,
        string gameDirectory,
        string instanceName,
        IProgress<ModLoaderInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await ModLoaderInstaller.EnsureVanillaAsync(
            gameDirectory, version.MinecraftVersion, progress, cancellationToken).ConfigureAwait(false);

        ModLoaderInstaller.Report(progress, 60, "获取 Quilt 配置", "正在读取 Quilt Loader 的版本 JSON…");
        string json = await DownloadSourceProvider.GetStringAsync(
            version.InstallSource, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

        await ModLoaderInstaller.WriteProfileVersionAsync(
            gameDirectory, instanceName, json, progress, cancellationToken).ConfigureAwait(false);
        return instanceName;
    }
}



public static class ForgeDownload
{
    public const string PromotionsUrl = ApiEndpoints.ForgeFiles + "/net/minecraftforge/forge/promotions_slim.json";
    public const string MetadataUrl = ApiEndpoints.ForgeMaven + "/net/minecraftforge/forge/maven-metadata.xml";
    public const string MavenRoot = ApiEndpoints.ForgeMaven + "/net/minecraftforge/forge/";
    public const string BmclForgeListUrl = ApiEndpoints.BmclApi + "/forge/minecraft/";

    private static readonly SemaphoreSlim PromotionsGate = new(1, 1);
    private static readonly object PromotionsLock = new();
    private static string? _promotionsJson;
    private static DateTimeOffset _promotionsAt;

    public static async Task<IReadOnlyList<ModLoaderVersionInfo>> GetVersionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        string mc = FabricDownload.RequireMinecraftVersion(minecraftVersion);

        IReadOnlyList<ModLoaderVersionInfo> full = await TryBmclFullListAsync(mc, cancellationToken)
            .ConfigureAwait(false);
        if (full.Count > 0) return full;

        full = await TryMavenMetadataAsync(mc, cancellationToken).ConfigureAwait(false);
        if (full.Count > 0) return full;

        string json = await GetPromotionsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ModLoaderVersionInfo> result = ParsePromotions(json, mc);
        if (result.Count > 0) return result;

        string official = await GetStringDirectAsync(
            PromotionsUrl, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        return ParsePromotions(official, mc);
    }

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






public static class NeoforgeLoaderDownload
{
    private const string OfficialMetadataUrl =
        ApiEndpoints.NeoForgeMaven + "/net/neoforged/neoforge/maven-metadata.xml";
    private const string OfficialInstallerRoot =
        ApiEndpoints.NeoForgeMaven + "/net/neoforged/neoforge/";
    private const string BmclListUrl = ApiEndpoints.BmclApi + "/neoforge/list/";
    private const string BmclInstallerRoot = ApiEndpoints.BmclApi + "/neoforge/version/";

    private static readonly Regex VersionTag = new(
        "<version>([^<]+)</version>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<IReadOnlyList<ModLoaderVersionInfo>> GetVersionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        string mc = FabricDownload.RequireMinecraftVersion(minecraftVersion);
        bool preferBmcl = !string.Equals(
            DownloadSourceProvider.Active.Name, DownloadSources.Official.Name, StringComparison.Ordinal);

        if (preferBmcl)
        {
            IReadOnlyList<ModLoaderVersionInfo> fromBmcl = await TryBmclAsync(mc, cancellationToken)
                .ConfigureAwait(false);
            if (fromBmcl.Count > 0) return fromBmcl;

            return await TryOfficialAsync(mc, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<ModLoaderVersionInfo> fromOfficial = await TryOfficialAsync(mc, cancellationToken)
            .ConfigureAwait(false);
        if (fromOfficial.Count > 0) return fromOfficial;

        return await TryBmclAsync(mc, cancellationToken).ConfigureAwait(false);
    }

    public static Task<string> InstallAsync(
        ModLoaderVersionInfo version,
        string gameDirectory,
        string instanceName,
        IProgress<ModLoaderInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string bare = Uri.EscapeDataString(version.LoaderVersion);
        string full = Uri.EscapeDataString(version.MinecraftVersion + "-" + version.LoaderVersion);
        List<string> sources =
        [
            version.InstallSource,
            $"{BmclInstallerRoot}{full}/download/installer.jar",
            $"{BmclInstallerRoot}{bare}/download/installer.jar",
            $"{OfficialInstallerRoot}{bare}/neoforge-{bare}-installer.jar",
            $"https://maven.neoforged.net/releases/net/neoforged/forge/{full}/forge-{full}-installer.jar",
        ];

        return ModLoaderInstaller.InstallWithInstallerAsync(
            version, gameDirectory, instanceName, progress, cancellationToken, sources,
            directInstallSources:
            [
                $"{OfficialInstallerRoot}{bare}/neoforge-{bare}-installer.jar",
                $"https://maven.neoforged.net/releases/net/neoforged/forge/{full}/forge-{full}-installer.jar",
            ]);
    }

    public static string InstallerUrl(string loaderVersion)
    {
        string version = Uri.EscapeDataString((loaderVersion ?? string.Empty).Trim());
        bool useBmcl = !string.Equals(
            DownloadSourceProvider.Active.Name, DownloadSources.Official.Name, StringComparison.Ordinal);
        return useBmcl
            ? $"{BmclInstallerRoot}{version}/download/installer.jar"
            : $"{OfficialInstallerRoot}{version}/neoforge-{version}-installer.jar";
    }

    public static string NormalizeVersion(string version)
    {
        string value = (version ?? string.Empty).Trim();
        int dash = value.IndexOf('-');
        if (dash > 0 && dash < value.Length - 1 && char.IsDigit(value[dash + 1]))
        {
            return value[(dash + 1)..];
        }
        return value;
    }

    public static string ToVersionPrefix(string minecraftVersion)
    {
        string[] parts = (minecraftVersion ?? string.Empty).Trim().Split('.');
        if (parts.Length >= 3) return parts[1] + "." + parts[2];
        if (parts.Length == 2) return parts[1] + ".0";
        return minecraftVersion ?? string.Empty;
    }

    private static async Task<IReadOnlyList<ModLoaderVersionInfo>> TryBmclAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            string json = await DownloadSourceProvider.GetStringAsync(
                BmclListUrl + Uri.EscapeDataString(minecraftVersion),
                TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);

            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            List<ModLoaderVersionInfo> result = [];
            foreach (JsonElement entry in document.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("version", out JsonElement versionElement)) continue;
                string version = NormalizeVersion(versionElement.GetString() ?? string.Empty);
                if (version.Length == 0 || !seen.Add(version)) continue;

                result.Add(new ModLoaderVersionInfo(
                    ModLoaderKind.NeoForge,
                    minecraftVersion,
                    version,
                    $"NeoForge {version}",
                    true,
                    $"{BmclInstallerRoot}{Uri.EscapeDataString(version)}/download/installer.jar"));
            }

            return ModLoaderInstaller.SortLoaderVersions(result);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return [];
        }
    }

    private static async Task<IReadOnlyList<ModLoaderVersionInfo>> TryOfficialAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[] metadataBytes = await DownloadSourceProvider.GetBytesDirectAsync(
                OfficialMetadataUrl, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            string metadata = System.Text.Encoding.UTF8.GetString(metadataBytes);

            string prefix = ToVersionPrefix(minecraftVersion);
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            List<ModLoaderVersionInfo> result = [];
            foreach (Match match in VersionTag.Matches(metadata))
            {
                string version = match.Groups[1].Value.Trim();
                if (!MatchesPrefix(version, prefix) || !seen.Add(version)) continue;

                result.Add(new ModLoaderVersionInfo(
                    ModLoaderKind.NeoForge,
                    minecraftVersion,
                    version,
                    $"NeoForge {version}",
                    true,
                    $"{OfficialInstallerRoot}{Uri.EscapeDataString(version)}/neoforge-{Uri.EscapeDataString(version)}-installer.jar"));
            }

            return ModLoaderInstaller.SortLoaderVersions(result);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return [];
        }
    }

    private static bool MatchesPrefix(string value, string prefix)
    {
        return string.Equals(value, prefix, StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase);
    }
}



public static class OptifineInstaller
{
    private const string DownloadsUrl = "https://optifine.net/downloads";
    private const string DownloadPageRoot = "https://optifine.net/";

    private static readonly Regex FileLink = new(
        @"(?:preview_)?OptiFine_[0-9][A-Za-z0-9._-]*?\.jar",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DownloadLink = new(
        @"downloadx\?f=[A-Za-z0-9._=&;-]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(2);

    public static async Task<IReadOnlyList<ModLoaderVersionInfo>> GetVersionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        string mc = FabricDownload.RequireMinecraftVersion(minecraftVersion);
        string page = await DownloadSourceProvider.GetStringAsync(
            DownloadsUrl, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<ModLoaderVersionInfo> result = [];
        foreach (Match match in FileLink.Matches(page))
        {
            string fileName = match.Value;
            if (!seen.Add(fileName)) continue;

            bool preview = fileName.StartsWith("preview_", StringComparison.OrdinalIgnoreCase);
            string baseName = preview ? fileName["preview_".Length..] : fileName;
            string name = baseName["OptiFine_".Length..];
            if (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^4];
            }

            int separator = name.IndexOf('_');
            if (separator <= 0) continue;

            string fileMinecraftVersion = name[..separator];
            if (!string.Equals(fileMinecraftVersion, mc, StringComparison.OrdinalIgnoreCase)) continue;

            string loaderVersion = name;
            result.Add(new ModLoaderVersionInfo(
                ModLoaderKind.OptiFine,
                mc,
                loaderVersion,
                preview ? $"OptiFine {name[separator..].TrimStart('_')}（预览）" : $"OptiFine {name[separator..].TrimStart('_')}",
                !preview,
                $"{DownloadPageRoot}adloadx?f={Uri.EscapeDataString(fileName)}"));
        }

        return ModLoaderInstaller.SortLoaderVersions(result);
    }

    public static async Task<string> InstallAsync(
        ModLoaderVersionInfo version,
        string gameDirectory,
        string instanceName,
        IProgress<ModLoaderInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await ModLoaderInstaller.EnsureVanillaAsync(
            gameDirectory, version.MinecraftVersion, progress, cancellationToken).ConfigureAwait(false);

        ModLoaderInstaller.Report(progress, 48, "下载 OptiFine", "正在解析官网下载页并下载安装器…");
        byte[] installer = await DownloadInstallerAsync(version.InstallSource, cancellationToken)
            .ConfigureAwait(false);

        string temporaryDirectory = Path.Combine(Path.GetTempPath(), "mofu-optifine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        string installerPath = Path.Combine(temporaryDirectory, "OptiFine-installer.jar");
        try
        {
            await File.WriteAllBytesAsync(installerPath, installer, cancellationToken).ConfigureAwait(false);
            return await ModLoaderInstaller.RunInstallerAndAlignAsync(
                gameDirectory, version, instanceName, installerPath, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                Directory.Delete(temporaryDirectory, true);
            }
            catch (Exception)
            {
            }
        }
    }

    public static string AdLoadUrl(string loaderVersion, string minecraftVersion)
    {
        string loader = (loaderVersion ?? string.Empty).Trim();
        if (loader.StartsWith("OptiFine_", StringComparison.OrdinalIgnoreCase))
        {
            loader = loader["OptiFine_".Length..];
        }

        bool preview = loader.Contains("_pre", StringComparison.OrdinalIgnoreCase);
        string fileName = (preview ? "preview_OptiFine_" : "OptiFine_") + loader + ".jar";
        return $"{DownloadPageRoot}adloadx?f={Uri.EscapeDataString(fileName)}";
    }

    private static async Task<byte[]> DownloadInstallerAsync(string adLoadUrl, CancellationToken cancellationToken)
    {
        string page = await DownloadSourceProvider.GetStringAsync(
            adLoadUrl, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        Match match = DownloadLink.Match(page);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                "无法从 OptiFine 下载页解析出安装器直链（官网页面结构可能已变化）。");
        }

        string link = match.Value.Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);
        string downloadUrl = DownloadPageRoot + link;
        byte[] bytes = await DownloadSourceProvider.GetBytesAsync(
            downloadUrl, InstallerTimeout, cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            throw new InvalidOperationException("OptiFine 安装器下载结果为空。");
        }

        if (bytes.Length < 4 || bytes[0] != 'P' || bytes[1] != 'K')
        {
            throw new InvalidOperationException("OptiFine 安装器下载内容不是有效的 JAR 文件。");
        }

        return bytes;
    }
}



public static class ModLoaderInstaller
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InstallationLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public static string DisplayName(ModLoaderKind kind) => kind switch
    {
        ModLoaderKind.Fabric => "Fabric",
        ModLoaderKind.Quilt => "Quilt",
        ModLoaderKind.Forge => "Forge",
        ModLoaderKind.NeoForge => "NeoForge",
        ModLoaderKind.OptiFine => "OptiFine",
        _ => "原版",
    };

    public static Task<IReadOnlyList<ModLoaderVersionInfo>> GetVersionsAsync(
        ModLoaderKind kind,
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        return kind switch
        {
            ModLoaderKind.Fabric => FabricDownload.GetVersionsAsync(minecraftVersion, cancellationToken),
            ModLoaderKind.Quilt => QuiltLoaderDownload.GetVersionsAsync(minecraftVersion, cancellationToken),
            ModLoaderKind.Forge => ForgeDownload.GetVersionsAsync(minecraftVersion, cancellationToken),
            ModLoaderKind.NeoForge => NeoforgeLoaderDownload.GetVersionsAsync(minecraftVersion, cancellationToken),
            ModLoaderKind.OptiFine => OptifineInstaller.GetVersionsAsync(minecraftVersion, cancellationToken),
            _ => Task.FromResult<IReadOnlyList<ModLoaderVersionInfo>>([]),
        };
    }

    public static async Task<string> InstallAsync(
        ModLoaderVersionInfo version,
        string gameDirectory,
        string? instanceName = null,
        IProgress<ModLoaderInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (version.Kind == ModLoaderKind.Vanilla)
        {
            throw new InvalidOperationException("原版没有加载器可以安装。");
        }

        string root = NormalizeGameDirectory(gameDirectory);
        string name = string.IsNullOrWhiteSpace(instanceName)
            ? DefaultInstanceName(version.Kind, version.MinecraftVersion, version.LoaderVersion)
            : instanceName.Trim();
        string lockKey = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        SemaphoreSlim installationLock = InstallationLocks.GetOrAdd(
            lockKey, _ => new SemaphoreSlim(1, 1));
        await installationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        FileStream? processLock = null;
        try
        {
            string processLockPath = InstanceUpdate.GetCrossProcessLockPath(root, "loader-installer");
            processLock = await InstanceUpdate.AcquireCrossProcessLockAsync(
                processLockPath, true, cancellationToken).ConfigureAwait(false);
            return await (version.Kind switch
            {
                ModLoaderKind.Fabric => FabricDownload.InstallAsync(version, root, name, progress, cancellationToken),
                ModLoaderKind.Quilt => QuiltLoaderDownload.InstallAsync(version, root, name, progress, cancellationToken),
                ModLoaderKind.Forge => ForgeDownload.InstallAsync(version, root, name, progress, cancellationToken),
                ModLoaderKind.NeoForge => NeoforgeLoaderDownload.InstallAsync(version, root, name, progress, cancellationToken),
                ModLoaderKind.OptiFine => OptifineInstaller.InstallAsync(version, root, name, progress, cancellationToken),
                _ => throw new InvalidOperationException("不支持的加载器类型。"),
            }).ConfigureAwait(false);
        }
        finally
        {
            processLock?.Dispose();
            installationLock.Release();
        }
    }

    public static ModLoaderVersionInfo CreateVersion(
        ModLoaderKind kind,
        string minecraftVersion,
        string loaderVersion)
    {
        string mc = (minecraftVersion ?? string.Empty).Trim();
        string loader = (loaderVersion ?? string.Empty).Trim();
        if (mc.Length == 0 || loader.Length == 0)
        {
            throw new InvalidOperationException("Minecraft 版本与加载器版本都不能为空。");
        }

        return kind switch
        {
            ModLoaderKind.Fabric => new ModLoaderVersionInfo(
                kind, mc, loader, $"Fabric Loader {loader}", true,
                $"{FabricDownload.MetaRoot}{Uri.EscapeDataString(mc)}/{Uri.EscapeDataString(loader)}/profile/json"),
            ModLoaderKind.Quilt => new ModLoaderVersionInfo(
                kind, mc, loader, $"Quilt Loader {loader}", true,
                $"{QuiltLoaderDownload.MetaRoot}{Uri.EscapeDataString(mc)}/{Uri.EscapeDataString(loader)}/profile/json"),
            ModLoaderKind.Forge => new ModLoaderVersionInfo(
                kind, mc, loader, $"Forge {loader}", true,
                ForgeDownload.InstallerUrl(loader, mc)),
            ModLoaderKind.NeoForge => new ModLoaderVersionInfo(
                kind, mc, loader, $"NeoForge {loader}", true,
                NeoforgeLoaderDownload.InstallerUrl(loader)),
            ModLoaderKind.OptiFine => new ModLoaderVersionInfo(
                kind, mc, loader, $"OptiFine {loader}", true,
                OptifineInstaller.AdLoadUrl(loader, mc)),
            _ => throw new InvalidOperationException("原版不需要加载器描述。"),
        };
    }

    public static string DefaultInstanceName(ModLoaderKind kind, string minecraftVersion, string loaderVersion)
    {
        string mc = (minecraftVersion ?? string.Empty).Trim();
        string loader = (loaderVersion ?? string.Empty).Trim();
        if (loader.Length == 0)
        {
            return kind switch
            {
                ModLoaderKind.Fabric => $"fabric-loader-{mc}",
                ModLoaderKind.Quilt => $"quilt-loader-{mc}",
                ModLoaderKind.Forge => $"forge-{mc}",
                ModLoaderKind.NeoForge => $"neoforge-{mc}",
                ModLoaderKind.OptiFine => $"OptiFine_{mc}",
                _ => mc,
            };
        }

        return kind switch
        {
            ModLoaderKind.Fabric => $"fabric-loader-{loader}-{mc}",
            ModLoaderKind.Quilt => $"quilt-loader-{loader}-{mc}",
            ModLoaderKind.Forge => loader.StartsWith(mc + "-", StringComparison.OrdinalIgnoreCase)
                ? $"forge-{loader}"
                : $"forge-{mc}-{loader}",
            ModLoaderKind.NeoForge => $"neoforge-{loader}",
            ModLoaderKind.OptiFine => loader.StartsWith("OptiFine_", StringComparison.OrdinalIgnoreCase)
                ? loader
                : $"OptiFine_{loader}",
            _ => mc,
        };
    }

    internal static IReadOnlyList<ModLoaderVersionInfo> SortLoaderVersions(IEnumerable<ModLoaderVersionInfo> versions)
    {
        return versions
            .OrderByDescending(version => version.IsStable)
            .ThenByDescending(version => version.LoaderVersion, Comparer<string>.Create(CompareLoaderVersion))
            .ToList();
    }

    private static int CompareLoaderVersion(string? left, string? right)
    {
        string[] a = Split(left);
        string[] b = Split(right);
        int length = Math.Max(a.Length, b.Length);
        for (int index = 0; index < length; index++)
        {
            string partA = index < a.Length ? a[index] : "0";
            string partB = index < b.Length ? b[index] : "0";

            if (long.TryParse(partA, out long numberA) && long.TryParse(partB, out long numberB))
            {
                if (numberA != numberB) return numberA.CompareTo(numberB);
                continue;
            }

            int text = string.Compare(partA, partB, StringComparison.OrdinalIgnoreCase);
            if (text != 0) return text;
        }
        return 0;
    }

    private static string[] Split(string? value) =>
        (value ?? string.Empty)
            .Split(['.', '-', '_', '+'], StringSplitOptions.RemoveEmptyEntries);

private const string LoaderTemporarySuffix = ".mofu-download";
    private static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan InstallerDownloadTimeout = TimeSpan.FromMinutes(2);

    public static async Task EnsureVanillaAsync(
        string gameDirectory,
        string minecraftVersion,
        IProgress<ModLoaderInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        string root = NormalizeGameDirectory(gameDirectory);
        string mc = FabricDownload.RequireMinecraftVersion(minecraftVersion);
        string jsonPath = Path.Combine(root, "versions", mc, mc + ".json");
        if (File.Exists(jsonPath))
        {
            Report(progress, 3, "检查原版", $"已找到 Minecraft {mc}");
            return;
        }

        Report(progress, 3, "准备原版", $"未安装 Minecraft {mc}，正在自动补装…");
        string? metadataUrl = await MinecraftDownload
            .FindVersionMetadataUrlAsync(mc, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(metadataUrl))
        {
            throw new InvalidOperationException($"版本清单中没有 Minecraft {mc} 的元数据地址。");
        }

        MinecraftDownload download = new(root);
        IProgress<MinecraftInstallProgress>? mapped = progress is null
            ? null
            : new Progress<MinecraftInstallProgress>(update => Report(
                progress,
                3 + Math.Clamp(update.Percentage, 0, 100) * 0.42,
                "安装原版 Minecraft",
                string.IsNullOrWhiteSpace(update.Detail)
                    ? update.StageName
                    : $"{update.StageName} · {update.Detail}",
                update.BytesPerSecond,
                update.CompletedBytes,
                update.TotalBytes));

        await download.InstallAsync(mc, metadataUrl, mapped, cancellationToken).ConfigureAwait(false);
        Report(progress, 45, "原版安装完成", $"Minecraft {mc} 已就绪");
    }

    public static async Task WriteProfileVersionAsync(
        string gameDirectory,
        string instanceName,
        string json,
        IProgress<ModLoaderInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        string root = NormalizeGameDirectory(gameDirectory);
        string id = ValidateInstanceName(instanceName);
        string directory = Path.Combine(root, "versions", id);
        Directory.CreateDirectory(directory);

        string finalJson = RewriteVersionId(json, id);
        string target = Path.Combine(directory, id + ".json");
        string temporary = target + LoaderTemporarySuffix;
        try
        {
            await File.WriteAllTextAsync(temporary, finalJson, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, target, true);
        }
        finally
        {
            DeleteLoaderFileIfExists(temporary);
        }

        Report(progress, 100, "安装完成", $"{id} 已写入 versions/{id}");
    }

    public static async Task<string> InstallWithInstallerAsync(
        ModLoaderVersionInfo version,
        string gameDirectory,
        string instanceName,
        IProgress<ModLoaderInstallProgress>? progress,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? installSources = null,
        IReadOnlyList<string>? directInstallSources = null)
    {
        await EnsureVanillaAsync(
            gameDirectory, version.MinecraftVersion, progress, cancellationToken).ConfigureAwait(false);

        string root = NormalizeGameDirectory(gameDirectory);
        string id = ValidateInstanceName(instanceName);
        if (string.Equals(id, version.MinecraftVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"实例名称不能与原版版本号相同（{version.MinecraftVersion}），请换一个名称。");
        }

        Report(progress, 48, "下载安装器", $"正在下载 {ModLoaderInstaller.DisplayName(version.Kind)} 安装器…");
        byte[] installerBytes = await DownloadInstallerAsync(
            installSources is { Count: > 0 } ? installSources : [version.InstallSource],
            directInstallSources,
            cancellationToken).ConfigureAwait(false);
        if (installerBytes.Length == 0)
        {
            throw new InvalidOperationException("加载器安装器下载结果为空。");
        }

        string temporaryDirectory = Path.Combine(Path.GetTempPath(), "mofu-loader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        string installerPath = Path.Combine(temporaryDirectory, "installer.jar");
        try
        {
            await File.WriteAllBytesAsync(installerPath, installerBytes, cancellationToken).ConfigureAwait(false);
            return await RunInstallerAndAlignAsync(
                root, version, id, installerPath, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(temporaryDirectory);
        }
    }

    private static async Task<byte[]> DownloadInstallerAsync(
        IReadOnlyList<string> sources,
        IReadOnlyList<string>? directSources,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (string source in sources)
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            try
            {
                byte[] bytes = await DownloadSourceProvider.GetBytesAsync(
                    source, InstallerDownloadTimeout, cancellationToken).ConfigureAwait(false);
                if (IsZip(bytes)) return bytes;
                lastError = new InvalidOperationException("安装器下载内容不是有效的 JAR 文件。");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                lastError = e;
            }
        }

        if (directSources is not null)
        {
            foreach (string source in directSources)
            {
                if (string.IsNullOrWhiteSpace(source)) continue;
                try
                {
                    byte[] bytes = await DownloadSourceProvider.GetBytesDirectAsync(
                        source, InstallerDownloadTimeout, cancellationToken).ConfigureAwait(false);
                    if (IsZip(bytes)) return bytes;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    lastError = e;
                }
            }
        }

        throw new InvalidOperationException("加载器安装器下载失败。", lastError);
    }

    private static bool IsZip(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != (byte)'P' || bytes[1] != (byte)'K') return false;
        try
        {
            // 真正读出中央目录并确认有条目：仅前两字节碰巧为 PK 的损坏/截断体不算有效 JAR，
            // 避免落盘后被 java -jar 执行
            using var archive = new System.IO.Compression.ZipArchive(
                new MemoryStream(bytes), System.IO.Compression.ZipArchiveMode.Read);
            return archive.Entries.Count > 0;
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return false;
        }
    }

    public static async Task<string> RunInstallerAndAlignAsync(
        string gameDirectory,
        ModLoaderVersionInfo version,
        string instanceName,
        string installerPath,
        IProgress<ModLoaderInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        string root = NormalizeGameDirectory(gameDirectory);
        string id = ValidateInstanceName(instanceName);
        if (string.Equals(id, version.MinecraftVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"实例名称不能与原版版本号相同（{version.MinecraftVersion}），请换一个名称。");
        }

        string versionsDirectory = Path.Combine(root, "versions");
        Directory.CreateDirectory(versionsDirectory);
        EnsureLauncherProfiles(root);

        string javaExecutable = await ResolveJavaAsync(
            root, version.MinecraftVersion, version.Kind, progress, cancellationToken).ConfigureAwait(false);

        HashSet<string> before = SnapshotDirectories(versionsDirectory);
        Report(progress, 68, "运行安装器", $"正在运行 {ModLoaderInstaller.DisplayName(version.Kind)} 安装器…");
        string output = await RunInstallerAttemptsAsync(
            javaExecutable, installerPath, root, version.Kind, cancellationToken).ConfigureAwait(false);

        Report(progress, 90, "整理版本目录", "正在把安装器产物对齐到实例目录…");
        string sourceDirectory = DetectInstallerDirectory(
            versionsDirectory, before, version.MinecraftVersion, id, version.Kind);
        AlignInstanceDirectory(sourceDirectory, Path.Combine(versionsDirectory, id), id);
        VerifyRuntimeArtifact(root, version.Kind);

        string targetJson = Path.Combine(versionsDirectory, id, id + ".json");
        if (!File.Exists(targetJson))
        {
            string tail = Tail(output, 1600);
            throw new InvalidOperationException(
                $"{ModLoaderInstaller.DisplayName(version.Kind)} 安装器结束，但没有生成版本配置 {id}.json。" +
                (tail.Length > 0 ? $"安装器输出：{tail}" : string.Empty));
        }

        Report(progress, 100, "安装完成", $"{id} 已就绪");
        return id;
    }

    public static void Report(
        IProgress<ModLoaderInstallProgress>? progress,
        double percentage,
        string stage,
        string detail,
        double bytesPerSecond = 0,
        long completedBytes = 0,
        long totalBytes = 0)
    {
        progress?.Report(new ModLoaderInstallProgress(
            1, stage, detail, Math.Clamp(percentage, 0, 100), bytesPerSecond, completedBytes, totalBytes));
    }


    private static async Task<string> ResolveJavaAsync(
        string gameDirectory,
        string minecraftVersion,
        ModLoaderKind kind,
        IProgress<ModLoaderInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        string configured = string.Empty;
        try
        {
            configured = YamlRead.JavaExecutable.Trim();
        }
        catch (Exception)
        {
        }

        int requiredMajor = await ReadRequiredJavaMajorAsync(gameDirectory, minecraftVersion, cancellationToken)
            .ConfigureAwait(false);

        string? java = await JavaPathSearch.FindExactMatchJavaAsync(
            configuredPath: configured.Length > 0 ? configured : null,
            requiredMajorVersion: requiredMajor,
            runtimeDirectory: JavaDownload.GetRuntimeDirectory(gameDirectory),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(java))
        {
            return java;
        }

        Report(progress, 58, "准备 Java", $"未找到 Java {requiredMajor}，正在自动下载…");
        InstalledJavaRuntime? installed = await JavaDownload.InstallForMajorVersionAsync(
            requiredMajor, gameDirectory, null, cancellationToken).ConfigureAwait(false);
        if (installed is null && requiredMajor != 21)
        {
            installed = await JavaDownload.InstallForMajorVersionAsync(
                21, gameDirectory, null, cancellationToken).ConfigureAwait(false);
        }

        if (installed is null || string.IsNullOrWhiteSpace(installed.JavaExecutablePath))
        {
            throw new InvalidOperationException(
                $"{ModLoaderInstaller.DisplayName(kind)} 安装器需要 Java，但本机没有可用的运行时，自动下载也失败了。");
        }

        return installed.JavaExecutablePath;
    }

    private static async Task<int> ReadRequiredJavaMajorAsync(
        string gameDirectory,
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        string jsonPath = Path.Combine(gameDirectory, "versions", minecraftVersion, minecraftVersion + ".json");
        if (!File.Exists(jsonPath)) return 21;

        try
        {
            string json = await File.ReadAllTextAsync(jsonPath, cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("javaVersion", out JsonElement javaVersion) &&
                javaVersion.TryGetProperty("majorVersion", out JsonElement major) &&
                major.TryGetInt32(out int value) && value > 0)
            {
                return value;
            }
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
        }

        return 21;
    }


    private static async Task<string> RunInstallerAttemptsAsync(
        string javaExecutable,
        string installerPath,
        string gameDirectory,
        ModLoaderKind kind,
        CancellationToken cancellationToken)
    {
        List<string[]> attempts = BuildInstallerAttempts(installerPath, gameDirectory, kind);
        string lastOutput = string.Empty;
        Exception? lastError = null;

        foreach (string[] arguments in attempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (int exitCode, string output) = await RunProcessAsync(
                javaExecutable, arguments, gameDirectory, cancellationToken).ConfigureAwait(false);
            lastOutput = output;

            if (exitCode == 0)
            {
                return output;
            }

            lastError = new InvalidOperationException(
                $"{ModLoaderInstaller.DisplayName(kind)} 安装器退出码 {exitCode}：{Tail(output, 1600)}");
            if (!IsUnrecognizedOption(output))
            {
                throw lastError;
            }
        }

        throw new InvalidOperationException(
            $"{ModLoaderInstaller.DisplayName(kind)} 安装器运行失败：{Tail(lastOutput, 1600)}", lastError);
    }

    private static List<string[]> BuildInstallerAttempts(
        string installerPath,
        string gameDirectory,
        ModLoaderKind kind)
    {
        List<string[]> attempts = [];
        if (kind == ModLoaderKind.OptiFine)
        {
            attempts.Add(["-Djava.awt.headless=true", "-cp", installerPath, "optifine.Installer", "--installGame", gameDirectory]);
            attempts.Add(["-Djava.awt.headless=true", "-cp", installerPath, "optifine.Installer", "--install", gameDirectory]);
            return attempts;
        }

        List<string[]> mirrors = BuildMirrorArguments();
        foreach (string installArgument in new[] { "--installClient", "--install-client" })
        {
            foreach (string[] mirror in mirrors)
            {
                List<string> arguments = ["-Djava.awt.headless=true", "-jar", installerPath, installArgument, gameDirectory];
                arguments.AddRange(mirror);
                attempts.Add([.. arguments]);
            }
        }

        return attempts;
    }

    private static List<string[]> BuildMirrorArguments()
    {
        List<string[]> result = [];
        List<string> mirrors = [];
        AddMirror(mirrors, DownloadSourceProvider.Active.Maven);
        DownloadSource? fallback = DownloadSourceProvider.Fallback;
        if (fallback is not null) AddMirror(mirrors, fallback.Maven);

        if (mirrors.Count > 0)
        {
            result.Add(["--mirror", .. mirrors]);
        }
        result.Add([]);
        return result;
    }

    private static void AddMirror(List<string> mirrors, string maven)
    {
        string value = (maven ?? string.Empty).Trim();
        if (value.Length == 0) return;
        if (string.Equals(value.TrimEnd('/'), DownloadSources.Official.Maven.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (string.Equals(value.TrimEnd('/'), "https://maven.neoforged.net/releases",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!mirrors.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            mirrors.Add(value);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        StringBuilder output = new();
        object gate = new();
        void Append(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (gate)
            {
                output.AppendLine(line);
                if (output.Length > 32_000)
                {
                    output.Remove(0, output.Length - 24_000);
                }
            }
        }

        process.OutputDataReceived += (_, args) => Append(args.Data);
        process.ErrorDataReceived += (_, args) => Append(args.Data);
        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动加载器安装器进程。");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(InstallerTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("加载器安装器运行超时（10 分钟）。");
        }

        process.WaitForExit();
        lock (gate)
        {
            return (process.ExitCode, output.ToString());
        }
    }

    private static bool IsUnrecognizedOption(string output)
    {
        string text = output.ToLowerInvariant();
        return text.Contains("is not a recognized option", StringComparison.Ordinal) ||
               text.Contains("unrecognized option", StringComparison.Ordinal) ||
               text.Contains("unknown option", StringComparison.Ordinal);
    }


    private static HashSet<string> SnapshotDirectories(string versionsDirectory)
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string directory in Directory.GetDirectories(versionsDirectory))
            {
                result.Add(Path.GetFileName(directory));
            }
        }
        catch (Exception)
        {
        }

        return result;
    }

    private static string DetectInstallerDirectory(
        string versionsDirectory,
        HashSet<string> before,
        string minecraftVersion,
        string instanceName,
        ModLoaderKind kind)
    {
        string marker = kind switch
        {
            ModLoaderKind.NeoForge => "neoforge",
            ModLoaderKind.OptiFine => "optifine",
            _ => "forge",
        };

        List<string> directories;
        try
        {
            directories = Directory.GetDirectories(versionsDirectory)
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .ToList();
        }
        catch (Exception e)
        {
            throw new InvalidOperationException("无法读取 versions 目录：" + e.Message, e);
        }

        foreach (string directory in directories)
        {
            string name = Path.GetFileName(directory);
            if (string.Equals(name, minecraftVersion, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(name, instanceName, StringComparison.OrdinalIgnoreCase)) continue;
            if (before.Contains(name)) continue;
            if (!name.Contains(marker, StringComparison.OrdinalIgnoreCase)) continue;
            return directory;
        }

        string target = Path.Combine(versionsDirectory, instanceName);
        if (Directory.Exists(target) && Directory.EnumerateFiles(target, "*.json").Any())
        {
            return target;
        }

        throw new InvalidOperationException(
            $"{ModLoaderInstaller.DisplayName(kind)} 安装器结束，但没有发现新的 versions/{marker}* 目录。" +
            "请检查安装器输出确认 Java 版本与网络。");
    }

    private static void AlignInstanceDirectory(string sourceDirectory, string targetDirectory, string instanceName)
    {
        string sourceName = Path.GetFileName(sourceDirectory);
        if (string.Equals(
                Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            NormalizeVersionDirectory(targetDirectory, sourceName, instanceName);
            return;
        }

        if (Directory.Exists(targetDirectory))
        {
            MergeInstallerOutput(sourceDirectory, targetDirectory, instanceName);
            return;
        }

        Directory.Move(sourceDirectory, targetDirectory);
        NormalizeVersionDirectory(targetDirectory, sourceName, instanceName);
    }

    private static void NormalizeVersionDirectory(string directory, string oldName, string instanceName)
    {
        string targetJson = Path.Combine(directory, instanceName + ".json");
        string? sourceJson = Directory.EnumerateFiles(directory, "*.json")
            .FirstOrDefault(path => !string.Equals(
                Path.GetFileName(path), instanceName + ".json", StringComparison.OrdinalIgnoreCase));

        if (sourceJson is not null)
        {
            string json = File.ReadAllText(sourceJson);
            string rewritten = RewriteVersionId(json, instanceName);
            File.WriteAllText(targetJson, rewritten, new UTF8Encoding(false));
            if (!string.Equals(sourceJson, targetJson, StringComparison.OrdinalIgnoreCase))
            {
                DeleteLoaderFileIfExists(sourceJson);
            }
        }

        string oldJar = Path.Combine(directory, oldName + ".jar");
        string newJar = Path.Combine(directory, instanceName + ".jar");
        if (!string.Equals(oldJar, newJar, StringComparison.OrdinalIgnoreCase) && File.Exists(oldJar))
        {
            File.Move(oldJar, newJar, true);
        }
    }

    private static void MergeInstallerOutput(string sourceDirectory, string targetDirectory, string instanceName)
    {
        string? sourceJson = Directory.EnumerateFiles(sourceDirectory, "*.json").FirstOrDefault();
        if (sourceJson is not null)
        {
            string json = File.ReadAllText(sourceJson);
            File.WriteAllText(
                Path.Combine(targetDirectory, instanceName + ".json"),
                RewriteVersionId(json, instanceName),
                new UTF8Encoding(false));
        }

        string sourceName = Path.GetFileName(sourceDirectory);
        string sourceJar = Path.Combine(sourceDirectory, sourceName + ".jar");
        string targetJar = Path.Combine(targetDirectory, instanceName + ".jar");
        if (File.Exists(sourceJar) && !File.Exists(targetJar))
        {
            File.Copy(sourceJar, targetJar, true);
        }

        foreach (string file in Directory.EnumerateFiles(sourceDirectory))
        {
            string extension = Path.GetExtension(file);
            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jar", StringComparison.OrdinalIgnoreCase))
            {
                DeleteLoaderFileIfExists(file);
            }
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(sourceDirectory).Any())
            {
                Directory.Delete(sourceDirectory);
            }
        }
        catch (Exception)
        {
        }
    }

    private static void VerifyRuntimeArtifact(string gameDirectory, ModLoaderKind kind)
    {
        if (kind == ModLoaderKind.OptiFine) return;

        string libraries = Path.Combine(gameDirectory, "libraries");
        if (kind == ModLoaderKind.NeoForge)
        {
            string patched = Path.Combine(libraries, "net", "neoforged", "minecraft-client-patched");
            if (FindFile(patched, "*.jar") is null)
            {
                throw new InvalidOperationException(
                    "NeoForge 安装器没有生成运行时客户端（minecraft-client-patched），安装不完整。");
            }
            return;
        }

        string forgeClient = Path.Combine(libraries, "net", "minecraft", "client");
        if (FindFile(forgeClient, "*-srg.jar") is null)
        {
            throw new InvalidOperationException(
                "Forge 安装器没有生成 SRG 重映射客户端（*-srg.jar），安装不完整。");
        }
    }

    private static string? FindFile(string root, string pattern)
    {
        if (!Directory.Exists(root)) return null;
        try
        {
            return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }


    private static void EnsureLauncherProfiles(string minecraftRoot)
    {
        string profilePath = Path.Combine(minecraftRoot, "launcher_profiles.json");
        if (File.Exists(profilePath))
        {
            return;
        }

        const string template = """
                                {
                                  "profiles": {},
                                  "selectedProfile": "(Default)",
                                  "clientToken": "mofumofu",
                                  "authenticationDatabase": {}
                                }
                                """;
        try
        {
            File.WriteAllText(profilePath, template, new UTF8Encoding(false));
        }
        catch (IOException)
        {
        }
    }

    private static string RewriteVersionId(string json, string instanceName)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(json);
            if (node is not JsonObject root) return json;

            root["id"] = instanceName;
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static string ValidateInstanceName(string instanceName)
    {
        string name = (instanceName ?? string.Empty).Trim();
        MinecraftDownload.ValidateVersionId(name);
        return name;
    }

    private static string NormalizeGameDirectory(string gameDirectory)
    {
        string root = (gameDirectory ?? string.Empty).Trim();
        if (root.Length == 0)
        {
            throw new InvalidOperationException("Minecraft 目录不能为空。");
        }
        return Path.GetFullPath(root);
    }

    private static string Tail(string value, int maximum)
    {
        string text = (value ?? string.Empty).Trim();
        return text.Length <= maximum ? text : text[^maximum..];
    }

    private static void DeleteLoaderFileIfExists(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch (Exception)
        {
        }
    }
}
