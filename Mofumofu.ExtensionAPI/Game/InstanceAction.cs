using Mofumofu.ExtensionAPI.Data;

namespace Mofumofu.ExtensionAPI.Game;

/// <summary>
/// 查看与启停实例 mods/ 目录里的模组文件；文件名一律用"启用态"的名字（不含 .disabled 后缀）。
/// </summary>
/// <remarks>
/// 权限：只读查询需 <see cref="PluginPermissionZones.Readonly"/>，写操作需
/// <see cref="PluginPermissionZones.InstanceWrite"/>。
/// <c>SetModEnabled</c> / <c>DeleteMod</c> 返回 false 同时表示「文件不存在 / 已是目标状态 / 磁盘错误」，
/// 三者不可区分，需精确诊断请查宿主日志（bool 签名保持不变以兼容已编译插件）。
/// </remarks>
public interface IInstanceAction
{
    Task<IReadOnlyList<ModFileInfo>> GetModsAsync(string instanceId, CancellationToken cancellationToken = default);

    /// <summary>磁盘上按 .jar ↔ .jar.disabled 改名。</summary>
    bool SetModEnabled(string instanceId, string fileName, bool enabled);

    /// <summary>删除不可恢复。</summary>
    bool DeleteMod(string instanceId, string fileName);
}
