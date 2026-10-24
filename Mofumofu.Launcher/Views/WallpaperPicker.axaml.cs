using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Mofumofu.Launcher.Services.Wallpaper;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views;

/// <summary>设置页里的自定义壁纸选择器：图片 / 图片轮播。</summary>
public partial class WallpaperPicker : UserControl
{
    private static readonly FilePickerFileType ImageType = new("图片")
    {
        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.gif"],
    };

    private readonly List<SlideThumb> _slides = [];
    private bool _loading;

    public WallpaperPicker()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        WallpaperService.Instance.Changed += OnServiceChanged;
        _ = RefreshAsync();
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        WallpaperService.Instance.Changed -= OnServiceChanged;
    }

    private void OnServiceChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => _ = RefreshAsync());
            return;
        }

        _ = RefreshAsync();
    }

    // ------------------------------------------------------------------
    // 刷新界面
    // ------------------------------------------------------------------

    private async Task RefreshAsync()
    {
        WallpaperSettings settings = WallpaperService.Instance.Settings;

        _loading = true;
        try
        {
            EnabledSwitch.IsChecked = settings.Enabled;
            ModeCombo.SelectedIndex = settings.Mode switch
            {
                WallpaperModes.Slideshow => 1,
                WallpaperModes.None => 2,
                _ => 0,
            };
            PauseSwitch.IsChecked = settings.PauseWhenInactive;
            IntervalSlider.Value = settings.IntervalSeconds;
            IntervalValue.Text = L.T("Wallpaper.Interval.Value", "{0} 秒", settings.IntervalSeconds);
            ShuffleSwitch.IsChecked = settings.Shuffle;
        BlurSlider.Value = Math.Clamp(settings.BlurPercent, 0, 100);
        DimSlider.Value = Math.Clamp(settings.DimPercent, 0, 100);
        BlurValue.Text = $"{settings.BlurPercent}%";
        DimValue.Text = $"{settings.DimPercent}%";

            UpdateModeUi(settings);
        }
        finally
        {
            _loading = false;
        }

        await RefreshPreviewsAsync(settings);
    }

    private void UpdateModeUi(WallpaperSettings settings)
    {
        var mode = settings.Mode;

        ImagePanel.IsVisible = mode == WallpaperModes.Image;
        SlideshowPanel.IsVisible = mode == WallpaperModes.Slideshow;

        PickImageButton.IsVisible = mode == WallpaperModes.Image;
        AddImagesButton.IsVisible = mode == WallpaperModes.Slideshow;

        StatusText.Text = WallpaperService.Instance.LastError.Length > 0
            ? L.T("Wallpaper.Status.Error", "壁纸未生效：{0}", WallpaperService.Instance.LastError)
            : settings.Enabled
                ? L.T("Wallpaper.Status.On", "壁纸已启用")
                : L.T("Wallpaper.Status.Off", "壁纸已关闭");
    }

    private async Task RefreshPreviewsAsync(WallpaperSettings settings)
    {
        var images = settings.Images.Where(WallpaperService.IsImageFile).ToList();

        var first = images.Count > 0 ? await LoadBitmapAsync(images[0]) : null;
        ImagePreview.Source = first;
        ImageEmptyText.IsVisible = first is null;

        var thumbs = new List<SlideThumb>();
        foreach (var path in images)
        {
            thumbs.Add(new SlideThumb { Path = path, Thumbnail = await LoadBitmapAsync(path) });
        }

        _slides.Clear();
        _slides.AddRange(thumbs);
        SlideshowList.ItemsSource = null;
        SlideshowList.ItemsSource = _slides;

        SlideshowEmptyText.IsVisible = settings.Mode == WallpaperModes.Slideshow && _slides.Count == 0;
    }

    private static Task<Bitmap?> LoadBitmapAsync(string path) => Task.Run(() =>
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return new Bitmap(stream);
        }
        catch
        {
            return (Bitmap?)null;
        }
    });

    // ------------------------------------------------------------------
    // 交互
    // ------------------------------------------------------------------

    private void OnEnabledChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        WallpaperService.Instance.SetEnabled(EnabledSwitch.IsChecked == true);
    }

    private void OnModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || ModeCombo.SelectedItem is not ComboBoxItem { Tag: string mode }) return;

        WallpaperService.Instance.SetMode(mode);
    }

    private async void OnPickImageClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync(ImageType, allowMultiple: false);
        if (path.Count > 0) WallpaperService.Instance.SetImages([path[0]]);
    }

    private async void OnAddImagesClick(object? sender, RoutedEventArgs e)
    {
        var paths = await PickFileAsync(ImageType, allowMultiple: true);
        if (paths.Count > 0) WallpaperService.Instance.AddImages(paths);
    }

    private void OnRemoveSlideClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path }) WallpaperService.Instance.RemoveImage(path);
    }

    private void OnBlurChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        int value = (int)Math.Round(e.NewValue);
        BlurValue.Text = value + "%";
        WallpaperService.Instance.SetBlurPercent(value);
    }

    private void OnDimChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        int value = (int)Math.Round(e.NewValue);
        DimValue.Text = value + "%";
        WallpaperService.Instance.SetDimPercent(value);
    }

    private void OnIntervalChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        IntervalValue.Text = L.T("Wallpaper.Interval.Value", "{0} 秒", (int)IntervalSlider.Value);
        if (_loading) return;

        WallpaperService.Instance.SetIntervalSeconds((int)IntervalSlider.Value);
    }

    private void OnShuffleChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        WallpaperService.Instance.SetShuffle(ShuffleSwitch.IsChecked == true);
    }

    private void OnPauseChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        WallpaperService.Instance.SetPauseWhenInactive(PauseSwitch.IsChecked == true);
    }

    private async Task<IReadOnlyList<string>> PickFileAsync(FilePickerFileType type, bool allowMultiple)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return [];

        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L.T("Wallpaper.PickerTitle", "选择壁纸文件"),
                AllowMultiple = allowMultiple,
                FileTypeFilter = [type],
            });

            return files
                .Select(file => file.Path.LocalPath)
                .Where(File.Exists)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private sealed class SlideThumb
    {
        public string Path { get; init; } = string.Empty;

        public Bitmap? Thumbnail { get; init; }
    }
}
