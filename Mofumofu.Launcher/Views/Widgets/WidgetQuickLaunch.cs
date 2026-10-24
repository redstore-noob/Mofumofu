using System;
using System.Collections.Generic;
using Mofumofu.Launcher.Services;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views.Widgets;

/// <summary>
/// 小组件的快速启动入口：按（可选的）实例解析启动配置，
/// 附加额外的游戏参数（直接进服 / 快速进存档）后走真实启动。
/// </summary>
internal static class WidgetQuickLaunch
{
    /// <summary>
    /// 用当前（或指定）实例启动游戏，并附加额外游戏参数。
    /// 失败（缺目录 / 缺 Java / 已在运行）时以 Toast 说明原因。
    /// </summary>
    public static void Launch(string? instanceId, IEnumerable<string> extraGameArgs, string successToast)
    {
        try
        {
            if (GameState.IsRunning)
            {
                Shell.Toast(L.T("Home.QuickLaunch.AlreadyRunning", "游戏已在运行中"));
                return;
            }

            if (!string.IsNullOrWhiteSpace(instanceId)
                && !string.Equals(instanceId, GameState.SelectedVersion, StringComparison.Ordinal))
            {
                GameState.SetVersion(instanceId);
            }

            if (!LaunchRequestResolver.TryResolve(
                    out var options, out var displayName, out var versionLabel, out var reason))
            {
                Shell.Toast(reason);
                return;
            }

            foreach (var argument in extraGameArgs)
            {
                options.AdditionalGameArguments.Add(argument);
            }

            if (LaunchProgress.Shared.StartReal(options, displayName, versionLabel))
            {
                Shell.Toast(successToast);
            }
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Home.QuickLaunch.Failed", "启动失败：{0}", ex.Message));
        }
    }
}
