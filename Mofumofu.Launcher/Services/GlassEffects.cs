using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fluid.Avalonia.Acrylic;
using Mofumofu.Launcher.Services.Wallpaper;

namespace Mofumofu.Launcher.Services;

/// <summary>统一控制所有 AcrylicSurface 的毛玻璃开关。</summary>
public static class GlassEffects
{
    private static readonly Color DarkOnTint = Color.Parse("#B30C121A");
    private static readonly Color DarkOffTint = Color.Parse("#F20C121A");
    private static readonly Color LightOnTint = Color.Parse("#D9FFFFFF");
    private static readonly Color LightOffTint = Color.Parse("#F5FFFFFF");

    private static bool _suspended;

    public static bool Enabled { get; private set; } = true;

    /// <summary>浅色主题使用白色半透明玻璃，避免深色底与浅色文字令牌混在一起。</summary>
    public static Color PanelTint => ThemeManager.IsLight
        ? Enabled ? LightOnTint : LightOffTint
        : Enabled ? DarkOnTint : DarkOffTint;

    /// <summary>卓越视效：开启后恢复高光、阴影、折射、渐进模糊等额外渲染。</summary>
    public static bool PremiumVisuals { get; set; }

    public static void SetPremiumVisuals(bool enabled)
    {
        PremiumVisuals = enabled;
        Refresh();
    }

    /// <summary>窗口不在前台时暂停模糊，减少 CPU / GPU 占用。</summary>
    public static void SetSuspended(bool suspended)
    {
        if (_suspended == suspended) return;
        _suspended = suspended;
        Refresh();
    }

    public static void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        Refresh();
    }

    /// <summary>页面切换 / 新建控件后重新套用当前毛玻璃状态。</summary>
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

    private static void ApplyToWindow(Window window)
    {
        // 视频壁纸走独立窗口，主窗口内容采样不到，开玻璃只是白耗性能
        var hasBackdrop = !WallpaperService.Instance.IsEnabled
                          || WallpaperService.Instance.UsesInAppSurface;
        var active = Enabled && !_suspended && hasBackdrop;
        var premium = active && PremiumVisuals;

        foreach (var surface in window.GetVisualDescendants().OfType<AcrylicSurface>())
        {
            surface.BlurRadius = active ? (premium ? 16 : 8) : 0;
            surface.Vibrancy = active ? (premium ? 1.25 : 1.0) : 1;
            surface.TintColor = PanelTint;

            // 默认只保留一次背景模糊；卓越视效才恢复逐帧叠加效果
            surface.RefractionAmount = premium ? 0.35 : 0;
            surface.RefractionHeight = premium ? 18 : 0;
            surface.HighlightEnabled = premium;
            surface.InnerShadowEnabled = premium;
            surface.ShadowEnabled = premium;
            surface.ProgressiveBlurEnabled = premium;
            surface.RevealBorderEnabled = premium;
            surface.AdaptiveLuminanceEnabled = premium;
        }
    }
}
