using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Mofumofu.Launcher.Controls;
using Mofumofu.Launcher.Services;
using Mofumofu.Launcher.Views.Widgets;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views;

internal static class ScreenshotViewer
{
    public static Control Tile(WidgetBackend.ScreenshotInfo shot, Action onClick, int decodeWidth = 480)
    {
        var image = new Image
        {
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        var tile = new Border
        {
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Background = ThemeTokens.Brush("NekoFieldSoftBrush"),
            Child = image,
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        ToolTip.SetTip(tile, $"{shot.FileName}\n{shot.Taken:yyyy-MM-dd HH:mm} · {WidgetBackend.FormatSize(shot.SizeBytes)}");

        tile.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(tile).Properties.IsLeftButtonPressed) onClick();
        };

        var cached = ScreenshotLibrary.Peek(shot.Path, decodeWidth);
        if (cached is not null)
        {
            image.Source = cached;
            return tile;
        }

        _ = LoadAsync();

        async Task LoadAsync()
        {
            var bitmap = await ScreenshotLibrary.LoadThumbnailAsync(shot.Path, decodeWidth).ConfigureAwait(false);
            if (bitmap is null) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (image.Parent is null) return;
                image.Source = bitmap;
            });
        }

        return tile;
    }

    public static void Show(IReadOnlyList<WidgetBackend.ScreenshotInfo> items, int index)
    {
        if (items.Count == 0) return;

        index = Math.Clamp(index, 0, items.Count - 1);

        var image = new Image
        {
            Stretch = Stretch.Uniform,
            MaxWidth = 1080,
            MaxHeight = 620,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var stage = new Border
        {
            Background = ThemeTokens.Brush("NekoFieldSoftBrush"),
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Height = 420,
            Child = image,
        };

        var caption = new TextBlock
        {
            FontSize = 11.5,
            Foreground = ThemeTokens.Brush("NekoDimBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var counter = new TextBlock
        {
            FontSize = 11.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeTokens.Brush("NekoAccentBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var navRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        Button previous = null!;
        Button next = null!;
        previous = MofuDialog.Secondary(L.T("Screenshots.Prev", "上一张"), () => Move(-1));
        next = MofuDialog.Secondary(L.T("Screenshots.Next", "下一张"), () => Move(1));

        navRow.Children.Add(previous);
        navRow.Children.Add(counter);
        navRow.Children.Add(next);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        footer.Children.Add(MofuDialog.Secondary(L.T("Screenshots.OpenFolder", "打开所在文件夹"), OpenFolder));
        footer.Children.Add(MofuDialog.Secondary(L.T("Screenshots.CopyPath", "复制路径"), CopyPath));

        var body = new StackPanel { Spacing = 10, MaxWidth = 720, MinWidth = 420 };
        body.Children.Add(stage);
        body.Children.Add(navRow);
        body.Children.Add(caption);
        body.Children.Add(footer);

        void Render(int target)
        {
            index = Math.Clamp(target, 0, items.Count - 1);
            var shot = items[index];

            caption.Text = $"{shot.FileName} · {shot.Taken:yyyy-MM-dd HH:mm} · "
                + (shot.Width > 0 && shot.Height > 0
                    ? $"{shot.Width}×{shot.Height} · "
                    : string.Empty)
                + WidgetBackend.FormatSize(shot.SizeBytes);

            counter.Text = $"{index + 1} / {items.Count}";
            previous.IsEnabled = index > 0;
            next.IsEnabled = index < items.Count - 1;

            image.Source = null;
            _ = LoadPreviewAsync();

            async Task LoadPreviewAsync()
            {
                var bitmap = await LoadFullAsync(shot.Path).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (index >= 0 && index < items.Count
                        && string.Equals(items[index].Path, shot.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        image.Source = bitmap;
                    }
                });
            }
        }

        void Move(int delta) => Render(index + delta);

        void OpenFolder()
        {
            var directory = items[index].Directory;
            try
            {
                if (Directory.Exists(directory))
                {
                    Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
                    return;
                }
            }
            catch
            {
            }

            Shell.Toast(L.T("Screenshots.OpenFolderFailed", "打开截图目录失败"));
        }

        async void CopyPath()
        {
            var clipboard = TopLevel.GetTopLevel(stage)?.Clipboard;
            if (clipboard is null)
            {
                Shell.Toast(L.T("Screenshots.NoClipboard", "剪贴板不可用"));
                return;
            }

            await clipboard.SetTextAsync(items[index].Path);
            Shell.Toast(L.T("Screenshots.PathCopied", "已复制截图路径"));
        }

        Render(index);
        Shell.Dialog(body, L.T("Screenshots.Preview", "截图预览"));
    }

    private static async Task<Bitmap?> LoadFullAsync(string path)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            if (bytes.Length == 0) return null;

            using var stream = new MemoryStream(bytes, writable: false);
            return Bitmap.DecodeToWidth(stream, 1920);
        }
        catch
        {
            return null;
        }
    }
}
