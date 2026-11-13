using Mofumofu.ExtensionAPI.Data;

namespace Mofumofu.ExtensionAPI.Game;

/// <summary>
/// 实例内容管理：查看与启停实例 mods/ 目录里的模组文件。
/// 文件名一律用"启用态"的名字（不含 .disabled 后缀）。
/// </summary>
/// <remarks>
/// 权限：只读查询需要 <see cref="PluginPermissionZones.Readonly"/>，启停 / 删除模组等写操作需要
/// <see cref="PluginPermissionZones.InstanceWrite"/>；写操作同样要权限，纠正「读要权限、写不要」的倒挂。
/// null / 参数契约：所有 <c>string</c> 参数按非 null、非空白处理；返回的 <c>IReadOnlyList</c> 为只读快照。
/// <c>SetModEnabled</c> / <c>DeleteMod</c> 返回 false 同时表示「文件不存在 / 已是目标状态 / 磁盘错误」，
/// 三者不可区分，需精确诊断请查宿主日志（保持既有 bool 签名以兼容已编译插件）。
/// </remarks>
public interface IInstanceAction
{
    /// <summary>
    /// 列出实例 mods/ 目录里的模组（含已停用的）。需要 <see cref="PluginPermissionZones.Readonly"/> 权限。
    /// </summary>
    /// <param name="instanceId">实例目录名（versions/ 下的名字）</param>
    /// <returns>按文件名排序的模组列表；实例不存在 mods 目录时返回空表</returns>
    Task<IReadOnlyList<ModFileInfo>> GetModsAsync(string instanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 启用 / 停用一个模组（改名 .jar ↔ .jar.disabled）。需要 <see cref="PluginPermissionZones.InstanceWrite"/> 权限。
    /// </summary>
    /// <param name="instanceId">实例目录名</param>
    /// <param name="fileName">模组文件名（启用态名字，如 "sodium.jar"）</param>
    /// <param name="enabled">true = 启用；false = 停用</param>
    /// <returns>true = 成功；false = 文件不存在 / 已是目标状态 / 磁盘错误</returns>
    bool SetModEnabled(string instanceId, string fileName, bool enabled);

    /// <summary>
    /// 删除一个模组文件（无论启用还是停用状态）。需要 <see cref="PluginPermissionZones.InstanceWrite"/> 权限。删除不可恢复。
    /// </summary>
    /// <returns>true = 成功；false = 文件不存在 / 磁盘错误</returns>
    bool DeleteMod(string instanceId, string fileName);
}
