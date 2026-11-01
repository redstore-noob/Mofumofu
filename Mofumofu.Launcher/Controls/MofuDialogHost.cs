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

namespace Mofumofu.Launcher.Controls;

/// <summary>
/// 自绘对话框宿主：覆盖全窗口的一层，负责遮罩、居中卡片、开合与 Esc 关闭。
/// <para>
/// 取代 SukiUI 的 SukiDialogHost。上游那套对 Mofumofu 只提供了三件事——
/// 遮罩、卡片底板、右上角关闭按钮——其余全是 Suki 自己的玻璃材质，而我们的弹窗
/// 早在 <c>AppStyles</c> 里就被强制改成实心底板了。所以这层可以自己长，不需要背整个库。
/// </para>
/// </summary>
public class MofuDialogHost : Panel
{
    // 遮罩固定用半透明黑，跟主题、跟 SukiUI 都无关：
    // 浅色主题下压成灰、深色主题下压成暗，永远是"把后面的东西退到幕后"这一个语义。
    // 之前走 NekoScrimBrush，浅色主题里它是纯白 → 一盖上去整个窗口变白，弹窗像浮在空气里。
    private static readonly IBrush ScrimBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00));

    // 浮层的遮罩比弹窗浅一档：面板贴在触发控件旁边，太重会把界面压成"另一个弹窗"。
    private static readonly IBrush PopoverScrimBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00));

    private Control? _card;
    private Border? _scrim;
    private bool _dismissOnBackdrop;
    private bool _closing;
    private bool _popover;
    private RelativePoint _origin = new(0.5, 0.5, RelativeUnit.Relative);

    public DialogManager Manager { get; }

    /// <summary>当前是否正有一个居中的弹窗 / 浮层展开（关闭动画进行中视为已关闭）。</summary>
    public bool IsOpen => IsVisible && !_closing;

    public MofuDialogHost()
    {
        Manager = new DialogManager(this);
        IsVisible = false;
        ClipToBounds = true;
        Focusable = true;
    }

    /// <summary>
    /// 居中弹窗：窗口级遮罩 + 居中卡片。
    /// <paramref name="anchor"/> 是触发这次弹窗的控件，卡片会从它所在的方向"蹦"出来
    /// （以控件中心为缩放原点放大）；未声明或找不到锚点时从窗口中心弹出。
    /// </summary>
    internal void Present(Control card, bool dismissOnBackdrop, Control? anchor = null)
    {
        Children.Clear();
        _closing = false;
        _dismissOnBackdrop = dismissOnBackdrop;
        _card = card;
        _popover = false;

        var scrim = CreateScrim(ScrimBrush);

        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment = VerticalAlignment.Center;
        // 卡片到遮罩层边缘的内边距
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

    /// <summary>
    /// 解析弹出原点（相对卡片自身 0-1）：以触发控件中心为目标，卡片看起来从控件里长出来。
    /// anchor 未声明、不在视觉树、量不出尺寸时回退窗口中心（0.5, 0.5）。
    /// </summary>
    private static RelativePoint ResolvePopOrigin(Control? anchor, Control card)
    {
        var center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        if (anchor is null) return center;

        var top = TopLevel.GetTopLevel(anchor);
        if (top is null) return center;

        // 控件中心换算到窗口坐标；卡片最终是居中的，量出期望尺寸就能反推它的落位
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

    /// <summary>
    /// 浮层：整窗压一层浅遮罩，面板紧贴触发控件（<paramref name="anchor"/>）弹出，
    /// 并以控件中心为缩放原点，看起来是从这个控件里"蹦"出来的。
    /// </summary>
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

        var scrim = CreateScrim(PopoverScrimBrush);

        var available = top is Window window ? window.ClientSize : new Size(1280, 800);
        var limitWidth = Math.Max(120, available.Width - 24);
        var limitHeight = Math.Max(120, available.Height - 24);
        if (double.IsNaN(card.MaxHeight) || card.MaxHeight > limitHeight) card.MaxHeight = limitHeight;

        card.Measure(new Size(limitWidth, limitHeight));
        var size = card.DesiredSize;
        if (size.Width <= 0 || size.Height <= 0) return false;

        const double gap = 8;
        const double edge = 12;

        // 右边缘对齐触发控件，默认在控件下方；下方放不下就翻到上方
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

    private Border CreateScrim(IBrush brush)
    {
        var scrim = new Border
        {
            Background = brush,
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

    /// <summary>取一个主题令牌笔刷；找不到时退回给定颜色，保证不会因为缺资源而崩。</summary>
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

/// <summary>对话框管理器：与 SukiUI 的 <c>SukiDialogManager</c> 保持同名同形，调用点不用改。</summary>
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

/// <summary>链式构造器。方法名与 SukiUI 对齐，方便调用点原样迁移。</summary>
public class DialogBuilder
{
    private readonly MofuDialogHost _host;
    private string _title = string.Empty;
    private object? _content;
    private bool _showCloseButton;
    private bool _dismissOnBackdrop;
    private Control? _anchor;

    internal DialogBuilder(MofuDialogHost host) => _host = host;

    /// <summary>指定触发控件：弹窗从它的方向蹦出来；不调用或控件找不到时从窗口中心弹出。</summary>
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

    /// <summary>显示右上角关闭按钮。</summary>
    public DialogBuilder Dismiss()
    {
        _showCloseButton = true;
        return this;
    }

    /// <summary>允许点击遮罩关闭（同时启用 Esc）。</summary>
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

        // 自带标题的弹窗（Rewind / 快照）只要右上角一个 ✕，其余骨架全留给内容自己画
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

            // 标题区与内容之间的发丝线：卡片白底上没这条线，头部和表单会糊成一片
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

        // 卡片外观统一走 MofuCard，别在这里手搓 Border（圆角/投影/描边会和浮层面板跑偏）
        return MofuCard.Dialog(body);
    }

    /// <summary>
    /// 给内容套一层统一内边距。老调用点里已经自己写了 Margin 的（内容自带留白）原样放入，
    /// 免得两边叠加变成一大块空白；自带骨架的弹窗同样不插手。
    /// </summary>
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
