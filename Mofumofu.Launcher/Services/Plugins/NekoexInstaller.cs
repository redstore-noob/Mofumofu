using System;
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

        // 安装源统一收进 plugins 根（同目录则原位使用）
        string canonical = Path.Combine(pluginsRoot, id + ".nekoex");
        if (!string.Equals(source, canonical, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(source, canonical, overwrite: true);
        }

        // 更新场景：先热卸载旧运行时、清掉旧目录（注意不能删 canonical——它就是要解压的源）
        PluginManager.Unload(id);
        string target = Path.Combine(pluginsRoot, id);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        await Task.Run(() => ZipFile.ExtractToDirectory(canonical, target, overwriteFiles: true), cancellationToken)
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
        string pluginsRoot = PluginManager.PluginsRoot;
        string directory = Path.Combine(pluginsRoot, pluginId);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);

        string package = Path.Combine(pluginsRoot, pluginId + ".nekoex");
        if (File.Exists(package)) File.Delete(package);
    }

    private static async Task<string?> ReadPackageIdAsync(string packagePath, CancellationToken cancellationToken)
    {
        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        foreach (string name in new[] { "info.yaml", "info.yml", "plugin.yaml", "plugin.yml" })
        {
            ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(e =>
                string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase));
            if (entry is null) continue;

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
                if (value.Length > 0) return value;
            }
        }

        return null;
    }
}
