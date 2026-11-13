using Mofumofu.ExtensionAPI.Data;

namespace Mofumofu.ExtensionAPI.Downloads;

/// <summary>
/// 资源下载接口：搜索 Modrinth 上的模组 / 光影 / 资源包并安装到实例。
/// CurseForge 源暂未开放（需要 API Key 策略），当前所有查询走 Modrinth。
/// </summary>
/// <remarks>
/// 权限：搜索 / 查询版本等只读操作需要 <see cref="PluginPermissionZones.NetworkReadonly"/>，
/// 下载并写入实例需要 <see cref="PluginPermissionZones.Network"/>；写操作同样要权限，
/// 纠正「读要权限、写不要」的倒挂。所有 <c>IReadOnlyList</c> 返回值均为只读快照，
/// 插件不要强转后修改。
/// null / 加载器契约：各方法的 <c>loader</c> 字符串统一用小写
/// <c>fabric</c> / <c>forge</c> / <c>neoforge</c> / <c>quilt</c>，传 null 一律表示「不过滤」；
/// 这与 <c>InstanceDownloadRequest.Loader</c> 的「null = 原版」语义不同，请勿混用。
/// <c>gameVersion</c> 为 null 也表示不过滤，<c>query</c> 为空串表示浏览热门。
/// </remarks>
public interface IResourceDownload
{
    /// <summary>
    /// 搜索资源。
    /// </summary>
    /// <param name="query">关键词；空串 = 浏览热门（按下载量排序）</param>
    /// <param name="kind">资源种类</param>
    /// <param name="gameVersion">按游戏版本过滤；null = 不过滤</param>
    /// <param name="loader">按加载器过滤（fabric / forge / neoforge / quilt）；null = 不过滤</param>
    /// <param name="limit">返回条数（1~100，默认 20）</param>
    /// <param name="offset">分页偏移</param>
    Task<IReadOnlyList<ResourceProject>> SearchAsync(
        string query,
        ResourceKind kind,
        string? gameVersion = null,
        string? loader = null,
        int limit = 20,
        int offset = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取项目在指定游戏版本 / 加载器下的可下载版本列表。
    /// gameVersion / loader 传 null 表示不过滤。
    /// </summary>
    Task<IReadOnlyList<ResourceFileInfo>> GetFilesAsync(
        string projectId,
        string? gameVersion = null,
        string? loader = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 下载资源的指定版本并装进实例（mod 类型会自动补齐依赖）。
    /// 目标目录由资源种类决定（mods / shaderpacks / resourcepacks）。
    /// </summary>
    /// <param name="versionId">要下载的版本 id（GetFilesAsync 里拿到）</param>
    /// <param name="kind">资源种类</param>
    /// <param name="instanceId">目标实例目录名</param>
    /// <param name="gameVersion">实例的游戏版本（用于依赖解析过滤；null = 不过滤）</param>
    /// <param name="loader">实例的加载器（用于依赖解析过滤；null = 不过滤）</param>
    /// <param name="includeDependencies">是否自动下载依赖（仅 mod 类型有效）</param>
    /// <param name="progress">进度：Completed/Total 为已完成的文件计数</param>
    /// <returns>落地的目录与实际写入的文件列表</returns>
    Task<ResourceDownloadResult> DownloadAsync(
        string versionId,
        ResourceKind kind,
        string instanceId,
        string? gameVersion = null,
        string? loader = null,
        bool includeDependencies = true,
        IProgress<(int Completed, int Total, string Detail)>? progress = null,
        CancellationToken cancellationToken = default);
}
