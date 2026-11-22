using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Mofumofu.Launcher.Services;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Controls;

/// <summary>底栏实例芯片展开后的选择面板：搜索、最近使用、全部实例。</summary>
public sealed class InstancePickerPopover : Border
{
    private readonly List<(Control Container, List<(Button Row, string Haystack)> Rows)> _groups = [];
    private readonly TextBlock _noMatch;

    public InstancePickerPopover(
        IReadOnlyList<InstanceSnapshot> items,
        string? selectedId,
        string? runningId,
        Action<string> onPick,
        Action onManage,
        Action onDownload)
    {
        Width = 380;
        MaxHeight = 460;
        Background = ThemeTokens.Brush("NekoDialogSolidBrush");
        BorderBrush = ThemeTokens.Brush("NekoBorderBrush");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(12);
        ClipToBounds = true;

        _noMatch = new TextBlock
        {
            Text = L.T("Shell.Picker.NoMatch", "没有匹配的实例"),
            FontSize = 11.5,
            Margin = new Thickness(8, 16, 8, 16),
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = ThemeTokens.Brush("NekoMutedBrush"),
            IsVisible = false,
        };

        Child = items.Count == 0
            ? BuildEmpty(onDownload)
            : BuildBody(items, selectedId, runningId, onPick, onManage);
    }

    private Control BuildBody(
        IReadOnlyList<InstanceSnapshot> items,
        string? selectedId,
        string? runningId,
        Action<string> onPick,
        Action onManage)
    {
        var search = new TextBox
        {
            PlaceholderText = L.T("Shell.Picker.SearchPlaceholder", "搜索实例名称 / 版本 / 加载器"),
            Classes = { "search" },
            Margin = new Thickness(8, 8, 8, 4),
            FontSize = 12.5,
        };
        search.TextChanged += (_, _) => ApplyFilter(search.Text);

        var list = new StackPanel { Spacing = 2 };
        var sorted = items.ToList();

        if (items.Count > 3)
        {
            AddGroup(list, L.T("Shell.Picker.Recent", "最近使用"), sorted.Take(3),
                selectedId, runningId, onPick);
        }

        AddGroup(list, L.T("Shell.Picker.All", "全部实例"), sorted,
            selectedId, runningId, onPick);
        list.Children.Add(_noMatch);

        var scroll = new ScrollViewer
        {
            Content = list,
            MaxHeight = 300,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 4, 0, 0),
        };

        var manage = new Button
        {
            Classes = { "instance-row" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = BuildManageContent(),
            Margin = new Thickness(8, 4, 8, 8),
        };
        manage.Click += (_, _) =>
        {
            MofuPopover.Close(this);
            onManage();
        };

        var footer = new Border
        {
            BorderBrush = ThemeTokens.Brush("NekoBorderBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = manage,
        };

        var body = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
        };
        body.Children.Add(search);
        Grid.SetRow(scroll, 1);
        body.Children.Add(scroll);
        Grid.SetRow(footer, 2);
        body.Children.Add(footer);

        Dispatcher.UIThread.Post(() => search.Focus(), DispatcherPriority.Background);
        return body;
    }

    private static Control BuildManageContent()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        panel.Children.Add(new IconParkIcon { Kind = "IconFolder", Width = 13, Height = 13 });
        panel.Children.Add(new TextBlock
        {
            Text = L.T("Shell.Picker.Manage", "管理实例"),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeTokens.Brush("NekoMutedBrush"),
        });
        return panel;
    }

    private void AddGroup(
        StackPanel host,
        string title,
        IEnumerable<InstanceSnapshot> items,
        string? selectedId,
        string? runningId,
        Action<string> onPick)
    {
        var container = new StackPanel { Spacing = 2 };
        container.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontWeight = FontWeight.Medium,
            Margin = new Thickness(8, 8, 8, 2),
            Foreground = ThemeTokens.Brush("NekoMutedBrush"),
        });

        var rows = new List<(Button Row, string Haystack)>();
        foreach (var item in items)
        {
            var selected = string.Equals(item.Id, selectedId, StringComparison.OrdinalIgnoreCase);
            var running = string.Equals(item.Id, runningId, StringComparison.OrdinalIgnoreCase);
            var row = BuildRow(item, selected, running, onPick);
            container.Children.Add(row);
            rows.Add((row, BuildHaystack(item)));
        }

        host.Children.Add(container);
        _groups.Add((container, rows));
    }

    private Button BuildRow(
        InstanceSnapshot item,
        bool selected,
        bool running,
        Action<string> onPick)
    {
        var iconBox = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(8),
            Background = ThemeTokens.Brush("NekoFieldSoftBrush"),
            BorderBrush = ThemeTokens.Brush("NekoBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = BuildIcon(item),
        };

        var text = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontSize = 12.5,
            FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            Foreground = ThemeTokens.Brush(selected ? "NekoAccentBrush" : "NekoTextBrush"),
        });
        text.Children.Add(new TextBlock
        {
            Text = BuildMeta(item),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            Foreground = ThemeTokens.Brush("NekoMutedBrush"),
        });

        var trailing = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (running)
        {
            trailing.Children.Add(new Border
            {
                Width = 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = ThemeTokens.Brush("NekoAccentBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            trailing.Children.Add(new TextBlock
            {
                Text = L.T("Launch.Status.Running", "运行中"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ThemeTokens.Brush("NekoMutedBrush"),
            });
        }

        if (selected)
        {
            trailing.Children.Add(new IconParkIcon { Kind = "IconCheck", Width = 14, Height = 14 });
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(iconBox);

        var textHost = new Border { Padding = new Thickness(8, 0, 8, 0), Child = text };
        Grid.SetColumn(textHost, 1);
        grid.Children.Add(textHost);

        Grid.SetColumn(trailing, 2);
        grid.Children.Add(trailing);

        var button = new Button
        {
            Classes = { "instance-row" },
            Content = grid,
        };
        button.Click += (_, _) =>
        {
            MofuPopover.Close(this);
            onPick(item.Id);
        };
        return button;
    }

    private static Control BuildIcon(InstanceSnapshot item)
    {
        if (item.Icon is not null)
        {
            return new Image
            {
                Source = item.Icon,
                Width = 20,
                Height = 20,
                Stretch = Stretch.Uniform,
            };
        }

        return new IconParkIcon { Kind = "IconCube", Width = 15, Height = 15 };
    }

    private static string BuildMeta(InstanceSnapshot item)
    {
        var loader = item.Loader == "Vanilla"
            ? L.T("Download.Minecraft.Vanilla", "原版")
            : item.Loader;
        var mods = L.T("Shell.Picker.Mods", "{0} 个模组", item.ModCount);
        return $"{item.Version} · {loader} · {mods}";
    }

    private static string BuildHaystack(InstanceSnapshot item)
    {
        var loader = item.Loader == "Vanilla"
            ? L.T("Download.Minecraft.Vanilla", "原版")
            : item.Loader;
        return $"{item.Name} {item.Id} {item.Version} {item.Loader} {loader} {item.ModCount}";
    }

    private Control BuildEmpty(Action onDownload)
    {
        var panel = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(24, 24, 24, 20),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        panel.Children.Add(new IconParkIcon
        {
            Kind = "IconCube",
            Width = 28,
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = L.T("Shell.Picker.Empty", "还没有任何实例"),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = ThemeTokens.Brush("NekoTextBrush"),
        });
        panel.Children.Add(new TextBlock
        {
            Text = L.T("Shell.Picker.EmptyHint", "先去下载页安装一个游戏版本"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = ThemeTokens.Brush("NekoMutedBrush"),
        });

        var go = new Button
        {
            Content = L.T("Shell.Picker.GoDownload", "前往下载"),
            Classes = { "primary", "dialog-action" },
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };
        go.Click += (_, _) =>
        {
            MofuPopover.Close(this);
            onDownload();
        };
        panel.Children.Add(go);

        return panel;
    }

    private void ApplyFilter(string? query)
    {
        var keyword = (query ?? string.Empty).Trim();
        var visibleGroups = 0;

        foreach (var (container, rows) in _groups)
        {
            var any = false;
            foreach (var (row, haystack) in rows)
            {
                var hit = keyword.Length == 0
                          || haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase);
                row.IsVisible = hit;
                if (hit) any = true;
            }

            container.IsVisible = any;
            if (any) visibleGroups++;
        }

        _noMatch.IsVisible = visibleGroups == 0;
    }
}
