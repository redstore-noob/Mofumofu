using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Mofumofu.Launcher.Services.Plugins;

/// <summary>
/// .nekoex 安装器：zip 包（info.yaml / description.md / extension.dll / icon.png）
/// 解包到 plugins/{id}/ 并热加载；卸载 = 热卸载 + 删目录（连同源 .nekoex）。
/// </summary>
public static class NekoexInstaller
{
    /// <summary>.nekoex 解压上限：条目数 / 单条目 / 总解压字节（防 zip 炸弹，口径对齐 MofuCore.Config.SizeLimits）。</summary>
    private const int MaxEntryCount = 256;
    private const long MaxSingleEntryBytes = MofuCore.Config.SizeLimits.PackageEntryMaximumBytes;
    private const long MaxTotalExtractedBytes = MofuCore.Config.SizeLimits.PackageExtractedMaximumBytes;

    /// <summary>包内允许的文件扩展名（可执行文件 / 脚本一律不放行）。</summary>
    private static readonly HashSet<string> AllowedEntryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".axaml", ".xaml", ".png", ".jpg", ".jpeg", ".gif", ".svg",
        ".json", ".yaml", ".yml", ".md", ".txt", ".xml", ".pdb", ".config",
        ".ttf", ".otf", ".woff", ".woff2",
    };

    /// <summary>
    /// 从 .nekoex 文件安装（或更新）插件：读包内 info.yaml 取 id → 解包 → 热加载。
    /// 包会被复制到 plugins/{id}.nekoex 作为安装源（卸载时一并删除）。
    /// </summary>
    /// <returns>安装后的插件 id</returns>
    public static async Task<string> InstallAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        string source = Path.GetFullPath(packagePath);
        if (File.Exists(source) == false)
        {
            throw new FileNotFoundException("安装包不存在：" + source);
        }

        string id = await ReadPackageIdAsync(source, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("安装包里找不到 info.yaml / plugin.yaml，无法确定插件 id。");

        string pluginsRoot = PluginManager.PluginsRoot;
        Directory.CreateDirectory(pluginsRoot);

        // id 白名单 + 路径包含校验：复制/解压/删除目标都落在 pluginsRoot 之内
        string canonical = PluginManager.ResolvePluginPath(id, ".nekoex");
        string target = PluginManager.ResolvePluginPath(id);

        // 安装源统一收进 plugins 根（同目录则原位使用）
        if (!string.Equals(source, canonical, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(source, canonical, overwrite: true);
        }

        // 更新场景：先热卸载旧运行时、清掉旧目录（注意不能删 canonical——它就是要解压的源）
        PluginManager.Unload(id);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        await Task.Run(() => ExtractPackage(canonical, target), cancellationToken)
            .ConfigureAwait(false);

        PluginLog.Host($"安装插件 {id}（{Path.GetFileName(source)}）");

        // LoadPlugin 需要 UI 线程（插件 Init 可能碰 UI）
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            PluginManager.LoadPlugin(target, canonical));

        return id;
    }

    /// <summary>卸载：热卸载运行时 + 删除插件目录与源 .nekoex。返回是否成功。</summary>
    public static bool Uninstall(string pluginId)
    {
        try
        {
            PluginManager.Unload(pluginId);
            UninstallFiles(pluginId);
            PluginLog.Host($"卸载插件 {pluginId}");
            return true;
        }
        catch (Exception error)
        {
            PluginLog.Host($"卸载 {pluginId} 失败：{error.Message}");
            return false;
        }
    }

    private static void UninstallFiles(string pluginId)
    {
        string directory = PluginManager.ResolvePluginPath(pluginId);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);

        string package = PluginManager.ResolvePluginPath(pluginId, ".nekoex");
        if (File.Exists(package)) File.Delete(package);
    }

    /// <summary>
    /// 安全解压 .nekoex：逐条目校验扩展名白名单、路径穿越、条目数 / 单条目 / 总解压字节上限，
    /// 校验通过才写盘。供安装流程与 PluginManager 解包裸包共用（校验通过后才允许 LoadPlugin）。
    /// </summary>
    internal static void ExtractPackage(string packagePath, string target)
    {
        string rootFull = Path.GetFullPath(target);
        string rootPrefix = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(rootFull);

        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        if (archive.Entries.Count > MaxEntryCount)
        {
            throw new InvalidOperationException($"插件包条目数超过上限 {MaxEntryCount}，拒绝解压。");
        }

        long totalExtracted = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string normalized = entry.FullName.Replace('\\', '/');
            if (normalized.Length == 0 || normalized.EndsWith('/')) continue; // 目录条目不写盘

            if (TryResolveSafeEntry(rootFull, rootPrefix, normalized, out string destination) == false)
            {
                throw new InvalidOperationException("插件包包含不允许的文件或路径穿越：" + entry.FullName);
            }

            if (entry.Length < 0 || entry.Length > MaxSingleEntryBytes)
            {
                throw new InvalidOperationException("插件包单条目超过大小上限：" + entry.FullName);
            }
            totalExtracted += entry.Length;
            if (totalExtracted > MaxTotalExtractedBytes)
            {
                throw new InvalidOperationException("插件包解压总量超过上限，拒绝解压。");
            }

            string? directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // 逐块写入并再次核对实际解压量（声明长度可能被伪造）。
            using Stream source = entry.Open();
            using FileStream output = new(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            long written = 0;
            byte[] buffer = new byte[81920];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > MaxSingleEntryBytes)
                {
                    throw new InvalidOperationException("插件包条目实际解压量超过上限：" + entry.FullName);
                }
                output.Write(buffer, 0, read);
            }
        }
    }

    /// <summary>校验条目名：拒绝绝对路径 / .. 穿越 / 非白名单扩展名，并解析出落在 target 内的目标路径。</summary>
    private static bool TryResolveSafeEntry(string rootFull, string rootPrefix, string normalized, out string destination)
    {
        destination = string.Empty;
        if (normalized.StartsWith('/') || normalized.Contains(':') || normalized.Contains("..")) return false;
        string fileName = Path.GetFileName(normalized);
        if (fileName.Length == 0) return false;
        if (AllowedEntryExtensions.Contains(Path.GetExtension(fileName)) == false) return false;

        string full = Path.GetFullPath(Path.Combine(rootFull, normalized));
        if (full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) == false) return false;
        destination = full;
        return true;
    }

    private static async Task<string?> ReadPackageIdAsync(string packagePath, CancellationToken cancellationToken)
    {
        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        foreach (string name in new[] { "info.yaml", "info.yml", "plugin.yaml", "plugin.yml" })
        {
            ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(e =>
                string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase));
            if (entry is null || entry.Length > MofuCore.Config.SizeLimits.PackageManifestMaximumBytes) continue;

            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            string content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            foreach (string line in content.Split('\n'))
            {
                if (line.TrimStart().StartsWith('#')) continue;
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                if (line[..colon].Trim().Equals("id", StringComparison.OrdinalIgnoreCase) == false) continue;

                string value = line[(colon + 1)..].Trim().Trim('"', '\'');
                if (PluginManifest.IsValidPluginId(value)) return value;
            }
        }

        return null;
    }
}
