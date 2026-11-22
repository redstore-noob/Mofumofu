using Mofumofu.ExtensionAPI.Data;

namespace Mofumofu.ExtensionAPI.Java;

/// <summary>
/// 按主版本安装 Mojang 官方运行时（自动放进游戏目录 runtime/），并查询本机已检测到的 Java。
/// </summary>
/// <remarks>
/// 权限：清单 / 已安装列表查询需 <see cref="PluginPermissionZones.NetworkReadonly"/>，
/// 安装运行时需 <see cref="PluginPermissionZones.Network"/>（写入游戏目录属写操作）。
/// <c>InstallAsync</c> 在清单里没有该主版本时返回 null。
/// </remarks>
public interface IJavaDownload
{
    /// <summary>只读快照，不要强转修改。</summary>
    IReadOnlyList<int> SupportedMajorVersions { get; }

    Task<IReadOnlyList<JavaRuntimeChoice>> GetAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>已存在时宿主会跳过重复下载；清单里没有该主版本时返回 null。</summary>
    Task<InstalledJavaInfo?> InstallAsync(
        int majorVersion,
        IProgress<(string Phase, long CompletedBytes, long TotalBytes)>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>来源包括环境变量 / 注册表 / 常见目录 / 游戏目录 runtime/。</summary>
    Task<IReadOnlyList<InstalledJavaInfo>> GetInstalledAsync(CancellationToken cancellationToken = default);
}
