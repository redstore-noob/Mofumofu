using System;

namespace MofuCore.Config;

/// <summary>网络请求的超时、轮询与并发默认值。</summary>
public static class NetworkDefaults
{
    public static readonly TimeSpan HttpRequestTimeout = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan LongHttpTimeout = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan DownloadHttpTimeout = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(6);

    public static readonly TimeSpan QuickProbeTimeout = TimeSpan.FromSeconds(3);

    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan BrowserLoginTimeout = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan ServerListTimeout = TimeSpan.FromSeconds(5);

    public const int DefaultParallelDownloads = 8;

    public static readonly TimeSpan ReleaseDownloadTimeout = TimeSpan.FromMinutes(30);
}
