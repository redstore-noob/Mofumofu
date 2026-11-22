using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Mofumofu.Launcher.Services;

namespace Mofumofu.Launcher.Controls;

public static class MofuDialog
{
    public const double FormWidth = 360;

    public static StackPanel Stack(double width = FormWidth, double spacing = 14)
    {
        var panel = new StackPanel { Spacing = spacing };
        if (width > 0) panel.Width = width;
        return panel;
    }

    public static TextBlock Message(string text) =>
        new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 21,
            Foreground = ThemeTokens.Brush("NekoTextBrush"),
        };

    public static TextBlock Label(string text)
    {
        var label = new TextBlock { Text = text };
        label.Classes.Add("field-label");
        return label;
    }

    public static StackPanel Field(string label, Control field) =>
        new() { Spacing = 6, Children = { Label(label), field } };

    public static TextBox Input(string? text = null, string? hint = null)
    {
        var box = new TextBox
        {
            Text = text ?? string.Empty,
            PlaceholderText = hint,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        box.Classes.Add("field");
        return box;
    }

    public static StackPanel Footer(params Control[] buttons)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
        };

        foreach (var button in buttons) row.Children.Add(button);
        return row;
    }

    public static Button Primary(string text, Action onClick) => ActionButton(text, "primary", onClick);

    public static Button Secondary(string text, Action onClick) => ActionButton(text, "dialog-secondary", onClick);

    public static Button Danger(string text, Action onClick) => ActionButton(text, "dialog-danger", onClick);

    private static Button ActionButton(string text, string kind, Action onClick)
    {
        object content = kind == "dialog-danger"
            ? new TextBlock { Text = text, Foreground = ThemeTokens.Brush("NekoAccentOnBrush") }
            : text;
        var button = new Button { Content = content, Classes = { kind, "dialog-action" } };
        button.Click += (_, _) => onClick();
        return button;
    }

    public static void Close(Control source) =>
        source.FindAncestorOfType<MofuDialogHost>(true)?.Manager?.DismissDialog();

    public static void SubmitOnEnter(TextBox box, Action submit)
    {
        box.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter) submit();
        };
    }

    public static Border Notice(string text, string tone = "NekoDanger")
    {
        return new Border
        {
            Background = ThemeTokens.Brush(tone + "SoftBrush"),
            BorderBrush = ThemeTokens.Brush(tone + "Brush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10),
            Child = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
                LineHeight = 20,
                Foreground = ThemeTokens.Brush(tone + "Brush"),
            },
        };
    }
}
