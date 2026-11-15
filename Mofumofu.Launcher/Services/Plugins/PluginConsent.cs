using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

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

    /// <summary>记录一次同意：写入当前指纹 + 整包声明权限 + 时间（整段走 LocalSettings 的 Gate）。</summary>
    public static void Grant(PluginManifest manifest, string dllHash)
    {
        LocalSettings.MutatePluginStates(states =>
        {
            PluginStateEntry state = states.FirstOrDefault(entry =>
                string.Equals(entry.Id, manifest.Id, StringComparison.OrdinalIgnoreCase))
                ?? NewState(states, manifest.Id);
            state.ConsentDllHash = dllHash;
            state.ConsentPermissions = manifest.Permissions
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            state.ConsentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            LocalSettings.Save();
        });
    }

    /// <summary>插件目录整包聚合 SHA256（十六进制小写）：覆盖所有 dll/axaml/yaml/manifest 等文件。</summary>
    public static string? ComputeDllHash(PluginManifest manifest)
    {
        string directory = manifest.Directory;
        if (string.IsNullOrEmpty(directory) || Directory.Exists(directory) == false) return null;

        try
        {
            string rootFull = Path.GetFullPath(directory);
            string[] files = Directory.GetFiles(rootFull, "*", SearchOption.AllDirectories);
            if (files.Length == 0) return null;

            // 逐文件把「相对路径 + 内容」哈希成固定 32 字节，再按相对路径排序聚合：
            // 任意文件（依赖 DLL / AXAML / 清单 / 资源）增删改都会改变最终指纹。
            var perFile = new List<(string Relative, byte[] Hash)>();
            foreach (string file in files)
            {
                string relative = Path.GetRelativePath(rootFull, file)
                    .Replace(Path.DirectorySeparatorChar, '/');
                using IncrementalHash fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                fileHash.AppendData(Encoding.UTF8.GetBytes(relative));
                using (FileStream stream = File.OpenRead(file))
                {
                    byte[] buffer = new byte[81920];
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        fileHash.AppendData(buffer, 0, read);
                    }
                }
                perFile.Add((relative, fileHash.GetHashAndReset()));
            }

            using IncrementalHash aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var pair in perFile.OrderBy(p => p.Relative, StringComparer.OrdinalIgnoreCase))
            {
                aggregate.AppendData(pair.Hash);
            }
            return Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    private static PluginStateEntry? FindState(string pluginId) =>
        LocalSettings.WithPluginStates(states => states.FirstOrDefault(entry =>
            string.Equals(entry.Id, pluginId, StringComparison.OrdinalIgnoreCase)));

    /// <summary>仅在 MutatePluginStates 的回调（已持 Gate）里调用：创建并登记一个新状态条目。</summary>
    private static PluginStateEntry NewState(List<PluginStateEntry> states, string pluginId)
    {
        var state = new PluginStateEntry { Id = pluginId };
        states.Add(state);
        return state;
    }
}
