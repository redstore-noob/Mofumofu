using System.Text.Json;
using MofuCore.Download.Minecraft;

namespace MofuCore.Download.Modrinth;

/// <summary>Modrinth 文件哈希反查命中的归属信息。</summary>
public sealed record ModrinthVersionFileMatch(
    string ProjectId,
    string VersionId,
    string VersionNumber,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders,
    string DatePublished,
    ModrinthVersionFileInfo File);

/// <summary>一个可下载文件的简要信息。</summary>
public sealed record ModrinthVersionFileInfo(string Url, string Filename, long Size);

/// <summary>批量「取最新版本」接口对一个哈希的回答。</summary>
public sealed record ModrinthVersionFileUpdate(
    string Hash,
    string VersionId,
    string ProjectId,
    string VersionNumber,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders,
    string DatePublished,
    ModrinthVersionFileInfo File);

/// <summary>已下载到磁盘的单个文件。</summary>
public sealed record ModrinthDownloadedFile(
    string ProjectId,
    string VersionId,
    string Filename,
    string FilePath,
    long Size,
    bool IsDependency);

/// <summary>按版本 + 依赖下载的结果。</summary>
public sealed class ModrinthDownloadResult
{
    public List<ModrinthDownloadedFile> Files { get; } = [];
    public List<string> Warnings { get; } = [];
}

/// <summary>按依赖下载的进度。</summary>
public sealed record ModrinthDownloadProgress(int Completed, int Total, string Detail);

/// <summary>
/// Modrinth 版本与文件接口：版本列表、单版本详情、主文件下载、按依赖下载、哈希反查与更新检测。
/// </summary>
public sealed class ModrinthResourceDownload
{
    public const int VersionFileLookupBatchSize = 100;
    private const int MaximumDependencyDepth = 8;

    /// <summary>获取项目版本列表，可按 MC 版本与加载器过滤。</summary>
    public static async Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(
        string projectId,
        IEnumerable<string>? gameVersions = null,
        IEnumerable<string>? loaders = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId)) throw new InvalidOperationException("projectId 不能为空。");

        List<string> parameters = [];
        List<string> versionList = Dedupe(gameVersions);
        if (versionList.Count > 0)
        {
            parameters.Add("game_versions=" + Uri.EscapeDataString("[" + string.Join(",", versionList.Select(value => JsonSerializer.Serialize(value))) + "]"));
        }
        List<string> loaderList = Dedupe(loaders);
        if (loaderList.Count > 0)
        {
            parameters.Add("loaders=" + Uri.EscapeDataString("[" + string.Join(",", loaderList.Select(value => JsonSerializer.Serialize(value))) + "]"));
        }

        string endpoint = "/project/" + Uri.EscapeDataString(projectId) + "/version";
        if (parameters.Count > 0) endpoint += "?" + string.Join("&", parameters);

        try
        {
            List<ModrinthVersion>? versions = await ModrinthApiClient.GetJsonAsync<List<ModrinthVersion>>(endpoint, cancellationToken).ConfigureAwait(false);
            return versions ?? [];
        }
        catch (InvalidOperationException e) when (e.InnerException is JsonException)
        {
            // 解析失败按"没有版本"处理，与旧实现保持一致
            return [];
        }
    }

    /// <summary>获取指定项目在指定 MC 版本 + 加载器下的版本列表。</summary>
    public static Task<IReadOnlyList<ModrinthVersion>> GetVersionsForComboAsync(
        string projectId,
        string gameVersion,
        string loader,
        CancellationToken cancellationToken = default)
    {
        string[] versions = string.IsNullOrWhiteSpace(gameVersion) ? [] : [gameVersion];
        string[] loaders = string.IsNullOrWhiteSpace(loader) ? [] : [loader];
        return GetVersionsAsync(projectId, versions, loaders, cancellationToken);
    }

    /// <summary>获取单个版本详情。</summary>
    public static async Task<ModrinthVersion> GetVersionAsync(string versionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(versionId)) throw new InvalidOperationException("versionId 不能为空。");
        return await ModrinthApiClient.GetJsonAsync<ModrinthVersion>("/version/" + Uri.EscapeDataString(versionId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>取项目在指定 MC 版本 + 加载器下最新版本的主文件。</summary>
    public static async Task<ModrinthVersionFile?> GetPrimaryFileAsync(
        string projectId,
        string? gameVersion = null,
        string? loader = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ModrinthVersion> versions = await GetVersionsForComboAsync(projectId, gameVersion ?? string.Empty, loader ?? string.Empty, cancellationToken).ConfigureAwait(false);
        foreach (ModrinthVersion version in versions)
        {
            ModrinthVersionFile? file = version.PrimaryFile();
            if (file is not null && !string.IsNullOrWhiteSpace(file.Url)) return file;
        }
        return null;
    }

    /// <summary>查询项目标题（失败时回退 slug）。</summary>
    public static async Task<string> GetProjectNameAsync(string projectId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId)) throw new InvalidOperationException("projectId 不能为空。");
        ProjectNamePayload payload = await ModrinthApiClient.GetJsonAsync<ProjectNamePayload>("/project/" + Uri.EscapeDataString(projectId), cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(payload.Title) ? payload.Slug : payload.Title;
    }

    /// <summary>下载单个版本文件到目标目录（含 SHA-1 校验），返回落盘路径。</summary>
    public static async Task<string> DownloadVersionFileAsync(
        ModrinthVersionFile file,
        string targetDirectory,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (string.IsNullOrWhiteSpace(targetDirectory)) throw new InvalidOperationException("targetDirectory 不能为空。");
        if (string.IsNullOrWhiteSpace(file.Url)) throw new InvalidOperationException("文件没有下载地址。");

        Directory.CreateDirectory(targetDirectory);
        string targetPath = Path.Combine(targetDirectory, SanitizeFileName(file.Filename));
        await ModrinthApiClient.DownloadToFileAsync(file.Url, targetPath, progress, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(file.Sha1) &&
            !await MinecraftFileHash.MatchesSha1Async(targetPath, file.Sha1, cancellationToken).ConfigureAwait(false))
        {
            File.Delete(targetPath);
            throw new InvalidOperationException($"文件 SHA-1 校验失败：{file.Filename}");
        }
        return targetPath;
    }

    /// <summary>
    /// 下载指定版本及其必需依赖到目标目录。依赖按 project_id 去重，递归深度上限 8。
    /// </summary>
    public static async Task<ModrinthDownloadResult> DownloadWithDependenciesAsync(
        string versionId,
        string targetDirectory,
        bool includeDependencies = true,
        string? gameVersion = null,
        string? loader = null,
        IProgress<ModrinthDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ModrinthDownloadResult result = new();
        ModrinthVersion root = await GetVersionAsync(versionId, cancellationToken).ConfigureAwait(false);

        HashSet<string> visitedProjects = new(StringComparer.OrdinalIgnoreCase) { root.ProjectId };
        Queue<(ModrinthVersion Version, int Depth, bool IsDependency)> pending = new();
        pending.Enqueue((root, 0, false));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (ModrinthVersion version, int depth, bool isDependency) = pending.Dequeue();

            ModrinthVersionFile? file = version.PrimaryFile();
            if (file is null || string.IsNullOrWhiteSpace(file.Url))
            {
                result.Warnings.Add($"版本 {version.DisplayName} 没有可下载的主文件，已跳过。");
                continue;
            }

            progress?.Report(new ModrinthDownloadProgress(result.Files.Count, result.Files.Count + pending.Count + 1, $"正在下载 {version.DisplayName}"));
            string path = await DownloadVersionFileAsync(file, targetDirectory, null, cancellationToken).ConfigureAwait(false);
            result.Files.Add(new ModrinthDownloadedFile(version.ProjectId, version.Id, file.Filename, path, file.Size, isDependency));

            if (!includeDependencies || depth >= MaximumDependencyDepth) continue;
            foreach (ModrinthDependency dependency in version.Dependencies)
            {
                if (!dependency.IsRequired) continue;
                string projectId = dependency.ProjectId?.Trim() ?? string.Empty;
                if (projectId.Length == 0 || !visitedProjects.Add(projectId)) continue;

                try
                {
                    ModrinthVersion dependencyVersion = string.IsNullOrWhiteSpace(dependency.VersionId)
                        ? await ResolveNewestVersionAsync(projectId, gameVersion, loader, cancellationToken).ConfigureAwait(false)
                        : await GetVersionAsync(dependency.VersionId!, cancellationToken).ConfigureAwait(false);
                    pending.Enqueue((dependencyVersion, depth + 1, true));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    result.Warnings.Add($"依赖 {projectId} 解析失败：{e.Message}");
                }
            }
        }

        progress?.Report(new ModrinthDownloadProgress(result.Files.Count, result.Files.Count, "下载完成"));
        return result;
    }

    private static async Task<ModrinthVersion> ResolveNewestVersionAsync(
        string projectId,
        string? gameVersion,
        string? loader,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ModrinthVersion> versions = await GetVersionsForComboAsync(projectId, gameVersion ?? string.Empty, loader ?? string.Empty, cancellationToken).ConfigureAwait(false);
        foreach (ModrinthVersion version in versions)
        {
            if (version.PrimaryFile() is not null) return version;
        }
        throw new InvalidOperationException($"项目 {projectId} 没有可用版本。");
    }

    /// <summary>批量按 SHA-1 反查文件归属；查不到的哈希不会出现在结果里（404 按全部查不到处理）。</summary>
    public static async Task<IReadOnlyDictionary<string, ModrinthVersionFileMatch>> GetVersionFilesByHashesAsync(
        IEnumerable<string> hashes,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, ModrinthVersionFileMatch> result = new(StringComparer.OrdinalIgnoreCase);
        List<string> unique = UniqueHashes(hashes);
        for (int start = 0; start < unique.Count; start += VersionFileLookupBatchSize)
        {
            List<string> batch = unique.GetRange(start, Math.Min(VersionFileLookupBatchSize, unique.Count - start));
            byte[] payload;
            try
            {
                payload = await ModrinthApiClient.RequestAsync(HttpMethod.Post, "/version_files",
                    new Dictionary<string, object> { ["hashes"] = batch, ["algorithm"] = "sha1" }, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException e) when (e.InnerException is ModrinthStatusException { StatusCode: 404 })
            {
                continue;
            }

            Dictionary<string, VersionFileMatchPayload>? decoded;
            try
            {
                decoded = JsonSerializer.Deserialize<Dictionary<string, VersionFileMatchPayload>>(payload, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException e)
            {
                throw new InvalidOperationException("解析 Modrinth 文件哈希查询结果失败：" + e.Message, e);
            }
            if (decoded is null) continue;
            foreach ((string hash, VersionFileMatchPayload entry) in decoded)
            {
                result[hash.Trim().ToLowerInvariant()] = entry.ToMatch(hash);
            }
        }
        return result;
    }

    /// <summary>批量查询这些哈希对应的最新版本；没有兼容版本时该哈希不会出现在结果里。</summary>
    public static async Task<IReadOnlyDictionary<string, ModrinthVersionFileUpdate>> GetLatestVersionFilesByHashesAsync(
        IEnumerable<string> hashes,
        IEnumerable<string>? gameVersions = null,
        IEnumerable<string>? loaders = null,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, ModrinthVersionFileUpdate> result = new(StringComparer.OrdinalIgnoreCase);
        List<string> unique = UniqueHashes(hashes);
        List<string> versionFilter = Dedupe(gameVersions);
        List<string> loaderFilter = Dedupe(loaders);

        for (int start = 0; start < unique.Count; start += VersionFileLookupBatchSize)
        {
            List<string> batch = unique.GetRange(start, Math.Min(VersionFileLookupBatchSize, unique.Count - start));
            Dictionary<string, object> body = new() { ["hashes"] = batch, ["algorithm"] = "sha1" };
            if (versionFilter.Count > 0) body["game_versions"] = versionFilter;
            if (loaderFilter.Count > 0) body["loaders"] = loaderFilter;

            byte[] payload;
            try
            {
                payload = await ModrinthApiClient.RequestAsync(HttpMethod.Post, "/version_files/update", body, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException e) when (e.InnerException is ModrinthStatusException { StatusCode: 404 })
            {
                continue;
            }

            foreach ((string hash, ModrinthVersionFileUpdate update) in DecodeUpdatePayload(payload))
            {
                result[hash] = update;
            }
        }
        return result;
    }

    private static Dictionary<string, ModrinthVersionFileUpdate> DecodeUpdatePayload(byte[] payload)
    {
        Dictionary<string, ModrinthVersionFileUpdate> result = new(StringComparer.OrdinalIgnoreCase);
        ReadOnlySpan<byte> trimmed = payload.AsSpan().TrimStart((byte)' ');
        if (trimmed.Length == 0) return result;

        try
        {
            if (trimmed[0] == (byte)'{')
            {
                // 形状一：{ "<project_id>": [version, ...] }；形状二：{ "<hash>": version }
                Dictionary<string, JsonElement> raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(trimmed) ?? new();
                foreach ((string key, JsonElement value) in raw)
                {
                    if (value.ValueKind == JsonValueKind.Array)
                    {
                        List<VersionFileMatchPayload>? versions = value.Deserialize<List<VersionFileMatchPayload>>();
                        if (versions is null) continue;
                        foreach (VersionFileMatchPayload version in versions)
                        {
                            foreach (VersionFileInfoPayload file in version.Files)
                            {
                                string hash = file.Sha1();
                                if (hash.Length == 0) continue;
                                result[hash] = BuildUpdate(hash, version, file);
                            }
                        }
                    }
                    else
                    {
                        VersionFileMatchPayload? version = value.Deserialize<VersionFileMatchPayload>();
                        if (version is null) continue;
                        string hash = key.Trim().ToLowerInvariant();
                        result[hash] = BuildUpdate(hash, version, PickFilePayload(version.Files, hash));
                    }
                }
            }
            else if (trimmed[0] == (byte)'[')
            {
                List<VersionFileMatchPayload>? versions = JsonSerializer.Deserialize<List<VersionFileMatchPayload>>(trimmed);
                if (versions is not null)
                {
                    foreach (VersionFileMatchPayload version in versions)
                    {
                        foreach (VersionFileInfoPayload file in version.Files)
                        {
                            string hash = file.Sha1();
                            if (hash.Length == 0) continue;
                            result[hash] = BuildUpdate(hash, version, file);
                        }
                    }
                }
            }
            else
            {
                throw new InvalidOperationException("Modrinth 最新版本查询返回了非预期的内容。");
            }
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException("解析 Modrinth 最新版本查询结果失败：" + e.Message, e);
        }
        return result;
    }

    private static ModrinthVersionFileUpdate BuildUpdate(string hash, VersionFileMatchPayload version, VersionFileInfoPayload file) => new(
        hash,
        version.Id,
        version.ProjectId,
        version.VersionNumber,
        version.GameVersions ?? [],
        version.Loaders ?? [],
        version.DatePublished,
        new ModrinthVersionFileInfo(file.Url, file.Filename, file.Size));

    private static VersionFileInfoPayload PickFilePayload(List<VersionFileInfoPayload> files, string hash)
    {
        foreach (VersionFileInfoPayload file in files)
        {
            if (string.Equals(file.Sha1(), hash, StringComparison.OrdinalIgnoreCase)) return file;
        }
        foreach (VersionFileInfoPayload file in files)
        {
            if (file.Primary) return file;
        }
        return files.Count > 0 ? files[0] : new VersionFileInfoPayload();
    }

    private static List<string> UniqueHashes(IEnumerable<string> hashes)
    {
        List<string> result = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? hash in hashes)
        {
            string normalized = hash?.Trim().ToLowerInvariant() ?? string.Empty;
            if (normalized.Length == 0 || !seen.Add(normalized)) continue;
            result.Add(normalized);
        }
        return result;
    }

    private static List<string> Dedupe(IEnumerable<string>? values)
    {
        List<string> result = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? value in values ?? [])
        {
            string trimmed = value?.Trim() ?? string.Empty;
            if (trimmed.Length == 0 || !seen.Add(trimmed)) continue;
            result.Add(trimmed);
        }
        return result;
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "download.bin";
        HashSet<char> invalid = [.. Path.GetInvalidFileNameChars()];
        string cleaned = new(name.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray());
        return cleaned.Length == 0 ? "download.bin" : cleaned;
    }

    private sealed class ProjectNamePayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("slug")] public string Slug { get; set; } = string.Empty;
    }

    private sealed class VersionFileMatchPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("project_id")] public string ProjectId { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("version_number")] public string VersionNumber { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("game_versions")] public List<string>? GameVersions { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("loaders")] public List<string>? Loaders { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("date_published")] public string DatePublished { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("files")] public List<VersionFileInfoPayload> Files { get; set; } = [];

        public ModrinthVersionFileMatch ToMatch(string hash) => new(
            ProjectId,
            Id,
            VersionNumber,
            GameVersions ?? [],
            Loaders ?? [],
            DatePublished,
            new ModrinthVersionFileInfo(PickFilePayload(Files, hash).Url, PickFilePayload(Files, hash).Filename, PickFilePayload(Files, hash).Size));
    }

    private sealed class VersionFileInfoPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("filename")] public string Filename { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("size")] public long Size { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("hashes")] public Dictionary<string, string> Hashes { get; set; } = [];
        [System.Text.Json.Serialization.JsonPropertyName("primary")] public bool Primary { get; set; }

        public string Sha1() => Hashes.TryGetValue("sha1", out string? hash) ? hash.Trim().ToLowerInvariant() : string.Empty;
    }
}
