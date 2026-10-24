using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MofuCore.Download.Minecraft;

namespace MofuCore.Download.Modrinth;

/// <summary>可打包的实例内容单元；RelativePath 相对实例内容目录、使用正斜杠。</summary>
public sealed record ModpackContentItem(string Category, string RelativePath, string Name, long SizeBytes, bool IsDirectory)
{
    public string CategoryDisplay => Category switch
    {
        ModrinthBundleMake.CategoryMods => "模组",
        ModrinthBundleMake.CategoryConfig => "配置",
        ModrinthBundleMake.CategoryRoot => "根文件",
        ModrinthBundleMake.CategoryResourcePacks => "资源包",
        ModrinthBundleMake.CategoryShaderPacks => "光影包",
        ModrinthBundleMake.CategorySaves => "存档",
        _ => Category,
    };
}

/// <summary>整合包导出进度。</summary>
public sealed record ModpackExportProgress(string Phase, int Current, int Total);

/// <summary>导出参数。</summary>
public sealed class ModpackExportOptions
{
    public string PackName { get; set; } = string.Empty;
    public string PackVersion { get; set; } = "1.0.0";
    public string Author { get; set; } = string.Empty;
    public string UpdateLink { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string MinecraftVersion { get; set; } = string.Empty;
    public string LoaderName { get; set; } = string.Empty;
    public string LoaderVersion { get; set; } = string.Empty;
    public string IconPngPath { get; set; } = string.Empty;
    public List<string> IncludedPaths { get; set; } = [];
    public bool ResolveModrinthLinks { get; set; } = true;
    public bool OnlyModrinthContent { get; set; }
}

/// <summary>导出结果统计。</summary>
public sealed class ModpackExportResult
{
    public string OutputPath { get; set; } = string.Empty;
    public int DeclaredFiles { get; set; }
    public int OverrideFiles { get; set; }
    public List<string> Warnings { get; } = [];
}

/// <summary>mrpack 的 modrinth.index.json。</summary>
public sealed class ModrinthIndex
{
    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; } = 1;
    [JsonPropertyName("game")] public string Game { get; set; } = "minecraft";
    [JsonPropertyName("versionId")] public string VersionId { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("summary")] public string? Summary { get; set; }
    [JsonPropertyName("files")] public List<ModrinthIndexFile> Files { get; set; } = [];
    [JsonPropertyName("dependencies")] public Dictionary<string, string> Dependencies { get; set; } = [];
}

/// <summary>mrpack index 中的单个文件声明。</summary>
public sealed class ModrinthIndexFile
{
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("hashes")] public Dictionary<string, string> Hashes { get; set; } = [];
    [JsonPropertyName("env")] public ModrinthIndexEnvironment? Env { get; set; }
    [JsonPropertyName("downloads")] public List<string> Downloads { get; set; } = [];
    [JsonPropertyName("fileSize")] public long FileSize { get; set; }
}

/// <summary>index 文件的客户端/服务端适用性。</summary>
public sealed class ModrinthIndexEnvironment
{
    [JsonPropertyName("client")] public string Client { get; set; } = "required";
    [JsonPropertyName("server")] public string Server { get; set; } = "required";
}

/// <summary>导入（读取 + 解压）结果。</summary>
public sealed class ModrinthBundleImportResult
{
    public ModrinthIndex? Index { get; set; }
    public List<string> ExtractedFiles { get; } = [];
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// Modrinth 整合包（.mrpack）生成与读取：overrides/ 承载实例文件，mods 等可回收内容
/// 按 SHA-1 匹配 Modrinth version_file 后改为 index 直链声明。
/// </summary>
public sealed class ModrinthBundleMake
{
    public const string CategoryMods = "mods";
    public const string CategoryConfig = "config";
    public const string CategoryRoot = "root";
    public const string CategoryResourcePacks = "resourcepacks";
    public const string CategoryShaderPacks = "shaderpacks";
    public const string CategorySaves = "saves";

    private const string IndexEntryName = "modrinth.index.json";
    private const string OverridesRoot = "overrides";

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>收集实例内容目录中可打包的内容单元。</summary>
    public static IReadOnlyList<ModpackContentItem> CollectContent(string contentDirectory)
    {
        List<ModpackContentItem> items = [];
        if (string.IsNullOrWhiteSpace(contentDirectory) || !Directory.Exists(contentDirectory)) return items;

        HashSet<string> added = new(StringComparer.OrdinalIgnoreCase);
        void Add(string category, string relativePath, string name, bool isDirectory)
        {
            string normalized = relativePath.Replace('\\', '/');
            if (!added.Add(normalized)) return;
            string fullPath = Path.Combine(contentDirectory, normalized.Replace('/', Path.DirectorySeparatorChar));
            long size = 0;
            if (Directory.Exists(fullPath)) size = DirectorySize(fullPath);
            else if (File.Exists(fullPath)) size = new FileInfo(fullPath).Length;
            items.Add(new ModpackContentItem(category, normalized, name, size, isDirectory));
        }

        string modsDirectory = Path.Combine(contentDirectory, "mods");
        if (Directory.Exists(modsDirectory))
        {
            foreach (string file in EnumerateSafe(modsDirectory))
            {
                string name = Path.GetFileName(file);
                string lower = name.ToLowerInvariant();
                if (!lower.EndsWith(".jar", StringComparison.Ordinal) && !lower.EndsWith(".jar.disabled", StringComparison.Ordinal)) continue;
                Add(CategoryMods, Path.GetRelativePath(contentDirectory, file), name, false);
            }
        }

        if (File.Exists(Path.Combine(contentDirectory, "options.txt")))
        {
            Add(CategoryRoot, "options.txt", "options.txt", false);
        }

        if (Directory.Exists(Path.Combine(contentDirectory, "config")))
        {
            Add(CategoryConfig, "config", "config/", true);
        }

        foreach ((string category, string folder) in new[] { (CategoryResourcePacks, "resourcepacks"), (CategoryShaderPacks, "shaderpacks") })
        {
            string directory = Path.Combine(contentDirectory, folder);
            if (!Directory.Exists(directory)) continue;
            foreach (string path in EnumerateTopLevel(directory))
            {
                bool isDirectory = Directory.Exists(path);
                string name = Path.GetFileName(path) + (isDirectory ? "/" : string.Empty);
                Add(category, Path.GetRelativePath(contentDirectory, path), name, isDirectory);
            }
        }

        string savesDirectory = Path.Combine(contentDirectory, "saves");
        if (Directory.Exists(savesDirectory))
        {
            foreach (string directory in Directory.EnumerateDirectories(savesDirectory))
            {
                string name = Path.GetFileName(directory);
                if (name.StartsWith('.')) continue;
                Add(CategorySaves, Path.GetRelativePath(contentDirectory, directory), name + "/", true);
            }
        }

        items.Sort((left, right) =>
        {
            int category = string.CompareOrdinal(left.Category, right.Category);
            return category != 0 ? category : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        });
        return items;
    }

    /// <summary>导出 .mrpack；单个文件读取失败时跳过并记入 Warnings。</summary>
    public static async Task<ModpackExportResult> ExportAsync(
        ModpackExportOptions options,
        string contentDirectory,
        string outputPath,
        IProgress<ModpackExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ModpackExportResult result = new();
        outputPath = SanitizeSavePath(outputPath);
        if (string.IsNullOrWhiteSpace(contentDirectory)) throw new InvalidOperationException("参数无效。");
        if (string.IsNullOrWhiteSpace(options.PackName)) throw new InvalidOperationException("整合包名称不能为空。");
        if (string.IsNullOrWhiteSpace(options.MinecraftVersion)) throw new InvalidOperationException("无法确定 Minecraft 版本。");
        if (!Directory.Exists(contentDirectory)) throw new InvalidOperationException($"实例内容目录不存在：{contentDirectory}");
        if (string.IsNullOrWhiteSpace(outputPath)) throw new InvalidOperationException("输出路径无效。");

        string packVersion = string.IsNullOrWhiteSpace(options.PackVersion) ? "1.0.0" : options.PackVersion;
        HashSet<string> included = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in options.IncludedPaths) included.Add(path.Replace('\\', '/'));

        List<ModpackContentItem> selected = [];
        foreach (ModpackContentItem item in CollectContent(contentDirectory))
        {
            if (included.Contains(item.RelativePath)) selected.Add(item);
        }
        if (selected.Count == 0) throw new InvalidOperationException("没有勾选任何要打包的内容。");

        List<PayloadEntry> payload = [];
        foreach (ModpackContentItem item in selected)
        {
            string source = Path.Combine(contentDirectory, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (item.IsDirectory)
            {
                foreach (string file in EnumerateSafe(source))
                {
                    payload.Add(new PayloadEntry(Path.GetRelativePath(contentDirectory, file).Replace('\\', '/'), file));
                }
            }
            else
            {
                payload.Add(new PayloadEntry(item.RelativePath, source));
            }
        }

        Dictionary<string, ModrinthIndexFile> declaredByPath = new(StringComparer.OrdinalIgnoreCase);
        if (options.ResolveModrinthLinks || options.OnlyModrinthContent)
        {
            HashSet<string> wantedCategories = [CategoryMods];
            if (options.OnlyModrinthContent)
            {
                wantedCategories.Add(CategoryResourcePacks);
                wantedCategories.Add(CategoryShaderPacks);
            }

            List<string> candidates = [];
            foreach (ModpackContentItem item in selected)
            {
                if (item.IsDirectory || !wantedCategories.Contains(item.Category)) continue;
                candidates.Add(item.RelativePath);
            }

            for (int index = 0; index < candidates.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string contentPath = candidates[index];
                progress?.Report(new ModpackExportProgress("正在解析 Modrinth 直链", index, candidates.Count));
                string fullPath = Path.Combine(contentDirectory, contentPath.Replace('/', Path.DirectorySeparatorChar));
                string sha1;
                try
                {
                    sha1 = await MinecraftFileHash.ComputeSha1Async(fullPath, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    result.Warnings.Add($"计算哈希失败（{contentPath}）：{e.Message}");
                    continue;
                }

                ModrinthVersionFile? match;
                try
                {
                    match = await ResolveModrinthFileAsync(sha1, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    result.Warnings.Add($"查询 Modrinth 失败（{contentPath}）：{e.Message}");
                    continue;
                }

                if (match is null) continue;
                Dictionary<string, string> hashes = new() { ["sha1"] = string.IsNullOrWhiteSpace(match.Sha1) ? sha1 : match.Sha1 };
                if (!string.IsNullOrWhiteSpace(match.Sha512)) hashes["sha512"] = match.Sha512;
                declaredByPath[contentPath] = new ModrinthIndexFile
                {
                    Path = contentPath,
                    Hashes = hashes,
                    Env = new ModrinthIndexEnvironment(),
                    Downloads = [match.Url],
                    FileSize = match.Size,
                };
            }

            if (declaredByPath.Count < candidates.Count)
            {
                int unmatched = candidates.Count - declaredByPath.Count;
                result.Warnings.Add(options.OnlyModrinthContent
                    ? $"{unmatched} 个内容文件未在 Modrinth 收录，不会被打进安装包。"
                    : $"{unmatched} 个 mod 未匹配到 Modrinth 直链，将直接打包进 overrides。");
            }
        }

        if (options.OnlyModrinthContent)
        {
            List<PayloadEntry> kept = [];
            List<string> excluded = [];
            foreach (PayloadEntry entry in payload)
            {
                if (IsModrinthContentPath(entry.ArchivePath) && !declaredByPath.ContainsKey(entry.ArchivePath))
                {
                    excluded.Add(entry.ArchivePath);
                    continue;
                }
                kept.Add(entry);
            }
            payload = kept;
            if (excluded.Count > 0)
            {
                IEnumerable<string> shown = excluded.Take(8);
                string suffix = excluded.Count > 8 ? " 等" : string.Empty;
                result.Warnings.Add($"只允许 Modrinth 直链：已排除 {excluded.Count} 个内容文件（{string.Join("、", shown)}{suffix}），玩家安装后需自行补装。");
            }
            if (payload.Count == 0 && declaredByPath.Count == 0)
            {
                throw new InvalidOperationException("勾选的内容在 Modrinth 上都找不到对应文件，打包出来会是一个空整合包。");
            }
        }

        string? outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outputDirectory)) Directory.CreateDirectory(outputDirectory);
        string temporaryPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".mrpack-tmp";
        try
        {
            await WriteArchiveAsync(temporaryPath, options, packVersion, declaredByPath, payload, result, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, outputPath, true);
        }
        finally
        {
            DeleteIfExists(temporaryPath);
        }

        progress?.Report(new ModpackExportProgress("完成", payload.Count, payload.Count));
        result.OutputPath = outputPath;
        result.DeclaredFiles = declaredByPath.Count;
        result.OverrideFiles = payload.Count(entry => !declaredByPath.ContainsKey(entry.ArchivePath));
        return result;
    }

    /// <summary>读取 .mrpack 的 modrinth.index.json。</summary>
    public static async Task<ModrinthIndex> ReadIndexAsync(string mrpackPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mrpackPath) || !File.Exists(mrpackPath))
        {
            throw new InvalidOperationException($"整合包不存在：{mrpackPath}");
        }

        await using FileStream stream = new(mrpackPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read);
        ZipArchiveEntry? entry = archive.GetEntry(IndexEntryName);
        if (entry is null) throw new InvalidOperationException($"不是有效的 Modrinth 整合包（缺少 {IndexEntryName}）。");

        await using Stream indexStream = entry.Open();
        try
        {
            ModrinthIndex? index = await JsonSerializer.DeserializeAsync<ModrinthIndex>(indexStream, ReadOptions, cancellationToken).ConfigureAwait(false);
            return index ?? throw new InvalidOperationException("整合包索引为空。");
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException("整合包索引不是有效 JSON：" + e.Message, e);
        }
    }

    /// <summary>把 .mrpack 的 overrides/ 解压到目标目录（文件已存在时覆盖）。</summary>
    public static async Task<ModrinthBundleImportResult> ExtractOverridesAsync(
        string mrpackPath,
        string targetDirectory,
        IProgress<ModpackExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory)) throw new InvalidOperationException("目标目录不能为空。");
        Directory.CreateDirectory(targetDirectory);

        ModrinthBundleImportResult result = new();
        try
        {
            result.Index = await ReadIndexAsync(mrpackPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            result.Warnings.Add(e.Message);
        }

        await using FileStream stream = new(mrpackPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read);
        List<ZipArchiveEntry> overrideEntries = archive.Entries
            .Where(entry => entry.FullName.StartsWith(OverridesRoot + "/", StringComparison.OrdinalIgnoreCase) && !entry.FullName.EndsWith('/'))
            .ToList();

        for (int index = 0; index < overrideEntries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ZipArchiveEntry entry = overrideEntries[index];
            progress?.Report(new ModpackExportProgress("正在解压 overrides", index, overrideEntries.Count));
            string relativePath = entry.FullName[(OverridesRoot.Length + 1)..].Replace('\\', '/');
            string target = ResolveInsideDirectory(targetDirectory, relativePath);
            string? directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            try
            {
                await using Stream source = entry.Open();
                await using FileStream destination = new(target, FileMode.Create, FileAccess.Write, FileShare.None);
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                result.ExtractedFiles.Add(relativePath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                result.Warnings.Add($"跳过 {relativePath}：{e.Message}");
            }
        }
        progress?.Report(new ModpackExportProgress("完成", overrideEntries.Count, overrideEntries.Count));
        return result;
    }

    /// <summary>按 SHA-1 查询 Modrinth 的 version_file（返回 null 表示未收录）。</summary>
    public static async Task<ModrinthVersionFile?> ResolveModrinthFileAsync(string sha1, CancellationToken cancellationToken = default)
    {
        if (!MinecraftFileHash.IsSha1(sha1)) return null;
        try
        {
            VersionFilePayload payload = await ModrinthApiClient
                .GetJsonAsync<VersionFilePayload>("/version_file/" + sha1 + "?algorithm=sha1", cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(payload.Url)) return null;
            return new ModrinthVersionFile
            {
                Url = payload.Url,
                Filename = payload.Filename,
                Size = payload.Size,
                Primary = false,
                Hashes = payload.Hashes ?? new Dictionary<string, string>(),
            };
        }
        catch (InvalidOperationException e) when (e.InnerException is ModrinthStatusException { StatusCode: 404 })
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- 打包

    private static async Task WriteArchiveAsync(
        string temporaryPath,
        ModpackExportOptions options,
        string packVersion,
        Dictionary<string, ModrinthIndexFile> declaredByPath,
        List<PayloadEntry> payload,
        ModpackExportResult result,
        CancellationToken cancellationToken)
    {
        await using FileStream file = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using ZipArchive archive = new(file, ZipArchiveMode.Create);
        await WriteTextEntryAsync(archive, IndexEntryName, BuildIndexJson(options, packVersion, declaredByPath), cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(options.IconPngPath) && File.Exists(options.IconPngPath))
        {
            await CopyFileEntryAsync(archive, OverridesRoot + "/icon.png", options.IconPngPath, cancellationToken).ConfigureAwait(false);
        }

        int overrides = 0;
        for (int index = 0; index < payload.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PayloadEntry entry = payload[index];
            if (declaredByPath.ContainsKey(entry.ArchivePath)) continue;
            string entryPath = OverridesRoot + "/" + entry.ArchivePath;
            try
            {
                await CopyFileEntryAsync(archive, entryPath, entry.SourcePath, cancellationToken).ConfigureAwait(false);
                overrides++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                result.Warnings.Add($"跳过 {entry.ArchivePath}：{e.Message}");
            }
        }
    }

    private static string BuildIndexJson(ModpackExportOptions options, string packVersion, Dictionary<string, ModrinthIndexFile> declaredByPath)
    {
        ModrinthIndex index = new()
        {
            FormatVersion = 1,
            Game = "minecraft",
            VersionId = packVersion,
            Name = options.PackName,
            Summary = options.Description,
            Files = [.. declaredByPath.Values.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)],
            Dependencies = new Dictionary<string, string> { ["minecraft"] = options.MinecraftVersion },
        };

        string loaderKey = MapLoaderKey(options.LoaderName);
        if (loaderKey.Length > 0 && !string.IsNullOrWhiteSpace(options.LoaderVersion))
        {
            index.Dependencies[loaderKey] = options.LoaderVersion;
        }
        return JsonSerializer.Serialize(index, WriteOptions);
    }

    private static string MapLoaderKey(string loaderName) => loaderName.Trim().ToLowerInvariant() switch
    {
        "fabric" => "fabric-loader",
        "quilt" => "quilt-loader",
        "forge" => "forge",
        "neoforge" => "neoforge",
        _ => string.Empty,
    };

    public static bool IsModrinthContentPath(string archivePath)
    {
        string lower = archivePath.Replace('\\', '/').ToLowerInvariant();
        return lower.StartsWith(CategoryMods + "/", StringComparison.Ordinal) ||
               lower.StartsWith(CategoryResourcePacks + "/", StringComparison.Ordinal) ||
               lower.StartsWith(CategoryShaderPacks + "/", StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 辅助

    private static async Task WriteTextEntryAsync(ZipArchive archive, string entryName, string content, CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using Stream stream = entry.Open();
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyFileEntryAsync(ZipArchive archive, string entryName, string sourcePath, CancellationToken cancellationToken)
    {
        // 先打开源文件再建条目：源不可读时不会在包里留下空条目
        await using FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using Stream destination = entry.Open();
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    private static List<string> EnumerateSafe(string directory)
    {
        List<string> result = [];
        SafeWalk(directory, result);
        return result;
    }

    private static void SafeWalk(string directory, List<string> result)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory);
        }
        catch (Exception)
        {
            return;
        }
        foreach (string path in entries)
        {
            if (Directory.Exists(path))
            {
                SafeWalk(path, result);
                continue;
            }
            result.Add(path);
        }
    }

    private static List<string> EnumerateTopLevel(string directory)
    {
        List<string> result = [];
        try
        {
            result.AddRange(Directory.EnumerateFileSystemEntries(directory));
        }
        catch (Exception)
        {
            return result;
        }
        result.Sort((left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static long DirectorySize(string directory)
    {
        long total = 0;
        foreach (string path in EnumerateSafe(directory))
        {
            try
            {
                total += new FileInfo(path).Length;
            }
            catch (IOException)
            {
                // 读不到的文件按 0 计
            }
        }
        return total;
    }

    private static string SanitizeSavePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        HashSet<char> invalid = [.. Path.GetInvalidPathChars(), '?', '*', '"', '<', '>', '|'];
        StringBuilder builder = new(path.Length);
        foreach (char c in path) builder.Append(invalid.Contains(c) ? '_' : c);
        return builder.ToString();
    }

    private static string ResolveInsideDirectory(string root, string relativePath)
    {
        string normalizedRoot = Path.GetFullPath(root);
        string target = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        if (!string.Equals(target, normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
            !target.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"压缩包条目超出目标目录：{relativePath}");
        }
        return target;
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // 清理失败不影响主流程
        }
    }

    private sealed record PayloadEntry(string ArchivePath, string SourcePath);

    private sealed class VersionFilePayload
    {
        [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
        [JsonPropertyName("filename")] public string Filename { get; set; } = string.Empty;
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("hashes")] public Dictionary<string, string>? Hashes { get; set; }
    }
}
