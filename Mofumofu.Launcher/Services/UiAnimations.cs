using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace Mofumofu.Launcher.Services;

/// <summary>统一的轻量过渡动画：淡入/缩放/下滑，供页面、标签页、开始菜单和小组件复用。</summary>
public static class UiAnimations
{
    /// <summary>全局过渡动画开关：关掉后页面/标签直接切换，不做淡入滑入。</summary>
    public static bool Enabled { get; set; } = true;

    public static void FadeSlideIn(Control? target, double fromScale = 0.985, double offsetY = 8, int milliseconds = 190)
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
                Easing = new CubicEaseOut(),
            },
        };

        target.Opacity = 0;
        target.RenderTransform = TransformOperations.Parse(
            $"translateY({offsetY}px) scale({fromScale.ToString(System.Globalization.CultureInfo.InvariantCulture)})");

        Dispatcher.UIThread.Post(() =>
        {
            target.Opacity = 1;
            target.RenderTransform = TransformOperations.Parse("translateY(0px) scale(1)");
        }, DispatcherPriority.Render);
    }

    /// <summary>开始菜单弹出：从按钮方向缩放 + 上浮。</summary>
    public static void PopIn(Control? target, double fromScale = 0.86, double offsetY = 18, int milliseconds = 220)
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
                Easing = new CubicEaseOut(),
            },
        };

        target.Opacity = 0;
        target.RenderTransform = TransformOperations.Parse(
            $"translateY({offsetY}px) scale({fromScale.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
    }

    /// <summary>开始菜单收起：缩回按钮方向并淡出。</summary>
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

    /// <summary>从指定相对原点（0-1，相对自身宽高）缩放弹出，用于从触发控件位置长出来的浮层。</summary>
    public static void PopFrom(Control? target, RelativePoint origin, double fromScale = 0.82, int milliseconds = 220)
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
                Easing = new CubicEaseOut(),
            },
        };

        target.Opacity = 0;
        target.RenderTransform = TransformOperations.Parse(
            $"scale({fromScale.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
    }

    /// <summary>缩回指定相对原点并淡出，与 <see cref="PopFrom"/> 配对：弹层关掉时朝触发控件方向收回去。</summary>
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

    /// <summary>
    /// 切换内容时的纯淡入：只动透明度，不缩放、不位移。
    /// 内容面板动辄几百像素高，哪怕 1% 的 scale 也会让远离原点的那一侧明显"滑一下"，
    /// 看起来像整块内容被从左上角拉出来——所以分区切换只用淡入。
    /// </summary>
    public static void FadeSwap(Control? target, int milliseconds = 150)
    {
        if (target is null) return;

        if (!Enabled)
        {
            target.Opacity = 1;
            return;
        }

        target.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(milliseconds),
                Easing = new CubicEaseOut(),
            },
        };

        target.Opacity = 0;
        Dispatcher.UIThread.Post(() => target.Opacity = 1, DispatcherPriority.Background);
    }

    /// <summary>恢复成没有变换的初始状态（供下一次重新播放进入动画）。</summary>
    public static void Reset(Control? target)
    {
        if (target is null) return;
        target.Opacity = 1;
        target.RenderTransform = TransformOperations.Parse("translateY(0px) scale(1)");
    }

    // ================= 通用补充 =================

    /// <summary>淡出并轻微缩小，与 <see cref="FadeSlideIn"/> 配对：列表项移除 / 浮层收起时先播它，再真正移除。</summary>
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

    /// <summary>原地弹一下（1 → peak → 1）：状态图标从「...」变「✓」这类瞬时切换给一点强调。</summary>
    public static async void Pulse(Control? target, double peak = 1.22, int milliseconds = 220)
    {
        if (!Enabled || target is null) return;

        var half = Math.Max(60, milliseconds / 2);
        target.Transitions = BuildTransitions(half, new CubicEaseOut());
        target.RenderTransform = ParseOrIdentity($"scale({Number(peak)})");
        await Task.Delay(half);

        target.Transitions = BuildTransitions(half, new CubicEaseOut());
        target.RenderTransform = ParseOrIdentity("scale(1)");
    }

    /// <summary>错峰入场：一组控件依次淡入（崩溃报告卡片、时间轴节点这类「一次建一批」的场景）。</summary>
    public static void Stagger(IEnumerable<Control?> targets, int step = 45, int milliseconds = 190,
        double offsetY = 8)
    {
        var items = new List<Control>();
        foreach (var target in targets)
        {
            if (target is not null) items.Add(target);
        }

        // 项目多时压缩间隔，保证整批在 400ms 内入场完，别让人干等
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
            _ = RevealLaterAsync(target, delay, milliseconds);
            delay += step;
        }
    }

    private static async Task RevealLaterAsync(Control target, int delay, int milliseconds)
    {
        if (delay > 0) await Task.Delay(delay);

        target.Transitions = BuildTransitions(milliseconds, new CubicEaseOut());
        target.Opacity = 1;
        target.RenderTransform = ParseOrIdentity("translateY(0px) scale(1)");
    }

    /// <summary>展开 / 收起：高度从 0 过渡到实测高度，结束后还原 NaN 让内容自适应。</summary>
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
            await Task.Delay(16);
            target.Height = full;
            await Task.Delay(milliseconds + 30);
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
        await Task.Delay(16);
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
}
