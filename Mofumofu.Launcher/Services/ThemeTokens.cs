using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Mofumofu.Launcher.Services;

/// <summary>
/// 主题令牌取用口：界面里所有颜色都应该走这里或 XAML 的 <c>{DynamicResource …}</c>，
/// 不要在视图里写死颜色。令牌本体定义在 <c>Styles/AppStyles.axaml</c>，
/// 深/浅色与强调色的实际取值由 <see cref="ThemeManager"/> 统一维护。
/// </summary>
public static class ThemeTokens
{
    /// <summary>取令牌资源（brush/color/其它）。找不到时返回 null。</summary>
    public static object? Find(string key)
    {
        if (Application.Current is { } app && app.TryFindResource(key, out var resource))
        {
            return resource;
        }

        return null;
    }

    /// <summary>取一个画刷令牌（<c>…Brush</c>）；令牌是 Color 时自动包成画刷。</summary>
    public static IBrush Brush(string key) => Find(key) switch
    {
        IBrush brush => brush,
        Color color => new SolidColorBrush(color),
        _ => Brushes.Transparent,
    };

    /// <summary>取一个颜色令牌（<c>NekoAccent</c> / <c>NekoText</c> …）；令牌是画刷时取其 Color。</summary>
    public static Color Color(string key) => Find(key) switch
    {
        ISolidColorBrush solid => solid.Color,
        Color color => color,
        _ => Colors.Transparent,
    };

    /// <summary>由颜色令牌新建一个独立画刷（需要各自改色时用，避免污染共享令牌）。</summary>
    public static IBrush BrushOf(string colorKey) => new SolidColorBrush(Color(colorKey));

    /// <summary>颜色令牌 + 透明度。</summary>
    public static IBrush WithAlpha(string colorKey, double alpha)
    {
        var color = Color(colorKey);
        var a = (byte)Math.Clamp(Math.Round(alpha * 255), 0, 255);
        return new SolidColorBrush(Avalonia.Media.Color.FromArgb(a, color.R, color.G, color.B));
    }

    /// <summary>按内容数据里的颜色字符串（工具色/实例色等）造画刷。</summary>
    public static IBrush FromHex(string? hex, IBrush? fallback = null)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback ?? Brushes.Transparent;

        try
        {
            return new SolidColorBrush(Avalonia.Media.Color.Parse(hex));
        }
        catch
        {
            return fallback ?? Brushes.Transparent;
        }
    }

    /// <summary>把两个令牌色按比例混合（t=0 取 a，t=1 取 b）。</summary>
    public static Color Blend(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Avalonia.Media.Color.FromArgb(
            (byte)Math.Round(a.A + (b.A - a.A) * t),
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }
}
