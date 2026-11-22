using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Mofumofu.Launcher.Services;

public static class UiAnimations
{
    private const int LayoutSettleMs = 16;
    private const int TransitionPaddingMs = 30;

    public static bool Enabled { get; set; } = true;

    private static readonly Dictionary<Control, int> PlayGeneration = new();

    public static void FadeSlideIn(Control? target, double fromScale = 0.985, double offsetY = 8, int milliseconds = 190)
    {
        PlayIn(target, 0, offsetY, fromScale, milliseconds, new CubicEaseOut());
    }

    public static void SlideInFromRight(Control? target, double offsetX = 56, int milliseconds = 260)
    {
        PlayIn(target, offsetX, 0, 1, milliseconds, new SplineEasing(new KeySpline(0.16, 1, 0.3, 1)));
    }

    private static async void PlayIn(Control? target, double offsetX, double offsetY, double fromScale,
        int milliseconds, Easing easing)
    {
        if (target is null) return;

        int duration = Math.Max(60, milliseconds);
        TransformOperations to = TransformOperations.Parse("none");

        target.Transitions = null;

        if (!Enabled)
        {
            target.Opacity = 1;
            target.RenderTransform = to;
            return;
        }

        // 同一个控件上重启动画时，让上一轮循环自行退出，避免两个循环抢着写属性。
        int generation = PlayGeneration.TryGetValue(target, out int latest) ? latest + 1 : 1;
        PlayGeneration[target] = generation;

        target.Opacity = 0;
        target.RenderTransform = MakeTransform(offsetX, offsetY, fromScale);

        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            while (true)
            {
                await Task.Delay(15);
                if (!PlayGeneration.TryGetValue(target, out int current) || current != generation) return;

                double progress = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds / duration;
                if (progress >= 1) break;

                double eased = easing.Ease(progress);

                target.Opacity = Math.Min(1d, eased / 0.45d);
                target.RenderTransform = MakeTransform(
                    offsetX * (1 - eased),
                    offsetY * (1 - eased),
                    1 + (fromScale - 1) * (1 - eased));
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine("[ui-animation] PlayIn: " + exception.Message);
        }
        finally
        {
            if (PlayGeneration.TryGetValue(target, out int tail) && tail == generation)
            {
                target.Opacity = 1;
                target.RenderTransform = to;
            }
        }
    }

    private static TransformOperations MakeTransform(double offsetX, double offsetY, double scale) =>
        TransformOperations.Parse(
            $"translate({Number(offsetX)}px, {Number(offsetY)}px) scale({Number(scale)})");

    public static void PopIn(Control? target, double fromScale = 0.86, double offsetY = 18, int milliseconds = 220)
    {
        PlayIn(target, 0, offsetY, fromScale, milliseconds, new CubicEaseOut());
    }

    public static void PopOut(Control? target, double toScale = 0.9, double offsetY = 14, int milliseconds = 150)
    {
        if (!Enabled || target is null) return;

        target.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(milliseconds),
            },
            new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = TimeSpan.FromMilliseconds(milliseconds),
                Easing = new CubicEaseIn(),
            },
        };

        target.Opacity = 0;
        target.RenderTransform = TransformOperations.Parse(
            $"translateY({offsetY}px) scale({toScale.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
    }

    public static void PopFrom(Control? target, RelativePoint origin, double fromScale = 0.82, int milliseconds = 220)
    {
        if (target is null || !Enabled) return;

        target.RenderTransformOrigin = origin;
        PlayIn(target, 0, 0, fromScale, milliseconds, new CubicEaseOut());
    }

    public static void PopOutFrom(Control? target, RelativePoint origin, double toScale = 0.88, int milliseconds = 150)
    {
        if (!Enabled || target is null) return;

        target.RenderTransformOrigin = origin;
        target.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(milliseconds),
            },
            new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = TimeSpan.FromMilliseconds(milliseconds),
                Easing = new CubicEaseIn(),
            },
        };

        target.Opacity = 0;
        target.RenderTransform = TransformOperations.Parse(
            $"scale({toScale.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
    }

    public static void FadeSwap(Control? target, int milliseconds = 150)
    {
        PlayIn(target, 0, 0, 1, milliseconds, new CubicEaseOut());
    }

    public static void Reset(Control? target)
    {
        if (target is null) return;
        target.Opacity = 1;
        target.RenderTransform = TransformOperations.Parse("translateY(0px) scale(1)");
    }

    public static void FadeSlideOut(Control? target, double toScale = 0.96, double offsetY = 6,
        int milliseconds = 150)
    {
        if (target is null) return;

        if (!Enabled)
        {
            target.Opacity = 0;
            return;
        }

        target.Transitions = BuildTransitions(milliseconds, new CubicEaseIn());
        target.Opacity = 0;
        target.RenderTransform = ParseOrIdentity(
            $"translateY({Number(offsetY)}px) scale({Number(toScale)})");
    }

    public static async void Pulse(Control? target, double peak = 1.22, int milliseconds = 220)
    {
        if (!Enabled || target is null) return;

        bool checkAlive = TopLevel.GetTopLevel(target) is not null;

        try
        {
            var half = Math.Max(60, milliseconds / 2);
            target.Transitions = BuildTransitions(half, new CubicEaseOut());
            target.RenderTransform = ParseOrIdentity($"scale({Number(peak)})");
            await Task.Delay(half);

            if (checkAlive && !IsAlive(target)) return;
            target.Transitions = BuildTransitions(half, new CubicEaseOut());
            target.RenderTransform = ParseOrIdentity("scale(1)");
        }
        catch (Exception exception)
        {
            // async void 的异常无法被外部观察，必须在这里兜底，避免跑到同步上下文里崩进程。
            System.Diagnostics.Debug.WriteLine("[ui-animation] Pulse: " + exception.Message);
        }
    }

    public static void Stagger(IEnumerable<Control?> targets, int step = 45, int milliseconds = 190,
        double offsetY = 8)
    {
        var items = new List<Control>();
        foreach (var target in targets)
        {
            if (target is not null) items.Add(target);
        }

        if (items.Count > 1) step = Math.Min(step, Math.Max(6, 400 / (items.Count - 1)));

        var delay = 0;
        foreach (var target in items)
        {
            if (!Enabled)
            {
                target.Opacity = 1;
                continue;
            }

            target.Transitions = null;
            target.Opacity = 0;
            target.RenderTransform = ParseOrIdentity(
                $"translateY({Number(offsetY)}px) scale(0.99)");
            // 即发即忘但观察异常，避免未观察异常；控件存活检查在 RevealLaterAsync 内部。
            // 记录调用时是否已上树：从未上树的控件仍要恢复可见，不能被存活检查误跳过。
            bool checkAlive = TopLevel.GetTopLevel(target) is not null;
            _ = RevealLaterAsync(target, delay, milliseconds, checkAlive).ContinueWith(
                static t => { _ = t.Exception; }, TaskScheduler.Default);
            delay += step;
        }
    }

    private static async Task RevealLaterAsync(
        Control target, int delay, int milliseconds, bool checkAlive)
    {
        try
        {
            if (delay > 0) await Task.Delay(delay);

            if (checkAlive && !IsAlive(target)) return;
            target.Transitions = BuildTransitions(milliseconds, new CubicEaseOut());
            target.Opacity = 1;
            target.RenderTransform = ParseOrIdentity("translateY(0px) scale(1)");
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine("[ui-animation] RevealLater: " + exception.Message);
        }
    }

    public static async Task SetExpandedAsync(Control? target, bool expanded, int milliseconds = 200)
    {
        if (target is null) return;

        if (!Enabled)
        {
            target.IsVisible = expanded;
            target.Height = double.NaN;
            return;
        }

        if (expanded)
        {
            target.IsVisible = true;
            target.Height = double.NaN;
            double full = target.Bounds.Height;
            if (full <= 0)
            {
                target.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                full = target.DesiredSize.Height;
            }

            if (full <= 0)
            {
                target.Height = double.NaN;
                return;
            }

            target.Height = 0;
            target.Transitions = BuildTransitions(milliseconds, new CubicEaseOut());
            await Task.Delay(LayoutSettleMs);
            target.Height = full;
            await Task.Delay(milliseconds + TransitionPaddingMs);
            target.Transitions = null;
            target.Height = double.NaN;
            return;
        }

        double current = target.Bounds.Height;
        if (current <= 0)
        {
            target.IsVisible = false;
            target.Height = double.NaN;
            return;
        }

        target.Height = current;
        target.Transitions = BuildTransitions(milliseconds, new CubicEaseIn());
        await Task.Delay(LayoutSettleMs);
        target.Height = 0;
        await Task.Delay(milliseconds + 30);
        target.Transitions = null;
        target.IsVisible = false;
        target.Height = double.NaN;
    }

    private static Transitions BuildTransitions(int milliseconds, Easing easing) => new()
    {
        new DoubleTransition
        {
            Property = Visual.OpacityProperty,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            Easing = easing,
        },
        new TransformOperationsTransition
        {
            Property = Visual.RenderTransformProperty,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            Easing = easing,
        },
    };

    private static TransformOperations ParseOrIdentity(string value)
    {
        try
        {
            return TransformOperations.Parse(value);
        }
        catch
        {
            return TransformOperations.Parse("translateY(0px) scale(1)");
        }
    }

    private static string Number(double value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static bool IsAlive(Control? target) =>
        target is not null && TopLevel.GetTopLevel(target) is not null;
}
