using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MofuCore.Download;

/// <summary>一个待下载文件：支持多个备用 URL 与可选的 SHA1 校验。</summary>
public sealed record FastDownloadItem(
    string Url,
    string TargetPath,
    long Size = 0,
    string? Sha1 = null,
    IReadOnlyList<string>? FallbackUrls = null);

/// <summary>批量下载进度（已接收字节 / 预估总字节）。</summary>
public sealed record FastDownloadProgress(
    int CompletedFiles,
    int TotalFiles,
    long ReceivedBytes,
    long TotalBytes);

/// <summary>
/// 高速下载器：多文件并行 + 大文件多连接分段（HTTP Range）下载。
/// 专为 Minecraft 的 library / jar / 资源文件这类小文件多、单文件大的场景设计。
/// </summary>
public static class FastDownloader
{
    private const int BufferSize = 128 * 1024;
    private const long MinSegmentedSize = 8L * 1024 * 1024;
    private const int DefaultAttempts = 3;

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 1024,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(6),
            AllowAutoRedirect = true,
        };

        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MofumofuLauncher/1.0 (fast-downloader)");
        return client;
    }

    /// <summary>
    /// 批量下载多个文件：文件之间并行，单个大文件内部再按 Range 分段并行。
    /// </summary>
    /// <param name="fileParallelism">同时下载的文件数，默认 8。</param>
    /// <param name="segments">单个大文件的分段连接数，默认 4。</param>
    public static async Task DownloadAllAsync(
        IEnumerable<FastDownloadItem> items,
        int fileParallelism = 32,
        int segments = 4,
        IProgress<FastDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        List<FastDownloadItem> list = items
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Url))
            .ToList();

        if (list.Count == 0) return;

        long totalBytes = list.Sum(item => Math.Max(0, item.Size));
        long receivedBytes = 0;
        var completedFiles = 0;

        void Report() => progress?.Report(new FastDownloadProgress(
            Volatile.Read(ref completedFiles),
            list.Count,
            Interlocked.Read(ref receivedBytes),
            totalBytes));

        Report();

        using var gate = new SemaphoreSlim(Math.Max(1, fileParallelism));
        var tasks = list.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await DownloadFileAsync(item, segments, delta =>
                {
                    Interlocked.Add(ref receivedBytes, delta);
                    Report();
                }, cancellationToken).ConfigureAwait(false);

                Interlocked.Increment(ref completedFiles);
                Report();
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>下载单个文件；大文件自动分段并行下载，失败会尝试备用 URL 并重试。</summary>
    public static async Task DownloadFileAsync(
        FastDownloadItem item,
        int segments,
        Action<long>? onBytes,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(item.TargetPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        List<string> urls = [item.Url];
        if (item.FallbackUrls is not null)
        {
            foreach (string url in item.FallbackUrls)
            {
                if (!string.IsNullOrWhiteSpace(url) &&
                    !urls.Contains(url, StringComparer.OrdinalIgnoreCase))
                {
                    urls.Add(url);
                }
            }
        }

        Exception? lastError = null;

        foreach (string url in urls)
        {
            for (var attempt = 1; attempt <= DefaultAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DeleteFile(item.TargetPath);

                try
                {
                    var segmented = item.Size >= MinSegmentedSize &&
                                    segments > 1 &&
                                    await TryDownloadSegmentedAsync(
                                        url, item.TargetPath, item.Size, segments, onBytes, cancellationToken)
                                        .ConfigureAwait(false);

                    if (!segmented)
                    {
                        await DownloadSingleAsync(url, item.TargetPath, onBytes, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (item.Size > 0)
                    {
                        long actual = new FileInfo(item.TargetPath).Length;
                        if (actual != item.Size)
                        {
                            throw new InvalidOperationException(
                                $"下载文件大小不匹配：{item.TargetPath}（{actual} != {item.Size}）");
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(item.Sha1) &&
                        !await MatchesSha1Async(item.TargetPath, item.Sha1!, cancellationToken).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException($"下载文件校验失败：{item.TargetPath}");
                    }

                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    DeleteFile(item.TargetPath);
                    throw;
                }
                catch (Exception error)
                {
                    lastError = error;
                    DeleteFile(item.TargetPath);
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                }
            }
        }

        throw new InvalidOperationException($"下载失败：{item.TargetPath}", lastError);
    }

    // ------------------------------------------------------------------
    // 单连接下载
    // ------------------------------------------------------------------

    private static async Task DownloadSingleAsync(
        string url,
        string targetPath,
        Action<long>? onBytes,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await Http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using Stream source = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        await using FileStream destination = new(
            targetPath, FileMode.Create, FileAccess.Write, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        byte[] buffer = new byte[BufferSize];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            onBytes?.Invoke(read);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // 多连接分段下载
    // ------------------------------------------------------------------

    private static async Task<bool> TryDownloadSegmentedAsync(
        string url,
        string targetPath,
        long size,
        int segments,
        Action<long>? onBytes,
        CancellationToken cancellationToken)
    {
        if (size <= 0) return false;

        segments = Math.Clamp(segments, 2, 8);
        long segmentSize = Math.Max(1, size / segments);
        int actualSegments = (int)Math.Min(segments, (size + segmentSize - 1) / segmentSize);

        var partPaths = new string[actualSegments];
        var tasks = new Task[actualSegments];

        try
        {
            for (var i = 0; i < actualSegments; i++)
            {
                long start = i * segmentSize;
                long end = i == actualSegments - 1 ? size - 1 : (i + 1) * segmentSize - 1;
                string partPath = targetPath + $".part{i}";
                partPaths[i] = partPath;

                int index = i;
                tasks[i] = DownloadRangeAsync(url, partPath, start, end, onBytes, cancellationToken);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (RangeNotSupportedException)
        {
            foreach (string path in partPaths) DeleteFile(path);
            return false;
        }
        catch
        {
            foreach (string path in partPaths) DeleteFile(path);
            throw;
        }

        try
        {
            await using (FileStream destination = new(
                             targetPath, FileMode.Create, FileAccess.Write, FileShare.None,
                             BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[BufferSize];
                foreach (string partPath in partPaths)
                {
                    await using FileStream part = new(
                        partPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    int read;
                    while ((read = await part.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }

            return true;
        }
        finally
        {
            foreach (string path in partPaths) DeleteFile(path);
        }
    }

    private static async Task DownloadRangeAsync(
        string url,
        string partPath,
        long start,
        long end,
        Action<long>? onBytes,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(start, end);

        using HttpResponseMessage response = await Http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new RangeNotSupportedException();
        }

        await using Stream source = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        await using FileStream destination = new(
            partPath, FileMode.Create, FileAccess.Write, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        byte[] buffer = new byte[BufferSize];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            onBytes?.Invoke(read);
        }
    }

    // ------------------------------------------------------------------
    // 工具
    // ------------------------------------------------------------------

    private static async Task<bool> MatchesSha1Async(
        string path,
        string expectedSha1,
        CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = new(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

            byte[] hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            string actual = Convert.ToHexString(hash);
            return string.Equals(actual, expectedSha1.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 删除失败不影响下载结果
        }
    }

    private sealed class RangeNotSupportedException : Exception { }
}
