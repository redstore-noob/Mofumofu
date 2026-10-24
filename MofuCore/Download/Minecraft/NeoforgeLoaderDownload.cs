using System.Text.Json;
using System.Text.RegularExpressions;

namespace MofuCore.Download.Minecraft;

/// <summary>
/// NeoForge：版本号格式为 "{mcMajor}.{mcMinor}.{patch}"（如 21.8.1）。
/// BMCL 源优先走 BMCLAPI，官方源优先走 maven.neoforged.net；与 Forge 一样必须运行安装器。
/// </summary>
public static class NeoforgeLoaderDownload
{
    private const string OfficialMetadataUrl =
        "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";
    private const string OfficialInstallerRoot =
        "https://maven.neoforged.net/releases/net/neoforged/neoforge/";
    private const string BmclListUrl = "https://bmclapi2.bangbang93.com/neoforge/list/";
    private const string BmclInstallerRoot = "https://bmclapi2.bangbang93.com/neoforge/version/";

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
        // BMCL 的 installer 端点在不同 NeoForge 世代使用不同版本键：
        // 1.20.1 需要 "1.20.1-47.x"，1.20.2+ 用裸 "21.x"；逐个尝试，最后回退官方 Maven。
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

    /// <summary>按当前下载源选择安装器地址。</summary>
    public static string InstallerUrl(string loaderVersion)
    {
        string version = Uri.EscapeDataString((loaderVersion ?? string.Empty).Trim());
        bool useBmcl = !string.Equals(
            DownloadSourceProvider.Active.Name, DownloadSources.Official.Name, StringComparison.Ordinal);
        return useBmcl
            ? $"{BmclInstallerRoot}{version}/download/installer.jar"
            : $"{OfficialInstallerRoot}{version}/neoforge-{version}-installer.jar";
    }

    /// <summary>把 BMCL 返回的 "1.20.1-47.1.12" 归一化为 "47.1.12"。</summary>
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

    /// <summary>Minecraft 1.21.8 → NeoForge 21.8；Minecraft 1.21 → 21.0。</summary>
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
            // 官方 Maven 元数据必须绕开镜像重写，否则 BMCL 的 /maven 路径未必有 maven-metadata.xml
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

    /// <summary>带版本边界的匹配：21.1 不能命中 21.11.x。</summary>
    private static bool MatchesPrefix(string value, string prefix)
    {
        return string.Equals(value, prefix, StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase);
    }
}
