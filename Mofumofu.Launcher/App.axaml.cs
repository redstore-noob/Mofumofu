using System;
using Avalonia;
using Mofumofu.Launcher.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MofuCore.Config;
using MofuCore.Download.Minecraft;
using Mofumofu.Launcher.Localization;
using Mofumofu.Launcher.Services;
using Mofumofu.Launcher.Services.Wallpaper;

namespace Mofumofu.Launcher;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        LocalSettings.Load();

        // 默认游戏目录：./.minecraft（不存在则创建）
        try
        {
            YamlRead.EnsureGameDirectory();
        }
        catch
        {
            // 目录不可创建时不阻塞启动
        }

        // 下载源与并行数：启动时应用本地设置，避免重启后首次下载用默认源
        try
        {
            var downloadSettings = LocalSettings.Current;
            DownloadSourceSettings.Apply(downloadSettings);
            DownloadSettings.ParallelDownloads = Math.Clamp(downloadSettings.ParallelDownloads, 1, 128);
        }
        catch
        {
            // 下载设置应用失败不阻塞启动
        }

        Localization.Localization.Initialize();

        // 主题与强调色都只有 ThemeManager 一个来源（设置页改完也走它）
        ThemeManager.Apply(LocalSettings.Current.Theme, LocalSettings.Current.AccentColor);
        IconParkIcon.UseColorfulPalette =
            !string.Equals(LocalSettings.Current.IconPalette, "theme", StringComparison.OrdinalIgnoreCase);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (LocalSettings.Current.OobeCompleted)
            {
                CreateMainWindow(desktop);
            }
            else
            {
                var oobe = new Views.OobeWindow();
                desktop.MainWindow = oobe;
                oobe.Completed += () => CreateMainWindow(desktop);
                oobe.Show();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }


    private static void CreateMainWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var window = new MainWindow();
        desktop.MainWindow = window;
        WallpaperService.AttachToMainWindow(window);
        window.Show();
        _ = AutoCheckUpdateAsync();
    }

    /// <summary>启动后静默检查一次 GitHub Releases 新版本；失败完全无声，不打扰启动。</summary>
    private static async System.Threading.Tasks.Task AutoCheckUpdateAsync()
    {
        try
        {
            if (!LocalSettings.Current.AutoCheckUpdates) return;
            await System.Threading.Tasks.Task.Delay(8000).ConfigureAwait(false);
            var release = await Services.GitHubUpdater.GetLatestAsync().ConfigureAwait(false);
            if (Services.GitHubUpdater.IsNewer(release.Tag))
            {
                string tag = release.Tag.TrimStart('v', 'V');
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    Shell.Toast(Localization.Localization.T(
                        "Settings.About.Update.NewToast",
                        "发现新版本 {0}，可在 设置 → 关于 中更新", tag)));
            }
        }
        catch
        {
            // 网络不通 / API 限流等一律静默
        }
    }

}
