using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Mofumofu.Launcher.Services;

namespace Mofumofu.Launcher.Controls;

/// <summary>
/// 弹窗内容拼装件：卡片骨架（标题 / 分隔线 / 内边距）由 <see cref="MofuDialogHost"/> 负责，
/// 按钮与输入框的外观由 AppStyles 里的 <c>dialog-*</c> / <c>TextBox.field</c> 负责。
/// <para>
/// 这里只决定结构。以前每个弹窗各写一套 <c>Height = 30/32/34</c>、<c>Padding = 12/14</c>、
/// <c>Spacing = 8/10/12</c>，同一个启动器里能数出四五种弹窗长相——现在统一走这几个方法，
/// 新弹窗也别再手搓按钮。
/// </para>
/// </summary>
public static class MofuDialog
{
    /// <summary>表单内容宽度（卡片会再补左右各 24 的内边距）。</summary>
    public const double FormWidth = 360;

    /// <summary>纵向表单容器：默认 14 行距。传 0 宽度表示不锁宽、由内容决定。</summary>
    public static StackPanel Stack(double width = FormWidth, double spacing = 14)
    {
        var panel = new StackPanel { Spacing = spacing };
        if (width > 0) panel.Width = width;
        return panel;
    }

    /// <summary>正文段落：13px、自动换行、主题文本色。</summary>
    public static TextBlock Message(string text) =>
        new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 21,
            Foreground = ThemeTokens.Brush("NekoTextBrush"),
        };

    /// <summary>表单标签（输入框上方的小字）。</summary>
    public static TextBlock Label(string text)
    {
        var label = new TextBlock { Text = text };
        label.Classes.Add("field-label");
        return label;
    }

    /// <summary>「标签 + 控件」一组，标签距控件统一 6。</summary>
    public static StackPanel Field(string label, Control field) =>
        new() { Spacing = 6, Children = { Label(label), field } };

    /// <summary>弹窗输入框：外观交给 <c>TextBox.field</c>。</summary>
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

    /// <summary>右下角按钮行：取消在左、主操作在右。</summary>
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

    /// <summary>主操作按钮（主题色实底）。</summary>
    public static Button Primary(string text, Action onClick) => ActionButton(text, "primary", onClick);

    /// <summary>次要操作按钮（软底无描边）。</summary>
    public static Button Secondary(string text, Action onClick) => ActionButton(text, "dialog-secondary", onClick);

    /// <summary>危险操作按钮（红底，确认删除/重置用）。</summary>
    public static Button Danger(string text, Action onClick) => ActionButton(text, "dialog-danger", onClick);

    private static Button ActionButton(string text, string kind, Action onClick)
    {
        var button = new Button { Content = text, Classes = { kind, "dialog-action" } };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>关掉按钮所在的弹窗（各页面里那堆 DismissDialog 副本的统一实现）。</summary>
    public static void Close(Control source) =>
        source.FindAncestorOfType<MofuDialogHost>(true)?.Manager?.DismissDialog();

    /// <summary>回车即提交。</summary>
    public static void SubmitOnEnter(TextBox box, Action submit)
    {
        box.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter) submit();
        };
    }

    /// <summary>提示条（危险确认里的红框说明之类）。</summary>
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
