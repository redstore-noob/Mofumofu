namespace Mofumofu.ExtensionAPI.Data;

/// <summary>实例使用的模组加载器类型；原版（无加载器）不在此列，用 null 表示。</summary>
public enum ModLoaderType
{
    /// <summary>显式固定序号，禁止依赖隐式序号，新增成员只能追加在末尾</summary>
    NeoForge = 0,

    Fabric = 1,

    Quilt = 2,

    Forge = 3,
}

/// <summary>官方清单中的一个可下载版本。</summary>
public sealed record DownloadableVersionInfo(
    string VersionId,
    string Type,
    DateTime ReleaseTime);

/// <summary>一次实例下载请求。</summary>
public sealed record InstanceDownloadRequest(
    string VersionId,
    ModLoaderType? Loader = null,
    string? LoaderVersion = null,
    string? DisplayName = null);

/// <summary>实例下载进度（与宿主内部 Java 安装进度同构）。</summary>
public sealed record InstanceDownloadProgress(
    string Phase,
    long CompletedBytes,
    long TotalBytes,
    long BytesPerSecond,
    int FilesDone,
    int FilesTotal)
{
    public double Percent => TotalBytes > 0 ? Math.Clamp((double)CompletedBytes / TotalBytes * 100d, 0, 100) : 0;
}
