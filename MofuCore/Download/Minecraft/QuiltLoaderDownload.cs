using System.Text.Json;

namespace MofuCore.Download.Minecraft;

/// <summary>
/// Quilt Loader：meta v3 的响应形状与 Fabric v2 相同，profile/json 也是标准版本 JSON
/// （inheritsFrom 原版 + Knot 启动主类），因此安装方式与 Fabric 一致。
/// </summary>
public static class QuiltLoaderDownload
{
    public const string MetaRoot = "https://meta.quiltmc.org/v3/versions/loader/";

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

            // Quilt 的 meta v3 没有 stable 字段：按版本号里的预发布标记推断
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
