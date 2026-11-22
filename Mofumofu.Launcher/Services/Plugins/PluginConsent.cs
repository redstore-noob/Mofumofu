using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Mofumofu.Launcher.Services.Plugins;

public enum PluginConsentState
{
    Pending,

    Changed,

    NewPermissions,

    Granted,
}

public static class PluginConsent
{
    public static PluginConsentState Evaluate(PluginManifest manifest, out string dllHash)
    {
        dllHash = ComputeDllHash(manifest) ?? string.Empty;

        PluginStateEntry? state = FindState(manifest.Id);
        if (state is null || state.ConsentDllHash.Length == 0) return PluginConsentState.Pending;

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

    public static string? ComputeDllHash(PluginManifest manifest)
    {
        string directory = manifest.Directory;
        if (string.IsNullOrEmpty(directory) || Directory.Exists(directory) == false) return null;

        try
        {
            string rootFull = Path.GetFullPath(directory);
            string[] files = Directory.GetFiles(rootFull, "*", SearchOption.AllDirectories);
            if (files.Length == 0) return null;

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

    private static PluginStateEntry NewState(List<PluginStateEntry> states, string pluginId)
    {
        var state = new PluginStateEntry { Id = pluginId };
        states.Add(state);
        return state;
    }
}
