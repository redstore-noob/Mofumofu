using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MofuCore.Launch;

namespace Mofumofu.Launcher.Services;

/// <summary>底栏实例选择器需要的轻量实例信息，不包含实例页的完整详情。</summary>
public sealed record InstanceSnapshot(
    string Id,
    string Name,
    string Version,
    string Loader,
    DateTime LastPlayed,
    int ModCount,
    Bitmap? Icon);

/// <summary>扫描游戏目录下的实例，供底栏芯片和弹出面板共用。</summary>
public static class InstanceCatalog
{
    private static readonly Dictionary<string, Bitmap?> IconCache = new(StringComparer.Ordinal);

    public static IReadOnlyList<InstanceSnapshot> Scan()
    {
        var root = ResolveGameDirectory();
        var versionsRoot = Path.Combine(root, "versions");
        var result = new List<InstanceSnapshot>();
        if (!Directory.Exists(versionsRoot)) return result;

        string[] directories;
        try
        {
            directories = Directory.GetDirectories(versionsRoot);
        }
        catch (Exception)
        {
            return result;
        }

        var versionLoader = new VersionLoader();
        foreach (var directory in directories)
        {
            var id = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (id.StartsWith(".mofu-update-", StringComparison.OrdinalIgnoreCase)) continue;

            var jsonPath = Path.Combine(directory, id + ".json");
            if (!File.Exists(jsonPath)) continue;

            DateTime lastPlayed;
            try
            {
                lastPlayed = File.GetLastWriteTime(jsonPath);
            }
            catch (Exception)
            {
                lastPlayed = DateTime.MinValue;
            }

            var version = id;
            var loader = "Vanilla";
            try
            {
                var profile = versionLoader.TryLoad(root, id);
                if (profile is not null)
                {
                    var source = string.IsNullOrWhiteSpace(profile.SourceId) ? id : profile.SourceId;
                    version = string.IsNullOrWhiteSpace(profile.InheritsFrom) ? source : profile.InheritsFrom;
                    loader = DetectLoader(profile);
                }
            }
            catch (Exception)
            {
                // 继承链或 JSON 损坏时仍保留实例，只是不显示加载器细节。
            }

            result.Add(new InstanceSnapshot(
                id,
                id,
                version,
                loader,
                lastPlayed,
                CountFiles(ContentDirectoryFor(root, id, "mods")),
                LoadIcon(loader)));
        }

        result.Sort((left, right) =>
        {
            var byTime = right.LastPlayed.CompareTo(left.LastPlayed);
            return byTime != 0 ? byTime : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        });

        return result;
    }

    public static string ResolveGameDirectory()
    {
        try
        {
            var configured = MofuCore.Config.YamlRead.GameDirectory;
            if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        }
        catch (Exception)
        {
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".minecraft"));
    }

    private static string ContentDirectoryFor(string root, string id, string name)
    {
        var instanceRoot = MofuCore.Config.YamlRead.DefaultVersionIsolation
            ? Path.Combine(root, "versions", id)
            : root;
        var isolated = Path.Combine(instanceRoot, name);
        return Directory.Exists(isolated) ? isolated : Path.Combine(root, name);
    }

    private static int CountFiles(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly).Length
                : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string DetectLoader(LaunchProfile profile)
    {
        var probe = profile.MainClass + " " + string.Join(' ',
            profile.Libraries.Select(library => library.GetRawText()));
        if (probe.Contains("org.quiltmc.loader", StringComparison.OrdinalIgnoreCase) ||
            probe.Contains("quilt-loader", StringComparison.OrdinalIgnoreCase)) return "Quilt";
        if (probe.Contains("net.fabricmc.loader", StringComparison.OrdinalIgnoreCase) ||
            probe.Contains("fabric-loader", StringComparison.OrdinalIgnoreCase)) return "Fabric";
        if (probe.Contains("net.neoforged", StringComparison.OrdinalIgnoreCase) ||
            probe.Contains("neoforge-", StringComparison.OrdinalIgnoreCase)) return "NeoForge";
        if (probe.Contains("optifine", StringComparison.OrdinalIgnoreCase)) return "OptiFine";
        if (probe.Contains("net.minecraftforge", StringComparison.OrdinalIgnoreCase) ||
            probe.Contains("minecraftforge", StringComparison.OrdinalIgnoreCase)) return "Forge";
        if (probe.Contains("liteloader", StringComparison.OrdinalIgnoreCase)) return "LiteLoader";
        return "Vanilla";
    }

    private static Bitmap? LoadIcon(string loader)
    {
        var file = loader switch
        {
            "Fabric" or "Quilt" => "fabric.png",
            "Forge" => "forge.png",
            "NeoForge" => "neoforge.png",
            "OptiFine" => "command_block.png",
            "LiteLoader" => "liteloader.png",
            _ => "vanilla.png",
        };

        lock (IconCache)
        {
            if (IconCache.TryGetValue(file, out var cached)) return cached;
        }

        Bitmap? bitmap = null;
        try
        {
            using var stream = AssetLoader.Open(new Uri(
                $"avares://Mofumofu.Launcher/Assets/instance-icons/{file}"));
            bitmap = new Bitmap(stream);
        }
        catch (Exception)
        {
        }

        lock (IconCache)
        {
            IconCache[file] = bitmap;
        }

        return bitmap;
    }
}
