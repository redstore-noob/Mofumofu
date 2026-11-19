using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Mofumofu.Launcher.Controls;

/// <summary>黄黑斜纹（危险警告带）底纹，用于核弹发射按钮。</summary>
public sealed class HazardStripeControl : Control
{
    public static readonly StyledProperty<IBrush?> BaseBrushProperty =
        AvaloniaProperty.Register<HazardStripeControl, IBrush?>(nameof(BaseBrush));

    public static readonly StyledProperty<IBrush?> StripeBrushProperty =
        AvaloniaProperty.Register<HazardStripeControl, IBrush?>(nameof(StripeBrush));

    public static readonly StyledProperty<double> StripeWidthProperty =
        AvaloniaProperty.Register<HazardStripeControl, double>(nameof(StripeWidth), 16);

    public static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<HazardStripeControl, double>(nameof(CornerRadius), 14);

    static HazardStripeControl()
    {
        AffectsRender<HazardStripeControl>(
            BaseBrushProperty, StripeBrushProperty, StripeWidthProperty, CornerRadiusProperty);
    }

    public IBrush? BaseBrush
    {
        get => GetValue(BaseBrushProperty);
        set => SetValue(BaseBrushProperty, value);
    }

    public IBrush? StripeBrush
    {
        get => GetValue(StripeBrushProperty);
        set => SetValue(StripeBrushProperty, value);
    }

    public double StripeWidth
    {
        get => GetValue(StripeWidthProperty);
        set => SetValue(StripeWidthProperty, value);
    }

    public double CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        var rect = new Rect(0, 0, width, height);
        using (context.PushGeometryClip(new RectangleGeometry(rect, CornerRadius, CornerRadius)))
        {
            if (BaseBrush is { } baseBrush) context.FillRectangle(baseBrush, rect);
            if (StripeBrush is not { } stripeBrush) return;

            var stripe = Math.Max(4, StripeWidth);
            var extent = width + height;
            using (context.PushTransform(Matrix.CreateRotation(Math.PI / 4)))
            {
                for (var x = -extent; x < extent; x += stripe * 2)
                {
                    context.FillRectangle(stripeBrush, new Rect(x, -extent, stripe, extent * 2));
                }
            }
        }
    }
}
