using System;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
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

        try
        {
            YamlRead.EnsureGameDirectory();
        }
        catch
        {
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
        }

        Localization.Localization.Initialize();

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


    private static bool _autoUpdateCheckScheduled;

    private static void CreateMainWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var window = new MainWindow();
        desktop.MainWindow = window;
        WallpaperService.AttachToMainWindow(window);
        window.Show();
        ScheduleAutoUpdateCheck();
    }

    private static void ScheduleAutoUpdateCheck()
    {
        if (_autoUpdateCheckScheduled) return;
        _autoUpdateCheckScheduled = true;
        Dispatcher.UIThread.Post(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            await RunAutoUpdateCheckAsync();
        }, DispatcherPriority.Background);
    }

    private static async Task RunAutoUpdateCheckAsync()
    {
        var settings = LocalSettings.Current;
        if (!settings.AutoCheckUpdates) return;

        var informational = typeof(App).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational) ||
            informational.Contains("Snapshot", StringComparison.OrdinalIgnoreCase))
            return;

        if (DateTimeOffset.TryParse(settings.LastUpdateCheckAt, out var lastCheck) &&
            DateTimeOffset.UtcNow - lastCheck < TimeSpan.FromHours(24))
            return;

        try
        {
            var release = await GitHubUpdater.GetLatestAsync().ConfigureAwait(true);
            settings.LastUpdateCheckAt = DateTimeOffset.UtcNow.ToString("O");
            LocalSettings.Save();
            if (GitHubUpdater.IsNewer(release.Tag))
            {
                Shell.Toast(Mofumofu.Launcher.Localization.Localization.T(
                    "Settings.About.AutoCheck.Found",
                    "发现新版本 {0}，可到「设置 → 关于」下载。",
                    release.Tag.TrimStart('v', 'V')));
            }
        }
        catch
        {
            // 自动检查保持静默，失败不打扰用户
        }
    }

}
