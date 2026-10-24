using Mofumofu.ExtensionAPI.Data;

namespace Mofumofu.ExtensionAPI.Game;

/// <summary>
/// 实例内容管理：查看与启停实例 mods/ 目录里的模组文件。
/// 文件名一律用"启用态"的名字（不含 .disabled 后缀）。
/// </summary>
public interface IInstanceAction
{
    /// <summary>
    /// 列出实例 mods/ 目录里的模组（含已停用的）。需要 readonly 权限。
    /// </summary>
    /// <param name="instanceId">实例目录名（versions/ 下的名字）</param>
    /// <returns>按文件名排序的模组列表；实例不存在 mods 目录时返回空表</returns>
    Task<IReadOnlyList<ModFileInfo>> GetModsAsync(string instanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 启用 / 停用一个模组（改名 .jar ↔ .jar.disabled）。需要 instanceWrite 权限。
    /// </summary>
    /// <param name="instanceId">实例目录名</param>
    /// <param name="fileName">模组文件名（启用态名字，如 "sodium.jar"）</param>
    /// <param name="enabled">true = 启用；false = 停用</param>
    /// <returns>true = 成功；false = 文件不存在 / 已是目标状态 / 磁盘错误</returns>
    bool SetModEnabled(string instanceId, string fileName, bool enabled);

    /// <summary>
    /// 删除一个模组文件（无论启用还是停用状态）。需要 instanceWrite 权限。删除不可恢复。
    /// </summary>
    /// <returns>true = 成功；false = 文件不存在 / 磁盘错误</returns>
    bool DeleteMod(string instanceId, string fileName);
}
