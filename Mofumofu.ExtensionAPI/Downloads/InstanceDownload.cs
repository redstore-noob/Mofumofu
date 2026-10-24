using Mofumofu.ExtensionAPI.Data;

namespace Mofumofu.ExtensionAPI.Downloads;

/// <summary>
/// 用于下载实例的接口
/// 为什么有这个专门的接口，好难猜啊😭
/// 如果启动器本体被举报了，你可以试着利用这一套API去重新写一个下载界面
/// </summary>
public interface IInstanceDownload
{
    /// <summary>
    /// 获取官方清单里可下载的游戏版本列表（原版）。
    /// 网络失败等异常直接抛出，不返回 null。
    /// </summary>
    Task<IReadOnlyList<DownloadableVersionInfo>> GetVersionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取某个游戏版本可用的加载器版本列表（Fabric / Forge / NeoForge / Quilt）。
    /// 网络失败等异常直接抛出。
    /// </summary>
    /// <param name="loader">加载器类型</param>
    /// <param name="gameVersion">游戏版本号（先从 GetVersionsAsync 拿）</param>
    Task<IReadOnlyList<LoaderVersionInfo>> GetLoaderVersionsAsync(
        ModLoaderType loader, string gameVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// 下载并安装一个实例（可选加载器），方法在安装完成后才返回。
    /// 带加载器时宿主会自动补装原版依赖再装加载器。
    /// 进度通过 progress 回报，取消传 cancellationToken。
    /// 失败（网络中断 / 版本不存在 / 磁盘不可写等）直接抛异常。
    /// </summary>
    /// <param name="request">要安装哪个版本、带什么加载器、实例叫什么</param>
    /// <param name="progress">下载/安装进度回调，可为 null</param>
    /// <param name="cancellationToken">取消令牌；取消后安装中止，已写入的文件由宿主清理</param>
    /// <returns>安装完成的实例快照（时长等统计字段为零值；Id 为 versions/ 下的实际目录名）</returns>
    Task<InstanceInfo> DownloadAsync(
        InstanceDownloadRequest request,
        IProgress<InstanceDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

}
