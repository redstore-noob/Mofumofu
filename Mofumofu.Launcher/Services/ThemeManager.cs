using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using SukiUI;
using SukiUI.Models;

namespace Mofumofu.Launcher.Services;

/// <summary>
/// 主题的唯一数据源：深/浅色表面 + 强调色都在这里算，视图只认令牌名。
/// 换主题时直接改进程内共享 brush 的 Color，所以 <c>{StaticResource …}</c> 与
/// <c>{DynamicResource …}</c> 都能立刻反映。
/// </summary>
public static class ThemeManager
{
    /// <summary>主题切换完成后触发，供 code-behind 里自绘的颜色跟着重刷。</summary>
    public static event Action? ThemeChanged;

    public static bool IsLight { get; private set; }

    public static string CurrentTheme { get; private set; } = "dark";

    public static string CurrentAccent { get; private set; } = "teal";

    /// <summary>每次应用主题自增；缓存的页面重新挂到可视树时据此判断要不要补刷颜色。</summary>
    public static int Version { get; private set; }

    /// <summary>强调色候选：Key → (显示名, 主色, 悬停色, 按下色)。</summary>
    private static readonly Dictionary<string, (string Name, string Primary, string Hover, string Pressed)> Accents = new()
    {
        ["teal"] = ("青绿", "#3FC6C1", "#57D8D3", "#33BDB8"),
        ["blue"] = ("蔚蓝", "#5AA9E6", "#7CBEED", "#4590CE"),
        ["green"] = ("新绿", "#4CC38A", "#6BD3A3", "#3AA876"),
        ["orange"] = ("暖橙", "#F2AE49", "#F7C470", "#D8962F"),
        ["pink"] = ("樱粉", "#E5698F", "#EE8AA9", "#CC5379"),
        ["purple"] = ("紫藤", "#A78BFA", "#BCA5FF", "#8F6EF0"),
    };

    /// <summary>预设强调色（Key / 显示名 / 主色），供设置页画色板。</summary>
    public static IReadOnlyList<(string Key, string Name, string Hex)> AccentOptions { get; } =
    [
        ("teal", "青绿", "#3FC6C1"),
        ("blue", "蔚蓝", "#5AA9E6"),
        ("green", "新绿", "#4CC38A"),
        ("orange", "暖橙", "#F2AE49"),
        ("pink", "樱粉", "#E5698F"),
        ("purple", "紫藤", "#A78BFA"),
    ];

    public static void Apply(string theme) => Apply(theme, null);

    public static void Apply(string theme, string? accentKey)
    {
        CurrentTheme = theme;
        var variant = theme switch
        {
            "light" => ThemeVariant.Light,
            "system" => ThemeVariant.Default,
            _ => ThemeVariant.Dark,
        };

        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = variant;
        }

        try
        {
            SukiTheme.GetInstance().ChangeBaseTheme(variant);
        }
        catch
        {
            // 主题服务未就绪时忽略
        }

        IsLight = theme == "light";
        if (theme == "system" && Application.Current?.PlatformSettings is { } platform)
        {
            IsLight = platform.GetColorValues().ThemeVariant == PlatformThemeVariant.Light;
        }

        var key = NormalizeAccentKey(accentKey);
        CurrentAccent = key;
        var accent = ResolveAccent(key);

        ApplySukiAccent(key, accent.Primary, accent.Hover);
        ApplyPalette(BuildPalette(IsLight, accent.Primary, accent.Hover, accent.Pressed, IsCustomAccent(key)));
        Version++;
        ThemeChanged?.Invoke();
    }

    /// <summary>是不是 #RRGGBB 形式的自定义主题色。</summary>
    public static bool IsCustomAccent(string? key) =>
        !string.IsNullOrWhiteSpace(key) && key.TrimStart().StartsWith('#');

    /// <summary>某个主题色键（预设 key 或 #RRGGBB）对应的颜色。</summary>
    public static Color AccentColor(string? key)
    {
        var normalized = NormalizeAccentKey(key);
        if (Accents.TryGetValue(normalized, out var preset)) return Color.Parse(preset.Primary);
        return Color.TryParse(normalized, out var custom) ? custom : Color.Parse(Accents["teal"].Primary);
    }

    /// <summary>把设置里保存的值规范成预设 key 或 #RRGGBB；无法识别时退回默认青绿。</summary>
    public static string NormalizeAccentKey(string? raw)
    {
        var text = raw?.Trim() ?? string.Empty;
        if (text.Length == 0) text = CurrentAccent;
        if (Accents.ContainsKey(text)) return text;

        if (Color.TryParse(text, out var color))
        {
            return HexRgb(Color.FromArgb(255, color.R, color.G, color.B));
        }

        return "teal";
    }

    /// <summary>预设色直接查表；自定义色由主色推导悬停/按下色。</summary>
    private static (string Name, string Primary, string Hover, string Pressed) ResolveAccent(string key)
    {
        if (Accents.TryGetValue(key, out var preset)) return preset;

        var color = Color.TryParse(key, out var parsed) ? parsed : Color.Parse(Accents["teal"].Primary);
        var opaque = Color.FromArgb(255, color.R, color.G, color.B);
        return (HexRgb(opaque), HexRgb(opaque), HexRgb(Blend(opaque, Colors.White, 0.16)),
            HexRgb(Blend(opaque, Colors.Black, 0.16)));
    }

    /// <summary>
    /// 把强调色同步给 SukiUI 自带控件（ToggleButton / Slider / ToggleSwitch 等）。
    /// 注意主题名每次都要不同：SukiUI 对同名主题不会覆盖，沿用固定名字时只有第一次
    /// 切换生效，之后这些控件会停在旧颜色上（看起来就像主题色切换不完整）。
    /// </summary>
    /// <summary>自定义主题色会反复应用，主题名加序号保证每次都是新的（SukiUI 不覆盖同名主题）。</summary>
    private static int _sukiThemeSerial;

    private static void ApplySukiAccent(string accentKey, string primary, string hover)
    {
        try
        {
            var suki = SukiTheme.GetInstance();
            var theme = new SukiColorTheme(
                $"Mofumofu {accentKey} {++_sukiThemeSerial}", Color.Parse(primary), Color.Parse(hover));
            suki.AddColorTheme(theme);
            suki.ChangeColorTheme(theme);
        }
        catch
        {
            // 主题服务未就绪时忽略
        }
    }

    /// <summary>把强调色与当前底色的关系算成完整令牌表。</summary>
    private static Dictionary<string, string[]> BuildPalette(bool light, string accent, string accentHover,
        string accentPressed, bool customAccent = false)
    {
        var accentColor = Color.Parse(accent);
        var surfaceBase = Color.Parse(light ? "#F2F5F8" : "#0A0E15");
        // 自定义色深浅不可控：按亮度决定强调色上的文字用深色还是浅色，避免白底白字
        var onAccent = customAccent
            ? (accentColor.R * 0.299 + accentColor.G * 0.587 + accentColor.B * 0.114 > 140
                ? "#06121A"
                : "#FFFFFF")
            : light ? "#FFFFFF" : "#06121A";

        string Soft(double ratio) => Hex(Blend(accentColor, surfaceBase, ratio));

        var palette = new Dictionary<string, string[]>
        {
            // 强调色族
            ["NekoAccentBrush"] = [accent],
            ["NekoAccentHoverBrush"] = [accentHover],
            ["NekoAccentPressedBrush"] = [accentPressed],
            ["NekoAccentSoftBrush"] = [Soft(0.82)],
            ["NekoAccentSoftHoverBrush"] = [Soft(0.72)],
            ["NekoAccentOnBrush"] = [onAccent],
            ["NekoAccentLineBrush"] = [Soft(0.45)],
        };

        var shape = light
            ? new Dictionary<string, string[]>
            {
                ["NekoTextBrush"] = ["#1B2430"],
                ["NekoMutedBrush"] = ["#5C6B7E"],
                ["NekoDimBrush"] = ["#7C8AA0"],
                ["NekoBorderBrush"] = ["#D5DEE8"],
                ["NekoCardBrush"] = ["#F7FFFFFF"],
                ["NekoCardSoftBrush"] = ["#FFF1F5F9"],
                ["NekoCardHoverBrush"] = ["#E6EEF4"],
                ["NekoSurfaceBrush"] = ["#F2F5F8"],
                ["NekoFieldBrush"] = ["#E7EEF5"],
                ["NekoFieldSoftBrush"] = ["#EEF3F8"],
                ["NekoFieldHoverBrush"] = ["#DCE7F0"],
                ["NekoPressBrush"] = ["#D6E3EE"],
                ["NekoControlHoverBrush"] = ["#DCE7F0"],
                ["NekoControlPressedBrush"] = ["#CDDCE8"],
                ["NekoStartBarBrush"] = ["#EEF4F9"],
                ["NekoTitleBarBrush"] = ["#E8F2F6FA"],
                ["NekoDrawerBrush"] = ["#FAF8FBFF"],
                ["NekoDialogBrush"] = ["#FAF8FBFF"],
                ["NekoDialogSolidBrush"] = ["#FFFFFF"],
                ["NekoOverlayBrush"] = ["#330B1119"],
                ["NekoScrimBrush"] = ["#330B1119"],
                ["NekoNavIdleBrush"] = ["#6A7A8E"],
                ["NekoBackgroundBrush"] = ["#F4F7FA", "#EEF4F9", "#F7FAFD"],
                ["NekoHeroGlowBrush"] = [Hex(accentColor, 0x1F), "#00EAF2F7", "#00F4F7FA"],
                ["NekoTerminalBrush"] = ["#F4F8FB"],
                ["NekoTerminalTextBrush"] = ["#1D6B57"],
                ["NekoDangerSoftBrush"] = ["#FBE7E8"],
                ["NekoSuccessSoftBrush"] = ["#E2F5EC"],
                ["NekoWarningSoftBrush"] = ["#FDF1DF"],
                ["NekoInfoSoftBrush"] = ["#E4F0FA"],
            }
            : new Dictionary<string, string[]>
            {
                ["NekoTextBrush"] = ["#E9EEF6"],
                ["NekoMutedBrush"] = ["#93A0B4"],
                ["NekoDimBrush"] = ["#74839A"],
                ["NekoBorderBrush"] = ["#26303D"],
                ["NekoCardBrush"] = ["#D9121822"],
                ["NekoCardSoftBrush"] = ["#B30E141C"],
                ["NekoCardHoverBrush"] = ["#1A2230"],
                ["NekoSurfaceBrush"] = ["#0A0E15"],
                ["NekoFieldBrush"] = ["#16202B"],
                ["NekoFieldSoftBrush"] = ["#0F151E"],
                ["NekoFieldHoverBrush"] = ["#1A2430"],
                ["NekoPressBrush"] = ["#1C2836"],
                ["NekoControlHoverBrush"] = ["#243343"],
                ["NekoControlPressedBrush"] = ["#2A3B4C"],
                ["NekoStartBarBrush"] = ["#E60D131B"],
                ["NekoTitleBarBrush"] = ["#B30C121A"],
                ["NekoDrawerBrush"] = ["#F50C121A"],
                ["NekoDialogBrush"] = ["#F20C121A"],
                ["NekoDialogSolidBrush"] = ["#121A24"],
                ["NekoOverlayBrush"] = ["#66101822"],
                ["NekoScrimBrush"] = ["#55000000"],
                ["NekoNavIdleBrush"] = ["#9AA9BC"],
                ["NekoBackgroundBrush"] = ["#0A0E15", "#0C1520", "#0A1018"],
                ["NekoHeroGlowBrush"] = [Hex(accentColor, 0x1F), "#00121822", "#000A0E15"],
                ["NekoTerminalBrush"] = ["#05090C"],
                ["NekoTerminalTextBrush"] = ["#9FE8D5"],
                ["NekoDangerSoftBrush"] = ["#3A1B1E"],
                ["NekoSuccessSoftBrush"] = ["#17342A"],
                ["NekoWarningSoftBrush"] = ["#3A2E18"],
                ["NekoInfoSoftBrush"] = ["#16293A"],
            };

        foreach (var (k, v) in shape) palette[k] = v;

        // 语义色本身不随强调色变
        palette["NekoDangerBrush"] = [light ? "#C93B41" : "#E5484D"];
        palette["NekoSuccessBrush"] = [light ? "#1F9D63" : "#4CC38A"];
        palette["NekoWarningBrush"] = [light ? "#B9791A" : "#F2AE49"];
        palette["NekoInfoBrush"] = [light ? "#2C7FC0" : "#5AA9E6"];

        return palette;
    }

    private static Color Blend(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)Math.Round(a.A + (b.A - a.A) * t),
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    private static string Hex(Color color, byte? alpha = null)
    {
        var a = alpha ?? color.A;
        return $"#{a:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    /// <summary>#RRGGBB（不带 alpha），用于保存自定义主题色。</summary>
    private static string HexRgb(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static void ApplyPalette(IReadOnlyDictionary<string, string[]> palette)
    {
        if (Application.Current is null) return;

        foreach (var (key, colors) in palette)
        {
            if (!Application.Current.TryFindResource(key, out var resource)) continue;

            switch (resource)
            {
                case SolidColorBrush brush when colors.Length > 0:
                    brush.Color = Color.Parse(colors[0]);
                    break;
                case LinearGradientBrush gradient:
                    for (var i = 0; i < gradient.GradientStops.Count && i < colors.Length; i++)
                    {
                        gradient.GradientStops[i].Color = Color.Parse(colors[i]);
                    }
                    break;
            }
        }
    }
}
