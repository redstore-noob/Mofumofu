using System.Text.Json;

namespace MofuCore.Download.Minecraft;



/// <summary>Minecraft 加载器类型。</summary>
public enum ModLoaderKind
{
    Vanilla,
    Fabric,
    Quilt,
    Forge,
    NeoForge,
    OptiFine,
}

/// <summary>
/// 一个可安装的加载器版本。安装源由各加载器自己的元数据接口决定：
/// Fabric/Quilt 是 profile JSON，Forge/NeoForge 是安装器 JAR，OptiFine 是 adloadx 引导页。
/// </summary>
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

/// <summary>
/// 加载器安装进度。原版补装阶段会带速率与字节数（透传自 MinecraftInstallProgress），
/// 加载器自身的配置 / 安装器阶段没有字节级进度，三者保持默认值。
/// </summary>
public sealed record ModLoaderInstallProgress(
    int StageIndex,
    string StageName,
    string Detail,
    double Percentage,
    double BytesPerSecond = 0,
    long CompletedBytes = 0,
    long TotalBytes = 0);

/// <summary>Fabric Loader：meta API 直接提供标准版本 JSON，写入 versions/ 即可。</summary>
public static class FabricDownload
{
    public const string MetaRoot = "https://meta.fabricmc.net/v2/versions/loader/";

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

        // 自定义实例名必须覆盖 profile 自带的 id，否则用户填的名字不会生效
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
