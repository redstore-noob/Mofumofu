using System;
using System.Collections.Generic;
using System.Linq;
using Mofumofu.ExtensionAPI;

namespace Mofumofu.Launcher.Services.Plugins;

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
    public const string Launch = "launch";

    public static readonly string[] All =
    {
        Notify, Widget, Network, NetworkReadonly, Storage, Readonly, ServerStatus, InstanceWrite, Launch,
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

        bool consented = LocalSettings.WithPluginStates(states =>
        {
            var consent = states.FirstOrDefault(entry =>
                string.Equals(entry.Id, runtime.Manifest.Id, StringComparison.OrdinalIgnoreCase));
            return consent is not null
                   && consent.ConsentPermissions.Contains(zone, StringComparer.OrdinalIgnoreCase);
        });
        if (consented == false)
        {
            throw new PluginPermissionException(zone,
                $"插件 {runtime.Manifest.Name} 的 {zone} 权限未经玩家同意，调用被拒绝。");
        }

        if (IsAllowedByUser(runtime.Manifest.Id, zone) == false)
        {
            throw new PluginPermissionException(zone,
                $"插件 {runtime.Manifest.Name} 的 {zone} 权限已被玩家关闭，调用被拒绝。");
        }
    }

    public static bool IsAllowedByUser(string pluginId, string zone)
    {
        return LocalSettings.WithPluginStates(states =>
        {
            var state = states?.FirstOrDefault(entry =>
                string.Equals(entry.Id, pluginId, StringComparison.OrdinalIgnoreCase));
            if (state is null) return true;
            return state.Permissions.TryGetValue(zone, out bool allowed) == false || allowed;
        });
    }

    /// <summary>写入玩家开关状态并持久化（整段走 LocalSettings 的 Gate，避免并发序列化异常）。</summary>
    public static void SetAllowed(string pluginId, string zone, bool allowed)
    {
        LocalSettings.MutatePluginStates(states =>
        {
            var state = states.FirstOrDefault(entry =>
                string.Equals(entry.Id, pluginId, StringComparison.OrdinalIgnoreCase));
            if (state is null)
            {
                state = new PluginStateEntry { Id = pluginId };
                states.Add(state);
            }

            state.Permissions[zone] = allowed;
            LocalSettings.Save();
        });
    }
}
