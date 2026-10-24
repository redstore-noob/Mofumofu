using System.IO.Compression;
using System.Text.Json;
using MofuCore.Launch;

namespace MofuCore.Download.Minecraft;

/// <summary>
/// 处理所有与Minecraft相关的库，启动时调用补全一次，下载时调用补全一次。
/// </summary>
public class LibraryResolver
{
    /// <summary>按 rules 过滤版本库列表，返回 classpath 与 native 依赖；缺失文件时报错。</summary>
    public Task<ResolvedLibraries> ResolveAsync(
        LaunchProfile profile,
        string minecraftDirectory,
        IReadOnlyDictionary<string, bool> features,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string librariesDirectory = Path.Combine(minecraftDirectory, "libraries");
        List<string> classpath = new(profile.Libraries.Count + 1);
        List<NativeLibrary> natives = [];
        List<string> missingFiles = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement library in profile.Libraries)
        {
            if (!MofuCore.Launch.MinecraftRuleEvaluator.IsAllowed(library, features))
            {
                continue;
            }

            if (TryGetArtifactPath(library, out string artifactRelativePath))
            {
                string artifactPath = ToAbsoluteLibraryPath(librariesDirectory, artifactRelativePath);
                if (File.Exists(artifactPath))
                {
                    if (seen.Add(artifactPath))
                    {
                        classpath.Add(artifactPath);
                    }
                }
                else
                {
                    missingFiles.Add(artifactPath);
                }
            }

            if (TryGetNativePath(library, out string nativeRelativePath))
            {
                string nativePath = ToAbsoluteLibraryPath(librariesDirectory, nativeRelativePath);
                if (File.Exists(nativePath))
                {
                    natives.Add(new NativeLibrary
                    {
                        ArchivePath = nativePath,
                        Exclusions = NativeExclusions(library),
                    });
                }
                else
                {
                    missingFiles.Add(nativePath);
                }
            }
        }

        string clientJar = Path.Combine(minecraftDirectory, "versions",
            profile.ClientJarVersionId, profile.ClientJarVersionId + ".jar");
        if (File.Exists(clientJar))
        {
            classpath.Add(clientJar);
        }
        else
        {
            missingFiles.Add(clientJar);
        }

        if (missingFiles.Count > 0)
        {
            int previewCount = Math.Min(missingFiles.Count, 5);
            string preview = string.Join("\n", missingFiles.Take(previewCount));
            string suffix = missingFiles.Count > 5 ? $"\n……另有 {missingFiles.Count - 5} 个文件" : string.Empty;
            throw new MinecraftLaunchException($"版本文件不完整，缺少以下依赖：\n{preview}{suffix}");
        }

        return Task.FromResult(new ResolvedLibraries { Classpath = classpath, Natives = natives });
    }

    /// <summary>下载时补全依赖库：给出每条库的下载地址与目标相对路径。</summary>
    public IReadOnlyList<LibraryDownloadPlan> ResolveDownloads(
        IEnumerable<MinecraftLibrary> libraries,
        IReadOnlyDictionary<string, bool> features)
    {
        List<LibraryDownloadPlan> plans = [];
        if (libraries is null) return plans;

        foreach (MinecraftLibrary library in libraries)
        {
            if (!MinecraftRuleEvaluator.IsAllowed(library.Rules, features)) continue;

            bool planned = false;
            if (library.Downloads is not null)
            {
                if (library.Downloads.Artifact is { } artifact && !string.IsNullOrWhiteSpace(artifact.Path))
                {
                    plans.Add(new LibraryDownloadPlan(artifact.Path, artifact.Url, artifact.Sha1, artifact.Size,
                        false, "依赖库"));
                }

                if (library.Downloads.Classifiers is not null && library.Natives is not null &&
                    library.Natives.TryGetValue(MinecraftRuleEvaluator.OsName, out string? rawClassifier) &&
                    library.Downloads.Classifiers.TryGetValue(ExpandArchPlaceholder(rawClassifier), out MinecraftDownloadInfo? native) &&
                    !string.IsNullOrWhiteSpace(native.Path))
                {
                    plans.Add(new LibraryDownloadPlan(native.Path, native.Url, native.Sha1, native.Size,
                        true, "原生依赖库"));
                }

                planned = true;
            }

            if (planned) continue;

            if (library.Natives is not null &&
                library.Natives.TryGetValue(MinecraftRuleEvaluator.OsName, out string? classifier))
            {
                string nativeRelative = CreateNativeMavenPath(library.Name, ExpandArchPlaceholder(classifier));
                if (nativeRelative.Length > 0)
                {
                    string baseUrl = string.IsNullOrWhiteSpace(library.Url)
                        ? "https://libraries.minecraft.net/"
                        : library.Url.TrimEnd('/') + "/";
                    plans.Add(new LibraryDownloadPlan(nativeRelative,
                        baseUrl + nativeRelative.Replace('\\', '/'), null, 0, true, "原生依赖库"));
                }

                continue;
            }

            string relative = CreateMavenPath(library.Name);
            if (relative.Length == 0) continue;

            string repository = string.IsNullOrWhiteSpace(library.Url)
                ? InferMavenRepository(library.Name)
                : library.Url;
            plans.Add(new LibraryDownloadPlan(relative,
                repository.TrimEnd('/') + "/" + relative.Replace('\\', '/'), null, 0, false, "依赖库"));
        }

        return plans;
    }

    /// <summary>解压 native 依赖到 Minecraft 目录下的 .mofu-natives，返回解压目录。</summary>
    public Task<string> ExtractNativesAsync(
        string versionId,
        IReadOnlyList<NativeLibrary> nativeLibraries,
        string minecraftDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string safeVersionId = new(versionId.Select(character =>
            character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || character < 0x20
                ? '_'
                : character).ToArray());

        string baseDirectory = string.IsNullOrWhiteSpace(minecraftDirectory)
            ? Path.Combine(Path.GetTempPath(), "MofuCore", "natives")
            : Path.Combine(minecraftDirectory, ".mofu-natives");
        CleanupStaleNativeDirectories(baseDirectory);

        string nativeDirectory = Path.Combine(baseDirectory,
            $"{safeVersionId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(nativeDirectory);

        try
        {
            foreach (NativeLibrary nativeLibrary in nativeLibraries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ExtractNativeArchive(nativeLibrary, nativeDirectory);
            }
        }
        catch
        {
            TryDeleteDirectory(nativeDirectory);
            throw;
        }

        return Task.FromResult(nativeDirectory);
    }

    /// <summary>尽力删除目录；游戏退出后的临时文件清理不应影响主流程。</summary>
    public static void TryDeleteDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch (IOException)
        {
            // 占用中/权限不足时忽略
        }
        catch (UnauthorizedAccessException)
        {
            // 占用中/权限不足时忽略
        }
    }

    private static void CleanupStaleNativeDirectories(string baseDirectory)
    {
        try
        {
            if (!Directory.Exists(baseDirectory)) return;

            foreach (string directory in Directory.EnumerateDirectories(baseDirectory))
            {
                try
                {
                    Directory.Delete(directory, true);
                }
                catch (IOException)
                {
                    // 上一轮游戏的进程还没退干净时保留目录
                }
                catch (UnauthorizedAccessException)
                {
                    // 忽略
                }
            }
        }
        catch (IOException)
        {
            // 忽略
        }
        catch (UnauthorizedAccessException)
        {
            // 忽略
        }
    }

    private static void ExtractNativeArchive(NativeLibrary nativeLibrary, string destinationRoot)
    {
        using ZipArchive archive = ZipFile.OpenRead(nativeLibrary.ArchivePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                continue;
            }

            string normalizedName = entry.FullName.Replace('\\', '/');
            if (HasExclusionPrefix(normalizedName, nativeLibrary.Exclusions))
            {
                continue;
            }

            if (normalizedName.Contains("META-INF/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string destinationPath = Path.Combine(destinationRoot, normalizedName.Replace('/', Path.DirectorySeparatorChar));
            EnsureContainedPath(destinationRoot, destinationPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, true);
        }
    }

    private static bool HasExclusionPrefix(string name, IReadOnlyList<string> exclusions)
    {
        foreach (string exclusion in exclusions)
        {
            if (name.StartsWith(exclusion, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void EnsureContainedPath(string root, string candidate)
    {
        string normalizedRoot = Path.GetFullPath(root);
        string normalizedCandidate = Path.GetFullPath(candidate);
        if (!normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new MinecraftLaunchException("native 归档包含越界路径：" + candidate);
        }
    }

    private static bool TryGetArtifactPath(JsonElement library, out string relativePath)
    {
        relativePath = string.Empty;
        if (!library.TryGetProperty("downloads", out JsonElement downloads) ||
            downloads.ValueKind != JsonValueKind.Object ||
            !downloads.TryGetProperty("artifact", out JsonElement artifact) ||
            artifact.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (VersionLoader.TryGetString(artifact, "path") is { Length: > 0 } artifactPath)
        {
            relativePath = artifactPath;
            return true;
        }

        return false;
    }

    private static bool TryGetNativePath(JsonElement library, out string relativePath)
    {
        relativePath = string.Empty;
        string? name = VersionLoader.TryGetString(library, "name");
        if (string.IsNullOrWhiteSpace(name)) return false;

        if (!library.TryGetProperty("natives", out JsonElement natives) ||
            natives.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string osName = MofuCore.Launch.MinecraftRuleEvaluator.OperatingSystemName;
        if (!natives.TryGetProperty(osName, out JsonElement classifierElement) ||
            classifierElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string classifier = ExpandArchPlaceholder(classifierElement.GetString() ?? string.Empty);
        if (classifier.Length == 0) return false;

        if (library.TryGetProperty("downloads", out JsonElement downloads) &&
            downloads.ValueKind == JsonValueKind.Object &&
            downloads.TryGetProperty("classifiers", out JsonElement classifiers) &&
            classifiers.ValueKind == JsonValueKind.Object &&
            classifiers.TryGetProperty(classifier, out JsonElement nativeArtifact) &&
            nativeArtifact.ValueKind == JsonValueKind.Object &&
            VersionLoader.TryGetString(nativeArtifact, "path") is { Length: > 0 } nativePath)
        {
            relativePath = nativePath;
            return true;
        }

        string legacy = CreateNativeMavenPath(name, classifier);
        if (legacy.Length == 0) return false;
        relativePath = legacy;
        return true;
    }

    private static IReadOnlyList<string> NativeExclusions(JsonElement library)
    {
        if (!library.TryGetProperty("extract", out JsonElement extract) ||
            extract.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        List<string> exclusions = [];
        foreach (JsonElement exclusion in VersionLoader.EnumerateArray(extract, "exclude"))
        {
            if (exclusion.ValueKind == JsonValueKind.String &&
                exclusion.GetString() is { Length: > 0 } value)
            {
                exclusions.Add(value.Replace('\\', '/'));
            }
        }

        return exclusions;
    }

    /// <summary>Maven 坐标 → libraries 下的相对路径（支持 @ext 后缀）。</summary>
    public static string CreateMavenPath(string coordinate)
    {
        if (string.IsNullOrWhiteSpace(coordinate)) return string.Empty;

        string extension = "jar";
        string name = coordinate;
        int at = coordinate.IndexOf('@');
        if (at >= 0)
        {
            extension = coordinate[(at + 1)..];
            name = coordinate[..at];
        }

        string[] parts = name.Split(':');
        if (parts.Length is < 3 or > 4) return string.Empty;
        foreach (string part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) return string.Empty;
        }

        string groupPath = parts[0].Replace('.', Path.DirectorySeparatorChar);
        string classifier = parts.Length == 4 ? "-" + parts[3] : string.Empty;
        return Path.Combine(groupPath, parts[1], parts[2], $"{parts[1]}-{parts[2]}{classifier}.{extension}");
    }

    /// <summary>旧版本 natives 的 Maven 路径回退（name + classifier）。</summary>
    public static string CreateNativeMavenPath(string coordinate, string classifier)
    {
        if (string.IsNullOrWhiteSpace(coordinate) || string.IsNullOrWhiteSpace(classifier)) return string.Empty;
        string[] parts = coordinate.Split(':');
        if (parts.Length is < 3 or > 4) return string.Empty;
        foreach (string part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) return string.Empty;
        }

        return Path.Combine(parts[0].Replace('.', Path.DirectorySeparatorChar), parts[1], parts[2],
            $"{parts[1]}-{parts[2]}-{classifier}.jar");
    }

    private static string ExpandArchPlaceholder(string classifier)
    {
        string arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture is
            System.Runtime.InteropServices.Architecture.X64 or System.Runtime.InteropServices.Architecture.Arm64
            ? "64"
            : "32";
        return classifier.Replace("${arch}", arch, StringComparison.Ordinal);
    }

    private static string InferMavenRepository(string coordinate)
    {
        if (string.IsNullOrWhiteSpace(coordinate)) return "https://libraries.minecraft.net/";
        string lower = coordinate.ToLowerInvariant();
        if (lower.StartsWith("net.neoforged", StringComparison.Ordinal)) return "https://maven.neoforged.net/releases/";
        if (lower.StartsWith("net.minecraft", StringComparison.Ordinal) && lower.EndsWith(":srg", StringComparison.Ordinal))
        {
            return "https://maven.neoforged.net/releases/";
        }
        if (lower.StartsWith("net.minecraftforge", StringComparison.Ordinal)) return "https://maven.minecraftforge.net/";
        if (lower.StartsWith("cpw.mods", StringComparison.Ordinal)) return "https://maven.minecraftforge.net/";
        if (lower.StartsWith("net.fabricmc", StringComparison.Ordinal)) return "https://maven.fabricmc.net/";
        if (lower.StartsWith("org.quiltmc", StringComparison.Ordinal)) return "https://maven.quiltmc.org/";
        if (lower.StartsWith("io.github.llamalad7", StringComparison.Ordinal)) return "https://maven.neoforged.net/releases/";
        return "https://libraries.minecraft.net/";
    }

    private static string ToAbsoluteLibraryPath(string libraryRoot, string relativePath)
    {
        string full = Path.GetFullPath(Path.Combine(libraryRoot, relativePath));
        return full;
    }
}

/// <summary>一个待解压的 native 依赖库。</summary>
public sealed class NativeLibrary
{
    public string ArchivePath { get; set; } = string.Empty;
    public IReadOnlyList<string> Exclusions { get; set; } = [];
}

/// <summary>依赖库解析结果：classpath 条目 + 待解压 natives。</summary>
public sealed class ResolvedLibraries
{
    public List<string> Classpath { get; init; } = [];
    public List<NativeLibrary> Natives { get; init; } = [];
}

/// <summary>下载用的依赖库条目。</summary>
public sealed record LibraryDownloadPlan(
    string RelativePath,
    string Url,
    string? Sha1,
    long Size,
    bool Native,
    string DisplayName);
