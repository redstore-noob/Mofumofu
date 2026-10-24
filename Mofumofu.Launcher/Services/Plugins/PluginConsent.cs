using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Mofumofu.Launcher.Services.Plugins;

/// <summary>安装同意的评估结果。</summary>
public enum PluginConsentState
{
    /// <summary>从未同意过（首次安装），必须弹窗。</summary>
    Pending,

    /// <summary>extension.dll 指纹变了（插件已更新），必须重新弹窗。</summary>
    Changed,

    /// <summary>清单新增了未授权的权限域，必须重新弹窗。</summary>
    NewPermissions,

    /// <summary>已同意且无变化，可直接加载。</summary>
    Granted,
}

/// <summary>
/// 插件安装同意流程的信任存储：extension.dll 的 SHA256 + 同意过的权限域 + 同意时间。
/// 「先同意、后加载」：PluginManager 在进 ALC 之前必须 Evaluate == Granted；
/// 指纹变化（插件更新）或清单新增权限 → 回到待授权、重新弹窗。
/// 记录落在 LocalSettings.PluginStates，按插件 Id 索引。
/// </summary>
public static class PluginConsent
{
    /// <summary>评估当前插件目录的授权状态，并给出当前 DLL 指纹。</summary>
    public static PluginConsentState Evaluate(PluginManifest manifest, out string dllHash)
    {
        dllHash = ComputeDllHash(manifest) ?? string.Empty;

        PluginStateEntry? state = FindState(manifest.Id);
        if (state is null || state.ConsentDllHash.Length == 0) return PluginConsentState.Pending;

        // 指纹对不上 = 插件更新过，声明内容都可能变了，整体重谈
        if (string.Equals(state.ConsentDllHash, dllHash, StringComparison.OrdinalIgnoreCase) == false)
        {
            return PluginConsentState.Changed;
        }

        if (manifest.Permissions.Any(permission =>
                state.ConsentPermissions.Contains(permission, StringComparer.OrdinalIgnoreCase) == false))
        {
            return PluginConsentState.NewPermissions;
        }

        return PluginConsentState.Granted;
    }

    /// <summary>记录一次同意：写入当前指纹 + 整包声明权限 + 时间。</summary>
    public static void Grant(PluginManifest manifest, string dllHash)
    {
        PluginStateEntry state = FindState(manifest.Id) ?? CreateState(manifest.Id);
        state.ConsentDllHash = dllHash;
        state.ConsentPermissions = manifest.Permissions
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        state.ConsentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        LocalSettings.Save();
    }

    /// <summary>插件主 DLL 的 SHA256（十六进制小写）；解析不到 DLL 时返回 null。</summary>
    public static string? ComputeDllHash(PluginManifest manifest)
    {
        string? dllPath = PluginManager.ResolveDllPath(manifest);
        if (dllPath is null) return null;

        try
        {
            using SHA256 sha = SHA256.Create();
            using FileStream stream = File.OpenRead(dllPath);
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    private static PluginStateEntry? FindState(string pluginId) =>
        LocalSettings.Current.PluginStates.FirstOrDefault(entry =>
            string.Equals(entry.Id, pluginId, StringComparison.OrdinalIgnoreCase));

    private static PluginStateEntry CreateState(string pluginId)
    {
        var state = new PluginStateEntry { Id = pluginId };
        LocalSettings.Current.PluginStates.Add(state);
        return state;
    }
}
