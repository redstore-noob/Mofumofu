namespace Mofumofu.ExtensionAPI.Data;

/// <summary>实例使用的模组加载器类型；原版（无加载器）不在此列，用 null 表示。</summary>
public enum ModLoaderType
{
    /// <summary>NeoForge</summary>
    NeoForge,

    /// <summary>Fabric</summary>
    Fabric,

    /// <summary>Quilt</summary>
    Quilt,

    /// <summary>Forge</summary>
    Forge,
}

/// <summary>官方清单中的一个可下载版本。</summary>
public sealed record DownloadableVersionInfo(
    string VersionId,        // 版本号，如 "1.21.4"、"25w14craftmine"
    string Type,             // release / snapshot / old_beta / old_alpha
    DateTime ReleaseTime);   // 官方发布时间

/// <summary>一次实例下载请求。</summary>
public sealed record InstanceDownloadRequest(
    string VersionId,             // 要安装的游戏版本号
    ModLoaderType? Loader = null, // 加载器；null = 原版
    string? LoaderVersion = null, // 加载器版本；null = 宿主自动选择最新适配版
    string? DisplayName = null);  // 实例显示名；null = 用版本号（加载器拼在后面）

/// <summary>实例下载进度（与宿主内部 Java 安装进度同构）。</summary>
public sealed record InstanceDownloadProgress(
    string Phase,              // 当前阶段描述，如 "下载版本清单" / "下载依赖库" / "安装加载器"
    long CompletedBytes,       // 已完成字节
    long TotalBytes,           // 总字节；未知时为 0
    long BytesPerSecond,       // 当前速度
    int FilesDone,             // 已完成文件数
    int FilesTotal)            // 总文件数；未知时为 0
{
    /// <summary>总体百分比 0~100；总量未知时为 0。</summary>
    public double Percent => TotalBytes > 0 ? Math.Clamp((double)CompletedBytes / TotalBytes * 100d, 0, 100) : 0;
}
