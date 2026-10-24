using System;
using System.IO;
using Avalonia.Platform;

namespace Mofumofu.Launcher.Services.Wallpaper;

/// <summary>内置默认壁纸：首次启动时释放到用户壁纸目录。</summary>
public static class WallpaperDefaults
{
    private const string AssetUri = "avares://Mofumofu.Launcher/Assets/default-wallpaper.png";

    public static string WallpaperDirectory => Path.Combine(
        MofuCore.Config.YamlRead.DataRoot,
        MofuCore.Config.YamlRead.DataDirectoryName,
        "wallpapers");

    public static string DefaultPath => Path.Combine(WallpaperDirectory, "default-mofu.png");

    /// <summary>确保默认壁纸已释放到用户目录，返回文件路径；失败时返回空串。</summary>
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
