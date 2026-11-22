using System;
using System.IO;

namespace Mofumofu.Launcher.Services.Plugins;

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
        }
    }

    public static void Host(string message) => Write("host", message);
}
