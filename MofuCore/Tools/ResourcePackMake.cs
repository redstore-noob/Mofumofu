using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MofuCore.Tools;

public class ResourcePackMake
{
    public const int MaximumFiles = 5000;
    public const long MaximumPngBytes = 8L << 20;
    public const long MaximumTextBytes = 2L << 20;
    public const long MaximumBinaryBytes = 32L << 20;
    public const long MaximumTotalBytes = 256L << 20;
    public const int MaximumDraftBytes = 256 << 20;
    public const string DefaultPackName = "My Resource Pack";
    public const string PackMetaName = "pack.mcmeta";
    public const string PackIconName = "pack.png";

    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".mcmeta", ".txt", ".md", ".properties", ".lang", ".cfg", ".toml",
        ".yml", ".yaml", ".xml", ".svg", ".mcfunction", ".fsh", ".vsh", ".glsl",
        ".csv", ".tsv", ".ini", ".html", ".css", ".js", ".ts",
    };

    private static readonly JsonSerializerOptions DraftJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    private static uint[]? _crcTable;

    private static string __dataRoot = AppContext.BaseDirectory;

    /// <summary>草稿写在 {DataRoot}/Mofumofu/resourcepack-draft。</summary>
    public static string DataRoot
    {
        get => __dataRoot;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                __dataRoot = value.Trim();
            }
        }
    }

    public static string DraftDirectory => Path.Combine(__dataRoot, "Mofumofu", "resourcepack-draft");

    public static string DraftFilePath => Path.Combine(DraftDirectory, "draft.json");

    public static ResourcePackProject CreateDraft(string name = DefaultPackName)
    {
        return new ResourcePackProject
        {
            Name = string.IsNullOrWhiteSpace(name) ? DefaultPackName : name.Trim(),
            Root = string.Empty,
            Files = [],
        };
    }

    public static ResourcePackFile AddTextFile(ResourcePackProject project, string path, string text)
    {
        ArgumentNullException.ThrowIfNull(project);
        string cleaned = CleanArchivePath(path);
        byte[] data = Encoding.UTF8.GetBytes(text ?? string.Empty);
        EnsureSize(cleaned, data.Length, MaximumTextBytes, "文本");
        Upsert(project, new ResourcePackFile { Path = cleaned, Kind = "text", Text = text ?? string.Empty });
        return project.Files.First(file => file.Path == cleaned);
    }

    public static ResourcePackFile AddTexture(ResourcePackProject project, string path, byte[] pngBytes)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(pngBytes);
        string cleaned = CleanArchivePath(path);
        if (!cleaned.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("贴图路径必须以 .png 结尾：" + path);
        }

        if (pngBytes.Length > MaximumPngBytes)
        {
            throw new InvalidOperationException($"贴图超过上限（{MaximumPngBytes >> 20} MB）：{cleaned}");
        }

        if (pngBytes.Length < PngMagic.Length || !pngBytes.AsSpan(0, PngMagic.Length).SequenceEqual(PngMagic))
        {
            throw new InvalidOperationException("数据不是 PNG 图片：" + cleaned);
        }

        Upsert(project, new ResourcePackFile
        {
            Path = cleaned,
            Kind = "png",
            PngBase64 = Convert.ToBase64String(pngBytes),
        });
        return project.Files.First(file => file.Path == cleaned);
    }

    public static ResourcePackFile AddBinaryFile(ResourcePackProject project, string path, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(data);
        string cleaned = CleanArchivePath(path);
        EnsureSize(cleaned, data.Length, MaximumBinaryBytes, "二进制");
        Upsert(project, new ResourcePackFile
        {
            Path = cleaned,
            Kind = "binary",
            Base64 = Convert.ToBase64String(data),
        });
        return project.Files.First(file => file.Path == cleaned);
    }

    public static ResourcePackFile AddModelJson(ResourcePackProject project, string path, string json)
    {
        string cleaned = CleanArchivePath(path);
        if (!cleaned.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) ||
            !cleaned.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("模型路径必须是 assets/**/*.json：" + path);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"模型 JSON 不合法（{cleaned}）：{e.Message}", e);
        }

        return AddTextFile(project, cleaned, json);
    }

    public static ResourcePackFile? GetFile(ResourcePackProject project, string path)
    {
        ArgumentNullException.ThrowIfNull(project);
        string cleaned = CleanArchivePath(path);
        return project.Files.FirstOrDefault(file => string.Equals(file.Path, cleaned, StringComparison.OrdinalIgnoreCase));
    }

    public static bool RemoveFile(ResourcePackProject project, string path)
    {
        ArgumentNullException.ThrowIfNull(project);
        string cleaned = CleanArchivePath(path);
        int index = project.Files.FindIndex(file => string.Equals(file.Path, cleaned, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        project.Files.RemoveAt(index);
        return true;
    }

    public static void SortFiles(ResourcePackProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        project.Files.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
    }

    public static string CreatePackMetaJson(int packFormat, string description, int? minimumFormat = null)
    {
        ResourcePackMeta meta = new()
        {
            Pack = new ResourcePackMetaPack
            {
                PackFormat = packFormat <= 0 ? 34 : packFormat,
                Description = string.IsNullOrWhiteSpace(description) ? DefaultPackName : description.Trim(),
                SupportedFormats = minimumFormat is > 0 && minimumFormat != packFormat
                    ? new ResourcePackMetaFormats { MinInclusive = minimumFormat.Value, MaxInclusive = packFormat }
                    : null,
            },
        };
        return JsonSerializer.Serialize(meta, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
    }

    public static ResourcePackFile WritePackMeta(ResourcePackProject project, int packFormat, string description, int? minimumFormat = null)
    {
        return AddTextFile(project, PackMetaName, CreatePackMetaJson(packFormat, description, minimumFormat));
    }

    public static ResourcePackFile SetIcon(ResourcePackProject project, byte[] pngBytes)
    {
        return AddTexture(project, PackIconName, pngBytes);
    }

    public static ResourcePackFile GenerateIcon(ResourcePackProject project, int size = 128, uint rgb = 0x3FC6C1)
    {
        ArgumentNullException.ThrowIfNull(project);
        int actualSize = Math.Clamp(size, 16, 512);
        return SetIcon(project, CreateCheckerPng(actualSize, rgb));
    }

    public static async Task<string> ExportAsync(
        ResourcePackProject project,
        string targetPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Files.Count == 0)
        {
            throw new InvalidOperationException("资源包里还没有任何文件。");
        }

        if (project.Files.Count > MaximumFiles)
        {
            throw new InvalidOperationException($"文件数超过上限（{MaximumFiles}）。");
        }

        string target = ResolveTargetPath(targetPath);
        Dictionary<string, byte[]> payloads = new(StringComparer.Ordinal);
        long total = 0;
        foreach (ResourcePackFile file in project.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] payload = DecodeFilePayload(file);
            if (!payloads.TryAdd(file.Path.ToLowerInvariant(), payload))
            {
                throw new InvalidOperationException("包内路径重复：" + file.Path);
            }

            total += payload.Length;
            if (total > MaximumTotalBytes)
            {
                throw new InvalidOperationException($"资源包总体积超过上限（{MaximumTotalBytes >> 20} MB）。");
            }
        }

        string? directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 先写临时文件再改名：失败不留半个 zip，也不会覆盖掉上一次的成品
        string temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (ZipArchive archive = new(stream, ZipArchiveMode.Create))
            {
                int index = 0;
                foreach ((string path, byte[] payload) in payloads)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string entryName = project.Files
                        .First(file => string.Equals(file.Path.ToLowerInvariant(), path, StringComparison.Ordinal))
                        .Path;
                    ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    await using Stream entryStream = entry.Open();
                    await entryStream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                    progress?.Report($"正在打包 {++index}/{payloads.Count}");
                }
            }

            File.Move(temporary, target, overwrite: true);
            temporary = string.Empty;
            progress?.Report("打包完成");
            return target;
        }
        finally
        {
            if (temporary.Length > 0 && File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    public static async Task<ResourcePackProject> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new InvalidOperationException("未选择资源包。");
        }

        string source = Path.GetFullPath(sourcePath.Trim());
        if (Directory.Exists(source))
        {
            return await ImportDirectoryAsync(source, cancellationToken).ConfigureAwait(false);
        }

        if (!File.Exists(source))
        {
            throw new InvalidOperationException("资源包不存在：" + sourcePath);
        }

        return await ImportZipAsync(source, cancellationToken).ConfigureAwait(false);
    }

    public static async Task SaveDraftAsync(ResourcePackProject project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Files.Count > MaximumFiles)
        {
            throw new InvalidOperationException($"草稿文件数超过上限（{MaximumFiles}）。");
        }

        ResourcePackDraft draft = new()
        {
            Found = true,
            Name = project.Name,
            SavedAt = DateTimeOffset.Now.ToString("O"),
            Files = project.Files,
        };
        byte[] raw = JsonSerializer.SerializeToUtf8Bytes(draft, DraftJsonOptions);
        if (raw.Length > MaximumDraftBytes)
        {
            throw new InvalidOperationException($"草稿序列化后超过上限（{MaximumDraftBytes >> 20} MB）。");
        }

        Directory.CreateDirectory(DraftDirectory);
        // 唯一临时名，避免并发保存互相覆盖；无论成功失败都清理残留临时文件
        string temporary = DraftFilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, raw, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, DraftFilePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (Exception)
            {
            }
        }
    }

    public static async Task<ResourcePackDraft> LoadDraftAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(DraftFilePath))
        {
            return new ResourcePackDraft();
        }

        byte[] raw = await File.ReadAllBytesAsync(DraftFilePath, cancellationToken).ConfigureAwait(false);
        try
        {
            ResourcePackDraft draft = JsonSerializer.Deserialize<ResourcePackDraft>(raw, DraftJsonOptions)
                                      ?? new ResourcePackDraft();
            draft.Files ??= [];
            draft.Found = true;
            return draft;
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException("资源包草稿已损坏：" + e.Message, e);
        }
    }

    public static Task ClearDraftAsync()
    {
        if (File.Exists(DraftFilePath))
        {
            File.Delete(DraftFilePath);
        }

        return Task.CompletedTask;
    }

    /// <summary>拒绝绝对路径与 .. 逃逸。</summary>
    public static string CleanArchivePath(string path)
    {
        string normalized = (path ?? string.Empty).Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("包内路径不能为空。");
        }

        if (normalized.StartsWith('/') || normalized.Contains(':'))
        {
            throw new InvalidOperationException("包内路径不合法（不能用绝对路径）：" + path);
        }

        string[] segments = normalized.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment == "." || segment == ".."))
        {
            throw new InvalidOperationException("包内路径不合法（含空段或 ..）：" + path);
        }

        return string.Join('/', segments);
    }

    private static async Task<ResourcePackProject> ImportDirectoryAsync(string root, CancellationToken cancellationToken)
    {
        ResourcePackProject project = new()
        {
            Name = Path.GetFileName(root),
            Root = root,
        };
        long total = 0;
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            FileInfo info = new(file);
            long limit = relative.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? MaximumPngBytes : MaximumBinaryBytes;
            if (info.Length > limit)
            {
                throw new InvalidOperationException($"{relative} 超过单文件上限（{limit >> 20} MB）。");
            }

            total += info.Length;
            if (total > MaximumTotalBytes)
            {
                throw new InvalidOperationException($"资源包总体积超过上限（{MaximumTotalBytes >> 20} MB）。");
            }

            byte[] data = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            project.Files.Add(BuildImportedFile(relative, data));
            if (project.Files.Count > MaximumFiles)
            {
                throw new InvalidOperationException($"文件数超过上限（{MaximumFiles}）。");
            }
        }

        SortFiles(project);
        return project;
    }

    private static async Task<ResourcePackProject> ImportZipAsync(string source, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read);
        string prefix = DetectRootPrefix(archive);
        ResourcePackProject project = new()
        {
            Name = Path.GetFileNameWithoutExtension(source),
            Root = source,
        };

        long total = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/') || string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            string name = CleanArchivePath(entry.FullName);
            if (prefix.Length > 0)
            {
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                name = name[prefix.Length..];
                if (name.Length == 0)
                {
                    continue;
                }
            }

            long limit = name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? MaximumPngBytes : MaximumBinaryBytes;
            if (entry.Length > limit)
            {
                throw new InvalidOperationException($"{name} 超过单文件上限（{limit >> 20} MB）。");
            }

            await using Stream entryStream = entry.Open();
            using MemoryStream buffer = new();
            byte[] chunk = new byte[81920];
            long readTotal = 0;
            while (true)
            {
                int read = await entryStream.ReadAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                readTotal += read;
                if (readTotal > limit)
                {
                    throw new InvalidOperationException($"{name} 解压数据超过单文件上限，疑似 Zip 炸弹。");
                }

                total += read;
                if (total > MaximumTotalBytes)
                {
                    throw new InvalidOperationException($"资源包总体积超过上限（{MaximumTotalBytes >> 20} MB）。");
                }

                buffer.Write(chunk, 0, read);
            }

            // 声明长度与实际解压长度必须一致，防止伪造小 Length 的 Zip 炸弹
            if (readTotal != entry.Length)
            {
                throw new InvalidOperationException($"{name} 解压长度与声明不一致。");
            }

            project.Files.Add(BuildImportedFile(name, buffer.ToArray()));
            if (project.Files.Count > MaximumFiles)
            {
                throw new InvalidOperationException($"文件数超过上限（{MaximumFiles}）。");
            }
        }

        SortFiles(project);
        return project;
    }

    private static string DetectRootPrefix(ZipArchive archive)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> topLevel = new(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string cleaned;
            try
            {
                cleaned = CleanArchivePath(entry.FullName);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            names.Add(cleaned);
            int index = cleaned.IndexOf('/');
            if (index > 0)
            {
                topLevel.Add(cleaned[..(index + 1)]);
            }
        }

        if (names.Contains(PackMetaName))
        {
            return string.Empty;
        }

        string? candidate = null;
        int found = 0;
        foreach (string prefix in topLevel)
        {
            if (names.Contains(prefix + PackMetaName))
            {
                candidate = prefix;
                found++;
            }
        }

        return found == 1 ? candidate! : string.Empty;
    }

    private static ResourcePackFile BuildImportedFile(string path, byte[] data)
    {
        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
            data.Length >= PngMagic.Length &&
            data.AsSpan(0, PngMagic.Length).SequenceEqual(PngMagic))
        {
            return new ResourcePackFile
            {
                Path = path,
                Kind = "png",
                PngBase64 = Convert.ToBase64String(data),
            };
        }

        string extension = Path.GetExtension(path);
        if (TextExtensions.Contains(extension) || (IsUtf8(data) && !LooksBinary(data)))
        {
            return new ResourcePackFile
            {
                Path = path,
                Kind = "text",
                Text = Encoding.UTF8.GetString(data),
            };
        }

        return new ResourcePackFile
        {
            Path = path,
            Kind = "binary",
            Base64 = Convert.ToBase64String(data),
        };
    }

    private static bool IsUtf8(byte[] data)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetString(data);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool LooksBinary(byte[] data)
    {
        int length = Math.Min(data.Length, 8000);
        for (int index = 0; index < length; index++)
        {
            if (data[index] == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] DecodeFilePayload(ResourcePackFile file)
    {
        if (!string.IsNullOrWhiteSpace(file.PngBase64))
        {
            return DecodePng(file.Path, file.PngBase64);
        }

        if (!string.IsNullOrWhiteSpace(file.Base64))
        {
            byte[] data = DecodeBase64(file.Base64);
            EnsureSize(file.Path, data.Length, MaximumBinaryBytes, "二进制");
            return data;
        }

        byte[] text = Encoding.UTF8.GetBytes(file.Text ?? string.Empty);
        EnsureSize(file.Path, text.Length, MaximumTextBytes, "文本");
        return text;
    }

    private static byte[] DecodePng(string path, string encoded)
    {
        byte[] data = DecodeBase64(encoded);
        if (data.Length > MaximumPngBytes)
        {
            throw new InvalidOperationException($"贴图超过上限（{MaximumPngBytes >> 20} MB）：{path}");
        }

        if (data.Length < PngMagic.Length || !data.AsSpan(0, PngMagic.Length).SequenceEqual(PngMagic))
        {
            throw new InvalidOperationException("数据不是 PNG 图片：" + path);
        }

        return data;
    }

    private static byte[] DecodeBase64(string encoded)
    {
        string data = encoded.Trim();
        int separator = data.IndexOf(',');
        if (separator >= 0 && data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            data = data[(separator + 1)..];
        }

        try
        {
            return Convert.FromBase64String(data);
        }
        catch (FormatException e)
        {
            throw new InvalidOperationException("base64 解码失败：" + e.Message, e);
        }
    }

    private static void EnsureSize(string path, long size, long limit, string kind)
    {
        if (size > limit)
        {
            throw new InvalidOperationException($"{path} {kind}内容超过上限（{limit >> 20} MB）。");
        }
    }

    private static void Upsert(ResourcePackProject project, ResourcePackFile file)
    {
        int index = project.Files.FindIndex(existing =>
            string.Equals(existing.Path, file.Path, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            project.Files[index] = file;
        }
        else
        {
            if (project.Files.Count >= MaximumFiles)
            {
                throw new InvalidOperationException($"文件数超过上限（{MaximumFiles}）。");
            }

            project.Files.Add(file);
        }
    }

    private static string ResolveTargetPath(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new InvalidOperationException("未选择保存位置。");
        }

        string target = Path.GetFullPath(targetPath.Trim());
        if (!target.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            target += ".zip";
        }

        return target;
    }

    private static byte[] CreateCheckerPng(int size, uint rgb)
    {
        byte r = (byte)(rgb >> 16);
        byte g = (byte)(rgb >> 8);
        byte b = (byte)rgb;
        byte darkR = (byte)Math.Clamp((int)(r * 0.55), 0, 255);
        byte darkG = (byte)Math.Clamp((int)(g * 0.55), 0, 255);
        byte darkB = (byte)Math.Clamp((int)(b * 0.55), 0, 255);

        int block = Math.Max(4, size / 8);
        using MemoryStream raw = new(size * (size * 3 + 1));
        for (int y = 0; y < size; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < size; x++)
            {
                bool light = ((x / block) + (y / block)) % 2 == 0;
                raw.WriteByte(light ? r : darkR);
                raw.WriteByte(light ? g : darkG);
                raw.WriteByte(light ? b : darkB);
            }
        }

        using MemoryStream compressed = new();
        using (ZLibStream zlib = new(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(zlib);
        }

        using MemoryStream png = new();
        png.Write(PngMagic);
        byte[] header = new byte[13];
        WriteBigEndian(header, 0, (uint)size);
        WriteBigEndian(header, 4, (uint)size);
        header[8] = 8;
        header[9] = 2;
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream target, string type, byte[] data)
    {
        byte[] length = new byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        target.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        target.Write(typeBytes);
        target.Write(data);

        byte[] crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, typeBytes.Length);
        byte[] crc = new byte[4];
        WriteBigEndian(crc, 0, Crc32(crcInput));
        target.Write(crc);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint Crc32(byte[] data)
    {
        uint[] table = _crcTable ??= BuildCrcTable();
        uint crc = 0xFFFFFFFFu;
        foreach (byte value in data)
        {
            crc = table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint index = 0; index < 256; index++)
        {
            uint value = index;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }
}

public class ResourcePackFile
{
    public string Path { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string PngBase64 { get; set; } = string.Empty;

    public string Base64 { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
}

public class ResourcePackProject
{
    public string Name { get; set; } = string.Empty;

    public string Root { get; set; } = string.Empty;

    public List<ResourcePackFile> Files { get; set; } = [];

    public int FileCount => Files.Count;
}

public class ResourcePackDraft
{
    public bool Found { get; set; }

    public string Name { get; set; } = string.Empty;

    public string SavedAt { get; set; } = string.Empty;

    public List<ResourcePackFile> Files { get; set; } = [];
}

public class ResourcePackMeta
{
    [JsonPropertyName("pack")] public ResourcePackMetaPack Pack { get; set; } = new();
}

public class ResourcePackMetaPack
{
    [JsonPropertyName("pack_format")] public int PackFormat { get; set; } = 34;

    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;

    [JsonPropertyName("supported_formats")] public ResourcePackMetaFormats? SupportedFormats { get; set; }
}

public class ResourcePackMetaFormats
{
    [JsonPropertyName("min_inclusive")] public int MinInclusive { get; set; }

    [JsonPropertyName("max_inclusive")] public int MaxInclusive { get; set; }
}
