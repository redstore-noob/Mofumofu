using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Mofumofu.Launcher.Services;
using SukiUI.Controls.GlassMorphism;

namespace Mofumofu.Launcher.Controls;

public class MofuDialogHost : Panel
{
    private static readonly IBrush ScrimBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00));

    private static readonly IBrush PopoverScrimBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00));

    private Control? _card;
    private Control? _scrim;
    private bool _dismissOnBackdrop;
    private bool _closing;
    private bool _popover;
    private RelativePoint _origin = new(0.5, 0.5, RelativeUnit.Relative);

    public DialogManager Manager { get; }

    public bool IsOpen => IsVisible && !_closing;

    public MofuDialogHost()
    {
        Manager = new DialogManager(this);
        IsVisible = false;
        ClipToBounds = true;
        Focusable = true;
    }

    internal void Present(Control card, bool dismissOnBackdrop, Control? anchor = null)
    {
        Children.Clear();
        _closing = false;
        _dismissOnBackdrop = dismissOnBackdrop;
        _card = card;
        _popover = false;

        var scrim = CreateScrim(ScrimBrush, blur: true);

        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment = VerticalAlignment.Center;
        card.Margin = new Thickness(25);

        _origin = ResolvePopOrigin(anchor, card);
        UiAnimations.PopFrom(card, _origin, 0.86, 220);

        _scrim = scrim;
        Children.Add(scrim);
        Children.Add(card);

        IsVisible = true;
        Manager.RaiseShown();

        // 先以 0 透明度入树，下一帧再赋值，Transition 才会真的跑起来
        Dispatcher.UIThread.Post(
            () =>
            {
                scrim.Opacity = 1;
                card.Opacity = 1;
                card.RenderTransform = TransformOperations.Parse("scale(1)");
            },
            DispatcherPriority.Background);

        Focus();
    }

    private static RelativePoint ResolvePopOrigin(Control? anchor, Control card)
    {
        var center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        if (anchor is null) return center;

        var top = TopLevel.GetTopLevel(anchor);
        if (top is null) return center;

        var anchorCenter = anchor.TranslatePoint(
            new Point(anchor.Bounds.Width / 2, anchor.Bounds.Height / 2), top);
        if (anchorCenter is not { } point) return center;

        var available = top is Window window ? window.ClientSize : new Size(1280, 800);
        if (available.Width <= 0 || available.Height <= 0) return center;

        card.Measure(new Size(Math.Max(120, available.Width - 48), Math.Max(120, available.Height - 48)));
        var size = card.DesiredSize;
        if (size.Width <= 0 || size.Height <= 0) return center;

        var x = (available.Width - size.Width) / 2;
        var y = (available.Height - size.Height) / 2;

        return new RelativePoint(
            Math.Clamp((point.X - x) / size.Width, 0, 1),
            Math.Clamp((point.Y - y) / size.Height, 0, 1),
            RelativeUnit.Relative);
    }

    internal bool PresentPopover(Control anchor, Control card, bool dismissOnBackdrop)
    {
        var top = TopLevel.GetTopLevel(anchor);
        var anchorPoint = top is null ? null : anchor.TranslatePoint(new Point(0, 0), top);
        if (top is null || anchorPoint is null) return false;

        Children.Clear();
        _closing = false;
        _dismissOnBackdrop = dismissOnBackdrop;
        _card = card;
        _popover = true;

        var scrim = CreateScrim(PopoverScrimBrush, blur: false);

        var available = top is Window window ? window.ClientSize : new Size(1280, 800);
        var limitWidth = Math.Max(120, available.Width - 24);
        var limitHeight = Math.Max(120, available.Height - 24);
        if (double.IsNaN(card.MaxHeight) || card.MaxHeight > limitHeight) card.MaxHeight = limitHeight;

        card.Measure(new Size(limitWidth, limitHeight));
        var size = card.DesiredSize;
        if (size.Width <= 0 || size.Height <= 0) return false;

        const double gap = 8;
        const double edge = 12;

        var x = anchorPoint.Value.X + anchor.Bounds.Width - size.Width;
        var y = anchorPoint.Value.Y + anchor.Bounds.Height + gap;
        if (y + size.Height + edge > available.Height)
        {
            y = anchorPoint.Value.Y - size.Height - gap;
        }

        y = Math.Max(edge, y);
        x = Math.Clamp(x, edge, Math.Max(edge, available.Width - size.Width - edge));

        var centerX = anchorPoint.Value.X + anchor.Bounds.Width / 2;
        var centerY = anchorPoint.Value.Y + anchor.Bounds.Height / 2;
        var origin = new RelativePoint(
            Math.Clamp((centerX - x) / size.Width, 0, 1),
            Math.Clamp((centerY - y) / size.Height, 0, 1),
            RelativeUnit.Relative);

        UiAnimations.PopFrom(card, origin, 0.82, 220);
        Canvas.SetLeft(card, x);
        Canvas.SetTop(card, y);

        var stage = new Canvas();
        stage.Children.Add(card);

        _scrim = scrim;
        Children.Add(scrim);
        Children.Add(stage);

        IsVisible = true;
        Manager.RaiseShown();

        // 先以初始缩放入树，下一帧再切到终态，Transition 才会真的跑起来
        Dispatcher.UIThread.Post(
            () =>
            {
                scrim.Opacity = 1;
                card.Opacity = 1;
                card.RenderTransform = TransformOperations.Parse("scale(1)");
            },
            DispatcherPriority.Background);

        Focus();
        return true;
    }

    private Border CreateScrim(IBrush brush, bool blur)
    {
        var layer = new Panel();

        if (blur && GlassEffects.Enabled && BlurBackground.IsGpuBlurAvailable)
        {
            layer.Children.Add(new BlurBackground
            {
                IntensityFactor = 1d,
                // 只留薄薄一层霜，压暗与色调仍交给上面的纯色遮罩，避免浅色主题发白。
                OverlayOpacity = 0.35,
            });
        }

        layer.Children.Add(new Border { Background = brush });

        var scrim = new Border
        {
            Child = layer,
            Opacity = 0,
            Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(150),
                },
            },
        };

        scrim.PointerPressed += (_, _) =>
        {
            if (_dismissOnBackdrop) Dismiss();
        };

        return scrim;
    }

    internal void Dismiss()
    {
        if (!IsVisible || _closing) return;
        _closing = true;

        // 简单淡出：先压暗再摘除，避免弹窗"啪"地消失；
        // 浮层与居中弹窗都缩回弹出原点（触发控件方向），没锚点时原点就是窗口中心
        if (_scrim is not null) _scrim.Opacity = 0;
        if (_card is not null)
        {
            if (_popover)
            {
                UiAnimations.PopOut(_card, 0.9, 4, 140);
            }
            else
            {
                UiAnimations.PopOutFrom(_card, _origin, 0.88, 150);
            }
        }

        DispatcherTimer.RunOnce(
            () =>
            {
                Children.Clear();
                _card = null;
                _scrim = null;
                _popover = false;
                IsVisible = false;
                _closing = false;
                Manager.RaiseDismissed();
            },
            TimeSpan.FromMilliseconds(160));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _dismissOnBackdrop)
        {
            Dismiss();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    internal static IBrush Res(string key, Color fallback)
    {
        if (Application.Current is { } app
            && app.TryFindResource(key, out var v)
            && v is IBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(fallback);
    }
}

public class DialogManager
{
    private readonly MofuDialogHost _host;

    internal DialogManager(MofuDialogHost host) => _host = host;

    public event EventHandler? OnDialogShown;

    public event EventHandler? OnDialogDismissed;

    public DialogBuilder CreateDialog() => new(_host);

    public void DismissDialog() => _host.Dismiss();

    internal void RaiseShown() => OnDialogShown?.Invoke(this, EventArgs.Empty);

    internal void RaiseDismissed() => OnDialogDismissed?.Invoke(this, EventArgs.Empty);
}

public class DialogBuilder
{
    private readonly MofuDialogHost _host;
    private string _title = string.Empty;
    private object? _content;
    private bool _showCloseButton;
    private bool _dismissOnBackdrop;
    private Control? _anchor;

    internal DialogBuilder(MofuDialogHost host) => _host = host;

    public DialogBuilder From(Control? anchor)
    {
        _anchor = anchor;
        return this;
    }

    public DialogBuilder WithTitle(string title)
    {
        _title = title ?? string.Empty;
        return this;
    }

    public DialogBuilder WithContent(object? content)
    {
        _content = content;
        return this;
    }

    public DialogBuilder Dismiss()
    {
        _showCloseButton = true;
        return this;
    }

    public DialogBuilder ByClickingBackground()
    {
        _dismissOnBackdrop = true;
        return this;
    }

    public void TryShow()
    {
        _host.Present(BuildCard(), _dismissOnBackdrop, _anchor);
    }

    private Control BuildCard()
    {
        var body = new StackPanel { Spacing = 0 };
        var hasTitle = !string.IsNullOrEmpty(_title);

        var selfChrome = _showCloseButton && !hasTitle;

        if (hasTitle || _showCloseButton)
        {
            var header = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(24, 16, 14, 14),
            };

            if (hasTitle)
            {
                header.Children.Add(new TextBlock
                {
                    Text = _title,
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxLines = 1,
                    Foreground = MofuDialogHost.Res("NekoTextBrush", Colors.White),
                });
            }

            if (_showCloseButton)
            {
                var close = new Button
                {
                    Content = "\u2715",
                    Classes = { "dialog-close" },
                    VerticalAlignment = VerticalAlignment.Center,
                };
                close.Click += (_, _) => _host.Dismiss();
                Grid.SetColumn(close, 1);
                header.Children.Add(close);
            }

            body.Children.Add(header);

            if (hasTitle)
            {
                body.Children.Add(new Border
                {
                    Height = 1,
                    Background = MofuDialogHost.Res("NekoBorderBrush", Color.FromArgb(0xFF, 0x26, 0x30, 0x3D)),
                });
            }
        }

        switch (_content)
        {
            case null:
                break;
            case Control control:
                body.Children.Add(Frame(control, selfChrome));
                break;
            case string text:
                body.Children.Add(new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                    LineHeight = 21,
                    Margin = new Thickness(24, 16, 24, 22),
                    Foreground = MofuDialogHost.Res("NekoTextBrush", Colors.White),
                });
                break;
            default:
                body.Children.Add(new TextBlock
                {
                    Text = _content.ToString(),
                    Margin = new Thickness(24, 16, 24, 22),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                });
                break;
        }

        return MofuCard.Dialog(body);
    }

    private static Control Frame(Control content, bool selfChrome)
    {
        var hasOwnMargin = content.Margin != default;
        if (selfChrome || hasOwnMargin) return content;

        return new Border
        {
            Padding = new Thickness(24, 18, 24, 22),
            Child = content,
        };
    }
}
