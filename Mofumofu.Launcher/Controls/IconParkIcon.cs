using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Mofumofu.Launcher.Services;

namespace Mofumofu.Launcher.Controls;

public sealed class IconParkIcon : Control
{
    public static readonly StyledProperty<string?> KindProperty =
        AvaloniaProperty.Register<IconParkIcon, string?>(nameof(Kind));

    private static readonly Color[] Palette =
    [
        Color.Parse("#2F88FF"),
        Color.Parse("#3FC6C1"),
        Color.Parse("#8B7CF6"),
        Color.Parse("#F26D8C"),
        Color.Parse("#F2AE49"),
        Color.Parse("#4CC38A"),
        Color.Parse("#5A9CF8"),
        Color.Parse("#C084FC"),
    ];

    private static readonly Dictionary<string, IBrush?[]> ColorfulBrushes = new(StringComparer.Ordinal);
    private static readonly List<WeakReference<IconParkIcon>> LiveIcons = new();
    private static bool _useColorfulPalette = true;

    public IconParkIcon()
    {
        Width = 16;
        Height = 16;
    }

    public static bool UseColorfulPalette
    {
        get => _useColorfulPalette;
        set
        {
            _useColorfulPalette = value;
            NotifyPaletteChanged();
        }
    }

    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    static IconParkIcon()
    {
        AffectsRender<IconParkIcon>(KindProperty);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        lock (LiveIcons)
        {
            LiveIcons.Add(new WeakReference<IconParkIcon>(this));
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        lock (LiveIcons)
        {
            LiveIcons.RemoveAll(reference => !reference.TryGetTarget(out var icon) || icon == this);
        }
    }

    public static void NotifyPaletteChanged()
    {
        lock (ColorfulBrushes)
        {
            ColorfulBrushes.Clear();
        }

        List<IconParkIcon> icons = [];
        lock (LiveIcons)
        {
            for (var i = LiveIcons.Count - 1; i >= 0; i--)
            {
                if (!LiveIcons[i].TryGetTarget(out var icon))
                {
                    LiveIcons.RemoveAt(i);
                    continue;
                }

                icons.Add(icon);
            }
        }

        foreach (var icon in icons)
        {
            icon.InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        if (Kind is not { Length: > 0 } key) return;
        if (!IconParkGlyphs.ByKey.TryGetValue(key, out var shapes)) return;

        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;

        var scale = size / 48.0;
        var offsetX = (Bounds.Width - size) / 2;
        var offsetY = (Bounds.Height - size) / 2;
        var brushes = BrushesFor(key);

        using (context.PushTransform(
                   Matrix.CreateScale(scale, scale) *
                   Matrix.CreateTranslation(offsetX / scale, offsetY / scale)))
        {
            foreach (var shape in shapes)
            {
                IBrush? fill = shape.FillPaint == IconParkPaint.None ? null : brushes[(int)shape.FillPaint];
                Pen? pen = shape.HasStroke && shape.StrokePaint != IconParkPaint.None
                    ? new Pen(brushes[(int)shape.StrokePaint], 4,
                        lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round)
                    : null;

                context.DrawGeometry(fill, pen, shape.Geometry);
            }
        }
    }

    private static IBrush?[] BrushesFor(string key)
    {
        if (!_useColorfulPalette)
        {
            return
            [
                null,
                ThemeTokens.Brush("NekoAccentBrush"),
                ThemeTokens.Brush("NekoAccentSoftBrush"),
                ThemeTokens.Brush("NekoTextBrush"),
                ThemeTokens.Brush("NekoFieldSoftBrush"),
            ];
        }

        lock (ColorfulBrushes)
        {
            if (ColorfulBrushes.TryGetValue(key, out var cached)) return cached;
        }

        var hash = 17;
        foreach (var ch in key)
        {
            hash = unchecked(hash * 31 + ch);
        }

        var primary = Palette[Math.Abs(hash) % Palette.Length];
        var secondary = Palette[(Math.Abs(hash) + 3) % Palette.Length];

        var brushes = new IBrush?[]
        {
            null,
            new SolidColorBrush(primary),
            new SolidColorBrush(WithAlpha(primary, 0x40)),
            new SolidColorBrush(secondary),
            new SolidColorBrush(WithAlpha(secondary, 0x33)),
        };

        lock (ColorfulBrushes)
        {
            ColorfulBrushes[key] = brushes;
        }

        return brushes;
    }

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);
}
