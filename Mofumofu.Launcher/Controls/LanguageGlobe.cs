using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Mofumofu.Launcher.Services;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Controls;

public sealed class LanguageGlobe : Control
{
    private const double GlobeRadius = 50;
    private const double OrbitRadius = 82;
    private const double OrbitTilt = 9;
    private const double FlagWidth = 36;
    private const double FlagHeight = 24;

    private static readonly Dictionary<string, Bitmap> FlagCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlyList<L.LanguageOption> _options = L.Languages;
    private readonly double _step;
    private readonly DispatcherTimer _timer;
    private readonly List<(Rect Bounds, int Index, double Depth)> _flagHits = new();
    private readonly Random _random = new();

    private double _rotation;
    private double _velocity;
    private double _snapTarget;
    private SpinState _state;
    private bool _dragMoved;
    private double _lastX;
    private double _pressX;
    private int _pressedFlag = -1;
    private DateTime _lastMoveTime;

    private IBrush _textBrush = Brushes.White;
    private IBrush _wireBrush = Brushes.White;
    private IBrush _borderBrush = Brushes.White;
    private IBrush _sphereBrush = Brushes.Transparent;
    private IBrush _accentBrush = Brushes.Teal;

    private enum SpinState { Idle, Drag, Inertia, Snap }

    public LanguageGlobe()
    {
        _step = Math.PI * 2 / _options.Count;
        Width = 216;
        Height = 158;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Tick();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public event Action<string>? LanguageSelected;

    private int FrontIndex
    {
        get
        {
            var k = (int)Math.Round(_rotation / _step);
            return ((-k) % _options.Count + _options.Count) % _options.Count;
        }
    }

    public void SetLanguage(string? code)
    {
        var index = 0;
        for (var i = 0; i < _options.Count; i++)
        {
            if (string.Equals(_options[i].Code, code, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        _rotation = -index * _step;
        _state = SpinState.Idle;
        _timer.Stop();
        InvalidateVisual();
    }

    private void RefreshBrushes()
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var accent = ThemeTokens.Color("NekoAccentBrush");
        _textBrush = ThemeTokens.Brush("NekoTextBrush");
        _borderBrush = ThemeTokens.Brush("NekoBorderBrush");
        _accentBrush = new SolidColorBrush(accent);
        _wireBrush = new SolidColorBrush(dark ? Color.FromArgb(72, 255, 255, 255) : Color.FromArgb(52, 0, 0, 0));

        var highlight = ThemeTokens.Blend(accent, Colors.White, dark ? 0.35 : 0.6);
        var shade = ThemeTokens.Blend(accent, Colors.Black, dark ? 0.3 : 0.05);
        _sphereBrush = new RadialGradientBrush
        {
            Center = new RelativePoint(0.35, 0.3, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(150, highlight.R, highlight.G, highlight.B), 0),
                new GradientStop(Color.FromArgb(60, shade.R, shade.G, shade.B), 1),
            },
        };
    }

    public override void Render(DrawingContext context)
    {
        RefreshBrushes();
        var center = new Point(Bounds.Width / 2, 64);
        _flagHits.Clear();

        var items = new List<(int Index, double Depth, Rect Bounds)>();
        for (var i = 0; i < _options.Count; i++)
        {
            var theta = _rotation + i * _step;
            var depth = Math.Cos(theta);
            var scale = 0.55 + 0.45 * (depth + 1) / 2;
            var w = FlagWidth * scale;
            var h = FlagHeight * scale;
            var x = center.X + OrbitRadius * Math.Sin(theta);
            var y = center.Y + OrbitTilt * depth;
            var bounds = new Rect(x - w / 2, y - h / 2, w, h);
            items.Add((i, depth, bounds));
            _flagHits.Add((bounds.Inflate(4), i, depth));
        }

        foreach (var item in items.Where(item => item.Depth < 0).OrderBy(item => item.Depth))
        {
            DrawFlag(context, item.Index, item.Depth, item.Bounds);
        }

        DrawGlobe(context, center);

        foreach (var item in items.Where(item => item.Depth >= 0).OrderBy(item => item.Depth))
        {
            DrawFlag(context, item.Index, item.Depth, item.Bounds);
        }

        DrawFrontName(context, center);
    }

    private void DrawGlobe(DrawingContext context, Point center)
    {
        context.DrawEllipse(_sphereBrush, new Pen(_borderBrush, 1), center, GlobeRadius, GlobeRadius);

        var wirePen = new Pen(_wireBrush, 1);
        for (var k = 0; k < 4; k++)
        {
            var phi = _rotation + k * Math.PI / 4;
            var rx = GlobeRadius * Math.Abs(Math.Sin(phi));
            if (rx < 2) continue;
            context.DrawEllipse(null, wirePen, center, rx, GlobeRadius);
        }

        foreach (var t in new[] { -0.55, 0.0, 0.55 })
        {
            var rx = GlobeRadius * Math.Sqrt(1 - t * t);
            context.DrawEllipse(null, wirePen,
                new Point(center.X, center.Y + GlobeRadius * t), rx, rx * 0.22);
        }
    }

    private void DrawFlag(DrawingContext context, int index, double depth, Rect bounds)
    {
        if (TryGetFlag(_options[index].FlagImage) is not { } bitmap) return;

        var opacity = 0.45 + 0.55 * (depth + 1) / 2;
        using (context.PushOpacity(opacity))
        {
            var px = bitmap.PixelSize;
            var scale = Math.Min(bounds.Width / px.Width, bounds.Height / px.Height);
            var w = px.Width * scale;
            var h = px.Height * scale;
            var dest = new Rect(bounds.X + (bounds.Width - w) / 2, bounds.Y + (bounds.Height - h) / 2, w, h);

            var shadow = new SolidColorBrush(Color.FromArgb(45, 0, 0, 0));
            context.DrawRectangle(shadow, null, dest.Translate(new Vector(0, 1.5)).Inflate(1), 3, 3);
            context.DrawImage(bitmap, dest);

            if (depth > 0.92)
            {
                context.DrawRectangle(null, new Pen(_accentBrush, 1.6), dest.Inflate(2.5), 4, 4);
            }
        }
    }

    private void DrawFrontName(DrawingContext context, Point center)
    {
        var option = _options[FrontIndex];
        var name = option.Code == L.SystemLanguage
            ? L.T("Settings.General.Language.System", option.Name)
            : option.Name;
        var text = new FormattedText(name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), 13, _textBrush);
        context.DrawText(text, new Point(center.X - text.Width / 2, center.Y + GlobeRadius + 14));
    }

    private static Bitmap? TryGetFlag(string uri)
    {
        if (FlagCache.TryGetValue(uri, out var cached)) return cached;

        try
        {
            using var stream = AssetLoader.Open(new Uri(uri));
            var bitmap = new Bitmap(stream);
            FlagCache[uri] = bitmap;
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var point = e.GetPosition(this);
        _state = SpinState.Drag;
        _velocity = 0;
        _dragMoved = false;
        _pressX = point.X;
        _lastX = point.X;
        _lastMoveTime = DateTime.UtcNow;
        _pressedFlag = _flagHits
            .Where(hit => hit.Bounds.Contains(point))
            .OrderByDescending(hit => hit.Depth)
            .Select(hit => (int?)hit.Index)
            .FirstOrDefault() ?? -1;

        e.Pointer.Capture(this);
        e.Handled = true;
        Focus();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_state != SpinState.Drag) return;

        var point = e.GetPosition(this);
        var dx = point.X - _lastX;
        var now = DateTime.UtcNow;
        var dt = Math.Max((now - _lastMoveTime).TotalMilliseconds, 1);
        var delta = dx / OrbitRadius;

        _rotation += delta;
        _velocity = 0.7 * _velocity + 0.3 * (delta / dt * 16);
        _lastX = point.X;
        _lastMoveTime = now;
        if (Math.Abs(point.X - _pressX) > 4) _dragMoved = true;

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_state != SpinState.Drag) return;

        e.Pointer.Capture(null);
        e.Handled = true;

        if (!_dragMoved)
        {
            if (_pressedFlag >= 0)
            {
                if (_pressedFlag == FrontIndex)
                {
                    _state = SpinState.Idle;
                    LanguageSelected?.Invoke(_options[_pressedFlag].Code);
                }
                else
                {
                    SnapTo(_pressedFlag);
                }
            }
            else
            {
                var center = new Point(Bounds.Width / 2, 64);
                var d = e.GetPosition(this) - center;
                if (d.X * d.X + d.Y * d.Y <= GlobeRadius * GlobeRadius)
                {
                    // 点地球本体：随手一拨，方向随机
                    _velocity = (_random.NextDouble() < 0.5 ? -1 : 1) * (0.10 + _random.NextDouble() * 0.06);
                    _state = SpinState.Inertia;
                    _timer.Start();
                }
                else
                {
                    _state = SpinState.Idle;
                }
            }
            return;
        }

        if (Math.Abs(_velocity) < 0.006)
        {
            SnapToNearest();
        }
        else
        {
            _state = SpinState.Inertia;
            _timer.Start();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Step(e.Delta.Y > 0 ? -1 : 1);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Left:
                Step(1);
                e.Handled = true;
                break;
            case Key.Right:
                Step(-1);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Space:
                LanguageSelected?.Invoke(_options[FrontIndex].Code);
                e.Handled = true;
                break;
        }
    }

    private void Step(int direction)
    {
        _snapTarget = (Math.Round(_rotation / _step) + direction) * _step;
        _state = SpinState.Snap;
        _timer.Start();
    }

    private void SnapTo(int index)
    {
        var target = -index * _step;
        var diff = (target - _rotation) % (Math.PI * 2);
        if (diff > Math.PI) diff -= Math.PI * 2;
        if (diff < -Math.PI) diff += Math.PI * 2;
        _snapTarget = _rotation + diff;
        _state = SpinState.Snap;
        _timer.Start();
    }

    private void SnapToNearest()
    {
        _snapTarget = Math.Round(_rotation / _step) * _step;
        _state = SpinState.Snap;
        _timer.Start();
    }

    private void Tick()
    {
        switch (_state)
        {
            case SpinState.Inertia:
                _rotation += _velocity;
                _velocity *= 0.94;
                if (Math.Abs(_velocity) < 0.006) SnapToNearest();
                break;
            case SpinState.Snap:
                var diff = _snapTarget - _rotation;
                if (Math.Abs(diff) < 0.003)
                {
                    _rotation = _snapTarget;
                    _state = SpinState.Idle;
                    _timer.Stop();
                    LanguageSelected?.Invoke(_options[FrontIndex].Code);
                }
                else
                {
                    _rotation += diff * 0.22;
                }
                break;
            default:
                _timer.Stop();
                break;
        }

        InvalidateVisual();
    }
}