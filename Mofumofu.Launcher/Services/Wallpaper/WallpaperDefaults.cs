using System;
using System.IO;
using Avalonia.Platform;

namespace Mofumofu.Launcher.Services.Wallpaper;

public static class WallpaperDefaults
{
    private const string AssetUri = "avares://Mofumofu.Launcher/Assets/default-wallpaper.png";

    public static string WallpaperDirectory => Path.Combine(
        MofuCore.Config.YamlRead.DataRoot,
        MofuCore.Config.YamlRead.DataDirectoryName,
        "wallpapers");

    public static string DefaultPath => Path.Combine(WallpaperDirectory, "default-mofu.png");

    public static string EnsureSeeded()
    {
        try
        {
            Directory.CreateDirectory(WallpaperDirectory);
            string target = DefaultPath;
            if (!File.Exists(target))
            {
                using Stream? source = AssetLoader.Open(new Uri(AssetUri));
                if (source is null) return string.Empty;

                string temporary = target + ".tmp";
                using (FileStream destination = File.Create(temporary))
                {
                    source.CopyTo(destination);
                }
                File.Move(temporary, target, true);
            }

            return File.Exists(target) ? target : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
