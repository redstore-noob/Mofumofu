using Mofumofu.Launcher.Controls;
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Mofumofu.Launcher.Models;
using Mofumofu.Launcher.Services;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views.Widgets;

internal static class WidgetChrome
{
    public const double CaptionSize = 10.5;
    public const double BodySize = 11.5;
    public const double ValueSize = 20;

    public static Border Card(Control content, bool live = false) =>
        new()
        {
            Background = ThemeTokens.Brush("NekoCardBrush"),
            BorderBrush = ThemeTokens.Brush(live ? "NekoAccentLineBrush" : "NekoBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 14, 16, 14),
            Child = content,
        };

    public static Border Card(string value, Control? action, Control? body, bool live = false) =>
        CardWithValue(value, action, body, live).Card;

    public static (Border Card, TextBlock ValueText) CardWithValue(
        string value, Control? action, Control? body, bool live = false)
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var valueText = Text(value, ValueSize, "NekoTextBrush", FontWeight.Bold);
        valueText.TextTrimming = TextTrimming.CharacterEllipsis;
        valueText.MaxLines = 2;
        valueText.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(valueText);

        if (action is not null)
        {
            Grid.SetColumn(action, 1);
            action.VerticalAlignment = VerticalAlignment.Center;
            action.Margin = new Thickness(8, 0, 0, 0);
            header.Children.Add(action);
        }

        // 用两行 Grid（Auto,*）而不是 StackPanel：内容区的 ScrollViewer 才能拿到
        // 确定的可用高度，卡片被拖小时内部才会滚动而不是溢出裁掉
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        grid.Children.Add(header);

        if (body is not null)
        {
            var bodyHost = Body(body);
            bodyHost.Margin = new Thickness(0, 10, 0, 0);
            Grid.SetRow(bodyHost, 1);
            grid.Children.Add(bodyHost);
        }

        return (Card(grid, live), valueText);
    }

    public static ScrollViewer Body(Control content) =>
        new()
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 2, 0),
        };

    public static TextBlock Text(string text, double size, string brushKey = "NekoTextBrush", FontWeight weight = FontWeight.Normal) =>
        new()
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            Foreground = ThemeTokens.Brush(brushKey),
            TextWrapping = TextWrapping.Wrap,
        };

    public static TextBlock Caption(string text) =>
        Text(text, CaptionSize, "NekoDimBrush");

    public static TextBlock Muted(string text) =>
        Text(text, BodySize, "NekoMutedBrush");

    public static TextBlock Stat(string text, double size = 16) =>
        Text(text, size, "NekoTextBrush", FontWeight.Bold);

    public static Control Icon(string iconKey, double size, string brushKey = "NekoAccentBrush") =>
        new IconParkIcon
        {
            Kind = iconKey,
            Width = size,
            Height = size,
        };

    public static Border IconTile(string iconKey, double size = 30, string brushKey = "NekoAccentBrush")
    {
        var icon = Icon(iconKey, size * 0.5, brushKey);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;

        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.28),
            Background = ThemeTokens.Brush("NekoAccentSoftBrush"),
            Child = icon,
        };
    }

    public static Control AssetImage(string assetUri, double size, string fallbackIcon = "IconFluentCube")
    {
        try
        {
            return new Image
            {
                Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri(assetUri))),
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
            };
        }
        catch
        {
            return IconTile(fallbackIcon, size);
        }
    }

    public static Button IconButton(string iconKey, string tip, Action onClick, double size = 26)
    {
        return IconButton(iconKey, tip, _ => onClick(), size);
    }

    public static Button IconButton(string iconKey, string tip, Action<Button> onClick, double size = 26)
    {
        var icon = Icon(iconKey, 12, "NekoMutedBrush");
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;

        var button = new Button
        {
            Width = size,
            Height = size,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(size / 2),
            Content = icon,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => onClick(button);
        return button;
    }

    public static Border Pill(string text, string foregroundToken = "NekoAccentBrush", string backgroundToken = "NekoAccentSoftBrush")
    {
        return new Border
        {
            Background = ThemeTokens.Brush(backgroundToken),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 2, 7, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = CaptionSize,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeTokens.Brush(foregroundToken),
            },
        };
    }

    public static Grid Bar(double percent, string brushKey = "NekoAccentBrush", double height = 5)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        var track = new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(height / 2),
            Background = ThemeTokens.Brush("NekoFieldBrush"),
        };

        var fill = new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(height / 2),
            Background = ThemeTokens.Brush(brushKey),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        // 用「百分比星列」当宽度：不依赖布局回调，首帧就是正确比例
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(
                $"{clamped.ToString(System.Globalization.CultureInfo.InvariantCulture)}*," +
                $"{(100 - clamped).ToString(System.Globalization.CultureInfo.InvariantCulture)}*"),
        };
        Grid.SetColumnSpan(track, 2);
        grid.Children.Add(track);
        grid.Children.Add(fill);
        return grid;
    }

    public static Border Divider() => new()
    {
        Height = 1,
        Background = ThemeTokens.Brush("NekoBorderBrush"),
        Margin = new Thickness(0, 2, 0, 2),
    };

    public static StackPanel Column(params Control[] children) => Column(8, children);

    public static StackPanel Column(double spacing, params Control[] children)
    {
        var stack = new StackPanel { Spacing = spacing };
        foreach (var child in children) stack.Children.Add(child);
        return stack;
    }

    public static StackPanel Row(params Control[] children) => Row(8, children);

    public static StackPanel Row(double spacing, params Control[] children)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var child in children) stack.Children.Add(child);
        return stack;
    }

    public static Grid Line(Control left, Control? right = null, double spacing = 8)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        left.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(left);

        if (right is not null)
        {
            Grid.SetColumn(right, 1);
            right.VerticalAlignment = VerticalAlignment.Center;
            right.Margin = new Thickness(spacing, 0, 0, 0);
            grid.Children.Add(right);
        }

        return grid;
    }

    public static Grid ListItem(string iconKey, string title, string subtitle, Control? trailing = null, double iconSize = 26)
    {
        var texts = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        var titleText = Text(title, BodySize, "NekoTextBrush", FontWeight.SemiBold);
        titleText.TextTrimming = TextTrimming.CharacterEllipsis;
        titleText.MaxLines = 1;
        texts.Children.Add(titleText);
        if (!string.IsNullOrEmpty(subtitle))
        {
            var subtitleText = Caption(subtitle);
            subtitleText.TextTrimming = TextTrimming.CharacterEllipsis;
            subtitleText.MaxLines = 1;
            texts.Children.Add(subtitleText);
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(IconTile(iconKey, iconSize));
        Grid.SetColumn(texts, 1);
        texts.Margin = new Thickness(9, 0, 6, 0);
        grid.Children.Add(texts);

        if (trailing is not null)
        {
            Grid.SetColumn(trailing, 2);
            trailing.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(trailing);
        }

        return grid;
    }

    public static Button LinkButton(string text, Action onClick, string iconKey = "")
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        if (!string.IsNullOrEmpty(iconKey)) content.Children.Add(Icon(iconKey, 11, "NekoAccentBrush"));
        content.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = CaptionSize + 0.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeTokens.Brush("NekoAccentBrush"),
        });

        var button = new Button
        {
            Padding = new Thickness(9, 4, 9, 4),
            Background = ThemeTokens.Brush("NekoAccentSoftBrush"),
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = content,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    public static Button ChipButton(string text, string iconKey, Action onClick)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        if (!string.IsNullOrEmpty(iconKey)) content.Children.Add(Icon(iconKey, 11, "NekoMutedBrush"));
        content.Children.Add(new TextBlock { Text = text, FontSize = CaptionSize + 0.5, Foreground = ThemeTokens.Brush("NekoMutedBrush") });

        var button = new Button
        {
            Padding = new Thickness(9, 5, 9, 5),
            Background = ThemeTokens.Brush("NekoFieldSoftBrush"),
            BorderBrush = ThemeTokens.Brush("NekoBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Content = content,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    public static Border Empty(string text) => new()
    {
        Background = ThemeTokens.Brush("NekoFieldSoftBrush"),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(12, 10),
        Child = new TextBlock
        {
            Text = text,
            FontSize = CaptionSize + 0.5,
            Foreground = ThemeTokens.Brush("NekoDimBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        },
    };

    public static Border EmptyStateCard(string iconKey, string hint, string actionText, Action action)
    {
        var tile = IconTile(iconKey, 32);
        tile.HorizontalAlignment = HorizontalAlignment.Center;

        var hintText = new TextBlock
        {
            Text = hint,
            FontSize = CaptionSize + 0.5,
            Foreground = ThemeTokens.Brush("NekoDimBrush"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var button = LinkButton(actionText, action);
        button.HorizontalAlignment = HorizontalAlignment.Center;

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(tile);
        body.Children.Add(hintText);
        body.Children.Add(button);

        return Card(L.T("Home.Empty.NoData", "暂无数据"), null, body);
    }

    public static Border Notice(string text, string foregroundToken = "NekoDangerBrush", string backgroundToken = "NekoDangerSoftBrush") =>
        new()
        {
            Background = ThemeTokens.Brush(backgroundToken),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(9, 6),
            Child = Row(6, Icon("IconWarning", 12, foregroundToken), new TextBlock
            {
                Text = text,
                FontSize = CaptionSize + 0.5,
                Foreground = ThemeTokens.Brush(foregroundToken),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            }),
        };

    public static StackPanel InlineIcon(string iconKey, string text, string brushKey = "NekoDimBrush", double iconSize = 11) =>
        Row(5, Icon(iconKey, iconSize, brushKey), new TextBlock
        {
            Text = text,
            FontSize = CaptionSize + 0.5,
            Foreground = ThemeTokens.Brush(brushKey),
            VerticalAlignment = VerticalAlignment.Center,
        });

    public static void OnDetach(Control control, Action cleanup) =>
        control.DetachedFromVisualTree += (_, _) => cleanup();

    public static void OnBecameVisible(Control control, Action action) =>
        control.AttachedToVisualTree += (_, _) => action();
}
