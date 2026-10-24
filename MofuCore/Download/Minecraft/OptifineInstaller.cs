using System.Text.RegularExpressions;

namespace MofuCore.Download.Minecraft;

/// <summary>
/// OptiFine：没有 meta API，版本列表来自官网下载页的文件链接；
/// 安装器是自解包程序（1.21+ 为 xdelta 补丁 + modlauncher 架构），必须运行
/// <c>java -cp installer.jar optifine.Installer --installGame &lt;目录&gt;</c>。
/// </summary>
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
                // 临时目录清理失败不影响安装
            }
        }
    }

    /// <summary>由版本列表里的 loaderVersion 推导 adloadx 引导页（稳定版）。</summary>
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

        // 简单确认下载到的确实是 JAR，避免把 HTML 错误页当安装器运行
        if (bytes.Length < 4 || bytes[0] != 'P' || bytes[1] != 'K')
        {
            throw new InvalidOperationException("OptiFine 安装器下载内容不是有效的 JAR 文件。");
        }

        return bytes;
    }
}
