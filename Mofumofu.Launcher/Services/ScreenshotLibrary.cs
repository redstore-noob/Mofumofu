using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

using MofuCore.Config;
namespace Mofumofu.Launcher.Services;

public static class ScreenshotLibrary
{
    private const int MemoryCapacity = 192;

    private const long MaxSourceBytes = SizeLimits.ScreenshotMaximumBytes;

    private const int MinWidth = 48;
    private const int MaxWidth = 1600;

    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mofumofu.Launcher",
        "screenshot-thumbs");

    private static readonly object Gate = new();

    /// <summary>清空代际：Clear 之后仍在进行的旧加载任务不再写回缓存，也不误删新的去重项。</summary>
    private static int _generation;

    private static readonly Dictionary<string, LinkedListNode<CacheEntry>> Memory = new(StringComparer.Ordinal);

    private static readonly LinkedList<CacheEntry> Lru = new();

    private static readonly Dictionary<string, Task<Bitmap?>> InFlight = new(StringComparer.Ordinal);

    private sealed record CacheEntry(string Key, Bitmap Bitmap);

    public static async Task<Bitmap?> LoadThumbnailAsync(
        string? path,
        int width,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        width = Math.Clamp(width, MinWidth, MaxWidth);

        string cacheKey;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxSourceBytes) return null;

            cacheKey = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{info.FullName}|{info.LastWriteTimeUtc.Ticks}|{info.Length}|{width}");
        }
        catch
        {
            return null;
        }

        Task<Bitmap?> shared;
        lock (Gate)
        {
            if (TryTakeNode(cacheKey) is { } node) return node.Value.Bitmap;

            if (!InFlight.TryGetValue(cacheKey, out shared!))
            {
                int generation = _generation;
                shared = LoadSharedAsync(path, cacheKey, width, generation);
                InFlight[cacheKey] = shared;
            }
        }

        try
        {
            return await shared.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    public static Bitmap? Peek(string? path, int width)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;

            var cacheKey = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{info.FullName}|{info.LastWriteTimeUtc.Ticks}|{info.Length}|{Math.Clamp(width, MinWidth, MaxWidth)}");

            lock (Gate)
            {
                return TryTakeNode(cacheKey)?.Value.Bitmap;
            }
        }
        catch
        {
            return null;
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            _generation++;
            Memory.Clear();
            Lru.Clear();
            InFlight.Clear();
        }

        try
        {
            if (!Directory.Exists(CacheDirectory)) return;

            foreach (var file in Directory.EnumerateFiles(CacheDirectory))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    public static (int Count, long Bytes) MeasureDiskCache()
    {
        try
        {
            if (!Directory.Exists(CacheDirectory)) return (0, 0);

            int count = 0;
            long bytes = 0;
            foreach (var file in Directory.EnumerateFiles(CacheDirectory))
            {
                try
                {
                    bytes += new FileInfo(file).Length;
                    count++;
                }
                catch
                {
                }
            }

            return (count, bytes);
        }
        catch
        {
            return (0, 0);
        }
    }


    private static async Task<Bitmap?> LoadSharedAsync(string path, string cacheKey, int width, int generation)
    {
        try
        {
            var diskPath = Path.Combine(CacheDirectory, Hash(cacheKey) + ".png");

            if (File.Exists(diskPath))
            {
                var cached = await DecodeFileAsync(diskPath, width).ConfigureAwait(false);
                if (cached is not null)
                {
                    if (!AddMemory(cacheKey, cached, generation)) return null;
                    return cached;
                }
            }

            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            if (bytes.Length == 0) return null;

            Bitmap? bitmap;
            using (var stream = new MemoryStream(bytes, writable: false))
            {
                bitmap = Bitmap.DecodeToWidth(stream, width);
            }

            if (bitmap is null) return null;

            TryStoreThumbnail(diskPath, bitmap);
            if (!AddMemory(cacheKey, bitmap, generation)) return null;
            return bitmap;
        }
        catch
        {
            return null;
        }
        finally
        {
            lock (Gate)
            {
                // 只移除本代次登记的去重项，避免误删 Clear 后新建的同 key 任务
                if (generation == _generation) InFlight.Remove(cacheKey);
            }
        }
    }

    private static async Task<Bitmap?> DecodeFileAsync(string file, int width)
    {
        try
        {
            await using var stream = File.OpenRead(file);
            return Bitmap.DecodeToWidth(stream, width);
        }
        catch
        {
            return null;
        }
    }

    private static void TryStoreThumbnail(string diskPath, Bitmap bitmap)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);

            var temp = diskPath + ".tmp";
            using (var output = File.Create(temp))
            {
                bitmap.Save(output);
            }

            File.Move(temp, diskPath, overwrite: true);
        }
        catch
        {
        }
    }


    private static bool AddMemory(string key, Bitmap bitmap, int generation)
    {
        lock (Gate)
        {
            // Clear() 之后返回的旧请求丢弃结果，不再写回缓存；此时位图尚无外部持有者，及时释放
            if (generation != _generation)
            {
                bitmap.Dispose();
                return false;
            }

            var node = Lru.AddFirst(new CacheEntry(key, bitmap));
            Memory[key] = node;

            while (Lru.Count > MemoryCapacity && Lru.Last is { } last)
            {
                Lru.RemoveLast();
                Memory.Remove(last.Value.Key);
                // 缓存位图可能仍被外部调用方持有，淘汰/清空时不在此 Dispose（见需架构决策）
            }
            return true;
        }
    }

    private static LinkedListNode<CacheEntry>? TryTakeNode(string key)
    {
        if (!Memory.TryGetValue(key, out var node)) return null;
        if (!ReferenceEquals(Lru.First, node)) Lru.Remove(node);
        Lru.AddFirst(node);
        return node;
    }

    private static string Hash(string key)
    {
        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(Encoding.UTF8.GetBytes(key), hash);
        return Convert.ToHexStringLower(hash);
    }
}
