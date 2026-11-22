using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Controls;
using Avalonia.Threading;
using Mofumofu.Launcher.Views;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Services.Wallpaper;

public sealed class WallpaperService
{
    private static readonly System.Diagnostics.Stopwatch StartupClock = System.Diagnostics.Stopwatch.StartNew();
    internal static void WallpaperStartupLog(string message)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mofu-wallpaper-startup.log"),
                $"{StartupClock.ElapsedMilliseconds,6} ms [svc] {message}\n");
        }
        catch
        {
        }
    }

    private static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif"];

    private Window? _main;
    private Bitmap? _preloadedImage;
    private string _preloadedPath = string.Empty;

    private WallpaperService()
    {
        Settings = WallpaperStore.Load();
        WallpaperStore.Save(Settings);
        EnsurePreload();
    }

    public static WallpaperService Instance { get; } = new();

    public WallpaperSettings Settings { get; }

    public event Action? Changed;

    public string LastError { get; private set; } = string.Empty;

    public static string SettingsPath => WallpaperStore.SettingsPath;

    public static bool IsImageFile(string path) =>
        File.Exists(path) && ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public bool HasValidContent => Settings.Mode switch
    {
        WallpaperModes.Image => ValidImages().Count > 0,
        WallpaperModes.Slideshow => ValidImages().Count > 0,
        _ => false,
    };

    public bool IsEnabled => Settings.Enabled && HasValidContent;

    /// <summary>图片 / 轮播画在主窗口内部，方便面板毛玻璃采样；视频仍走独立窗口。</summary>
    public bool UsesInAppSurface =>
        Settings.Mode is WallpaperModes.Image or WallpaperModes.Slideshow;

    public static void AttachToMainWindow(Window main) => Instance.Attach(main);

    public void Attach(Window main)
    {
        ArgumentNullException.ThrowIfNull(main);
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Attach(main));
            return;
        }

        Detach();
        _main = main;

        if (main.IsVisible)
        {
            Apply();
        }
        else
        {
            main.Opened += OnMainOpened;
        }
    }

    private void OnMainOpened(object? sender, EventArgs e)
    {
        if (_main is not null) _main.Opened -= OnMainOpened;
        Apply();
    }

    public void Detach()
    {
        if (_main is not null)
        {
            _main.Opened -= OnMainOpened;
        }

        _main = null;
    }

    public void SetEnabled(bool enabled) => Update(settings => settings.Enabled = enabled);

    public int BlurPercent => Settings.BlurPercent;
    public int DimPercent => Settings.DimPercent;

    public void SetBlurPercent(int percent) =>
        Update(settings => settings.BlurPercent = Math.Clamp(percent, 0, 100));

    public void SetDimPercent(int percent) =>
        Update(settings => settings.DimPercent = Math.Clamp(percent, 0, 100));

    public void SetMode(string mode) => Update(settings => settings.Mode = WallpaperModes.Normalize(mode));

    public void SetImages(IEnumerable<string> images) => Update(settings =>
    {
        settings.Images.Clear();
        settings.Images.AddRange(images.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase));
    });

    public void AddImages(IEnumerable<string> images) => Update(settings =>
    {
        foreach (var image in images.Where(File.Exists))
        {
            if (!settings.Images.Contains(image, StringComparer.OrdinalIgnoreCase))
            {
                settings.Images.Add(image);
            }
        }
    });

    public void RemoveImage(string path) => Update(settings => settings.Images.RemoveAll(
        image => string.Equals(image, path, StringComparison.OrdinalIgnoreCase)));

    public void SetIntervalSeconds(int seconds) => Update(settings =>
        settings.IntervalSeconds = Math.Clamp(seconds, 5, 3600));

    public void SetShuffle(bool shuffle) => Update(settings => settings.Shuffle = shuffle);

    public void Refresh() => Apply();

    public void NotifyThemeChanged()
    {
    }

    private void Update(Action<WallpaperSettings> mutate)
    {
        mutate(Settings);
        WallpaperStore.Save(Settings);
        Apply();
    }

    private void Apply()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Apply);
            return;
        }

        try
        {
            var mainWindow = _main as MainWindow;

            if (!IsEnabled)
            {
                LastError = Settings.Enabled
                    ? L.T("Wallpaper.Error.NoContent", "壁纸文件不存在或已被删除")
                    : string.Empty;
                mainWindow?.WallpaperSurface.StopSlideshow();
                if (mainWindow is not null) mainWindow.WallpaperSurface.IsVisible = false;
                Changed?.Invoke();
                return;
            }

            var images = ValidImages();
            if (mainWindow is not null)
            {
                var surface = mainWindow.WallpaperSurface;
                surface.IsVisible = true;

                if (Settings.Mode == WallpaperModes.Slideshow)
                {
                    EnsurePreload();
                    surface.ShowSlideshow(images, Settings.IntervalSeconds, Settings.Shuffle,
                        CachedBitmap(images[0]));
                }
                else
                {
                    EnsurePreload();
                    surface.ShowImage(images[0], CachedBitmap(images[0]));
                }
            }

            LastError = string.Empty;
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
        }

        Changed?.Invoke();
    }

    private List<string> ValidImages() =>
        Settings.Images.Where(IsImageFile).ToList();

    /// <summary>提前在后台解码首张图片，避免壁纸窗口出现后才开始加载。</summary>
    private void EnsurePreload()
    {
        var path = Settings.Images.FirstOrDefault(IsImageFile);
        if (path is null || _preloadedPath == path) return;

        _preloadedPath = path;
        _preloadedImage = null;

        _ = Task.Run(() => DecodeBitmap(path))
            .ContinueWith(task => Dispatcher.UIThread.Post(() =>
            {
                if (task.Result is not null && _preloadedPath == path)
                {
                    _preloadedImage = task.Result;
                }
            }));
    }

    private Bitmap? CachedBitmap(string path) =>
        _preloadedPath == path ? _preloadedImage : null;

    private static Bitmap? DecodeBitmap(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, 2560);
        }
        catch
        {
            return null;
        }
    }
}
