using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mofumofu.Launcher.Services.Wallpaper;
using SukiUI.Controls;
using SukiUI.Controls.GlassMorphism;

namespace Mofumofu.Launcher.Services;

public static class GlassEffects
{
    /// <summary>模糊强度。关掉毛玻璃时归零，避免 GPU 白算。</summary>
    private const double BlurIntensity = 1.0;

    // 固定高密度遮罩，避免历史透明度设置把面板冲淡。
    private const double OverlayOpacity = 0.96;

    public static bool Enabled { get; private set; } = true;

    public static void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        Refresh();
    }

    public static void Refresh()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        Window? window = desktop.MainWindow;
        if (window is null) return;

        ApplyToWindow(window);
        Dispatcher.UIThread.Post(() =>
        {
            if (desktop.MainWindow is { } current) ApplyToWindow(current);
        }, DispatcherPriority.Loaded);
    }

    private static bool IsActive()
    {
        // 视频壁纸走独立窗口，主窗口内容采样不到，开玻璃只是白耗性能
        var hasBackdrop = !WallpaperService.Instance.IsEnabled
                          || WallpaperService.Instance.UsesInAppSurface;
        return Enabled && hasBackdrop;
    }

    /// <summary>
    /// 单张卡片套用当前毛玻璃状态。动态新建的卡片（如主页小组件）要在 Loaded 后调用：
    /// Suki 模板把底板 Opacity 绑在 GlassOpacity 上（深色只有 0.18），不修正底色会被冲得很浅。
    /// </summary>
    public static void ApplyToCard(GlassCard card) => ApplyToCard(card, IsActive());

    private static void ApplyToCard(GlassCard card, bool active)
    {
        // IsOpaque 是 Suki 官方语义：true = 不透明卡片，模板不渲染模糊层
        card.IsOpaque = !active;
        if (card.Classes.Contains("Control") || card.Classes.Contains("Discrete")) return;
        foreach (var surface in card.GetVisualDescendants().OfType<GlassCardBorder>()
                     .Where(border => border.TemplatedParent == card))
        {
            surface.SetCurrentValue(Visual.OpacityProperty, 1d);
            surface.SetCurrentValue(Border.BackgroundProperty,
                ThemeTokens.Brush(active ? "NekoCardBrush" : "NekoCardSolidBrush"));
            surface.SetCurrentValue(GlassCardBorder.UseDarkBorderProperty, false);
            surface.SetCurrentValue(GlassCardBorder.LightBorderBrushProperty, ThemeTokens.Brush("NekoBorderBrush"));
        }

        foreach (var blur in card.GetVisualDescendants().OfType<SukiBlurBackground>())
        {
            blur.IntensityFactor = active ? BlurIntensity : 0;
            blur.OverlayOpacity = active ? OverlayOpacity : 1.0;
        }
    }

    private static void ApplyToWindow(Window window)
    {
        var active = IsActive();

        foreach (var card in window.GetVisualDescendants().OfType<GlassCard>())
        {
            ApplyToCard(card, active);
        }

        foreach (var blur in window.GetVisualDescendants().OfType<SukiBlurBackground>())
        {
            blur.IntensityFactor = active ? BlurIntensity : 0;
            blur.OverlayOpacity = active ? OverlayOpacity : 1.0;
        }
    }
}
