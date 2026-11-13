using Mofumofu.ExtensionAPI.Data;

namespace Mofumofu.ExtensionAPI.Java;

/// <summary>
/// Java 运行时下载接口：按主版本安装 Mojang 官方运行时（自动放进游戏目录 runtime/），
/// 并查询本机已检测到的 Java。
/// </summary>
/// <remarks>
/// 权限：清单 / 已安装列表查询需要 <see cref="PluginPermissionZones.NetworkReadonly"/>，
/// 安装运行时需要 <see cref="PluginPermissionZones.Network"/>；写入游戏目录属于写操作，同样要权限。
/// 所有 <c>IReadOnlyList</c> 返回值都应是宿主提供的不可变快照，插件不要强转后修改。
/// <c>InstallAsync</c> 在清单里没有该主版本时返回 null。
/// </remarks>
public interface IJavaDownload
{
    /// <summary>宿主支持安装的 Java 主版本列表（当前 8 / 11 / 17 / 21 / 25）；只读快照，不要强转修改。</summary>
    IReadOnlyList<int> SupportedMajorVersions { get; }

    /// <summary>
    /// 获取 Mojang 官方运行时清单（组件名 + 版本号 + 发布时间）。
    /// </summary>
    Task<IReadOnlyList<JavaRuntimeChoice>> GetAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 安装指定主版本的运行时到游戏目录（已存在时宿主会跳过重复下载）。
    /// 进度带阶段名与字节级数据。
    /// </summary>
    /// <param name="majorVersion">Java 主版本（见 SupportedMajorVersions）</param>
    /// <returns>安装结果；清单里没有该主版本时返回 null</returns>
    Task<InstalledJavaInfo?> InstallAsync(
        int majorVersion,
        IProgress<(string Phase, long CompletedBytes, long TotalBytes)>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出本机已检测到的 Java（环境变量 / 注册表 / 常见目录 / 游戏目录 runtime/）。
    /// </summary>
    Task<IReadOnlyList<InstalledJavaInfo>> GetInstalledAsync(CancellationToken cancellationToken = default);
}
