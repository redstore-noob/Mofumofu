using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MofuCore.Config;

namespace Mofumofu.Launcher.Views.Widgets;

/// <summary>桌面小组件用的真实本机数据：扫描游戏目录下的实例（versions）与世界（saves）。</summary>
internal static class WidgetBackend
{
    /// <summary>本机已安装实例（来自 &lt;gameDir&gt;/versions/id/id.json）。</summary>
    internal sealed record InstanceInfo(
        string Id,
        string DisplayVersion,
        string Loader,
        string Icon,
        DateTime LastWrite,
        string Directory);

    /// <summary>本机存档（来自 &lt;gameDir&gt;/saves/*）。</summary>
    internal sealed record WorldInfo(string Name, DateTime LastWrite, long SizeBytes, string Directory);

    /// <summary>带所属实例的存档：隔离实例的存档归属该实例，共享目录的存档 InstanceId 为空（跟随当前选择）。</summary>
    internal sealed record InstanceWorldInfo(WorldInfo World, string? InstanceId);

    /// <summary>游戏目录：没配置时退回当前工作目录下的 .minecraft（不创建、不写配置）。</summary>
    internal static string GameDirectory
    {
        get
        {
            try
            {
                var configured = (YamlRead.GameDirectory ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(configured)) return configured;
            }
            catch
            {
                // 配置读不到时回落
            }

            try
            {
                return Path.GetFullPath(".minecraft");
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    /// <summary>扫描本机已安装版本；IO 在后台线程完成，失败返回空表。</summary>
    internal static Task<IReadOnlyList<InstanceInfo>> LoadInstancesAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<InstanceInfo>>(() =>
        {
            var result = new List<InstanceInfo>();
            try
            {
                var versionsRoot = Path.Combine(GameDirectory, "versions");
                if (!Directory.Exists(versionsRoot)) return result;

                foreach (var directory in Directory.GetDirectories(versionsRoot))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var id = Path.GetFileName(directory);
                    var jsonPath = Path.Combine(directory, id + ".json");
                    if (!File.Exists(jsonPath)) continue;

                    try
                    {
                        using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
                        var root = document.RootElement;

                        var version = root.TryGetProperty("id", out var idElement)
                            ? idElement.GetString() ?? id
                            : id;
                        var inherits = root.TryGetProperty("inheritsFrom", out var parentElement)
                            ? parentElement.GetString()
                            : null;
                        var type = root.TryGetProperty("type", out var typeElement)
                            ? typeElement.GetString() ?? "release"
                            : "release";

                        var loader = DetectLoader(inherits, id);
                        var displayVersion = string.IsNullOrWhiteSpace(inherits) ? version : inherits!;
                        var icon = InstanceIconFor(loader, displayVersion, type);
                        var lastWrite = File.GetLastWriteTime(jsonPath);

                        result.Add(new InstanceInfo(id, displayVersion, loader, icon, lastWrite, directory));
                    }
                    catch
                    {
                        // 单个版本 JSON 坏掉不影响其它实例
                    }
                }

                result.Sort((left, right) => right.LastWrite.CompareTo(left.LastWrite));
            }
            catch
            {
                // 游戏目录不可读时按空表处理
            }

            Log($"instances={result.Count} gameDir={GameDirectory}");
            return result;
        }, cancellationToken);

    /// <summary>扫描本机存档：名字=文件夹名，时间=level.dat 修改时间，大小=目录体积。</summary>
    internal static Task<IReadOnlyList<WorldInfo>> LoadWorldsAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<WorldInfo>>(() =>
        {
            var result = new List<WorldInfo>();
            try
            {
                var savesRoot = Path.Combine(GameDirectory, "saves");
                if (!Directory.Exists(savesRoot))
                {
                    Log($"worlds=0 savesExists=false savesRoot={savesRoot}");
                    return result;
                }

                foreach (var directory in Directory.GetDirectories(savesRoot))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var levelDat = Path.Combine(directory, "level.dat");
                        var lastWrite = File.Exists(levelDat)
                            ? File.GetLastWriteTime(levelDat)
                            : Directory.GetLastWriteTime(directory);

                        result.Add(new WorldInfo(
                            Path.GetFileName(directory),
                            lastWrite,
                            DirectorySize(directory),
                            directory));
                    }
                    catch
                    {
                        // 单个存档不可读时跳过
                    }
                }

                result.Sort((left, right) => right.LastWrite.CompareTo(left.LastWrite));
            }
            catch
            {
                // 存档目录不可读时按空表处理
            }

            Log($"worlds={result.Count} savesExists=true savesRoot={Path.Combine(GameDirectory, "saves")}");
            return result;
        }, cancellationToken);

    /// <summary>扫描所有实例的存档：共享 saves/ + 每个隔离实例 versions/&lt;id&gt;/saves/，按时间倒序。</summary>
    internal static async Task<IReadOnlyList<InstanceWorldInfo>> LoadAllWorldsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<InstanceWorldInfo>();

        var shared = await LoadWorldsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var world in shared)
        {
            result.Add(new InstanceWorldInfo(world, null));
        }

        try
        {
            var instances = await LoadInstancesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var instance in instances)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var savesRoot = Path.Combine(instance.Directory, "saves");
                    if (!Directory.Exists(savesRoot)) continue;

                    foreach (var directory in Directory.GetDirectories(savesRoot))
                    {
                        try
                        {
                            var levelDat = Path.Combine(directory, "level.dat");
                            var lastWrite = File.Exists(levelDat)
                                ? File.GetLastWriteTime(levelDat)
                                : Directory.GetLastWriteTime(directory);

                            result.Add(new InstanceWorldInfo(
                                new WorldInfo(
                                    Path.GetFileName(directory),
                                    lastWrite,
                                    DirectorySize(directory),
                                    directory),
                                instance.Id));
                        }
                        catch
                        {
                            // 单个存档不可读时跳过
                        }
                    }
                }
                catch
                {
                    // 单个实例目录不可读时跳过
                }
            }
        }
        catch
        {
            // 实例扫描失败时至少保留共享目录的存档
        }

        result.Sort((left, right) => right.World.LastWrite.CompareTo(left.World.LastWrite));

        // 同一目录可能被共享扫描与实例扫描各收一次（如未开隔离时）：去重，且优先保留带实例归属的一条
        var byDirectory = new Dictionary<string, InstanceWorldInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in result)
        {
            if (byDirectory.TryGetValue(item.World.Directory, out var existing)
                && !(existing.InstanceId is null && item.InstanceId is not null))
            {
                continue;
            }

            byDirectory[item.World.Directory] = item;
        }

        return [.. byDirectory.Values.OrderByDescending(item => item.World.LastWrite)];
    }

    internal static string FormatSize(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0.0} MB";
        if (bytes >= 1024) return $"{bytes / 1024d:0} KB";
        return $"{bytes} B";
    }

    /// <summary>递归求目录体积；读不到的文件按 0 计。</summary>
    private static long DirectorySize(string directory)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch
                {
                    // 单个文件读不到时忽略
                }
            }
        }
        catch
        {
            // 目录枚举失败时按 0 处理
        }

        return total;
    }

    private static string DetectLoader(string? inheritsFrom, string id)
    {
        var probe = (inheritsFrom ?? string.Empty) + " " + id;
        if (probe.Contains("fabric", StringComparison.OrdinalIgnoreCase)) return "Fabric";
        if (probe.Contains("quilt", StringComparison.OrdinalIgnoreCase)) return "Quilt";
        if (probe.Contains("neoforge", StringComparison.OrdinalIgnoreCase)) return "NeoForge";
        if (probe.Contains("forge", StringComparison.OrdinalIgnoreCase)) return "Forge";
        if (probe.Contains("liteloader", StringComparison.OrdinalIgnoreCase)) return "LiteLoader";
        return "Vanilla";
    }

    private static string InstanceIconFor(string loader, string version, string type)
    {
        var file = loader switch
        {
            "Fabric" => "fabric.png",
            "Quilt" => "fabric.png",
            "Forge" => "forge.png",
            "NeoForge" => "neoforge.png",
            "LiteLoader" => "liteloader.png",
            _ => type == "snapshot" ? "snapshot_version.png"
                : version.StartsWith("1.7", StringComparison.Ordinal) ? "old_version.png"
                : "vanilla.png",
        };

        return "avares://Mofumofu.Launcher/Assets/instance-icons/" + file;
    }

    /// <summary>把扫描结果写进临时日志，便于 QA 报告贴数据来源证据。</summary>
    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "mofu-widget-backend.log"),
                $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响小组件
        }
    }
}
