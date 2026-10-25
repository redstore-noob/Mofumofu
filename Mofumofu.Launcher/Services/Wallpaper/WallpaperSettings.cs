using System.Collections.Generic;

namespace Mofumofu.Launcher.Services.Wallpaper;

/// <summary>壁纸模式。</summary>
public static class WallpaperModes
{
    public const string None = "none";
    public const string Image = "image";
    public const string Slideshow = "slideshow";

    public static bool IsValid(string? mode) =>
        mode is Image or Slideshow or None;

    public static string Normalize(string? mode) =>
        // 旧版的视频壁纸模式（已随 libVLC 一起移除）落到“无”，避免误报壁纸失效
        mode == "video" ? None
        : IsValid(mode) ? mode!
        : Image;
}

/// <summary>
/// 自定义壁纸设置（持久化到 {DataRoot}/Mofumofu/config/wallpaper.yaml）。
/// 支持单张图片、图片轮播两种模式，不再依赖 Wallpaper Engine。
/// </summary>
public sealed class WallpaperSettings
{
    public bool Enabled { get; set; }

    public string Mode { get; set; } = WallpaperModes.Image;

    /// <summary>图片壁纸 / 轮播使用的图片绝对路径列表。</summary>
    public List<string> Images { get; set; } = new();

    /// <summary>轮播间隔（秒）。</summary>
    public int IntervalSeconds { get; set; } = 30;

    /// <summary>轮播是否随机顺序。</summary>
    public bool Shuffle { get; set; }

    /// <summary>主窗口失去焦点时暂停（图片轮播）。</summary>
    public bool PauseWhenInactive { get; set; } = true;

    /// <summary>背景模糊强度（0-100，默认 20）。</summary>
    public int BlurPercent { get; set; } = 20;

    /// <summary>背景遮罩强度（0-100，默认 40）：深色主题压黑，浅色主题压白。</summary>
    public int DimPercent { get; set; } = 40;
}
