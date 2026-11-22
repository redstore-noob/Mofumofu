using Mofumofu.ExtensionAPI.Data;

namespace Mofumofu.ExtensionAPI.Downloads;

/// <summary>
/// 资源下载接口：搜索 Modrinth 上的模组 / 光影 / 资源包并安装到实例；
/// 另提供 CurseForge 按 ID 安装（仅当玩家给出明确的 modId / fileId，不做搜索、浏览或推荐）。
/// </summary>
/// <remarks>
/// 权限：只读查询需 <see cref="PluginPermissionZones.NetworkReadonly"/>，下载写入实例需
/// <see cref="PluginPermissionZones.Network"/>。
/// 各方法的 <c>loader</c> 用小写 <c>fabric</c> / <c>forge</c> / <c>neoforge</c> / <c>quilt</c>，
/// 传 null 表示「不过滤」——与 <c>InstanceDownloadRequest.Loader</c> 的「null = 原版」语义不同，请勿混用；
/// <c>gameVersion</c> 为 null 同样表示不过滤。
/// </remarks>
public interface IResourceDownload
{
    Task<IReadOnlyList<ResourceProject>> SearchAsync(
        string query,
        ResourceKind kind,
        string? gameVersion = null,
        string? loader = null,
        int limit = 20,
        int offset = 0,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ResourceFileInfo>> GetFilesAsync(
        string projectId,
        string? gameVersion = null,
        string? loader = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// mod 类型会自动补齐依赖，目标目录由资源种类决定（mods / shaderpacks / resourcepacks）；
    /// 进度中的 Completed / Total 是文件计数。
    /// </summary>
    Task<ResourceDownloadResult> DownloadAsync(
        string versionId,
        ResourceKind kind,
        string instanceId,
        string? gameVersion = null,
        string? loader = null,
        bool includeDependencies = true,
        IProgress<(int Completed, int Total, string Detail)>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// CurseForge 源：按玩家明确给出的 modId / fileId 安装单个模组到实例 mods/。
    /// 不提供搜索/浏览/推荐；需要玩家在「设置 → 下载」配置 API Key。
    /// 作者关闭第三方分发或接口无下载地址时，返回的 InstalledFiles 为空。
    /// 旧插件无需实现此方法，默认抛出 NotSupportedException。
    /// </summary>
    Task<ResourceDownloadResult> DownloadCurseForgeAsync(
        int modId,
        int fileId,
        string instanceId,
        IProgress<(int Completed, int Total, string Detail)>? progress = null,
        CancellationToken cancellationToken = default)
        => throw new System.NotSupportedException("当前插件上下文不支持 CurseForge 安装。");
}
