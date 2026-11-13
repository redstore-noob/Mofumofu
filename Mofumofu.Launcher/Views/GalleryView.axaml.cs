using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Mofumofu.Launcher.Services;
using Mofumofu.Launcher.Views.Widgets;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views;

public partial class GalleryView : UserControl
{
    private const double TileWidth = 232;

    private const int MaxRendered = 240;

    private const string SharedFilter = "\u0000shared";

    private IReadOnlyList<WidgetBackend.ScreenshotInfo> _all = [];

    private string? _filter;

    private bool _busy;

    public GalleryView()
    {
        InitializeComponent();

        TitleText.Text = L.T("Gallery.Title", "截图画廊");
        RefreshText.Text = L.T("Gallery.Refresh", "重新扫描");
        OpenFolderText.Text = L.T("Gallery.OpenFolder", "打开截图目录");
        SummaryText.Text = L.T("Gallery.Scanning", "正在扫描…");
        EmptyText.Text = L.T("Gallery.Empty", "还没有截图。在游戏里按 F2，第一张就会出现在这里。");
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (!_busy) _ = LoadAsync();
    }

    private void OnRefreshClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _ = LoadAsync();

    private async Task LoadAsync()
    {
        if (_busy) return;
        _busy = true;

        SummaryText.Text = L.T("Gallery.Scanning", "正在扫描…");

        try
        {
            _all = await WidgetBackend.LoadAllScreenshotsAsync();

            if (_filter is not null
                && _filter != SharedFilter
                && !_all.Any(shot => string.Equals(shot.InstanceId, _filter, StringComparison.Ordinal)))
            {
                _filter = null;
            }

            RebuildFilters();
            Render();
        }
        catch
        {
            _all = [];
            RebuildFilters();
            Render();
        }
        finally
        {
            _busy = false;
        }
    }

    private void RebuildFilters()
    {
        FilterHost.Children.Clear();

        FilterHost.Children.Add(FilterButton(L.T("Gallery.Filter.All", "全部截图"), null, _all.Count));

        int shared = _all.Count(shot => shot.InstanceId is null);
        if (shared > 0)
        {
            FilterHost.Children.Add(FilterButton(L.T("Gallery.Filter.Shared", "共享目录"), SharedFilter, shared));
        }

        foreach (var group in _all
                     .Where(shot => shot.InstanceId is not null)
                     .GroupBy(shot => shot.InstanceId!, StringComparer.Ordinal)
                     .OrderByDescending(group => group.Max(shot => shot.Taken)))
        {
            FilterHost.Children.Add(FilterButton(group.Key, group.Key, group.Count()));
        }
    }

    private Button FilterButton(string text, string? key, int count)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var badge = new TextBlock
        {
            Text = count.ToString(CultureInfo.InvariantCulture),
            FontSize = 10.5,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(label);
        Grid.SetColumn(badge, 1);
        grid.Children.Add(badge);

        var button = new Button
        {
            Content = grid,
            Classes = { "pill" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 2),
        };

        if (string.Equals(_filter, key, StringComparison.Ordinal)) button.Classes.Add("active");

        button.Click += (_, _) =>
        {
            _filter = key;
            RebuildFilters();
            Render();
        };

        return button;
    }

    private List<WidgetBackend.ScreenshotInfo> Filtered() => _filter switch
    {
        null => [.. _all],
        SharedFilter => [.. _all.Where(shot => shot.InstanceId is null)],
        _ => [.. _all.Where(shot => string.Equals(shot.InstanceId, _filter, StringComparison.Ordinal))],
    };

    private void Render()
    {
        var filtered = Filtered();

        TileHost.Children.Clear();
        EmptyPanel.IsVisible = filtered.Count == 0;
        ScrollHost.IsVisible = filtered.Count > 0;

        if (filtered.Count == 0)
        {
            SummaryText.Text = L.T("Gallery.SummaryEmpty", "没有截图");
            EmptyText.Text = L.T("Gallery.Empty", "还没有截图。在游戏里按 F2，第一张就会出现在这里。");
            return;
        }

        var newest = filtered[0].Taken;
        SummaryText.Text = filtered.Count > MaxRendered
            ? L.T("Gallery.SummaryCapped", "{0} 张 · 最近 {1} · 仅显示最近 {2} 张",
                filtered.Count, newest.ToString("MM-dd HH:mm"), MaxRendered)
            : L.T("Gallery.Summary", "{0} 张 · 最近 {1}", filtered.Count, newest.ToString("MM-dd HH:mm"));

        var shown = filtered.Take(MaxRendered).ToList();
        for (int i = 0; i < shown.Count; i++)
        {
            int index = i;
            var shot = shown[index];

            double height = Math.Clamp(TileWidth / Math.Max(0.6, shot.AspectRatio), 110, 210);

            var tile = ScreenshotViewer.Tile(shot, () => ScreenshotViewer.Show(shown, index), 420);
            tile.Width = TileWidth;
            tile.Height = height;
            tile.Margin = new Thickness(0, 0, 8, 8);

            TileHost.Children.Add(tile);
        }
    }

    private void OnOpenFolderClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        string? directory = _filter switch
        {
            null => _all.FirstOrDefault()?.Directory,
            SharedFilter => _all.FirstOrDefault(shot => shot.InstanceId is null)?.Directory,
            _ => _all.FirstOrDefault(shot => string.Equals(shot.InstanceId, _filter, StringComparison.Ordinal))?.Directory,
        };

        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.Combine(WidgetBackend.GameDirectory, "screenshots");
        }

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

        Shell.Toast(L.T("Gallery.OpenFolderFailed", "没有找到截图目录，先进游戏截一张吧"));
    }
}
