using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Mofumofu.Launcher.Services;

namespace Mofumofu.Launcher.Controls;

/// <summary>
/// 旋转的渐变圈：一个带渐变描边的圆环，而不是由点拼出来的 loading。
/// 主题切换后重新取强调色。
/// </summary>
public sealed class GradientSpinner : UserControl
{
    private readonly Avalonia.Controls.Shapes.Ellipse _ring;
    private readonly DispatcherTimer _timer;
    private double _angle;

    public GradientSpinner()
    {
        Width = 30;
        Height = 30;
        IsHitTestVisible = false;

        _ring = new Avalonia.Controls.Shapes.Ellipse
        {
            StrokeThickness = 3,
            RenderTransformOrigin = RelativePoint.Center,
            RenderTransform = TransformOperations.Parse("rotate(0deg)"),
        };

        Content = _ring;
        ApplyBrush();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Rotate();
        AttachedToVisualTree += (_, _) =>
        {
            ApplyBrush();
            _timer.Start();
        };
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    private void Rotate()
    {
        _angle = (_angle + 7) % 360;
        _ring.RenderTransform = TransformOperations.Parse($"rotate({_angle:0.#}deg)");
    }

    private void ApplyBrush()
    {
        Color accent = ThemeTokens.Color("NekoAccentBrush");
        Color clear = Color.FromArgb(0, accent.R, accent.G, accent.B);

        _ring.Stroke = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(accent, 0),
                new GradientStop(Color.FromArgb(140, accent.R, accent.G, accent.B), 0.55),
                new GradientStop(clear, 1),
            },
        };
    }
}
