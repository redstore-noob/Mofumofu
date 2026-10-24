using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Mofumofu.Launcher.Services;

namespace Mofumofu.Launcher.Views.Widgets;

/// <summary>一段样式相同的 MOTD 文本（解析结果的最小单位）。</summary>
public readonly record struct MotdSegment(
    string Text,
    Color? Color,
    bool Bold,
    bool Italic,
    bool Underlined,
    bool Strikethrough,
    bool Obfuscated)
{
    public bool HasStyle => Color is not null || Bold || Italic || Underlined || Strikethrough || Obfuscated;
}

/// <summary>MOTD 渲染参数。</summary>
public sealed class MotdOptions
{
    public double FontSize { get; init; } = 11.5;

    public FontFamily? FontFamily { get; init; }

    /// <summary>最多显示几行（0 = 不限制）。</summary>
    public int MaxLines { get; init; }

    public TextWrapping Wrapping { get; init; } = TextWrapping.Wrap;

    public IBrush? Foreground { get; init; }
}

/// <summary>
/// Minecraft 服务器 MOTD 渲染器：把 §0-§f / §k-§r / §x(§r§r§g§g§b§b) 颜色样式码，
/// 以及 MOTD 的 JSON 文本组件（<c>{"text":…,"extra":[…],"color":…}</c> 或数组形态）
/// 解析成带样式的分段，并渲染到 Avalonia 的 <see cref="TextBlock.Inlines"/>。
/// 未指定颜色的部分不顶色，跟随卡片正文的主题令牌，浅色主题下同样可读。
/// </summary>
public static class MotdRenderer
{
    /// <summary>Minecraft Java 版 16 色码 → 官方默认色板。</summary>
    private static readonly Dictionary<char, Color> LegacyColors = new()
    {
        ['0'] = Color.Parse("#000000"),
        ['1'] = Color.Parse("#0000AA"),
        ['2'] = Color.Parse("#00AA00"),
        ['3'] = Color.Parse("#00AAAA"),
        ['4'] = Color.Parse("#AA0000"),
        ['5'] = Color.Parse("#AA00AA"),
        ['6'] = Color.Parse("#FFAA00"),
        ['7'] = Color.Parse("#AAAAAA"),
        ['8'] = Color.Parse("#555555"),
        ['9'] = Color.Parse("#5555FF"),
        ['a'] = Color.Parse("#55FF55"),
        ['b'] = Color.Parse("#55FFFF"),
        ['c'] = Color.Parse("#FF5555"),
        ['d'] = Color.Parse("#FF55FF"),
        ['e'] = Color.Parse("#FFFF55"),
        ['f'] = Color.Parse("#FFFFFF"),
    };

    /// <summary>JSON 文本组件的具名颜色（与原版一致）。</summary>
    private static readonly Dictionary<string, Color> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = Color.Parse("#000000"),
        ["dark_blue"] = Color.Parse("#0000AA"),
        ["dark_green"] = Color.Parse("#00AA00"),
        ["dark_aqua"] = Color.Parse("#00AAAA"),
        ["dark_red"] = Color.Parse("#AA0000"),
        ["dark_purple"] = Color.Parse("#AA00AA"),
        ["gold"] = Color.Parse("#FFAA00"),
        ["gray"] = Color.Parse("#AAAAAA"),
        ["grey"] = Color.Parse("#AAAAAA"),
        ["dark_gray"] = Color.Parse("#555555"),
        ["dark_grey"] = Color.Parse("#555555"),
        ["blue"] = Color.Parse("#5555FF"),
        ["green"] = Color.Parse("#55FF55"),
        ["aqua"] = Color.Parse("#55FFFF"),
        ["red"] = Color.Parse("#FF5555"),
        ["light_purple"] = Color.Parse("#FF55FF"),
        ["yellow"] = Color.Parse("#FFFF55"),
        ["white"] = Color.Parse("#FFFFFF"),
    };

    /// <summary>去掉 § 样式码与 JSON 组件结构，只留可读文本（工具提示、Toast 用）。</summary>
    public static string Strip(string? motd)
    {
        if (string.IsNullOrEmpty(motd)) return string.Empty;

        var builder = new StringBuilder();
        foreach (var segment in Parse(motd))
        {
            builder.Append(segment.Text);
        }

        return builder.ToString().Trim();
    }

    /// <summary>解析 MOTD：先是 JSON 文本组件，失败后按 § 样式码流转译。</summary>
    public static IReadOnlyList<MotdSegment> Parse(string? motd)
    {
        var segments = new List<MotdSegment>();
        if (string.IsNullOrWhiteSpace(motd)) return segments;

        var text = motd.Trim();
        if (TryParseJsonComponent(text, out var jsonSegments))
        {
            segments.AddRange(jsonSegments);
            return segments;
        }

        ParseLegacy(text, segments);
        return segments;
    }

    /// <summary>渲染成 TextBlock（多行自动换行，换行符转成硬换行）。</summary>
    public static TextBlock Render(string? motd, MotdOptions? options = null)
    {
        var settings = options ?? new MotdOptions();
        var block = new TextBlock
        {
            FontSize = settings.FontSize,
            TextWrapping = settings.Wrapping,
            VerticalAlignment = VerticalAlignment.Center,
            LineHeight = Math.Round(settings.FontSize * 1.45),
        };

        if (settings.MaxLines > 0) block.MaxLines = settings.MaxLines;
        if (settings.FontFamily is not null) block.FontFamily = settings.FontFamily;
        if (settings.Foreground is not null) block.Foreground = settings.Foreground;

        Apply(block, motd, options);
        return block;
    }

    /// <summary>把 MOTD 写进已有 TextBlock（替换 Inlines）。</summary>
    public static void Apply(TextBlock target, string? motd, MotdOptions? options = null)
    {
        var settings = options ?? new MotdOptions();
        target.Inlines ??= new InlineCollection();
        target.Inlines.Clear();

        if (settings.MaxLines > 0) target.MaxLines = settings.MaxLines;

        var segments = Parse(motd);
        if (segments.Count == 0) return;

        // 浅色主题下把亮色（白/黄/浅绿…）压暗一点，否则白字贴在白卡片上看不见
        var adjust = ThemeManager.IsLight;

        foreach (var segment in segments)
        {
            AppendSegment(target, segment, adjust);
        }
    }

    private static void AppendSegment(TextBlock target, MotdSegment segment, bool adjustForLight)
    {
        if (segment.Text.Length == 0) return;

        var color = segment.Color is { } raw
            ? adjustForLight ? Darken(raw) : raw
            : (Color?)null;

        var parts = segment.Text.Split('\n');
        for (var index = 0; index < parts.Length; index++)
        {
            if (index > 0) target.Inlines!.Add(new LineBreak());
            if (parts[index].Length == 0) continue;

            var run = new Run(parts[index]);
            if (color is { } value) run.Foreground = new SolidColorBrush(value);
            if (segment.Bold) run.FontWeight = FontWeight.Bold;
            if (segment.Italic) run.FontStyle = FontStyle.Italic;

            TextDecorationCollection? decorations = null;
            if (segment.Underlined)
            {
                decorations = new TextDecorationCollection { new TextDecoration { Location = TextDecorationLocation.Underline } };
            }
            if (segment.Strikethrough)
            {
                decorations ??= new TextDecorationCollection();
                decorations.Add(new TextDecoration { Location = TextDecorationLocation.Strikethrough });
            }
            if (decorations is not null) run.TextDecorations = decorations;

            // §k 在原版是随机抖动的乱码；静态卡片里保留原文，保证信息可读
            target.Inlines!.Add(run);
        }
    }

    /// <summary>浅色主题用：把过亮的颜色往黑色方向压 35%，保留原有色相。</summary>
    private static Color Darken(Color color)
    {
        var luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255d;
        if (luminance < 0.55) return color;

        var ratio = 0.45;
        return Color.FromArgb(
            color.A,
            (byte)Math.Round(color.R * ratio),
            (byte)Math.Round(color.G * ratio),
            (byte)Math.Round(color.B * ratio));
    }

    // ================= § 样式码 =================

    private static void ParseLegacy(string source, List<MotdSegment> segments)
    {
        var style = new StyleState();
        var buffer = new StringBuilder();
        var bufferStyle = style.Snapshot();

        void Flush()
        {
            if (buffer.Length == 0) return;
            segments.Add(bufferStyle.ToSegment(buffer.ToString()));
            buffer.Clear();
        }

        for (var index = 0; index < source.Length; index++)
        {
            var current = source[index];
            if (current != '\u00a7' || index + 1 >= source.Length)
            {
                buffer.Append(current);
                continue;
            }

            var code = char.ToLowerInvariant(source[index + 1]);
            var skip = 2;
            var next = new StyleState(style);

            if (code == 'x')
            {
                // Bungee/Spigot 的 hex 色序列：§x§r§r§g§g§b§b
                if (TryReadHexColor(source, index, out var hex, out var consumed))
                {
                    next = new StyleState { Color = hex };
                    skip = consumed;
                }
                else
                {
                    continue;
                }
            }
            else if (LegacyColors.TryGetValue(code, out var legacyColor))
            {
                // 颜色码按原版语义清空装饰样式
                next = new StyleState { Color = legacyColor };
            }
            else
            {
                switch (code)
                {
                    case 'k': next.Obfuscated = true; break;
                    case 'l': next.Bold = true; break;
                    case 'o': next.Italic = true; break;
                    case 'n': next.Underlined = true; break;
                    case 'm': next.Strikethrough = true; break;
                    case 'r': next = new StyleState(); break;
                    default: break;
                }

                if (code is not ('k' or 'l' or 'o' or 'n' or 'm' or 'r')) continue;
            }

            if (!next.Equals(style))
            {
                Flush();
                style = next;
                bufferStyle = style.Snapshot();
            }

            index += skip - 1;
        }

        Flush();
    }

    private static bool TryReadHexColor(string source, int start, out Color color, out int consumed)
    {
        color = default;
        consumed = 0;

        var digits = new char[6];
        var cursor = start + 2;
        for (var step = 0; step < 6; step++)
        {
            if (cursor + 1 >= source.Length) return false;
            if (source[cursor] != '\u00a7') return false;

            var digit = source[cursor + 1];
            if (!Uri.IsHexDigit(digit)) return false;

            digits[step] = digit;
            cursor += 2;
        }

        color = Color.Parse("#" + new string(digits));
        consumed = cursor - start;
        return true;
    }

    // ================= JSON 文本组件 =================

    private static bool TryParseJsonComponent(string source, out List<MotdSegment> segments)
    {
        segments = new List<MotdSegment>();

        var first = source[0];
        if (first != '{' && first != '[') return false;

        try
        {
            using var document = JsonDocument.Parse(source);
            FlattenJson(document.RootElement, new StyleState(), segments);
            return segments.Count > 0;
        }
        catch (JsonException)
        {
            segments.Clear();
            return false;
        }
    }

    private static void FlattenJson(JsonElement element, StyleState parent, List<MotdSegment> segments)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                FlattenJson(item, parent, segments);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object) return;

        var style = new StyleState(parent);

        if (element.TryGetProperty("color", out var colorProperty) && colorProperty.ValueKind == JsonValueKind.String)
        {
            style.Color = ParseJsonColor(colorProperty.GetString());
        }

        style.Bold = ReadBool(element, "bold", parent.Bold);
        style.Italic = ReadBool(element, "italic", parent.Italic);
        style.Underlined = ReadBool(element, "underlined", parent.Underlined);
        style.Strikethrough = ReadBool(element, "strikethrough", parent.Strikethrough);
        style.Obfuscated = ReadBool(element, "obfuscated", parent.Obfuscated);

        if (element.TryGetProperty("text", out var textProperty) && textProperty.ValueKind == JsonValueKind.String)
        {
            var text = textProperty.GetString();
            if (!string.IsNullOrEmpty(text)) segments.Add(style.ToSegment(text));
        }

        if (element.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in extra.EnumerateArray())
            {
                FlattenJson(item, style, segments);
            }
        }
    }

    private static bool ReadBool(JsonElement element, string name, bool fallback) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => fallback,
            }
            : fallback;

    private static Color? ParseJsonColor(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var value = name.Trim();
        if (value.Equals("reset", StringComparison.OrdinalIgnoreCase)) return null;
        if (value.StartsWith('#')) return Color.TryParse(value, out var hex) ? hex : null;
        if (NamedColors.TryGetValue(value, out var named)) return named;

        return null;
    }

    /// <summary>MOTD 样式状态（解析过程中的可变状态，按值比较）。</summary>
    private sealed class StyleState : IEquatable<StyleState>
    {
        public StyleState()
        {
        }

        public StyleState(StyleState other)
        {
            Color = other.Color;
            Bold = other.Bold;
            Italic = other.Italic;
            Underlined = other.Underlined;
            Strikethrough = other.Strikethrough;
            Obfuscated = other.Obfuscated;
        }

        public Color? Color { get; set; }
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public bool Underlined { get; set; }
        public bool Strikethrough { get; set; }
        public bool Obfuscated { get; set; }

        public StyleState Snapshot() => new(this);

        public MotdSegment ToSegment(string text) =>
            new(text, Color, Bold, Italic, Underlined, Strikethrough, Obfuscated);

        public bool Equals(StyleState? other) =>
            other is not null
            && Color == other.Color
            && Bold == other.Bold
            && Italic == other.Italic
            && Underlined == other.Underlined
            && Strikethrough == other.Strikethrough
            && Obfuscated == other.Obfuscated;

        public override bool Equals(object? obj) => Equals(obj as StyleState);

        public override int GetHashCode() =>
            HashCode.Combine(Color, Bold, Italic, Underlined, Strikethrough, Obfuscated);
    }
}
