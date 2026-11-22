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

using MofuCore.Config;
namespace MofuCore.Download;

public sealed record FastDownloadItem(
    string Url,
    string TargetPath,
    long Size = 0,
    string? Sha1 = null,
    IReadOnlyList<string>? FallbackUrls = null);

public sealed record FastDownloadProgress(
    int CompletedFiles,
    int TotalFiles,
    long ReceivedBytes,
    long TotalBytes);

public static class FastDownloader
{
    private const int BufferSize = 128 * 1024;
    private const long MinSegmentedSize = SizeLimits.SegmentedDownloadMinimumBytes;
    private const int DefaultAttempts = 3;

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 1024,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = NetworkDefaults.ConnectTimeout,
            AllowAutoRedirect = true,
        };

        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MofumofuLauncher/1.0 (fast-downloader)");
        return client;
    }

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

                // 每次尝试单独兜底超时，避免只依赖外部 CancellationToken 时连接永久挂起
                using CancellationTokenSource attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(NetworkDefaults.DownloadHttpTimeout);

                // 本次尝试累计的字节；失败或分片回退时按同一数值扣回，保证增减同源
                long attemptBytes = 0;
                Action<long>? countedOnBytes = onBytes is null
                    ? null
                    : delta =>
                    {
                        Interlocked.Add(ref attemptBytes, delta);
                        onBytes(delta);
                    };

                try
                {
                    var segmented = item.Size >= MinSegmentedSize &&
                                    segments > 1 &&
                                    await TryDownloadSegmentedAsync(
                                        url, item.TargetPath, item.Size, segments, countedOnBytes, attemptCts.Token)
                                        .ConfigureAwait(false);

                    if (!segmented)
                    {
                        // 分段不可用回退单连接：先扣回分段阶段已计入的字节，避免重复累计
                        RollbackAttemptProgress(onBytes, ref attemptBytes);
                        await DownloadSingleAsync(url, item.TargetPath, countedOnBytes, attemptCts.Token)
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
                        !await MatchesSha1Async(item.TargetPath, item.Sha1!, attemptCts.Token).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException($"下载文件校验失败：{item.TargetPath}");
                    }

                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    RollbackAttemptProgress(onBytes, ref attemptBytes);
                    DeleteFile(item.TargetPath);
                    throw;
                }
                catch (Exception error)
                {
                    lastError = error;
                    DeleteFile(item.TargetPath);
                    RollbackAttemptProgress(onBytes, ref attemptBytes);
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

        // 只判断 206 不够：服务器可能忽略 Range 返回整包或错位分片。
        // 核对 Content-Range 起止与请求一致，并在读取后核对实际分片长度。
        long expected = end - start + 1;
        ContentRangeHeaderValue? contentRange = response.Content.Headers.ContentRange;
        if (contentRange is null || !contentRange.HasRange ||
            contentRange.From != start || contentRange.To != end)
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
        long written = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;

            written += read;
            if (written > expected)
            {
                throw new InvalidOperationException($"分片 {start}-{end} 返回内容超过请求范围。");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            onBytes?.Invoke(read);
        }

        if (written != expected)
        {
            throw new InvalidOperationException(
                $"分片 {start}-{end} 实际长度与 Content-Range 不符（{written} != {expected}）。");
        }
    }

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

    /// <summary>把本次尝试已计入的进度按同一数值扣回，避免重试 / 回退后进度超过 100%。</summary>
    private static void RollbackAttemptProgress(Action<long>? onBytes, ref long attemptBytes)
    {
        long accrued = Interlocked.Exchange(ref attemptBytes, 0);
        if (accrued > 0) onBytes?.Invoke(-accrued);
    }

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    private sealed class RangeNotSupportedException : Exception { }
}
