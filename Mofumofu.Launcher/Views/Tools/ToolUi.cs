using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Mofumofu.Launcher.Controls;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views;

internal static class ToolUi
{
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

    public static TextBlock Status(string text) => new()
    {
        Text = text,
        Classes = { "caption" },
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 8, 0, 2),
    };

    public static ToggleSwitch Switch(bool isChecked = false) => new()
    {
        IsChecked = isChecked,
        OnContent = L.T("Common.Enabled", "开"),
        OffContent = L.T("Common.Disabled", "关"),
        VerticalAlignment = VerticalAlignment.Center,
    };

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