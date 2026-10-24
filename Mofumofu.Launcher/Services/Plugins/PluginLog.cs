using System;
using System.IO;

namespace Mofumofu.Launcher.Services.Plugins;

/// <summary>插件系统日志：{DataRoot}/Mofumofu/logs/plugins.log，宿主与插件共用。</summary>
public static class PluginLog
{
    private static readonly object Gate = new();

    private static string LogPath => Path.Combine(
        MofuCore.Config.YamlRead.DataRoot,
        MofuCore.Config.YamlRead.DataDirectoryName,
        "logs",
        "plugins.log");

    public static void Write(string pluginId, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{pluginId}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志写失败不影响任何功能
        }
    }

    public static void Host(string message) => Write("host", message);
}
