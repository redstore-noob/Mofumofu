using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Mofumofu.Launcher.Services;

/// <summary>生成安装器可直接读取的完整 Windows 载荷，不包含账户和启动器用户配置。</summary>
internal static class SoloPackageBuilder
{
    public static (int Files, long Size) Build(string outputPath, string launcherPath, string gameDirectory,
        string versionId, string manifestJson, byte[] icon, string? javaHome,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        outputPath = Path.GetFullPath(outputPath);
        launcherPath = Path.GetFullPath(launcherPath);
        if (!File.Exists(launcherPath) || !launcherPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("NekoSolo 需要从 Windows 启动器导出。");

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string source, string entry)
        {
            if (string.Equals(Path.GetFullPath(source), outputPath, StringComparison.OrdinalIgnoreCase)) return;
            files.TryAdd(entry.Replace('\\', '/'), source);
        }
        void AddTree(string directory, string prefix)
        {
            if (!Directory.Exists(directory)) return;
            foreach (string file in Directory.EnumerateFiles(directory, "*", new EnumerationOptions
                     { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
                Add(file, prefix + Path.GetRelativePath(directory, file));
        }

        Add(launcherPath, "files/Mofumofu.Launcher.exe");
        string appDirectory = Path.GetDirectoryName(launcherPath)!;
        // 单文件发布只需 exe；常规发布还需程序集、运行时及资源程序集。
        foreach (string file in Directory.EnumerateFiles(appDirectory))
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                name is "Mofumofu.Launcher.deps.json" or "Mofumofu.Launcher.runtimeconfig.json")
                Add(file, "files/" + name);
        }
        AddTree(Path.Combine(appDirectory, "runtimes"), "files/runtimes/");
        foreach (string directory in Directory.EnumerateDirectories(appDirectory))
        {
            // 只收集卫星资源程序集，避免把启动器配置、账户和其他用户数据带入包中。
            foreach (string resource in Directory.EnumerateFiles(directory, "*.resources.dll"))
                Add(resource, "files/" + Path.GetRelativePath(appDirectory, resource));
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddVersion(string id, bool instance)
        {
            if (string.IsNullOrWhiteSpace(id) || id is "." or ".." || id.IndexOfAny(['/', '\\', ':']) >= 0)
                throw new InvalidOperationException("无效的游戏版本目录：" + id);
            if (!visited.Add(id)) return;
            string directory = Path.Combine(gameDirectory, "versions", id);
            string jsonPath = Path.Combine(directory, id + ".json");
            if (!File.Exists(jsonPath)) throw new FileNotFoundException("缺少游戏版本元数据", jsonPath);
            using JsonDocument metadata = JsonDocument.Parse(File.ReadAllText(jsonPath));
            if (instance)
                AddTree(directory, "minecraft/versions/" + id + "/");
            else
            {
                Add(jsonPath, "minecraft/versions/" + id + "/" + id + ".json");
                string jar = Path.Combine(directory, id + ".jar");
                if (File.Exists(jar)) Add(jar, "minecraft/versions/" + id + "/" + id + ".jar");
            }
            foreach (string key in new[] { "inheritsFrom", "jar" })
                if (metadata.RootElement.TryGetProperty(key, out JsonElement parent) && parent.GetString() is { Length: > 0 } parentId && parentId != id)
                    AddVersion(parentId, false);
        }
        AddVersion(versionId, true);
        AddTree(Path.Combine(gameDirectory, "libraries"), "minecraft/libraries/");
        AddTree(Path.Combine(gameDirectory, "assets"), "minecraft/assets/");
        // 非隔离实例的共享内容也应保留在游戏根目录。
        foreach (string folder in new[] { "mods", "config", "defaultconfigs", "resourcepacks", "shaderpacks", "saves", "kubejs", "scripts" })
            AddTree(Path.Combine(gameDirectory, folder), "minecraft/" + folder + "/");
        foreach (string name in new[] { "options.txt", "servers.dat" })
        {
            string file = Path.Combine(gameDirectory, name);
            if (File.Exists(file)) Add(file, "minecraft/" + name);
        }
        if (javaHome is not null)
        {
            if (!File.Exists(Path.Combine(javaHome, "bin", "java.exe")))
                throw new InvalidOperationException("所选 Java 不是完整的 Windows Java 运行时。");
            AddTree(javaHome, "jre/");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        string temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            int done = 0;
            int total = files.Count + 2;
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open()))
                    writer.Write(manifestJson);
                using (Stream destination = archive.CreateEntry("icon.png").Open()) destination.Write(icon);
                done = 2;
                foreach ((string entry, string source) in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    archive.CreateEntryFromFile(source, entry, CompressionLevel.Optimal);
                    done++;
                    progress?.Report((done, total));
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, outputPath, overwrite: true);
            return (done, new FileInfo(outputPath).Length);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
