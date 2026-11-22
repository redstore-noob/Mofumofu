using Mofumofu.ExtensionAPI.Data;

namespace Mofumofu.ExtensionAPI.Downloads;

/// <summary>
/// 下载实例的接口。独立出来是为了让插件能自己重写一套下载界面，不依赖启动器内置 UI。
/// </summary>
/// <remarks>
/// 权限：版本 / 加载器清单等只读查询需 <see cref="PluginPermissionZones.NetworkReadonly"/>，
/// 下载安装整个实例需 <see cref="PluginPermissionZones.Network"/>。
/// 网络失败 / 版本不存在等异常直接抛出，不返回 null；<c>progress</c> 可为 null；
/// <c>InstanceDownloadRequest.Loader</c> 为 null 表示原版。
/// </remarks>
public interface IInstanceDownload
{
    Task<IReadOnlyList<DownloadableVersionInfo>> GetVersionsAsync(CancellationToken cancellationToken = default);

    /// <summary>gameVersion 先从 GetVersionsAsync 拿。</summary>
    Task<IReadOnlyList<LoaderVersionInfo>> GetLoaderVersionsAsync(
        ModLoaderType loader, string gameVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// 方法在安装完成后才返回；带加载器时宿主会自动补装原版依赖再装加载器。
    /// 取消后安装中止，已写入的文件由宿主清理。
    /// </summary>
    Task<InstanceInfo> DownloadAsync(
        InstanceDownloadRequest request,
        IProgress<InstanceDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

}
