using System.Collections.Generic;

namespace Mofumofu.Launcher.Services.Wallpaper;

public static class WallpaperModes
{
    public const string None = "none";
    public const string Image = "image";
    public const string Slideshow = "slideshow";

    public static bool IsValid(string? mode) =>
        mode is Image or Slideshow or None;

    public static string Normalize(string? mode) =>
        IsValid(mode) ? mode!
        : Image;
}

public sealed class WallpaperSettings
{
    public bool Enabled { get; set; }

    public string Mode { get; set; } = WallpaperModes.Image;

    public List<string> Images { get; set; } = new();

    public int IntervalSeconds { get; set; } = 30;

    public bool Shuffle { get; set; }

    public int BlurPercent { get; set; } = 20;

    public int DimPercent { get; set; } = 40;
}
