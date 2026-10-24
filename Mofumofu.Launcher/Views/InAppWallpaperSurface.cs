using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Mofumofu.Launcher.Views;

/// <summary>
/// 主窗口内部的最底层壁纸画布（图片 / 轮播）。
/// 画在 Avalonia 内容里，AcrylicSurface 之类的库才能采样到壁纸做毛玻璃。
/// </summary>
public sealed class InAppWallpaperSurface : Panel
{
    private readonly Image _imageA;
    private readonly Image _imageB;
    private readonly DispatcherTimer _slideTimer = new();
    private List<string> _slides = [];
    private int _slideIndex;
    private bool _showingA = true;
    private bool _loading;
    private bool _paused;

    public InAppWallpaperSurface()
    {
        _imageA = CreateImage();
        _imageB = CreateImage();
        Children.Add(_imageA);
        Children.Add(_imageB);

        _slideTimer.Tick += (_, _) => AdvanceSlide();
    }

    private static Image CreateImage() => new()
    {
        Stretch = Stretch.UniformToFill,
        IsVisible = true,
        Opacity = 0,
        Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(700),
            },
        },
    };

    public void ShowImage(string path, Bitmap? bitmap = null)
    {
        StopSlideshow();

        if (bitmap is not null) ShowBitmap(bitmap);
        else _ = LoadAndShowAsync(path);
    }

    public void ShowSlideshow(IReadOnlyList<string> images, int intervalSeconds, bool shuffle,
        Bitmap? firstBitmap = null)
    {
        _slides = images.Where(File.Exists).ToList();
        if (_slides.Count == 0)
        {
            StopSlideshow();
            return;
        }

        if (shuffle && _slides.Count > 1)
        {
            _slides = _slides.OrderBy(_ => Random.Shared.Next()).ToList();
        }

        _slideIndex = 0;
        _slideTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(intervalSeconds, 5, 3600));
        _slideTimer.Start();

        if (firstBitmap is not null) ShowBitmap(firstBitmap);
        else _ = LoadAndShowAsync(_slides[0]);
    }

    public void SetBlurPercent(int percent)
    {
        double radius = Math.Clamp(percent, 0, 100) * 0.3;
        IEffect? effect = radius <= 0.1 ? null : new BlurEffect { Radius = radius };
        _imageA.Effect = effect;
        _imageB.Effect = effect;
    }

    public void StopSlideshow()
    {
        _slideTimer.Stop();
        _slides = [];
        _slideIndex = 0;
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (paused) _slideTimer.Stop();
        else if (_slides.Count > 0) _slideTimer.Start();
    }

    private async Task LoadAndShowAsync(string path)
    {
        if (_loading) return;
        _loading = true;

        try
        {
            Bitmap? bitmap = await Task.Run(() =>
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
            });

            if (bitmap is not null) ShowBitmap(bitmap);
        }
        finally
        {
            _loading = false;
        }
    }

    private void ShowBitmap(Bitmap bitmap)
    {
        Image active = _showingA ? _imageA : _imageB;
        Image hidden = _showingA ? _imageB : _imageA;

        active.Source = bitmap;
        active.Opacity = 1;
        hidden.Opacity = 0;
        _showingA = !_showingA;
    }

    private void AdvanceSlide()
    {
        if (_paused || _slides.Count == 0) return;

        _slideIndex = (_slideIndex + 1) % _slides.Count;
        _ = LoadAndShowAsync(_slides[_slideIndex]);
    }
}
