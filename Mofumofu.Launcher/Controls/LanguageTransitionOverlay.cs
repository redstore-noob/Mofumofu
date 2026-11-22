using System;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Mofumofu.Launcher.Services;

namespace Mofumofu.Launcher.Controls;

public sealed class LanguageTransitionOverlay : Control
{
    private const double FadeIn = 0.16;
    private const double SwapAt = 0.4;
    private const double FadeOut = 0.3;
    private const double SafetyEnd = 6;

    private static readonly (string Text, double X, double Y)[] Greets =
    {
        ("你好", -0.34, -6),
        ("Hello", 0.30, 4),
        ("こんにちは", -0.10, -2),
        ("Hallo", 0.14, 6),
        ("Привет", -0.30, -4),
        ("Saluton", 0.34, 2),
        ("您好", 0.0, 0),
    };

    private const double Stagger = 0.16;
    private const double Life = 0.7;
    private static readonly double Cycle = Greets.Length * Stagger + Life;

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private Action? _swap;
    private Action? _queued;
    private bool _swapped;
    private bool _swapDone;
    private bool _fadeIn = true;
    private double _doneAt;

    public LanguageTransitionOverlay()
    {
        IsHitTestVisible = true;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Tick();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    public void Play(Action swap)
    {
        if (!_clock.IsRunning)
        {
            _swap = swap;
            _swapped = false;
            _swapDone = false;
            _fadeIn = true;
            IsVisible = true;
            _clock.Restart();
            _timer.Start();
            InvalidateVisual();
            return;
        }

        if (_swapDone)
        {
            // 上一轮已重建完、正在淡出：接着罩住再来一轮，跳过淡入避免闪暗
            _swap = swap;
            _swapped = false;
            _swapDone = false;
            _fadeIn = false;
            _clock.Restart();
            InvalidateVisual();
            return;
        }

        // 淡入中或重建中：排队，本轮重建完成后立刻接下一轮，不吞掉这次切换
        _queued = swap;
    }

    public void ReportDone()
    {
        if (!_clock.IsRunning || _swapDone) return;
        if (_queued is { } next)
        {
            _queued = null;
            _swap = next;
            _swapped = false;
            _fadeIn = false;
            _clock.Restart();
            return;
        }
        _swapDone = true;
        _doneAt = _clock.Elapsed.TotalSeconds;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Handled = true;
    }

    private void Tick()
    {
        var t = _clock.Elapsed.TotalSeconds;

        if (!_swapped && t >= SwapAt)
        {
            _swapped = true;
            _swap?.Invoke();
        }

        if (t >= SafetyEnd && _queued is { } queued)
        {
            // 重建超时兜底：排队的切换不能吞，罩住再试一轮
            _queued = null;
            _swap = queued;
            _swapped = false;
            _swapDone = false;
            _fadeIn = false;
            _clock.Restart();
            InvalidateVisual();
            return;
        }

        if ((_swapDone && t - _doneAt >= FadeOut) || t >= SafetyEnd)
        {
            _timer.Stop();
            _clock.Stop();
            IsVisible = false;
            _swap = null;
            _queued = null;
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var t = _clock.Elapsed.TotalSeconds;
        var envelope = Envelope(t);
        if (envelope <= 0) return;

        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var scrim = dark ? Color.FromArgb(210, 16, 20, 28) : Color.FromArgb(225, 238, 241, 246);
        scrim = Color.FromArgb((byte)(scrim.A * envelope), scrim.R, scrim.G, scrim.B);
        context.FillRectangle(new SolidColorBrush(scrim), Bounds);

        var text = ThemeTokens.Brush("NekoTextBrush");
        var accent = ThemeTokens.Color("NekoAccentBrush");
        var track = ThemeTokens.Brush("NekoBorderBrush");

        var cx = Bounds.Width / 2;
        var cy = Bounds.Height / 2;
        var width = Math.Clamp(Bounds.Width * 0.42, 220, 420);

        using (context.PushOpacity(envelope))
        {
            DrawGreets(context, t, cx, cy, width, text);
            DrawProgress(context, t, cx, cy, width, track, accent);
        }
    }

    private void DrawGreets(DrawingContext context, double t, double cx, double cy, double width, IBrush text)
    {
        var tc = t % Cycle;
        for (var i = 0; i < Greets.Length; i++)
        {
            var lt = tc - i * Stagger;
            if (lt < 0 || lt > Life) continue;

            var alpha = lt < 0.22 ? lt / 0.22 : lt > Life - 0.3 ? (Life - lt) / 0.3 : 1;
            var drift = 18 * (lt / Life);
            var (greet, fx, fy) = Greets[i];

            var formatted = new FormattedText(greet, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), 17, text);
            var x = cx + fx * width - formatted.Width / 2;
            var y = cy - 40 + fy - drift;

            using (context.PushOpacity(Math.Clamp(alpha, 0, 1)))
            {
                context.DrawText(formatted, new Point(x, y));
            }
        }
    }

    private void DrawProgress(DrawingContext context, double t, double cx, double cy, double width,
        IBrush track, Color accent)
    {
        var timeP = 1 - Math.Pow(1 - Math.Clamp((t - FadeIn) / 1.2, 0, 1), 3);
        var p = _swapDone ? 1 : timeP * 0.9;

        var rect = new Rect(cx - width / 2, cy + 26, width, 6);
        context.DrawRectangle(track, null, rect, 3, 3);

        if (p > 0.01)
        {
            var fill = new Rect(rect.X, rect.Y, width * p, rect.Height);
            context.DrawRectangle(new SolidColorBrush(accent), null, fill, 3, 3);
        }
    }

    private double Envelope(double t)
    {
        if (_fadeIn && t < FadeIn) return t / FadeIn;
        if (!_swapDone) return 1;
        return Math.Clamp(1 - (t - _doneAt) / FadeOut, 0, 1);
    }
}