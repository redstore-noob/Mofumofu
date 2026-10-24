using System;
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

    /// <summary>恢复成没有变换的初始状态（供下一次重新播放进入动画）。</summary>
    public static void Reset(Control? target)
    {
        if (target is null) return;
        target.Opacity = 1;
        target.RenderTransform = TransformOperations.Parse("translateY(0px) scale(1)");
    }
}
