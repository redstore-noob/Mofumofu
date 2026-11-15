using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using MofuCore.Download.Modrinth;

using MofuCore.Config;

namespace MofuCore.Download.CurseForge;

public sealed class CurseForgeMinecraft
{
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;

    [JsonPropertyName("modLoaders")] public List<CurseForgeModLoader> ModLoaders { get; set; } = [];
}

public sealed class CurseForgeModLoader
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    [JsonPropertyName("primary")] public bool Primary { get; set; }
}

public sealed class CurseForgeManifestFile
{
    [JsonPropertyName("projectID")] public int ProjectId { get; set; }

    [JsonPropertyName("fileID")] public int FileId { get; set; }

    [JsonPropertyName("required")] public bool Required { get; set; } = true;
}

public sealed class CurseForgeManifest
{
    [JsonPropertyName("manifestType")] public string ManifestType { get; set; } = string.Empty;

    [JsonPropertyName("manifestVersion")] public int ManifestVersion { get; set; }

    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;

    [JsonPropertyName("author")] public string Author { get; set; } = string.Empty;

    [JsonPropertyName("minecraft")] public CurseForgeMinecraft Minecraft { get; set; } = new();

    [JsonPropertyName("files")] public List<CurseForgeManifestFile> Files { get; set; } = [];

    [JsonPropertyName("overrides")] public string Overrides { get; set; } = "overrides";
}

public static class CurseForgeBundleInstall
{
    private const string ApiRoot = "https://api.curseforge.com/v1";
    private const int MaxParallelDownloads = 8;
    // 清单文本与解压条目沿用 Bundle* 口径；整包体积上限沿用 Package* 口径
    private const long MaximumManifestBytes = MofuCore.Config.SizeLimits.BundleIndexMaximumBytes;
    private const long MaximumArchiveBytes = MofuCore.Config.SizeLimits.PackageArchiveMaximumBytes;
    private const long MaximumDownloadBytes = MofuCore.Config.SizeLimits.BundleEntryMaximumBytes;
    // 解压条目数 / 单条目 / 累计解压体积上限（复用 SizeLimits 的 Bundle* 常量）
    private const int MaximumExtractedEntries = 20000;
    private const long MaximumEntryBytes = MofuCore.Config.SizeLimits.BundleEntryMaximumBytes;
    private const long MaximumExtractedBytes = MofuCore.Config.SizeLimits.BundleExtractedMaximumBytes;
    private const int CopyBufferSize = 128 * 1024;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    // 文件下载专用：禁用自动重定向，交给 DownloadUrlGuard 逐跳校验；API 请求仍用上面的 Http
    private static readonly HttpClient DownloadHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromMinutes(30),
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static bool LooksLikeCurseForge(string packagePath)
    {
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(packagePath);
            return archive.GetEntry("modrinth.index.json") is null && archive.GetEntry("manifest.json") is not null;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<ModpackInstallResult> InstallAsync(
        string packagePath,
        string gameDirectory,
        string apiKey,
        ModpackInstallOptions? options = null,
        IProgress<ModpackInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ModpackInstallOptions();

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "CurseForge 整合包需要 API Key 才能解析文件清单，请先在「设置 → 下载」里填写 CurseForge API Key。");
        }

        if (!File.Exists(packagePath)) throw new FileNotFoundException("整合包文件不存在。", packagePath);

        // 整包体积上限（Package* 口径），避免超大 zip 直接交给解压流程
        if (new FileInfo(packagePath).Length > MaximumArchiveBytes)
        {
            throw new InvalidOperationException("整合包体积超过上限。");
        }

        Directory.CreateDirectory(gameDirectory);

        var result = new ModpackInstallResult { DetectedFormat = "curseforge" };

        using ZipArchive archive = ZipFile.OpenRead(packagePath);

        ZipArchiveEntry manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidOperationException("压缩包里没有 manifest.json，不是 CurseForge 整合包。");

        CurseForgeManifest manifest = await ReadManifestAsync(manifestEntry, cancellationToken).ConfigureAwait(false);

        result.DeclaredMinecraftVersion = manifest.Minecraft.Version ?? string.Empty;

        ModpackRequirements? requirements = ResolveRequirements(manifest);
        if (requirements is not null)
        {
            result.DeclaredLoaderName = requirements.LoaderName;
            result.DeclaredLoaderVersion = requirements.LoaderVersion;
            result.DeclaredLoaderSupported = requirements.LoaderSupported;
        }

        progress?.Report(new ModpackInstallProgress("正在解压整合包内容…", 0, 0, 0, manifest.Files.Count));

        string overrides = string.IsNullOrWhiteSpace(manifest.Overrides)
            ? "overrides"
            : manifest.Overrides.Trim().TrimEnd('/');

        result.InstalledFiles = ExtractDirectory(archive, overrides + "/", gameDirectory, cancellationToken);

        var files = manifest.Files.Where(file => file is not null && file.ProjectId > 0 && file.FileId > 0).ToList();

        if (files.Count > 0)
        {
            string modsDirectory = Path.Combine(gameDirectory, "mods");
            Directory.CreateDirectory(modsDirectory);

            var client = new CurseForgeClient(apiKey);
            int completed = 0;
            int downloaded = 0;
            int reused = 0;
            var errors = new List<string>();
            var warnings = new List<string>();
            var gate = new object();

            progress?.Report(new ModpackInstallProgress(
                $"正在下载整合包依赖（0/{files.Count}）…", 0, 0, 0, files.Count));

            using var semaphore = new SemaphoreSlim(MaxParallelDownloads);

            await Parallel.ForEachAsync(
                files,
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelDownloads, CancellationToken = cancellationToken },
                async (file, token) =>
                {
                    await semaphore.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        string? url = await client.GetDownloadUrlAsync(file.ProjectId, file.FileId, token).ConfigureAwait(false);

                        if (string.IsNullOrWhiteSpace(url))
                        {
                            lock (gate)
                            {
                                warnings.Add($"项目 {file.ProjectId} 的文件 {file.FileId} 无法获取可用的下载地址（接口无数据、网络失败或作者关闭了第三方分发），已跳过");
                            }
                            return;
                        }

                        string fileName = SanitizeFileName(GetFileNameFromUrl(url));
                        string target = Path.Combine(modsDirectory, fileName);

                        if (File.Exists(target))
                        {
                            Interlocked.Increment(ref reused);
                            return;
                        }

                        await DownloadAsync(url, target, token).ConfigureAwait(false);
                        Interlocked.Increment(ref downloaded);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception error)
                    {
                        lock (gate)
                        {
                            errors.Add($"项目 {file.ProjectId} / 文件 {file.FileId} 下载失败：{error.Message}");
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                        int done = Interlocked.Increment(ref completed);

                        progress?.Report(new ModpackInstallProgress(
                            $"正在下载整合包依赖（{done}/{files.Count}）…",
                            0, 0, done, files.Count));
                    }
                }).ConfigureAwait(false);

            result.DownloadedFiles = downloaded;
            result.ReusedFiles = reused;
            result.Warnings.AddRange(warnings);
            result.Errors.AddRange(errors);
        }

        if (options.InstallLoader && requirements is { LoaderSupported: true })
        {
            try
            {
                progress?.Report(new ModpackInstallProgress("正在安装加载器…", 0, 0, 0, 0));

                result.InstalledLoaderVersionId = await ModrinthBundleInstall.InstallLoaderAsync(
                        gameDirectory,
                        requirements,
                        options,
                        new Progress<string>(text => progress?.Report(
                            new ModpackInstallProgress(text, 0, 0, 0, 0))),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                result.Warnings.Add("加载器自动安装失败：" + error.Message);
            }
        }
        else if (options.InstallLoader && requirements is null)
        {
            result.Warnings.Add("清单没有声明可识别的加载器，已跳过加载器安装。");
        }

        return result;
    }

    private static ModpackRequirements? ResolveRequirements(CurseForgeManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Minecraft.Version)) return null;

        var loader = manifest.Minecraft.ModLoaders.FirstOrDefault(item => item.Primary)
                     ?? manifest.Minecraft.ModLoaders.FirstOrDefault();

        if (loader is null || string.IsNullOrWhiteSpace(loader.Id))
        {
            return new ModpackRequirements
            {
                MinecraftVersion = manifest.Minecraft.Version,
                Loader = ModpackLoader.Vanilla,
            };
        }

        int separator = loader.Id.IndexOf('-');
        if (separator <= 0) return null;

        string name = loader.Id[..separator].Trim().ToLowerInvariant();
        string version = loader.Id[(separator + 1)..].Trim();

        ModpackLoader kind = name switch
        {
            "fabric" => ModpackLoader.Fabric,
            "quilt" => ModpackLoader.Quilt,
            "forge" => ModpackLoader.Forge,
            "neoforge" => ModpackLoader.NeoForge,
            "optifine" => ModpackLoader.OptiFine,
            _ => ModpackLoader.Vanilla,
        };

        return new ModpackRequirements
        {
            MinecraftVersion = manifest.Minecraft.Version,
            Loader = kind,
            LoaderVersion = version,
            RawLoaderKey = name,
        };
    }

    private static async Task<CurseForgeManifest> ReadManifestAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.Length > MaximumManifestBytes)
        {
            throw new InvalidOperationException("manifest.json 体积超过上限。");
        }

        await using Stream stream = entry.Open();
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        long total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;
            total += read;
            if (total > MaximumManifestBytes)
            {
                throw new InvalidOperationException("manifest.json 体积超过上限。");
            }
            buffer.Write(chunk, 0, read);
        }
        buffer.Position = 0;

        try
        {
            CurseForgeManifest? manifest = await JsonSerializer
                .DeserializeAsync<CurseForgeManifest>(buffer, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (manifest is null) throw new InvalidOperationException("manifest.json 内容为空。");

            // JSON 里显式写入的 null 会覆盖属性默认值；归一化后再交给上层解引用
            manifest.Minecraft ??= new CurseForgeMinecraft();
            manifest.Minecraft.ModLoaders ??= [];
            manifest.Files ??= [];
            return manifest;
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException("manifest.json 解析失败：" + error.Message, error);
        }
    }

    private static int ExtractDirectory(
        ZipArchive archive,
        string prefix,
        string destination,
        CancellationToken cancellationToken)
    {
        int count = 0;
        long extracted = 0;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            string relative = name[prefix.Length..];
            if (relative.Length == 0 || relative.EndsWith('/')) continue;

            relative = SanitizeRelativePath(relative);
            if (relative.Length == 0) continue;

            string target;
            try
            {
                target = ResolveInsideDirectory(destination, relative);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            // 条目数 / 声明体积 / 累计体积上限：防压缩炸弹
            if (count >= MaximumExtractedEntries)
            {
                throw new InvalidOperationException("整合包解压条目数超过上限，疑似压缩炸弹。");
            }
            if (entry.Length > MaximumEntryBytes || extracted + entry.Length > MaximumExtractedBytes)
            {
                throw new InvalidOperationException($"整合包解压体积超过上限：{entry.FullName}");
            }

            string? directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // 逐块读取并累计实际解压字节，避免声明大小被伪造；读写失败显式抛出，不再静默跳过
            using Stream source = entry.Open();
            using FileStream output = new(target, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize);
            byte[] buffer = new byte[CopyBufferSize];
            long written = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > MaximumEntryBytes || extracted + written > MaximumExtractedBytes)
                {
                    throw new InvalidOperationException($"条目 {entry.FullName} 实际解压大小超过上限，疑似压缩炸弹。");
                }
                output.Write(buffer, 0, read);
            }
            extracted += written;
            count++;
        }

        return count;
    }

    private static string SanitizeRelativePath(string relative)
    {
        string[] parts = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var safe = new List<string>(parts.Length);

        foreach (string part in parts)
        {
            if (part is "." or "..") continue;
            if (part.Contains(':')) continue;
            safe.Add(part);
        }

        return string.Join(Path.DirectorySeparatorChar, safe);
    }

    private static string ResolveInsideDirectory(string destination, string relative)
    {
        string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string target = Path.GetFullPath(Path.Combine(root, relative));
        if (!string.Equals(target, root, StringComparison.OrdinalIgnoreCase)
            && !target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"压缩包条目超出目标目录：{relative}");
        }
        return target;
    }

    private static string GetFileNameFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            string name = Path.GetFileName(uri.AbsolutePath);
            return string.IsNullOrWhiteSpace(name) ? "downloaded.jar" : name;
        }
        catch
        {
            return "downloaded.jar";
        }
    }

    private static string SanitizeFileName(string name)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) return "downloaded.jar";

        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            trimmed = trimmed.Replace(invalid, '_');
        }

        trimmed = trimmed.Replace('/', '_').Replace('\\', '_');
        return trimmed is "." or ".." ? "downloaded.jar" : trimmed;
    }

    private static async Task DownloadAsync(string url, string targetPath, CancellationToken cancellationToken)
    {
        if (!ModrinthApiClient.IsAllowedDownloadUrl(url))
        {
            throw new InvalidOperationException($"下载地址不是允许的 HTTPS URL：{url}");
        }

        // 逐跳校验（含 DNS 私网拦截），最终 RequestUri 也会兜底校验
        using HttpResponseMessage response = await DownloadUrlGuard
            .SendWithRedirectGuardAsync(DownloadHttp, url, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        string temporary = targetPath + ".mofu-download";
        try
        {
            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = new FileStream(
                             temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                byte[] buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read <= 0) break;
                    total += read;
                    if (total > MaximumDownloadBytes)
                    {
                        throw new InvalidOperationException($"下载 {url} 超过单文件体积上限。");
                    }
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }

            File.Move(temporary, targetPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch
            {
            }

            throw;
        }
    }

    private sealed class CurseForgeClient
    {
        private readonly string _apiKey;

        public CurseForgeClient(string apiKey) => _apiKey = apiKey;

        public async Task<string?> GetDownloadUrlAsync(int modId, int fileId, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{ApiRoot}/mods/{modId}/files/{fileId}/download-url");

            request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            try
            {
                using HttpResponseMessage response = await Http
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound) return null;
                if (!response.IsSuccessStatusCode) return null;

                await using Stream stream = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);

                using JsonDocument document = await JsonDocument
                    .ParseAsync(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (!document.RootElement.TryGetProperty("data", out JsonElement data)) return null;
                string? downloadUrl = data.ValueKind == JsonValueKind.String ? data.GetString() : null;
                return ModrinthApiClient.IsAllowedDownloadUrl(downloadUrl) ? downloadUrl : null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }
    }
}
