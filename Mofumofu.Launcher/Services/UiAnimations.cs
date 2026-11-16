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

/// <summary>统一的轻量过渡动画：淡入/缩放/下滑，供页面、标签页、开始菜单和小组件复用。</summary>
public static class UiAnimations
{
    private const int LayoutSettleMs = 16;
    private const int TransitionPaddingMs = 30;

    /// <summary>全局过渡动画开关：关掉后页面/标签直接切换，不做淡入滑入。</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>每个控件当前入场动画的代号，用来让被顶替的旧循环安静退出。</summary>
    private static readonly Dictionary<Control, int> PlayGeneration = new();

    /// <summary>页面/列表项入场：淡入 + 从下方轻微上浮，配合缩放。</summary>
    public static void FadeSlideIn(Control? target, double fromScale = 0.985, double offsetY = 8, int milliseconds = 190)
    {
        PlayIn(target, 0, offsetY, fromScale, milliseconds, new CubicEaseOut());
    }

    /// <summary>
    /// 新页面像一张卡片从右侧滑进来：透明度 0 → 1，横向位移 <paramref name="offsetX"/> → 0。
    /// </summary>
    public static void SlideInFromRight(Control? target, double offsetX = 56, int milliseconds = 260)
    {
        // 减速曲线：起步快、收尾稳，卡片到位时像被"停"住而不是滑停。
        PlayIn(target, offsetX, 0, 1, milliseconds, new SplineEasing(new KeySpline(0.16, 1, 0.3, 1)));
    }

    /// <summary>
    /// 播放「从偏移/缩小状态进入到终态」的动画（淡入 + 位移 + 缩放）。
    /// 这里既不用 Transitions，也不用 Animation.RunAsync：
    /// Transitions 在"设起始值"这一步就会触发一次过渡，紧接着设终态把那次过渡打断并回卷，
    /// 两次改动互相抵消，控件看上去一动不动；RunAsync 依赖动画时钟，页面刚挂上还没开始出帧时
    /// 启动容易被整段跳过。所以改成自己按帧写属性——只要属性写下去能重绘，就一定看得见。
    /// </summary>
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

                // 透明度走得更快，卡片到位时已经是实心的。
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

    /// <summary>开始菜单弹出：从按钮方向缩放 + 上浮。</summary>
    public static void PopIn(Control? target, double fromScale = 0.86, double offsetY = 18, int milliseconds = 220)
    {
        PlayIn(target, 0, offsetY, fromScale, milliseconds, new CubicEaseOut());
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
        if (target is null || !Enabled) return;

        target.RenderTransformOrigin = origin;
        PlayIn(target, 0, 0, fromScale, milliseconds, new CubicEaseOut());
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

    public static void FadeSwap(Control? target, int milliseconds = 150)
    {
        // 只动透明度：内容面板动辄几百像素高，哪怕 1% 的缩放也会让远侧明显"滑一下"。
        PlayIn(target, 0, 0, 1, milliseconds, new CubicEaseOut());
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

        // 记录调用时是否已在可视树里：从未上树的控件（弹窗构造期）仍需正常恢复动画状态，
        // 只有「原本在树里、延迟后被移除」才属于页面销毁，需要跳过写 UI 属性。
        bool checkAlive = TopLevel.GetTopLevel(target) is not null;

        try
        {
            var half = Math.Max(60, milliseconds / 2);
            target.Transitions = BuildTransitions(half, new CubicEaseOut());
            target.RenderTransform = ParseOrIdentity($"scale({Number(peak)})");
            await Task.Delay(half);

            // 延迟期间控件若已从可视树移除，不再继续写 UI 属性。
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

            // 仅当控件原本挂在可视树里、如今已被移除（页面 / 窗口销毁）时跳过，
            // 避免继续写 UI 属性；从未上树的控件仍正常恢复，不破坏入场动画。
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

    /// <summary>控件是否仍挂在可视化树上（页面 / 窗口销毁后会返回 false）。</summary>
    private static bool IsAlive(Control? target) =>
        target is not null && TopLevel.GetTopLevel(target) is not null;
}
