using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace Mofumofu.Launcher.Services.Music;

/// <summary>
/// 封面加载：网易云用专辑图 URL 下载，本地文件从音频内嵌封面提取。
/// 按 key 缓存解码结果，同一个封面只加载一次。
/// </summary>
public static class CoverCache
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> Cache = new();

    public static Task<Bitmap?> GetAsync(MusicItem item)
    {
        string key = item.IsCloud
            ? "net:" + item.CoverUrl
            : "local:" + item.LocalPath.ToLowerInvariant();

        if (Cache.TryGetValue(key, out Task<Bitmap?>? existing))
        {
            return existing;
        }

        Task<Bitmap?> task = LoadAsync(item);
        Cache[key] = task;
        return task;
    }

    private static async Task<Bitmap?> LoadAsync(MusicItem item)
    {
        try
        {
            byte[]? data = item.IsCloud
                ? await DownloadAsync(item.CoverUrl).ConfigureAwait(false)
                : await Task.Run(() => LocalCoverExtractor.Extract(item.LocalPath)).ConfigureAwait(false);

            if (data is { Length: > 0 })
            {
                using var stream = new MemoryStream(data);
                return new Bitmap(stream);
            }
        }
        catch (Exception)
        {
            // 没有封面就退回占位图标
        }

        return null;
    }

    private static async Task<byte[]?> DownloadAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // 网易图床会按请求头拦截，带上 UA 就够了
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        using HttpResponseMessage response = await Http.SendAsync(request).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// 本地音频内嵌封面提取：MP3 的 ID3v2 APIC 与 FLAC 的 PICTURE 块。
/// 不依赖第三方标签库，解析失败返回 null。
/// </summary>
public static class LocalCoverExtractor
{
    /// <summary>把流读满 buffer；文件提前结束返回 false。</summary>
    private static bool ReadExact(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read <= 0) return false;
            total += read;
        }

        return true;
    }

    public static byte[]? Extract(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            using FileStream stream = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            if (!ReadExact(stream, magic)) return null;
            stream.Position = 0;

            return magic[0] == (byte)'I' && magic[1] == (byte)'D' && magic[2] == (byte)'3'
                ? ExtractId3(stream)
                : magic.SequenceEqual("fLaC"u8) ? ExtractFlac(stream)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------- MP3 / ID3v2 ----------

    private static byte[]? ExtractId3(FileStream stream)
    {
        Span<byte> header = stackalloc byte[10];
        if (!ReadExact(stream, header)) return null;

        int version = header[3];
        int tagSize = SyncSafe(header[6], header[7], header[8], header[9]);
        long tagEnd = Math.Min(10L + tagSize, stream.Length);

        // v2.2 帧头只有 6 字节且帧 ID 3 字符，比较少见，直接跳过
        if (version <= 2) return null;

        long position = 10;
        Span<byte> frameHeader = stackalloc byte[10];
        while (position + 10 <= tagEnd)
        {
            stream.Position = position;
            if (!ReadExact(stream, frameHeader)) return null;
            if (frameHeader[0] == 0) return null; // padding

            string id = System.Text.Encoding.Latin1.GetString(frameHeader[..4]);
            int frameSize = version >= 4
                ? SyncSafe(frameHeader[4], frameHeader[5], frameHeader[6], frameHeader[7])
                : (frameHeader[4] << 24) | (frameHeader[5] << 16) | (frameHeader[6] << 8) | frameHeader[7];
            if (frameSize <= 0 || position + 10 + frameSize > tagEnd) return null;

            if (id == "APIC")
            {
                byte[] data = new byte[frameSize];
                if (!ReadExact(stream, data)) return null;
                return ParseApic(data);
            }

            position += 10 + frameSize;
        }

        return null;
    }

    /// <summary>APIC 帧：编码字节 + MIME(拉丁1, 0 结尾) + 图片类型 + 描述 + 图片数据。</summary>
    private static byte[]? ParseApic(byte[] data)
    {
        int offset = 1;
        int mimeEnd = Array.IndexOf(data, (byte)0, offset);
        if (mimeEnd < 0) return null;

        string mime = System.Text.Encoding.Latin1.GetString(data, offset, mimeEnd - offset);
        offset = mimeEnd + 1; // 图片类型 1 字节

        byte encoding = data[0];
        if (encoding is 1 or 2)
        {
            // UTF-16 描述：找双 0 结尾（对齐 2 字节）
            while (offset + 1 < data.Length && (data[offset] != 0 || data[offset + 1] != 0))
            {
                offset += 2;
            }
            offset += 2;
        }
        else
        {
            int descEnd = Array.IndexOf(data, (byte)0, offset);
            if (descEnd < 0) return null;
            offset = descEnd + 1;
        }

        if (offset >= data.Length) return null;

        byte[] image = new byte[data.Length - offset];
        Array.Copy(data, offset, image, 0, image.Length);
        return IsSupportedImage(mime, image) ? image : null;
    }

    private static int SyncSafe(byte a, byte b, byte c, byte d) =>
        (a << 21) | (b << 14) | (c << 7) | d;

    // ---------- FLAC ----------

    private static byte[]? ExtractFlac(FileStream stream)
    {
        stream.Position = 4; // 跳过 "fLaC"
        Span<byte> blockHeader = stackalloc byte[4];
        while (ReadExact(stream, blockHeader))
        {
            bool last = (blockHeader[0] & 0x80) != 0;
            int type = blockHeader[0] & 0x7F;
            int length = (blockHeader[1] << 16) | (blockHeader[2] << 8) | blockHeader[3];
            long next = stream.Position + length;
            if (next > stream.Length) return null;

            if (type == 6)
            {
                byte[] block = new byte[length];
                if (!ReadExact(stream, block)) return null;
                return ParseFlacPicture(block);
            }

            stream.Position = next;
            if (last) return null;
        }

        return null;
    }

    /// <summary>PICTURE 块：类型(4) + MIME 长度/MIME + 描述 + 16 字节宽高色深 + 数据长度 + 数据。</summary>
    private static byte[]? ParseFlacPicture(byte[] data)
    {
        int offset = 4;
        if (!ReadInt32(data, ref offset, out int mimeLength) || mimeLength <= 0) return null;
        string mime = System.Text.Encoding.Latin1.GetString(data, offset, mimeLength);
        offset += mimeLength;

        if (!ReadInt32(data, ref offset, out int descLength)) return null;
        offset += descLength + 16; // 描述 + 宽/高/色深/色数

        if (!ReadInt32(data, ref offset, out int dataLength) || dataLength <= 0 ||
            offset + dataLength > data.Length)
        {
            return null;
        }

        byte[] image = new byte[dataLength];
        Array.Copy(data, offset, image, 0, dataLength);
        return IsSupportedImage(mime, image) ? image : null;
    }

    private static bool ReadInt32(byte[] data, ref int offset, out int value)
    {
        value = 0;
        if (offset + 4 > data.Length) return false;
        value = (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
        offset += 4;
        return true;
    }

    private static bool IsSupportedImage(string mime, byte[] data)
    {
        if (data.Length < 8) return false;
        bool jpeg = data[0] == 0xFF && data[1] == 0xD8;
        bool png = data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G';
        return jpeg || png || mime.Contains("jpeg", StringComparison.OrdinalIgnoreCase) ||
               mime.Contains("png", StringComparison.OrdinalIgnoreCase);
    }
}
