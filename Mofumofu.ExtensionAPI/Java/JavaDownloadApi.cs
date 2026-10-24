using Mofumofu.ExtensionAPI.Data;

namespace Mofumofu.ExtensionAPI.Java;

/// <summary>
/// Java 运行时下载接口：按主版本安装 Mojang 官方运行时（自动放进游戏目录 runtime/），
/// 并查询本机已检测到的 Java。
/// </summary>
public interface IJavaDownload
{
    /// <summary>宿主支持安装的 Java 主版本列表（当前 8 / 11 / 17 / 21 / 25）。</summary>
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
