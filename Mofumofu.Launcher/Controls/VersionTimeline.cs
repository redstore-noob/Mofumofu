using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Mofumofu.Launcher.Services;

namespace Mofumofu.Launcher.Controls;

public sealed class VersionTimelineNode
{
    public string Title { get; init; } = string.Empty;

    public string Subtitle { get; init; } = string.Empty;

    public string Badge { get; init; } = string.Empty;

    public string BadgeTone { get; init; } = "NekoMuted";

    public string GroupLabel { get; init; } = string.Empty;

    public bool IsCurrent { get; init; }

    public bool IsSelectable { get; init; } = true;

    public object? Tag { get; init; }
}

public sealed class VersionTimeline : UserControl
{
    private readonly StackPanel _rows = new();
    private readonly List<NodeView> _views = [];
    private readonly IBrush _lineBrush;

    public VersionTimeline()
    {
        _lineBrush = ThemeTokens.Brush("NekoBorderBrush");

        _rows.Spacing = 0;
        Content = new ScrollViewer
        {
            Content = _rows,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 2, 6, 2),
        };
    }

    public event EventHandler<VersionTimelineNode>? NodeSelected;

    public VersionTimelineNode? SelectedNode { get; private set; }

    public void SetNodes(IEnumerable<VersionTimelineNode> nodes, VersionTimelineNode? selected = null)
    {
        _rows.Children.Clear();
        _views.Clear();
        SelectedNode = null;

        foreach (VersionTimelineNode node in nodes)
        {
            if (!string.IsNullOrWhiteSpace(node.GroupLabel))
            {
                _rows.Children.Add(BuildGroupRow(node.GroupLabel));
            }

            NodeView view = BuildRow(node);
            _views.Add(view);
            _rows.Children.Add(view.Row);
        }

        if (selected is not null)
        {
            Select(selected, raiseEvent: false);
        }
    }

    public void ScrollToSelected()
    {
        NodeView? view = _views.FirstOrDefault(item => ReferenceEquals(item.Node, SelectedNode));
        if (view is null) return;

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                view.Card.BringIntoView();
            }
            catch (Exception)
            {
            }
        }, DispatcherPriority.Background);
    }

    private Control BuildGroupRow(string label)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*") };
        grid.Children.Add(CreateAxisLine());

        var text = new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeTokens.Brush("NekoDimBrush"),
            Margin = new Thickness(0, 12, 0, 4),
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private NodeView BuildRow(VersionTimelineNode node)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*") };

        var axis = new Grid();
        axis.Children.Add(CreateAxisLine());

        Border dot = CreateDot(node);
        axis.Children.Add(dot);
        row.Children.Add(axis);

        Border card = CreateCard(node);
        Grid.SetColumn(card, 1);
        row.Children.Add(card);

        var view = new NodeView(row, card, dot, node);
        ApplyState(view, node.IsCurrent);

        card.PointerEntered += (_, _) =>
        {
            if (node.IsSelectable && !ReferenceEquals(SelectedNode, node))
            {
                card.Background = ThemeTokens.Brush("NekoFieldBrush");
            }
        };
        card.PointerExited += (_, _) =>
        {
            if (!ReferenceEquals(SelectedNode, node))
            {
                ApplyState(view, node.IsCurrent);
            }
        };
        card.PointerPressed += (_, args) =>
        {
            if (!node.IsSelectable) return;
            if (!args.GetCurrentPoint(card).Properties.IsLeftButtonPressed) return;
            Select(node);
        };

        return view;
    }

    private Border CreateAxisLine() => new()
    {
        Width = 1.5,
        Background = _lineBrush,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Stretch,
    };

    private static Border CreateDot(VersionTimelineNode node) => new()
    {
        Width = node.IsCurrent ? 11 : 9,
        Height = node.IsCurrent ? 11 : 9,
        CornerRadius = new CornerRadius(node.IsCurrent ? 5.5 : 4.5),
        Background = ThemeTokens.Brush(node.IsCurrent ? "NekoAccentBrush" : "NekoBorderBrush"),
        BorderBrush = ThemeTokens.Brush("NekoCardSolidBrush"),
        BorderThickness = new Thickness(2),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private Border CreateCard(VersionTimelineNode node)
    {
        var title = new TextBlock
        {
            Text = node.Title,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeTokens.Brush("NekoTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var subtitle = new TextBlock
        {
            Text = node.Subtitle,
            FontSize = 11.5,
            Foreground = ThemeTokens.Brush("NekoMutedBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var texts = new StackPanel { Spacing = 2 };
        texts.Children.Add(title);
        if (!string.IsNullOrWhiteSpace(node.Subtitle)) texts.Children.Add(subtitle);

        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        content.Children.Add(texts);

        if (!string.IsNullOrWhiteSpace(node.Badge))
        {
            var badge = new Border
            {
                Background = ThemeTokens.Brush(node.BadgeTone + "SoftBrush"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(7, 2),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = node.Badge,
                    FontSize = 10.5,
                    Foreground = ThemeTokens.Brush(node.BadgeTone + "Brush"),
                },
            };
            Grid.SetColumn(badge, 1);
            content.Children.Add(badge);
        }

        var card = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 2, 0, 2),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent,
            Child = content,
            Cursor = new Cursor(node.IsSelectable ? StandardCursorType.Hand : StandardCursorType.Arrow),
        };

        return card;
    }

    private void Select(VersionTimelineNode node, bool raiseEvent = true)
    {
        SelectedNode = node;
        foreach (NodeView view in _views)
        {
            ApplyState(view, ReferenceEquals(view.Node, node));
        }

        if (raiseEvent) NodeSelected?.Invoke(this, node);
    }

    private static void ApplyState(NodeView view, bool active)
    {
        bool current = view.Node.IsCurrent;
        view.Card.Background = active
            ? ThemeTokens.Brush("NekoAccentSoftBrush")
            : current ? ThemeTokens.Brush("NekoFieldSoftBrush") : Brushes.Transparent;
        view.Card.BorderBrush = active
            ? ThemeTokens.Brush("NekoAccentLineBrush")
            : current ? ThemeTokens.Brush("NekoBorderBrush") : Brushes.Transparent;

        if (view.Dot is null) return;
        bool highlight = active || current;
        view.Dot.Width = highlight ? 11 : 9;
        view.Dot.Height = highlight ? 11 : 9;
        view.Dot.CornerRadius = new CornerRadius(highlight ? 5.5 : 4.5);
        view.Dot.Background = ThemeTokens.Brush(highlight ? "NekoAccentBrush" : "NekoBorderBrush");
    }

    private sealed record NodeView(Control Row, Border Card, Border? Dot, VersionTimelineNode Node);
}
