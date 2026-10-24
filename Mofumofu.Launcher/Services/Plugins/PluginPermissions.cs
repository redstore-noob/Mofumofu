using System;
using System.Collections.Generic;
using System.Linq;
using Mofumofu.ExtensionAPI;

namespace Mofumofu.Launcher.Services.Plugins;

/// <summary>
/// 权限区定义与检查。权限区在插件清单 permissions 里声明，玩家可在插件页开关；
/// 每次插件 API 调用前 Demand 一次，未声明或已关闭 → PluginPermissionException。
/// </summary>
public static class PluginPermissions
{
    public const string Notify = "notify";
    public const string Widget = "widget";
    public const string Network = "network";
    public const string NetworkReadonly = "networkReadonly";
    public const string Storage = "storage";
    public const string Readonly = "readonly";
    public const string ServerStatus = "serverStatus";
    public const string InstanceWrite = "instanceWrite";

    /// <summary>全部已知权限区。</summary>
    public static readonly string[] All =
    {
        Notify, Widget, Network, NetworkReadonly, Storage, Readonly, ServerStatus, InstanceWrite,
    };

    public static bool IsKnown(string zone) => All.Contains(zone);

    /// <summary>
    /// 检查插件是否拥有某权限区：清单未声明 → 拒绝；玩家已关闭 → 拒绝；否则放行。
    /// </summary>
    public static void Demand(PluginRuntime runtime, string zone)
    {
        if (runtime.Manifest.Permissions.Contains(zone, StringComparer.OrdinalIgnoreCase) == false)
        {
            throw new PluginPermissionException(zone,
                $"插件 {runtime.Manifest.Name} 未在清单中声明权限 {zone}，调用被拒绝。");
        }

        if (IsAllowedByUser(runtime.Manifest.Id, zone) == false)
        {
            throw new PluginPermissionException(zone,
                $"插件 {runtime.Manifest.Name} 的 {zone} 权限已被玩家关闭，调用被拒绝。");
        }
    }

    /// <summary>玩家开关状态（LocalSettings 持久化）；未记录 = 默认允许。</summary>
    public static bool IsAllowedByUser(string pluginId, string zone)
    {
        var state = LocalSettings.Current.PluginStates?
            .FirstOrDefault(entry => string.Equals(entry.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (state is null) return true;
        return state.Permissions.TryGetValue(zone, out bool allowed) == false || allowed;
    }

    /// <summary>写入玩家开关状态并持久化。</summary>
    public static void SetAllowed(string pluginId, string zone, bool allowed)
    {
        var states = LocalSettings.Current.PluginStates;
        var state = states.FirstOrDefault(entry =>
            string.Equals(entry.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (state is null)
        {
            state = new PluginStateEntry { Id = pluginId };
            states.Add(state);
        }

        state.Permissions[zone] = allowed;
        LocalSettings.Save();
    }
}
