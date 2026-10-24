using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Mofumofu.Launcher.Controls;

/// <summary>
/// HSV 拾色轮：外圈色相环 + 内部饱和度圆盘 + 右侧明度滑条。
/// 自己绘制，不依赖额外的 ColorPicker 包；颜色变化通过 <see cref="ColorChanged"/> 通知。
/// </summary>
public sealed class ColorWheel : Control
{
    private const int RingSegments = 72;

    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<ColorWheel, Color>(nameof(Color), Colors.White);

    private readonly List<(Geometry Geometry, IBrush Brush)> _ringSegments = new();
    private double _builtWidth = -1;
    private double _builtHeight = -1;
    private int _dragMode; // 0 无 / 1 色相环 / 2 饱和度圆盘 / 3 明度滑条
    private double _hue;   // 0..360
    private double _sat;   // 0..1
    private double _val = 1; // 0..1

    public ColorWheel()
    {
        ClipToBounds = true;
        var initial = Colors.White;
        var (h, s, v) = ToHsv(initial);
        _hue = h;
        _sat = s;
        _val = v;
    }

    /// <summary>玩家选中的颜色变化时触发（用户拖动/点击拾色轮时）。</summary>
    public event Action<Color>? ColorChanged;

    public Color Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ColorProperty) return;
        if (change.NewValue is not Color color) return;

        var (h, s, v) = ToHsv(color);
        _hue = h;
        _sat = s;
        _val = v;
        InvalidateVisual();
    }

    // ==================== 绘制 ====================

    public override void Render(DrawingContext context)
    {
        var layout = ComputeLayout();
        EnsureRing(layout);

        // 色相环
        foreach (var (geometry, brush) in _ringSegments)
        {
            context.DrawGeometry(brush, null, geometry);
        }

        // 饱和度圆盘：底色为纯色相，白色径向覆盖（中心 S=0），再叠一层黑色表示明度
        var hueBrush = new SolidColorBrush(FromHsv(_hue, 1, 1));
        context.DrawEllipse(hueBrush, null, layout.Center, layout.DiscRadius, layout.DiscRadius);

        var whiteCenter = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(255, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
            },
        };
        context.DrawEllipse(whiteCenter, null, layout.Center, layout.DiscRadius, layout.DiscRadius);

        var dark = (byte)Math.Clamp(Math.Round((1 - _val) * 255), 0, 255);
        if (dark > 0)
        {
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(dark, 0, 0, 0)), null,
                layout.Center, layout.DiscRadius, layout.DiscRadius);
        }

        context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)), 1),
            layout.Center, layout.DiscRadius, layout.DiscRadius);

        // 明度滑条：顶部纯色相 → 底部黑色
        var sliderBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(FromHsv(_hue, 1, 1), 0),
                new GradientStop(Color.FromArgb(255, 0, 0, 0), 1),
            },
        };
        context.DrawRectangle(sliderBrush, null, layout.SliderRect, 13, 13);

        // 色相环滑块
        var ringAngle = _hue * Math.PI / 180;
        var ringPos = new Point(
            layout.Center.X + Math.Sin(ringAngle) * layout.RingKnobRadius,
            layout.Center.Y - Math.Cos(ringAngle) * layout.RingKnobRadius);
        context.DrawEllipse(new SolidColorBrush(FromHsv(_hue, 1, 1)),
            new Pen(Brushes.White, 2.5), ringPos, 9, 9);

        // 饱和度滑块
        var satPos = new Point(
            layout.Center.X + Math.Sin(ringAngle) * _sat * layout.DiscRadius,
            layout.Center.Y - Math.Cos(ringAngle) * _sat * layout.DiscRadius);
        context.DrawEllipse(new SolidColorBrush(FromHsv(_hue, _sat, _val)),
            new Pen(Brushes.White, 2.5), satPos, 7.5, 7.5);

        // 明度滑块
        var sliderY = layout.SliderRect.Y + (1 - _val) * layout.SliderRect.Height;
        var sliderKnob = new Rect(
            layout.SliderRect.X - 4, sliderY - 8, layout.SliderRect.Width + 8, 16);
        context.DrawRectangle(Brushes.White, null, sliderKnob, 8, 8);
    }

    private void EnsureRing(Layout layout)
    {
        if (Math.Abs(layout.OuterRadius - _builtWidth) < 0.5
            && Math.Abs(layout.Center.Y * 2 - _builtHeight) < 0.5
            && _ringSegments.Count > 0)
        {
            return;
        }

        _builtWidth = layout.OuterRadius;
        _builtHeight = layout.Center.Y * 2;
        _ringSegments.Clear();

        var step = 360.0 / RingSegments;
        for (var i = 0; i < RingSegments; i++)
        {
            var start = i * step;
            var end = start + step + 0.6; // 稍微重叠，避免缝隙
            var hueColor = FromHsv((start + step / 2) % 360, 1, 1);
            _ringSegments.Add((BuildSegment(layout, start, end), new SolidColorBrush(hueColor)));
        }
    }

    private static Geometry BuildSegment(Layout layout, double startDeg, double endDeg)
    {
        var outerStart = Polar(layout.Center, layout.OuterRadius, startDeg);
        var outerEnd = Polar(layout.Center, layout.OuterRadius, endDeg);
        var innerEnd = Polar(layout.Center, layout.InnerRadius, endDeg);
        var innerStart = Polar(layout.Center, layout.InnerRadius, startDeg);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(outerStart, true);
            ctx.LineTo(outerEnd);
            ctx.LineTo(innerEnd);
            ctx.LineTo(innerStart);
            ctx.EndFigure(true);
        }

        return geometry;
    }

    private static Point Polar(Point center, double radius, double angleDeg)
    {
        var angle = angleDeg * Math.PI / 180;
        return new Point(center.X + Math.Sin(angle) * radius, center.Y - Math.Cos(angle) * radius);
    }

    private Layout ComputeLayout()
    {
        var width = Bounds.Width > 1 ? Bounds.Width : 340;
        var height = Bounds.Height > 1 ? Bounds.Height : 300;
        var center = new Point(130, height / 2);
        var slider = new Rect(width - 56, 40, 26, Math.Max(80, height - 80));
        return new Layout(center, 126, 104, 98, 115, slider);
    }

    private readonly record struct Layout(
        Point Center, double OuterRadius, double InnerRadius, double DiscRadius,
        double RingKnobRadius, Rect SliderRect);

    // ==================== 输入 ====================

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        var layout = ComputeLayout();

        if (layout.SliderRect.Inflate(10).Contains(point))
        {
            _dragMode = 3;
            UpdateValue(point, layout);
        }
        else
        {
            var distance = Distance(point, layout.Center);
            if (distance <= layout.DiscRadius)
            {
                _dragMode = 2;
                UpdateSaturation(point, layout);
            }
            else if (distance <= layout.OuterRadius + 10)
            {
                _dragMode = 1;
                UpdateHue(point, layout);
            }
            else
            {
                return;
            }
        }

        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
        RaiseColorChanged();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragMode == 0) return;

        var point = e.GetPosition(this);
        var layout = ComputeLayout();
        switch (_dragMode)
        {
            case 1: UpdateHue(point, layout); break;
            case 2: UpdateSaturation(point, layout); break;
            case 3: UpdateValue(point, layout); break;
        }

        InvalidateVisual();
        RaiseColorChanged();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragMode == 0) return;

        _dragMode = 0;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void UpdateHue(Point point, Layout layout)
    {
        var dx = point.X - layout.Center.X;
        var dy = layout.Center.Y - point.Y;
        var angle = Math.Atan2(dx, dy) * 180 / Math.PI;
        _hue = (angle + 360) % 360;
    }

    private void UpdateSaturation(Point point, Layout layout)
    {
        var distance = Math.Clamp(Distance(point, layout.Center) / layout.DiscRadius, 0, 1);
        _sat = distance;
    }

    private void UpdateValue(Point point, Layout layout)
    {
        var ratio = (point.Y - layout.SliderRect.Y) / Math.Max(1, layout.SliderRect.Height);
        _val = Math.Clamp(1 - ratio, 0, 1);
    }

    private void RaiseColorChanged()
    {
        var color = FromHsv(_hue, _sat, _val);
        SetCurrentValue(ColorProperty, color);
        ColorChanged?.Invoke(color);
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // ==================== HSV 工具 ====================

    /// <summary>RGB → HSV（H:0..360 / S,V:0..1）。</summary>
    public static (double H, double S, double V) ToHsv(Color color)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        double hue;
        if (delta < 1e-6) hue = 0;
        else if (max == r) hue = 60 * (((g - b) / delta) % 6);
        else if (max == g) hue = 60 * ((b - r) / delta + 2);
        else hue = 60 * ((r - g) / delta + 4);

        if (hue < 0) hue += 360;
        var sat = max <= 0 ? 0 : delta / max;
        return (hue, sat, max);
    }

    /// <summary>HSV → RGB。</summary>
    public static Color FromHsv(double hue, double sat, double val)
    {
        hue = ((hue % 360) + 360) % 360;
        sat = Math.Clamp(sat, 0, 1);
        val = Math.Clamp(val, 0, 1);

        var c = val * sat;
        var x = c * (1 - Math.Abs((hue / 60) % 2 - 1));
        var m = val - c;

        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Color.FromArgb(255,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
