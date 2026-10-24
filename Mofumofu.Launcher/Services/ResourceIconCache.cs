using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace Mofumofu.Launcher.Services;

/// <summary>
/// 资源图标缓存（Modrinth 图标 URL / 本地图片路径）：
/// 内存 LRU（≤256）+ 磁盘 PNG 缓存（<c>%LOCALAPPDATA%\Mofumofu.Launcher\icons\&lt;sha1(key)&gt;.png</c>），
/// 同一个 key 并发只加载一次，10 秒超时，任何失败都返回 null（不抛）。
/// 只接受 http/https 与本地文件路径，其它 scheme（avares://、gameicon: 等）直接忽略。
/// </summary>
public static class ResourceIconCache
{
    private const int MemoryCapacity = 256;
    private const int MaxDownloadBytes = 16 * 1024 * 1024;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mofumofu.Launcher",
        "icons");

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly object Gate = new();

    /// <summary>key → 内存缓存节点（LRU 链表里同时保存值，方便 O(1) 移动）。</summary>
    private static readonly Dictionary<string, LinkedListNode<CacheEntry>> Memory = new(StringComparer.Ordinal);

    private static readonly LinkedList<CacheEntry> Lru = new();

    /// <summary>同 key 正在进行的加载任务，用于并发去重。</summary>
    private static readonly Dictionary<string, Task<Bitmap?>> InFlight = new(StringComparer.Ordinal);

    /// <summary>清空代际：Clear 之后旧请求即使完成也不再写回缓存。</summary>
    private static int _generation;

    private sealed record CacheEntry(string Key, Bitmap Bitmap);

    /// <summary>
    /// 取图标：<paramref name="source"/> 可为 https 图标地址或本地图片路径，
    /// <paramref name="key"/> 是缓存键（建议用 <c>modrinth:&lt;projectId&gt;</c> / 实例 id 这类稳定值）。
    /// 失败、超时、非法 source 都返回 null。
    /// </summary>
    public static async Task<Bitmap?> LoadAsync(string? source, string key, CancellationToken cancellationToken = default)
    {
        var cacheKey = NormalizeKey(source, key);
        if (cacheKey is null) return null;

        lock (Gate)
        {
            if (TryTakeNode(cacheKey) is { } node)
            {
                return node.Value.Bitmap;
            }
        }

        Task<Bitmap?> shared;
        int generation;
        lock (Gate)
        {
            if (TryTakeNode(cacheKey) is { } node)
            {
                return node.Value.Bitmap;
            }

            generation = _generation;
            if (!InFlight.TryGetValue(cacheKey, out shared!))
            {
                shared = LoadSharedAsync(source, cacheKey, generation);
                InFlight[cacheKey] = shared;
            }
        }

        try
        {
            // 这里只取消「等待」，不取消共享的下载任务：同 key 的其它调用还能拿到结果
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

    /// <summary>清空内存缓存并删除磁盘图标缓存目录（失败忽略）。</summary>
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
            if (Directory.Exists(CacheDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(CacheDirectory))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // 单个文件被占用时跳过
                    }
                }
            }
        }
        catch
        {
            // 缓存目录不可写时忽略
        }
    }

    // ---- 加载 ----

    private static async Task<Bitmap?> LoadSharedAsync(string? source, string cacheKey, int generation)
    {
        try
        {
            var diskPath = Path.Combine(CacheDirectory, Sha1(cacheKey) + ".png");

            // 1. 磁盘缓存
            if (File.Exists(diskPath))
            {
                var cached = await DecodeAsync(diskPath).ConfigureAwait(false);
                if (cached is not null)
                {
                    AddMemory(cacheKey, cached, generation);
                    return cached;
                }
            }

            // 2. 下载 / 读本地文件
            var bytes = await FetchAsync(source).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0) return null;

            var bitmap = Decode(bytes);
            if (bitmap is null) return null;

            // 3. 原子写回磁盘缓存
            TryStore(diskPath, bytes);

            AddMemory(cacheKey, bitmap, generation);
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
                InFlight.Remove(cacheKey);
            }
        }
    }

    private static async Task<byte[]?> FetchAsync(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;

        var trimmed = source.Trim();

        // 本地文件路径（含 file:// URI）
        if (TryResolveLocalPath(trimmed) is { } localPath)
        {
            try
            {
                var info = new FileInfo(localPath);
                if (!info.Exists || info.Length <= 0 || info.Length > MaxDownloadBytes) return null;
                return await File.ReadAllBytesAsync(localPath).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        // 网络地址：只认 http/https
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;

        using var timeout = new CancellationTokenSource(Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentLength is { } length && length > MaxDownloadBytes) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false);
                if (read <= 0) break;
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxDownloadBytes) return null;
            }

            return buffer.Length == 0 ? null : buffer.ToArray();
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

    private static string? TryResolveLocalPath(string source)
    {
        string path = source;
        if (source.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(source, UriKind.Absolute, out var fileUri)) return null;
            path = fileUri.LocalPath;
        }

        try
        {
            if (!Path.IsPathRooted(path)) return null;
            return File.Exists(path) ? Path.GetFullPath(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<Bitmap?> DecodeAsync(string path)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            return Decode(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private static void TryStore(string diskPath, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            var temp = diskPath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, diskPath, overwrite: true);
        }
        catch
        {
            // 磁盘缓存失败不影响本次显示
        }
    }

    // ---- 内存 LRU ----

    private static LinkedListNode<CacheEntry>? TryTakeNode(string key)
    {
        if (!Memory.TryGetValue(key, out var node)) return null;

        Lru.Remove(node);
        Lru.AddFirst(node);
        return node;
    }

    private static void AddMemory(string key, Bitmap bitmap, int generation)
    {
        lock (Gate)
        {
            // Clear() 之后返回的旧请求不再写回缓存
            if (generation != _generation) return;

            if (Memory.TryGetValue(key, out var existing))
            {
                Lru.Remove(existing);
                Lru.AddFirst(existing);
                return;
            }

            var node = Lru.AddFirst(new CacheEntry(key, bitmap));
            Memory[key] = node;

            while (Memory.Count > MemoryCapacity && Lru.Last is { } last)
            {
                Lru.RemoveLast();
                Memory.Remove(last.Value.Key);
            }
        }
    }

    // ---- 工具 ----

    private static string? NormalizeKey(string? source, string key)
    {
        if (!string.IsNullOrWhiteSpace(key)) return key.Trim();
        if (!string.IsNullOrWhiteSpace(source)) return source.Trim();
        return null;
    }

    private static string Sha1(string value)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
        var client = new HttpClient(handler) { Timeout = Timeout };
        try
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mofumofu.Launcher/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("image/*");
        }
        catch
        {
            // 头部设置失败不影响使用
        }

        return client;
    }
}
