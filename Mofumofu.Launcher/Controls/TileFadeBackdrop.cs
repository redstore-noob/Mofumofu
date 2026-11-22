using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Mofumofu.Launcher.Controls;

public sealed class TileFadeBackdrop : Control
{
    public static readonly StyledProperty<double> TileSizeProperty =
        AvaloniaProperty.Register<TileFadeBackdrop, double>(nameof(TileSize), 26);

    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<TileFadeBackdrop, double>(nameof(Gap), 3);

    public static readonly StyledProperty<Color> TileColorProperty =
        AvaloniaProperty.Register<TileFadeBackdrop, Color>(nameof(TileColor), Color.Parse("#002FA7"));

    public static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<TileFadeBackdrop, double>(nameof(CornerRadius), 8);

    static TileFadeBackdrop()
    {
        AffectsRender<TileFadeBackdrop>(
            TileSizeProperty, GapProperty, TileColorProperty, CornerRadiusProperty);
    }

    public double TileSize
    {
        get => GetValue(TileSizeProperty);
        set => SetValue(TileSizeProperty, value);
    }

    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public Color TileColor
    {
        get => GetValue(TileColorProperty);
        set => SetValue(TileColorProperty, value);
    }

    public double CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        double tile = Math.Max(6, TileSize);
        double gap = Math.Clamp(Gap, 0, tile / 2);
        double step = tile + gap;

        var clip = new Rect(0, 0, width, height);
        using (context.PushGeometryClip(new RectangleGeometry(clip, CornerRadius, CornerRadius)))
        {
            int columns = (int)Math.Ceiling((width + gap) / step);
            int rows = (int)Math.Ceiling((height + gap) / step);
            double radius = tile / 2;
            double startX = width / 2;
            double span = Math.Max(1, width - startX);

            for (int col = 0; col < columns; col++)
            {
                double centerX = col * step + radius;
                if (centerX <= startX) continue;

                double t = Math.Clamp((centerX - startX) / span, 0, 1);
                double alpha = Math.Pow(t, 1.3);
                if (alpha <= 0.01) continue;

                var brush = new SolidColorBrush(TileColor) { Opacity = alpha };
                for (int row = 0; row < rows; row++)
                {
                    double centerY = row * step + radius;
                    if (centerY - radius > height) break;

                    context.DrawEllipse(brush, null, new Point(centerX, centerY), radius, radius);
                }
            }
        }
    }
}
