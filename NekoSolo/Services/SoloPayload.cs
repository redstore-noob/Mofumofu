using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace NekoSolo.Services;

/// <summary>
/// NekoSolo 载荷读取（格式见 NekoSolo/FORMAT.md）：
/// exe = 安装器本体字节 + 载荷 zip + 32 字节尾标。
/// 尾标（小端）：0..8 魔数 "NKSOLO1\x01"、8..16 偏移、16..24 长度、24..28 CRC32、28..32 保留。
/// </summary>
public static class SoloPayload
{
    public const int TrailerSize = 32;

    // 外部安装包的统一体积上限；清单读取、条目校验、下载入口共用，避免各处硬编码不同上限形成可绕过旁路。
    internal const long ManifestMaximumBytes = 1L * 1024 * 1024;
    internal const long ArchiveMaximumBytes = 4L * 1024 * 1024 * 1024;
    internal const long EntryMaximumBytes = 2L * 1024 * 1024 * 1024;
    internal const long ExtractedMaximumBytes = 8L * 1024 * 1024 * 1024;
    internal const int MaxExtractEntries = 100_000;
    internal const int CopyBufferBytes = 81920;

    private static readonly byte[] Magic = "NKSOLO1\x01"u8.ToArray();

    public static bool TryRead(string executablePath, out SoloPayloadInfo info, out string error)
    {
        info = default;
        error = string.Empty;
        try
        {
            using var stream = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < TrailerSize)
            {
                error = SoloStrings.T("Error.NoPayload");
                return false;
            }

            byte[] trailer = new byte[TrailerSize];
            stream.Seek(-TrailerSize, SeekOrigin.End);
            if (stream.Read(trailer, 0, TrailerSize) != TrailerSize)
            {
                error = SoloStrings.T("Error.NoPayload");
                return false;
            }

            for (int i = 0; i < Magic.Length; i++)
            {
                if (trailer[i] != Magic[i])
                {
                    error = SoloStrings.T("Error.NoPayload");
                    return false;
                }
            }

            long offset = BitConverter.ToInt64(trailer, 8);
            long length = BitConverter.ToInt64(trailer, 16);
            uint crc = BitConverter.ToUInt32(trailer, 24);
            if (offset <= 0 || length <= 0 || offset > stream.Length - TrailerSize ||
                length > stream.Length - TrailerSize - offset)
            {
                error = SoloStrings.F("Error.BadPayload", "载荷位置越界");
                return false;
            }

            info = new SoloPayloadInfo(executablePath, offset, length, crc);
            return true;
        }
        catch (Exception ex)
        {
            error = SoloStrings.F("Error.BadPayload", ex.Message);
            return false;
        }
    }

    /// <summary>按 CRC32 校验载荷完整性（整包读一遍，大包会慢，进度回调用于 UI）。</summary>
    public static async Task<bool> VerifyAsync(SoloPayloadInfo info, IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        await using FileStream stream = new(info.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            1024 * 1024, true);
        stream.Seek(info.Offset, SeekOrigin.Begin);

        uint[] table = Crc32Table.Value;
        uint crc = 0xFFFFFFFFu;
        byte[] buffer = new byte[1024 * 512];
        long remaining = info.Length;
        long done = 0;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int read = await stream.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;
            for (int i = 0; i < read; i++)
            {
                crc = table[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
            }

            done += read;
            remaining -= read;
            progress?.Report(done / (double)info.Length);
        }

        return remaining == 0 && (crc ^ 0xFFFFFFFFu) == info.Crc;
    }

    public static Stream OpenPayloadStream(SoloPayloadInfo info)
    {
        var stream = new FileStream(info.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            1024 * 1024, true);
        stream.Seek(info.Offset, SeekOrigin.Begin);
        return new BoundedStream(stream, info.Length);
    }

    /// <summary>从载荷 zip 顶层读 manifest.json（不解压整包）。</summary>
    public static SoloManifest? ReadManifest(SoloPayloadInfo info)
    {
        using Stream payload = OpenPayloadStream(info);
        return ReadManifest(payload);
    }

    /// <summary>从任意 zip 流读取顶层 manifest.json（外部 GitHub Releases 安装包也适用）。</summary>
    public static SoloManifest? ReadManifest(Stream zipStream)
    {
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(item =>
            string.Equals(item.FullName, "manifest.json", StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        if (entry.Length > ManifestMaximumBytes)
            throw new InvalidOperationException("manifest.json 体积超过上限：" + entry.Length);

        using Stream data = entry.Open();
        using MemoryStream buffer = new();
        byte[] chunk = new byte[CopyBufferBytes];
        long readTotal = 0;
        int read;
        while ((read = data.Read(chunk, 0, chunk.Length)) > 0)
        {
            readTotal += read;
            if (readTotal > ManifestMaximumBytes)
                throw new InvalidOperationException("manifest.json 体积超过上限");
            buffer.Write(chunk, 0, read);
        }

        string json = Encoding.UTF8.GetString(buffer.ToArray()).TrimStart('\uFEFF');
        return SoloManifest.FromJson(json);
    }

    /// <summary>
    /// 释放/启动之前统一校验 zip：条目路径（防 zip slip）、声明体积上限、解压实际长度。
    /// 读尽条目流会触发 ZipArchive 对条目 CRC32 的校验；任一失败即抛异常，调用方不得继续释放。
    /// </summary>
    internal static void VerifyArchive(ZipArchive archive, Action<string>? log = null)
    {
        long total = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (name.Length == 0 || name[0] == '/' || name.Contains(':') ||
                name.Split('/').Any(part => part is ".." or ".") ||
                name.Any(ch => char.IsControl(ch)))
            {
                throw new InvalidOperationException("安装包条目路径非法：" + entry.FullName);
            }

            if (entry.Length < 0 || entry.Length > EntryMaximumBytes)
            {
                throw new InvalidOperationException("安装包条目体积超限：" + entry.FullName);
            }

            total += entry.Length;
            if (total > ExtractedMaximumBytes)
            {
                throw new InvalidOperationException("安装包解压后总体积超限");
            }

            if (entry.Name.Length == 0) continue; // 目录条目无数据可读

            using Stream data = entry.Open();
            byte[] chunk = new byte[CopyBufferBytes];
            long actual = 0;
            int read;
            while ((read = data.Read(chunk, 0, chunk.Length)) > 0)
            {
                actual += read;
                if (actual > entry.Length || actual > EntryMaximumBytes)
                    throw new InvalidOperationException("安装包条目实际体积超限：" + entry.FullName);
            }

            if (actual != entry.Length)
            {
                throw new InvalidOperationException("安装包条目长度不符（疑似截断或伪造）：" + entry.FullName);
            }
        }

        log?.Invoke($"zip: {archive.Entries.Count} 个条目路径/体积/完整性校验通过");
    }

    /// <summary>把载荷里的某个前缀目录解压到目标目录（files/ 与 minecraft/、jre/ 规则不同，由调用方决定）。</summary>
    public static long ExtractPrefix(SoloPayloadInfo info, ZipArchive archive, string prefix, string targetRoot,
        Action<string>? log, IProgress<double>? progress, CancellationToken cancellationToken = default,
        Action<string>? onWritten = null)
    {
        string normalized = prefix.EndsWith('/') ? prefix : prefix + "/";
        List<ZipArchiveEntry> entries = archive.Entries
            .Where(entry => entry.FullName.StartsWith(normalized, StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrEmpty(entry.Name))
            .ToList();
        if (entries.Count == 0) return 0;

        // public 入口独立设防：条目数、单条体积、累计体积都要在上限内，避免绕过 VerifyArchive 直接调用时被撑爆。
        if (entries.Count > MaxExtractEntries)
            throw new InvalidOperationException("安装包释放条目数超过上限：" + entries.Count);
        long total = 0;
        foreach (ZipArchiveEntry entry in entries)
        {
            if (entry.Length < 0 || entry.Length > EntryMaximumBytes)
                throw new InvalidOperationException("安装包条目体积超限：" + entry.FullName);
            total += entry.Length;
            if (total > ExtractedMaximumBytes)
                throw new InvalidOperationException("安装包解压后总体积超限");
        }

        long written = 0;
        long count = 0;
        var writtenPaths = new List<string>();
        bool completed = false;
        try
        {
            foreach (ZipArchiveEntry entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = entry.FullName[normalized.Length..].Replace('/', Path.DirectorySeparatorChar);
                string destination = SafeCombine(targetRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
                writtenPaths.Add(destination);
                onWritten?.Invoke(destination);
                written += entry.Length;
                count++;
                log?.Invoke(relative);
                progress?.Report(total <= 0 ? 1 : written / (double)total);
            }

            completed = true;
            return count;
        }
        finally
        {
            // 中途失败/取消：删除本次已释放的文件，避免半成品残留与下次安装新旧文件混装。
            if (!completed)
            {
                foreach (string path in writtenPaths)
                {
                    try { if (File.Exists(path)) File.Delete(path); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
    }

    /// <summary>读取载荷里的顶层清单（文件名 + 大小），用于 UI 展示"包里有什么"。</summary>
    public static IReadOnlyList<(string Name, long Length)> ListTopEntries(SoloPayloadInfo info)
    {
        using Stream payload = OpenPayloadStream(info);
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true);
        return archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name) && !entry.FullName.Contains('/'))
            .Select(entry => (entry.FullName, entry.Length))
            .ToList();
    }

    private static string SafeCombine(string root, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(root, relative));
        string rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string rootPrefix = Path.EndsInDirectorySeparator(rootFull) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(full, rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("载荷条目路径越界：" + relative);
        }

        return full;
    }

    private static readonly Lazy<uint[]> Crc32Table = new(() =>
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    });
}

public readonly record struct SoloPayloadInfo(string ExecutablePath, long Offset, long Length, uint Crc)
{
    public double SizeMegabytes => Length / 1024d / 1024d;
}

/// <summary>把文件里的一段（载荷区）包成只读流，供 ZipArchive 顺序读取。</summary>
internal sealed class BoundedStream : Stream
{
    private readonly Stream inner;
    private readonly long length;
    private long _remaining;

    public BoundedStream(Stream inner, long length)
    {
        this.inner = inner;
        this.length = length;
        _remaining = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => length - _remaining;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining <= 0) return 0;
        int want = (int)Math.Min(count, _remaining);
        int read = inner.Read(buffer, offset, want);
        _remaining -= read;
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        if (_remaining <= 0) return 0;
        int want = (int)Math.Min(buffer.Length, _remaining);
        int read = inner.Read(buffer[..want]);
        _remaining -= read;
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
