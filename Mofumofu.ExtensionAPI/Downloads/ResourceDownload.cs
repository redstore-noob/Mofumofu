using Mofumofu.ExtensionAPI.Data;

namespace Mofumofu.ExtensionAPI.Downloads;

/// <summary>
/// 资源下载接口：搜索 Modrinth 上的模组 / 光影 / 资源包并安装到实例。
/// CurseForge 源暂未开放（需要 API Key 策略），当前所有查询走 Modrinth。
/// </summary>
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
