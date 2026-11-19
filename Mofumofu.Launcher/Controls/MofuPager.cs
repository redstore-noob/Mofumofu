using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Mofumofu.Launcher.Services;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Controls;

/// <summary>
/// 下载页通用分页器：前后箭头 + 最多五个圆形页码按钮（当前页 ±2）+ 页码输入回车跳转。
/// 只有一页时整体隐藏。
/// </summary>
public sealed class MofuPager : UserControl
{
    private const int MaxDots = 5;

    private readonly StackPanel _dots;
    private readonly Button _prev;
    private readonly Button _next;
    private readonly TextBox _jump;
    private readonly TextBlock _total;

    private int _pageCount = 1;
    private int _current;

    public event Action<int>? PageChanged;

    public int PageCount => _pageCount;
    public int CurrentPage => _current;

    public MofuPager()
    {
        _dots = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _prev = CreateArrow("IconArrowLeft");
        _prev.Margin = new Thickness(0, 0, 6, 0);
        ToolTip.SetTip(_prev, L.T("Common.Pager.Prev", "上一页"));
        _prev.Click += (_, _) => Select(Math.Max(0, _current - 1));

        _next = CreateArrow("IconArrowRight");
        _next.Margin = new Thickness(6, 0, 0, 0);
        ToolTip.SetTip(_next, L.T("Common.Pager.Next", "下一页"));
        _next.Click += (_, _) => Select(Math.Min(_pageCount - 1, _current + 1));

        _jump = new TextBox
        {
            Width = 44,
            Height = 26,
            MinWidth = 0,
            Padding = new Thickness(0),
            FontSize = 10.5,
            TextAlignment = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(_jump, L.T("Common.Pager.JumpHint", "输入页码按回车跳转"));
        _jump.KeyDown += OnJumpKeyDown;
        _jump.GotFocus += (_, _) => _jump.SelectAll();

        _total = new TextBlock
        {
            Text = "/ 1",
            FontSize = 10.5,
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeTokens.Brush("NekoMutedBrush"),
        };

        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _prev, _dots, _next, _jump, _total },
        };
        IsVisible = false;
    }

    public void SetPageCount(int pageCount, int currentPage)
    {
        int newCount = Math.Max(1, pageCount);
        int page = Math.Clamp(currentPage, 0, newCount - 1);
        // 页数变化也要重建：首次可能是「空列表 1 页」，数据到位后才变成 N 页，
        // 只比较页码会漏掉这一次，分页器就永远藏着不显示。
        bool changed = newCount != _pageCount || page != _current || _dots.Children.Count == 0;
        _pageCount = newCount;
        _current = page;
        if (changed) Rebuild();
    }

    private void Select(int page)
    {
        if (page == _current) return;
        _current = page;
        Rebuild();
        PageChanged?.Invoke(_current);
    }

    private void OnJumpKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (int.TryParse(_jump.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int page))
        {
            Select(Math.Clamp(page - 1, 0, _pageCount - 1));
        }

        TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);
        _jump.Text = (_current + 1).ToString(CultureInfo.InvariantCulture);
    }

    private void Rebuild()
    {
        _dots.Children.Clear();
        IsVisible = _pageCount > 1;
        _total.Text = "/ " + _pageCount.ToString(CultureInfo.InvariantCulture);
        _prev.IsEnabled = _current > 0;
        _next.IsEnabled = _current < _pageCount - 1;
        _prev.Opacity = _prev.IsEnabled ? 1 : 0.35;
        _next.Opacity = _next.IsEnabled ? 1 : 0.35;

        if (!_jump.IsFocused) _jump.Text = (_current + 1).ToString(CultureInfo.InvariantCulture);

        int start = Math.Clamp(_current - MaxDots / 2, 0, Math.Max(0, _pageCount - MaxDots));
        int end = Math.Min(_pageCount, start + MaxDots);
        for (int page = start; page < end; page++)
        {
            _dots.Children.Add(CreateDot(page, page == _current));
        }
    }

    private static Button CreateArrow(string iconKind)
    {
        var arrow = new Button
        {
            Content = new IconParkIcon { Kind = iconKind, Width = 11, Height = 11 },
            Width = 24,
            Height = 24,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(0),
            Background = ThemeTokens.Brush("NekoFieldSoftBrush"),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        arrow.PointerEntered += (_, _) =>
        {
            if (arrow.IsEnabled) arrow.Background = ThemeTokens.Brush("NekoFieldHoverBrush");
        };
        arrow.PointerExited += (_, _) => arrow.Background = ThemeTokens.Brush("NekoFieldSoftBrush");
        return arrow;
    }

    private Button CreateDot(int page, bool active)
    {
        var number = new TextBlock
        {
            Text = (page + 1).ToString(CultureInfo.InvariantCulture),
            FontSize = 10.5,
            FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = active
                ? ThemeTokens.Brush("NekoAccentOnBrush")
                : ThemeTokens.Brush("NekoMutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var dot = new Button
        {
            Content = number,
            Width = 24,
            Height = 24,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(0),
            Background = active
                ? ThemeTokens.Brush("NekoAccentBrush")
                : ThemeTokens.Brush("NekoFieldSoftBrush"),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        if (!active)
        {
            dot.PointerEntered += (_, _) => dot.Background = ThemeTokens.Brush("NekoFieldHoverBrush");
            dot.PointerExited += (_, _) => dot.Background = ThemeTokens.Brush("NekoFieldSoftBrush");
        }

        dot.Click += (_, _) => Select(page);
        return dot;
    }
}
