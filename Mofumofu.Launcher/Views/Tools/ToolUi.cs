using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Mofumofu.Launcher.Controls;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views;

/// <summary>
/// 创作中心工具页的统一面板构件：与设置页同一套视觉语言
/// （setting-group 卡片 + 图标标题 + setting-row 左标签右控件，整页上下滚动）。
/// </summary>
internal static class ToolUi
{
    /// <summary>一张面板：图标徽章 + 标题（可选副标题），下面依次排各行。</summary>
    public static Border Group(string iconKey, string title, string? hint, params Control[] children)
    {
        var panel = new StackPanel();
        panel.Children.Add(Header(iconKey, title, hint));
        foreach (var child in children)
        {
            DetachFromParent(child);
            panel.Children.Add(child);
        }

        return new Border
        {
            Classes = { "setting-group" },
            Child = panel,
        };
    }

    /// <summary>面板头：30×30 图标徽章 + 标题（可选副标题），与设置页一致。</summary>
    public static Grid Header(string iconKey, string title, string? hint)
    {
        var headerText = new StackPanel
        {
            Spacing = 2,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerText.Children.Add(new TextBlock { Text = title, Classes = { "group-title" } });
        if (!string.IsNullOrWhiteSpace(hint))
        {
            headerText.Children.Add(new TextBlock { Text = hint, Classes = { "row-hint" } });
        }
        Grid.SetColumn(headerText, 1);

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(0, 0, 0, 8),
        };
        header.Children.Add(new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(9),
            Background = Services.ThemeTokens.Brush("NekoAccentSoftBrush"),
            Child = new IconParkIcon
            {
                Kind = iconKey,
                Width = 15,
                Height = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        header.Children.Add(headerText);
        return header;
    }

    /// <summary>一行设置：左侧标签 + 可选提示，右侧控件。</summary>
    public static Border Row(string label, string? hint, Control control)
    {
        var left = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 16, 0) };
        left.Children.Add(new TextBlock { Text = label, Classes = { "row-label" } });
        if (!string.IsNullOrWhiteSpace(hint))
        {
            left.Children.Add(new TextBlock { Text = hint, Classes = { "row-hint" } });
        }

        return RowCore(left, control);
    }

    public static Border Row(string label, Control control) => Row(label, null, control);

    /// <summary>提示文本需要动态更新时用这个重载（例如校验错误）。</summary>
    public static Border RowWithHintControl(string label, TextBlock hint, Control control)
    {
        hint.Classes.Add("row-hint");
        var left = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 16, 0) };
        left.Children.Add(new TextBlock { Text = label, Classes = { "row-label" } });
        left.Children.Add(hint);
        return RowCore(left, control);
    }

    private static Border RowCore(StackPanel left, Control control)
    {
        DetachFromParent(control);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(left);
        grid.Children.Add(control);

        return new Border
        {
            Classes = { "setting-row" },
            Child = grid,
        };
    }

    /// <summary>面板里的整宽内容块（代码预览、画布等），软底圆角。</summary>
    public static Border SoftBlock(Control content)
    {
        DetachFromParent(content);
        return new Border
        {
            Classes = { "card-soft" },
            Padding = new Thickness(12, 10),
            Child = content,
        };
    }

    /// <summary>右对齐的操作按钮行。</summary>
    public static StackPanel Actions(params Control[] buttons)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        foreach (var button in buttons)
        {
            DetachFromParent(button);
            row.Children.Add(button);
        }

        return row;
    }

    /// <summary>面板底部的状态说明。</summary>
    public static TextBlock Status(string text) => new()
    {
        Text = text,
        Classes = { "caption" },
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 8, 0, 2),
    };

    /// <summary>与设置页一致的开关。</summary>
    public static ToggleSwitch Switch(bool isChecked = false) => new()
    {
        IsChecked = isChecked,
        OnContent = L.T("Common.Enabled", "开"),
        OffContent = L.T("Common.Disabled", "关"),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>整页：面板自上而下排列，垂直滚动。</summary>
    public static ScrollViewer Page(params Control[] groups)
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(2, 13, 10, 24),
            MaxWidth = 1000,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        foreach (var group in groups)
        {
            panel.Children.Add(group);
        }

        return new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    /// <summary>控件换父级前先摘下来，避免 Avalonia「already has a visual parent」崩溃。</summary>
    public static void DetachFromParent(Control control)
    {
        switch (control.Parent)
        {
            case Panel panel:
                panel.Children.Remove(control);
                break;
            case Decorator decorator:
                decorator.Child = null;
                break;
            case ContentControl contentControl:
                contentControl.Content = null;
                break;
        }
    }
}