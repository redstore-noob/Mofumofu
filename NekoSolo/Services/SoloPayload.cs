using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace NekoSolo.Services;

/// <summary>
/// NekoSolo 载荷读取（格式见 NekoLauncher/NekoSolo/FORMAT.md）：
/// exe = 安装器本体字节 + 载荷 zip + 32 字节尾标。
/// 尾标（小端）：0..8 魔数 "NKSOLO1\x01"、8..16 偏移、16..24 长度、24..28 CRC32、28..32 保留。
/// </summary>
public static class SoloPayload
{
    public const int TrailerSize = 32;
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
            if (offset <= 0 || length <= 0 || offset + length > stream.Length - TrailerSize)
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

        return (crc ^ 0xFFFFFFFFu) == info.Crc;
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
        ZipArchiveEntry? entry = archive.GetEntry("manifest.json")
                                 ?? archive.Entries.FirstOrDefault(item =>
                                     string.Equals(Path.GetFileName(item.FullName), "manifest.json",
                                         StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        using StreamReader reader = new(entry.Open(), Encoding.UTF8);
        return SoloManifest.FromJson(reader.ReadToEnd());
    }

    /// <summary>把载荷里的某个前缀目录解压到目标目录（files/ 与 minecraft/、jre/ 规则不同，由调用方决定）。</summary>
    public static long ExtractPrefix(SoloPayloadInfo info, ZipArchive archive, string prefix, string targetRoot,
        Action<string>? log, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        string normalized = prefix.EndsWith('/') ? prefix : prefix + "/";
        List<ZipArchiveEntry> entries = archive.Entries
            .Where(entry => entry.FullName.StartsWith(normalized, StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrEmpty(entry.Name))
            .ToList();
        if (entries.Count == 0) return 0;

        long total = entries.Sum(entry => entry.Length);
        long written = 0;
        long count = 0;
        foreach (ZipArchiveEntry entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = entry.FullName[normalized.Length..].Replace('/', Path.DirectorySeparatorChar);
            string destination = SafeCombine(targetRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
            written += entry.Length;
            count++;
            log?.Invoke(relative);
            progress?.Report(total <= 0 ? 1 : written / (double)total);
        }

        return count;
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
        string rootFull = Path.GetFullPath(root);
        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
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
internal sealed class BoundedStream(Stream inner, long length) : Stream
{
    private long _remaining = length;

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
