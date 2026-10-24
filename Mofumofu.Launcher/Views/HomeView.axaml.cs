using Mofumofu.Launcher.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mofumofu.Launcher.Services;
using Mofumofu.Launcher.Views.Widgets;
using Mofumofu.Launcher.Web;
using SukiUI.Controls;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views;

/// <summary>
/// 桌面：可自由摆放的小组件格子板：
/// 20 列格子、长按 1 秒拖动、落点吸附、右下角拖动改占格（双击恢复默认）、
/// 拖到开始栏或组件盒移除、组件盒支持搜索 / 点击添加 / 拖出放置，布局写入本地设置。
/// 组件内容由 <see cref="WidgetRegistry"/> 列出、<see cref="WidgetCards"/> 构建。
/// </summary>
public partial class HomeView : UserControl
{
    private const int Cols = 20;
    private const double Gap = 12;
    private const int MinRows = 9;
    private const int MaxRows = 40;
    private const double HoldMilliseconds = 1000;
    private const double HoldTolerance = 10;

    /// <summary>行最小高度：窗口太矮时格子缩到这么高为止，再不够就让整块板竖向滚动。</summary>
    private const double RowMinHeight = 48;

    /// <summary>桌面布局版本：默认版式变化时 +1 触发一次重排。</summary>
    private const int LayoutVersion = 5;

    private sealed class WidgetHost
    {
        public WidgetHost(WidgetDefinition definition, Grid host, Border resizeHandle)
        {
            Definition = definition;
            Host = host;
            ResizeHandle = resizeHandle;
        }

        public WidgetDefinition Definition { get; }
        public Grid Host { get; }
        public Border ResizeHandle { get; }
        public Control? Card { get; set; }
    }

    private readonly List<WidgetHost> _widgets = new();
    private readonly Dictionary<string, DesktopWidgetPlacement> _placements = new();
    private List<DesktopWidgetPage> _widgetPages = new();
    private int _activeWidgetPage;

    private DispatcherTimer? _holdTimer;
    private DispatcherTimer? _holdProgressTimer;
    private WidgetHost? _pressHost;
    private WidgetHost? _dragHost;
    private WidgetHost? _resizeHost;
    private WidgetHost? _libraryCandidate;
    private IPointer? _pressPointer;
    private Point _libraryPressPoint;
    private bool _dragFromLibrary;

    private Point _pressPoint;
    private Point _lastPointer;
    private Point _grabOffsetBoard;
    private int _targetX;
    private int _targetY;
    private bool _pressing;
    private bool _dragging;
    private bool _resizing;
    private bool _overDelete;
    private double _cellWidth = 48;
    private double _rowHeight = 66;
    private int _resizeStartW;
    private int _resizeStartH;
    private string _drawerQuery = string.Empty;

    public HomeView()
    {
        InitializeComponent();

        WidgetBoard.Background = Brushes.Transparent;
        BuildWidgetHosts();
        BuildBoardInteractions();

        // 插件动态小组件增删改 → 增量同步宿主（不走全量重建，避免闪烁）
        WidgetRegistry.Changed += OnDynamicWidgetsChanged;
        DetachedFromVisualTree += (_, _) => WidgetRegistry.Changed -= OnDynamicWidgetsChanged;

        AddHandler(PointerMovedEvent, OnRootPointerMoved, RoutingStrategies.Bubble, true);
        AddHandler(PointerReleasedEvent, OnRootPointerReleased, RoutingStrategies.Bubble, true);

        BoardScroll.SizeChanged += (_, _) => LayoutBoard();
        Loaded += (_, _) =>
        {
            LoadLayout();
            LayoutBoard();
            RefreshDrawer();
        };

        // 页面会被缓存：主题在别的页面切换时本页收不到事件，重新挂载时按版本补刷一次
        _themeVersion = ThemeManager.Version;
        AttachedToVisualTree += (_, _) =>
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
            ThemeManager.ThemeChanged += OnThemeChanged;
            if (_themeVersion != ThemeManager.Version)
            {
                OnThemeChanged();
            }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
        };
    }

    // ==================== 右侧启动卡 ====================

    private int _themeVersion = -1;

    // ================= 组件宿主 =================

    private void BuildWidgetHosts()
    {
        foreach (var definition in WidgetRegistry.All)
        {
            CreateWidgetHost(definition);
        }
    }

    /// <summary>为一个组件定义创建隐藏宿主并挂到组件层（内置与插件动态组件共用）。</summary>
    private void CreateWidgetHost(WidgetDefinition definition)
    {
        {
            var host = new Grid
            {
                ClipToBounds = true,
                IsVisible = false,
                RenderTransformOrigin = RelativePoint.Center,
            };

            var card = BuildCard(definition);
            host.Children.Add(card);

            var marker = new Border
            {
                Width = 10,
                Height = 10,
                Margin = new Thickness(0, 0, 4, 4),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                BorderBrush = ThemeTokens.Brush("NekoAccentBrush"),
                BorderThickness = new Thickness(0, 0, 2, 2),
                CornerRadius = new CornerRadius(0, 0, 3, 0),
            };

            var resizeHandle = new Border
            {
                Width = 20,
                Height = 20,
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Opacity = 0,
                Cursor = new Cursor(StandardCursorType.BottomRightCorner),
                Child = marker,
            };
            host.Children.Add(resizeHandle);

            WidgetLayer.Children.Add(host);
            _widgets.Add(new WidgetHost(definition, host, resizeHandle) { Card = card });
        }
    }

    private void OnDynamicWidgetsChanged()
    {
        if (Dispatcher.UIThread.CheckAccess() == false)
        {
            Dispatcher.UIThread.Post(OnDynamicWidgetsChanged);
            return;
        }

        List<string> dynamicIds = WidgetRegistry.DynamicIds.ToList();

        // 移除已注销的动态组件宿主
        foreach (WidgetHost stale in _widgets
                     .Where(widget => IsDynamicWidgetId(widget.Definition.Id) &&
                                      dynamicIds.Contains(widget.Definition.Id) == false)
                     .ToList())
        {
            WidgetLayer.Children.Remove(stale.Host);
            _widgets.Remove(stale);
            _placements.Remove(stale.Definition.Id);
        }

        // 已有动态组件被编辑（标题/内容变化）→ 重建它的宿主
        foreach (WidgetHost existing in _widgets
                     .Where(widget => IsDynamicWidgetId(widget.Definition.Id))
                     .ToList())
        {
            var fresh = WidgetRegistry.ById(existing.Definition.Id);
            if (fresh is null || ReferenceEquals(fresh, existing.Definition)) continue;

            bool wasVisible = existing.Host.IsVisible;
            var placement = _placements.TryGetValue(existing.Definition.Id, out var current)
                ? new { current.X, current.Y, current.W, current.H }
                : null;

            WidgetLayer.Children.Remove(existing.Host);
            _widgets.Remove(existing);
            _placements.Remove(existing.Definition.Id);

            CreateWidgetHost(fresh);
            if (placement is not null && _widgets.FirstOrDefault(w => w.Definition.Id == fresh.Id) is { } created)
            {
                _placements[fresh.Id] = new DesktopWidgetPlacement
                {
                    Id = fresh.Id, X = placement.X, Y = placement.Y, W = placement.W, H = placement.H,
                };
                created.Host.IsVisible = wasVisible;
            }
        }

        // 新增的动态组件：创建隐藏宿主（等待玩家从组件盒添加）
        foreach (string id in dynamicIds)
        {
            if (_widgets.Any(widget => widget.Definition.Id == id)) continue;
            var definition = WidgetRegistry.ById(id);
            if (definition is not null) CreateWidgetHost(definition);
        }

        RefreshDrawer();
        LayoutBoard();
        SaveLayout();
    }

    /// <summary>动态组件 id 约定带点号（插件Id.组件id），内置组件 id 都不含点号。</summary>
    private static bool IsDynamicWidgetId(string id) => id.Contains('.');

    private Control BuildCard(WidgetDefinition definition)
    {
        try
        {
            var inner = definition.Build();
            if (inner is Border border)
            {
                border.Background = Brushes.Transparent;
            }

            return new Fluid.Avalonia.Acrylic.AcrylicSurface
            {
                CornerRadius = new CornerRadius(16),
                BlurRadius = 8,
                Vibrancy = 1.0,
                TintColor = Color.Parse("#B30C121A"),
                ShadowEnabled = false,
                Child = inner,
            };
        }
        catch (Exception ex)
        {
            // 记录到临时日志：QA 时可用它确认所有卡片都构建成功
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mofu-widget-build.log"),
                    $"{DateTime.Now:O} render failed: {definition.Id}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
            }
            catch
            {
                // 日志失败不影响界面
            }

            return WidgetChrome.Notice(L.T("Home.Widget.RenderFailed", "「{0}」渲染失败：{1}", definition.Title, ex.Message));
        }
    }

    private void BuildBoardInteractions()
    {
        foreach (var widget in _widgets)
        {
            var captured = widget;
            captured.Host.PointerPressed += (_, e) => OnWidgetPointerPressed(captured, e);
            captured.Host.PointerEntered += (_, _) => captured.ResizeHandle.Opacity = 1;
            captured.Host.PointerExited += (_, _) =>
            {
                if (!_resizing) captured.ResizeHandle.Opacity = 0;
            };
            captured.ResizeHandle.PointerPressed += (_, e) => OnResizeHandlePressed(captured, e);
            captured.ResizeHandle.PointerReleased += (_, _) => EndResize();
            captured.ResizeHandle.DoubleTapped += (_, _) => ResetWidgetSize(captured);

            var removeItem = new MenuItem { Header = L.T("Home.Menu.RemoveWidget", "移除组件") };
            removeItem.Click += (_, _) => RemoveWidget(captured);
            var resetItem = new MenuItem { Header = L.T("Home.Menu.ResetSize", "恢复默认大小") };
            resetItem.Click += (_, _) => ResetWidgetSize(captured);
            captured.Host.ContextMenu = new ContextMenu { ItemsSource = new[] { removeItem, resetItem } };
        }

        WidgetBoard.ContextMenu = BuildDesktopMenu();
    }

    private ContextMenu BuildDesktopMenu()
    {
        var wallpaper = new MenuItem { Header = L.T("Home.Menu.Wallpaper", "更换壁纸") };
        wallpaper.Click += (_, _) => Shell.Go("settings");

        var arrange = new MenuItem { Header = L.T("Home.Menu.Arrange", "整理桌面") };
        arrange.Click += (_, _) => ArrangeDesktop();

        var library = new MenuItem { Header = L.T("Home.Menu.OpenLibrary", "打开组件盒") };
        library.Click += (_, _) => OpenWidgetDrawer();

        return new ContextMenu { ItemsSource = new[] { wallpaper, arrange, library } };
    }

    /// <summary>主题切换：卡片里的派生色（进度条 / 标题色）需要按新令牌重建。</summary>
    private void OnThemeChanged()
    {
        _themeVersion = ThemeManager.Version;
        foreach (var widget in _widgets)
        {
            widget.Host.Children.Clear();
            var card = BuildCard(widget.Definition);
            widget.Card = card;
            widget.Host.Children.Add(card);
            widget.Host.Children.Add(widget.ResizeHandle);
        }

        ApplyDragVisualBrushes(false);
        RefreshDrawer();
    }

    // ================= 布局 =================

    private void LoadLayout()
    {
        _widgetPages = LocalSettings.Current.DesktopWidgetPages;
        if (_widgetPages.Count == 0)
        {
            var first = new DesktopWidgetPage
            {
                Id = "page-1",
                Name = "1",
                Widgets = LocalSettings.Current.DesktopWidgets ?? new(),
            };
            _widgetPages.Add(first);
        }

        foreach (DesktopWidgetPage page in _widgetPages)
        {
            page.Widgets ??= new();
        }

        _activeWidgetPage = Math.Clamp(LocalSettings.Current.ActiveDesktopWidgetPage, 0, _widgetPages.Count - 1);
        LoadActivePage(applyDefault: true);
        RebuildPageTabs();
    }

    private void LoadActivePage(bool applyDefault)
    {
        _placements.Clear();
        DesktopWidgetPage page = _widgetPages[_activeWidgetPage];
        var needsDefault = applyDefault && LocalSettings.Current.DesktopLayoutVersion < LayoutVersion &&
                           _activeWidgetPage == 0;

        if (!needsDefault && page.Widgets.Count > 0)
        {
            foreach (var placement in page.Widgets)
            {
                var definition = WidgetRegistry.ById(placement.Id);
                if (definition is null) continue;
                if (placement.W < 1 || placement.H < 1) continue;
                _placements[placement.Id] = placement;
            }
        }

        if (_placements.Count == 0 && applyDefault && _activeWidgetPage == 0)
        {
            foreach (var item in WidgetRegistry.DefaultLayout)
            {
                var definition = WidgetRegistry.ById(item.Id);
                if (definition is null) continue;

                _placements[item.Id] = new DesktopWidgetPlacement
                {
                    Id = item.Id,
                    X = item.X,
                    Y = item.Y,
                    W = item.W,
                    H = item.H,
                };
            }
        }

        if (needsDefault) LocalSettings.Current.DesktopLayoutVersion = LayoutVersion;
        SaveLayout();
    }

    private void SwitchWidgetPage(int index)
    {
        if (index < 0 || index >= _widgetPages.Count || index == _activeWidgetPage) return;

        SaveLayout();
        _activeWidgetPage = index;
        LoadActivePage(applyDefault: false);
        LayoutBoard();
        RebuildPageTabs();
        _ = CloseWidgetDrawerAsync();
    }

    private void RebuildPageTabs()
    {
        // 页面 tab 只负责切换；编号 = 位置（1 起始），删除中间页后自动往前补位
        WidgetPageTabs.Children.Clear();
        for (var index = 0; index < _widgetPages.Count; index++)
        {
            int pageIndex = index;
            var button = new Button
            {
                Content = (index + 1).ToString(),
                Height = 32,
                MinWidth = 32,
                Padding = new Thickness(8, 0),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            button.Classes.Add("ghost");
            if (index == _activeWidgetPage) button.Classes.Add("primary");
            button.Click += (_, _) => SwitchWidgetPage(pageIndex);

            WidgetPageTabs.Children.Add(button);
        }
    }

    private void AddWidgetPage()
    {
        SaveLayout();
        var page = new DesktopWidgetPage
        {
            Id = "page-" + Guid.NewGuid().ToString("N")[..8],
            Name = (_widgetPages.Count + 1).ToString(),
        };
        _widgetPages.Add(page);
        _activeWidgetPage = _widgetPages.Count - 1;
        LoadActivePage(applyDefault: false);
        LayoutBoard();
        RebuildPageTabs();
        LocalSettings.Save();
        Shell.Toast(L.T("Home.Page.Added", "已新建组件页面「{0}」", page.Name));
    }

    private void ConfirmDeleteWidgetPage(int index)
    {
        if (index < 0 || index >= _widgetPages.Count) return;
        if (_widgetPages.Count <= 1)
        {
            Shell.Toast(L.T("Home.Page.LastOne", "至少保留一个组件页面"));
            return;
        }

        DesktopWidgetPage page = _widgetPages[index];
        var content = new StackPanel { Spacing = 12, Width = 360 };
        content.Children.Add(new TextBlock
        {
            Text = L.T("Home.Page.DeleteConfirm", "确定删除「{0}」吗？页面里的组件摆放会被移除。", index + 1),
            TextWrapping = TextWrapping.Wrap,
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancel = new Button { Content = L.T("Common.Cancel", "取消"), Classes = { "ghost" }, Height = 30 };
        cancel.Click += (_, _) => DismissDialog(cancel);
        var confirm = new Button { Content = L.T("Common.Ok", "确定"), Classes = { "primary" }, Height = 30 };
        confirm.Click += (_, _) =>
        {
            DismissDialog(confirm);
            DeleteWidgetPage(index);
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        content.Children.Add(buttons);
        Shell.Dialog(content, L.T("Home.Page.Delete", "删除页面"));
    }

    private void DeleteWidgetPage(int index)
    {
        if (index < 0 || index >= _widgetPages.Count || _widgetPages.Count <= 1) return;

        SaveLayout();
        _widgetPages.RemoveAt(index);
        if (_activeWidgetPage >= _widgetPages.Count) _activeWidgetPage = _widgetPages.Count - 1;
        else if (_activeWidgetPage > index) _activeWidgetPage--;
        // 编号跟位置走：删除后把存储的名字按新顺序重排（1, 2, 3…）
        for (var i = 0; i < _widgetPages.Count; i++)
        {
            _widgetPages[i].Name = (i + 1).ToString();
        }
        LocalSettings.Current.DesktopWidgetPages = _widgetPages;
        LocalSettings.Current.ActiveDesktopWidgetPage = _activeWidgetPage;
        LocalSettings.Save();
        LoadActivePage(applyDefault: false);
        LayoutBoard();
        RebuildPageTabs();
        Shell.Toast(L.T("Home.Page.Deleted", "已删除组件页面"));
    }

    private static void DismissDialog(Control control) =>
        control.FindAncestorOfType<SukiDialogHost>(true)?.Manager?.DismissDialog();

    private void LayoutBoard()
    {
        var width = BoardScroll.Viewport.Width;
        if (width <= 1) width = BoardScroll.Bounds.Width;
        if (width <= 1) return;

        width = Math.Max(360, width);
        WidgetBoard.Width = width;
        WidgetLayer.Width = width;
        _cellWidth = Math.Max(24, (width - (Cols - 1) * Gap) / Cols);

        // 行高按可见区域等分「一屏的行数」，与上一代一致：窗口越高格子越大，
        // 太矮时收到 RowMinHeight 并让整块板滚动
        var viewportHeight = BoardScroll.Viewport.Height;
        if (viewportHeight <= 1) viewportHeight = BoardScroll.Bounds.Height;
        if (viewportHeight > 1)
        {
            _rowHeight = Math.Max(RowMinHeight, (viewportHeight - 24) / MinRows - Gap);
        }

        foreach (var widget in _widgets)
        {
            if (!_placements.TryGetValue(widget.Definition.Id, out var placement))
            {
                widget.Host.IsVisible = false;
                continue;
            }

            var left = placement.X * (_cellWidth + Gap);
            var top = placement.Y * (_rowHeight + Gap);
            var widgetWidth = placement.W * _cellWidth + (placement.W - 1) * Gap;
            var widgetHeight = placement.H * _rowHeight + (placement.H - 1) * Gap;

            widget.Host.IsVisible = true;
            Canvas.SetLeft(widget.Host, left);
            Canvas.SetTop(widget.Host, top);
            widget.Host.Width = Math.Max(180, widgetWidth);
            widget.Host.Height = Math.Max(120, widgetHeight);
        }

        // 行数 = max(一屏行数, 已用到的最底行)：内容在一屏内时板子正好铺满，不出现
        // 无意义的滚动；放到更下面时板子跟着长高，外层 ScrollViewer 接管
        var rows = _placements.Count == 0
            ? MinRows
            : Math.Max(MinRows, _placements.Values.Max(p => p.Y + p.H));
        var height = rows * _rowHeight + (rows - 1) * Gap + 24;
        WidgetBoard.Height = height;
        WidgetLayer.Height = height;

        BuildGridLines();
        EmptyBoardHint.IsVisible = _placements.Count == 0;
        UpdateDrawerHint();
    }

    /// <summary>按当前列宽/行高重建整块格子板的细线。</summary>
    private void BuildGridLines()
    {
        var width = WidgetBoard.Width;
        var height = WidgetBoard.Height;
        if (width <= 1 || height <= 1) return;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var stepX = _cellWidth + Gap;
            for (var index = 0; index <= Cols; index++)
            {
                var x = Math.Min(width, index * stepX);
                context.BeginFigure(new Point(x, 0), false);
                context.LineTo(new Point(x, height));
            }

            var stepY = _rowHeight + Gap;
            var rowCount = Math.Max(1, (int)Math.Ceiling(height / stepY));
            for (var index = 0; index <= rowCount; index++)
            {
                var y = Math.Min(height, index * stepY);
                context.BeginFigure(new Point(0, y), false);
                context.LineTo(new Point(width, y));
            }
        }

        GridLines.Data = geometry;
        GridLines.Width = width;
        GridLines.Height = height;
    }

    private void SaveLayout()
    {
        if (_widgetPages.Count == 0) return;

        DesktopWidgetPage page = _widgetPages[_activeWidgetPage];
        page.Widgets = _placements.Values
            .Select(p => new DesktopWidgetPlacement
            {
                Id = p.Id,
                X = p.X,
                Y = p.Y,
                W = p.W,
                H = p.H,
            })
            .ToList();

        LocalSettings.Current.DesktopWidgetPages = _widgetPages;
        LocalSettings.Current.ActiveDesktopWidgetPage = _activeWidgetPage;
        LocalSettings.Current.DesktopWidgets = _widgetPages[0].Widgets;
        LocalSettings.Save();
    }

    private void OnWidgetPointerPressed(WidgetHost widget, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;
        if (IsInteractiveSource(e.Source, widget.Host)) return;

        CancelPress();
        _pressHost = widget;
        _pressing = true;
        _pressPoint = e.GetPosition(this);
        _lastPointer = _pressPoint;
        _pressPointer = e.Pointer;

        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoldMilliseconds) };
        _holdTimer.Tick += (_, _) =>
        {
            StopHoldTimer();
            if (_pressing && _pressHost is not null)
            {
                BeginDrag(_pressHost);
            }
        };
        _holdTimer.Start();
        StartHoldProgress(widget);
    }

    private void OnRootPointerMoved(object? sender, PointerEventArgs e)
    {
        _lastPointer = e.GetPosition(this);

        if (_resizing)
        {
            UpdateResize(_lastPointer);
            return;
        }

        if (_dragging)
        {
            UpdateDrag(_lastPointer);
            return;
        }

        if (_libraryCandidate is not null)
        {
            if (Distance(_lastPointer, _libraryPressPoint) > 5)
            {
                BeginLibraryDrag(_libraryCandidate);
            }

            return;
        }

        if (!_pressing) return;
        if (Distance(_lastPointer, _pressPoint) > HoldTolerance)
        {
            CancelPress();
        }
    }

    private void OnRootPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_resizing)
        {
            EndResize();
            return;
        }

        if (_dragging)
        {
            CompleteDrag(_lastPointer);
            return;
        }

        if (_libraryCandidate is not null)
        {
            var widget = _libraryCandidate;
            _libraryCandidate = null;
            _pressPointer = null;
            AddWidget(widget);
            return;
        }

        CancelPress();
    }

    private void BeginLibraryDrag(WidgetHost widget)
    {
        _libraryCandidate = null;
        _pressing = false;
        _pressHost = null;
        _dragging = true;
        _dragHost = widget;
        _dragFromLibrary = true;
        _grabOffsetBoard = new Point(
            (widget.Definition.DefaultW * _cellWidth + (widget.Definition.DefaultW - 1) * Gap) / 2,
            (widget.Definition.DefaultH * _rowHeight + (widget.Definition.DefaultH - 1) * Gap) / 2);

        // 指针从组件盒拖到桌面时可能离开组件盒/HomeView，捕获到页面继续收移动事件
        _pressPointer?.Capture(this);

        DragGhostIcon.Kind = widget.Definition.IconKey;
        DragGhostText.Text = widget.Definition.Title;
        DragGhostHint.Text = L.T("Home.Drag.DropAnywhere", "拖到桌面任意位置放置");
        DragGhost.Opacity = 1;
        DragOverlay.IsVisible = true;
        DeleteHint.IsVisible = false;

        BuildGridLines();
        GridLines.IsVisible = true;
        GridLines.Opacity = 0.6;

        UpdateDrag(_lastPointer);
    }

    private void OnLibraryRowPressed(WidgetHost widget, Border row, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;
        if (IsInteractiveSource(e.Source, row)) return;
        if (_placements.ContainsKey(widget.Definition.Id)) return;

        CancelPress();
        _libraryCandidate = widget;
        _libraryPressPoint = e.GetPosition(this);
        _lastPointer = _libraryPressPoint;
        _pressPointer = e.Pointer;
    }

    private void BeginDrag(WidgetHost widget)
    {
        if (!_placements.TryGetValue(widget.Definition.Id, out var placement)) return;

        StopHoldProgress();
        _pressing = false;
        _pressHost = null;
        _dragging = true;
        _dragHost = widget;
        _dragFromLibrary = false;
        _grabOffsetBoard = new Point(0, 0);

        GridLines.IsVisible = true;
        GridLines.Opacity = 0.6;

        if (this.TranslatePoint(_lastPointer, WidgetBoard) is { } boardPoint)
        {
            _grabOffsetBoard = boardPoint - new Point(
                Canvas.GetLeft(widget.Host),
                Canvas.GetTop(widget.Host));
        }

        widget.Host.Opacity = 0.35;
        widget.Host.RenderTransform = TransformOperations.Parse("scale(0.965)");
        // 捕获指针：拖到开始栏/组件盒（HomeView 之外）也能收到移动与松手
        _pressPointer?.Capture(widget.Host);
        DragGhostIcon.Kind = widget.Definition.IconKey;
        DragGhostText.Text = widget.Definition.Title;
        DragGhostHint.Text = L.T("Home.Drag.Hint", "拖到格子放下 · 拖到开始栏移除");
        DragGhost.Opacity = 1;
        DragOverlay.IsVisible = true;
        DeleteHint.IsVisible = true;
        DeleteHintText.Text = L.T("Home.Drag.MoveHint", "拖到开始栏或组件盒可移除");
        ApplyDragVisualBrushes(false);

        _targetX = placement.X;
        _targetY = placement.Y;
        UpdateDrag(_lastPointer);
    }

    private void UpdateDrag(Point point)
    {
        if (_dragHost is null) return;
        if (!_placements.TryGetValue(_dragHost.Definition.Id, out var placement))
        {
            if (!_dragFromLibrary) return;
            placement = new DesktopWidgetPlacement
            {
                Id = _dragHost.Definition.Id,
                W = _dragHost.Definition.DefaultW,
                H = _dragHost.Definition.DefaultH,
            };
        }

        Canvas.SetLeft(DragGhost, point.X + 16);
        Canvas.SetTop(DragGhost, point.Y + 14);

        _overDelete = !_dragFromLibrary && ComputeOverDelete(point);
        var boardPoint = this.TranslatePoint(point, WidgetBoard);

        if (boardPoint is { } local)
        {
            var stepX = _cellWidth + Gap;
            var stepY = _rowHeight + Gap;
            var targetX = (int)Math.Round((local.X - _grabOffsetBoard.X) / stepX);
            var targetY = (int)Math.Round((local.Y - _grabOffsetBoard.Y) / stepY);

            _targetX = Math.Clamp(targetX, 0, Math.Max(0, Cols - placement.W));
            _targetY = Math.Clamp(targetY, 0, MaxRows - placement.H);
        }

        if (_overDelete || boardPoint is null)
        {
            DropPreview.Opacity = 0;
            DropPreview.IsVisible = false;
        }
        else
        {
            var stepX = _cellWidth + Gap;
            var stepY = _rowHeight + Gap;
            DropPreview.IsVisible = true;
            DropPreview.Opacity = 1;
            Canvas.SetLeft(DropPreview, _targetX * stepX);
            Canvas.SetTop(DropPreview, _targetY * stepY);
            DropPreview.Width = placement.W * _cellWidth + (placement.W - 1) * Gap;
            DropPreview.Height = placement.H * _rowHeight + (placement.H - 1) * Gap;
        }

        DragGhostHint.Text = _dragFromLibrary
            ? L.T("Home.Drag.DropAnywhere", "拖到桌面任意位置放置")
            : _overDelete
                ? L.T("Home.Drag.OverDelete", "松开以移除")
                : L.T("Home.Drag.Hint", "拖到格子放下 · 拖到开始栏移除");
        DeleteHint.IsVisible = !_dragFromLibrary;
        DeleteHintText.Text = _overDelete
            ? L.T("Home.Drag.ReleaseToRemove", "松开以移除该组件")
            : L.T("Home.Drag.MoveHint", "拖到开始栏或组件盒可移除");
        ApplyDragVisualBrushes(_overDelete);
    }

    private void ApplyDragVisualBrushes(bool overDelete)
    {
        DeleteHint.Background = overDelete
            ? ThemeTokens.WithAlpha("NekoDanger", 0.32)
            : ThemeTokens.Brush("NekoDangerSoftBrush");
        DeleteHint.BorderBrush = ThemeTokens.Brush(overDelete ? "NekoDangerBrush" : "NekoBorderBrush");
        DeleteHintText.Foreground = ThemeTokens.Brush(overDelete ? "NekoTextBrush" : "NekoDangerBrush");
        DragGhost.BorderBrush = ThemeTokens.Brush(overDelete ? "NekoDangerBrush" : "NekoAccentBrush");
        DragGhostHint.Foreground = ThemeTokens.Brush(overDelete ? "NekoDangerBrush" : "NekoDimBrush");
    }

    private void CompleteDrag(Point point)
    {
        if (_dragHost is null) return;

        var widget = _dragHost;
        var wasOverDelete = _overDelete;
        var targetX = _targetX;
        var targetY = _targetY;
        var fromLibrary = _dragFromLibrary;

        ResetDragVisuals(widget);

        if (fromLibrary)
        {
            if (wasOverDelete) return;
            AddWidgetAt(widget, targetX, targetY);
            return;
        }

        if (wasOverDelete)
        {
            RemoveWidget(widget);
            return;
        }

        PlaceWidget(widget.Definition.Id, targetX, targetY);
    }

    private void ResetDragVisuals(WidgetHost widget)
    {
        _pressPointer?.Capture(null);
        _pressPointer = null;
        if (!_dragFromLibrary)
        {
            widget.Host.Opacity = 1;
            widget.Host.RenderTransform = TransformOperations.Parse("scale(1)");
        }

        _dragging = false;
        _dragHost = null;
        _dragFromLibrary = false;
        _libraryCandidate = null;
        _overDelete = false;
        DragGhost.Opacity = 0;
        DragOverlay.IsVisible = false;
        DropPreview.Opacity = 0;
        DropPreview.IsVisible = false;
        DeleteHint.IsVisible = false;
        ApplyDragVisualBrushes(false);
        HideGridLinesAnimated();
    }

    private bool ComputeOverDelete(Point point)
    {
        // 拖回组件盒 = 移除（原版行为）
        if (WidgetDrawer.IsVisible
            && this.TranslatePoint(point, WidgetDrawer) is { } drawerPoint
            && drawerPoint.X >= 0 && drawerPoint.Y >= 0
            && drawerPoint.X <= WidgetDrawer.Bounds.Width
            && drawerPoint.Y <= WidgetDrawer.Bounds.Height)
        {
            return true;
        }

        // 底部开始栏区域 = 移除
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not null
            && this.TranslatePoint(point, topLevel) is { } topPoint
            && topPoint.Y >= topLevel.ClientSize.Height - 78)
        {
            return true;
        }

        return false;
    }

    private void CancelPress()
    {
        StopHoldTimer();
        StopHoldProgress();
        _pressing = false;
        _pressHost = null;
        _pressPointer = null;
        _libraryCandidate = null;
        HideGridLinesAnimated();
    }

    private void StartHoldProgress(WidgetHost widget)
    {
        BuildGridLines();
        GridLines.IsVisible = true;
        GridLines.Opacity = 0;

        HoldBar.Width = Math.Max(64, Math.Min(220, widget.Host.Width - 12));
        Canvas.SetLeft(HoldBar, Math.Max(0, Canvas.GetLeft(widget.Host) + 6));
        Canvas.SetTop(HoldBar, Math.Max(0, Canvas.GetTop(widget.Host) - 8));
        HoldProgress.Value = 0;
        HoldBar.IsVisible = true;

        _holdProgressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _holdProgressTimer.Tick += (_, _) =>
        {
            HoldProgress.Value = Math.Min(1, HoldProgress.Value + 0.032);
            // 格子线随长按进度逐渐浮现
            GridLines.Opacity = 0.6 * HoldProgress.Value;
            if (HoldProgress.Value >= 1)
            {
                StopHoldProgress();
            }
        };
        _holdProgressTimer.Start();
    }

    private void HideGridLinesAnimated()
    {
        if (!GridLines.IsVisible) return;
        GridLines.Opacity = 0;
        _ = HideGridLinesLaterAsync();
    }

    private async System.Threading.Tasks.Task HideGridLinesLaterAsync()
    {
        await System.Threading.Tasks.Task.Delay(240);
        if (!_pressing && !_dragging)
        {
            GridLines.IsVisible = false;
        }
    }

    private void StopHoldProgress()
    {
        _holdProgressTimer?.Stop();
        _holdProgressTimer = null;
        HoldBar.IsVisible = false;
        HoldProgress.Value = 0;
    }

    private void StopHoldTimer()
    {
        if (_holdTimer is null) return;
        _holdTimer.Stop();
        _holdTimer = null;
    }

    private static double Distance(Point a, Point b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static bool IsInteractiveSource(object? source, Control boundary)
    {
        if (source is not Visual visual) return false;

        foreach (var ancestor in visual.GetVisualAncestors())
        {
            if (ReferenceEquals(ancestor, boundary)) break;
            if (ancestor is Button or TextBox or ComboBox or Slider or ToggleSwitch or ToggleButton
                or ScrollBar)
            {
                return true;
            }
        }

        return false;
    }


    // ================= 摆放 / 移除 / 整理 =================

    private void PlaceWidget(string id, int x, int y)
    {
        if (!_placements.TryGetValue(id, out var placement)) return;

        if (!Fits(id, x, y, placement.W, placement.H))
        {
            var spot = FindNearestFreeSpot(id, placement.W, placement.H, x, y);
            if (spot is null)
            {
                Shell.Toast(L.T("Home.Toast.NoRoom", "桌面摆不下了，先移掉几个组件"));
                return;
            }

            x = spot.Value.X;
            y = spot.Value.Y;
        }

        placement.X = x;
        placement.Y = y;
        SaveLayout();
        LayoutBoard();
    }

    private async void RemoveWidget(WidgetHost widget)
    {
        if (!_placements.ContainsKey(widget.Definition.Id)) return;

        widget.Host.Opacity = 0;
        widget.Host.RenderTransform = TransformOperations.Parse("scale(0.9)");
        await System.Threading.Tasks.Task.Delay(170);

        if (!_placements.Remove(widget.Definition.Id))
        {
            widget.Host.Opacity = 1;
            widget.Host.RenderTransform = TransformOperations.Parse("scale(1)");
            return;
        }

        widget.Host.IsVisible = false;
        widget.Host.Opacity = 1;
        widget.Host.RenderTransform = TransformOperations.Parse("scale(1)");
        SaveLayout();
        LayoutBoard();
        RefreshDrawer();
        Shell.Toast(L.T("Home.Toast.Removed", "已移除「{0}」", widget.Definition.Title));
    }

    private void AddWidget(WidgetHost widget)
    {
        if (_placements.ContainsKey(widget.Definition.Id))
        {
            Shell.Toast(L.T("Home.Toast.AlreadyPlaced", "「{0}」已经在桌面上了", widget.Definition.Title));
            return;
        }

        var spot = FindFreeSpot(widget.Definition.DefaultW, widget.Definition.DefaultH);
        if (spot is null)
        {
            Shell.Toast(L.T("Home.Toast.NoRoom", "桌面摆不下了，先移掉几个组件"));
            return;
        }

        AddWidgetAt(widget, spot.Value.X, spot.Value.Y);
    }

    private void AddWidgetAt(WidgetHost widget, int x, int y)
    {
        if (_placements.ContainsKey(widget.Definition.Id)) return;

        var width = widget.Definition.DefaultW;
        var height = widget.Definition.DefaultH;

        if (!Fits(null, x, y, width, height))
        {
            var spot = FindNearestFreeSpot(widget.Definition.Id, width, height, x, y);
            if (spot is null)
            {
                Shell.Toast(L.T("Home.Toast.NoRoom", "桌面摆不下了，先移掉几个组件"));
                return;
            }

            x = spot.Value.X;
            y = spot.Value.Y;
        }

        _placements[widget.Definition.Id] = new DesktopWidgetPlacement
        {
            Id = widget.Definition.Id,
            X = x,
            Y = y,
            W = width,
            H = height,
        };

        SaveLayout();
        LayoutBoard();
        UiAnimations.FadeSlideIn(widget.Host, 0.94, 10, 200);
        RefreshDrawer();
        Shell.Toast(L.T("Home.Toast.Added", "已添加「{0}」", widget.Definition.Title));
    }

    /// <summary>整理桌面：按「先上后下、先左后右」的阅读顺序重新紧凑排一遍。</summary>
    private void ArrangeDesktop()
    {
        if (_placements.Count == 0) return;

        var ordered = _placements.Values
            .OrderBy(placement => placement.Y)
            .ThenBy(placement => placement.X)
            .ToList();

        _placements.Clear();
        foreach (var placement in ordered)
        {
            var spot = FindFreeSpot(placement.W, placement.H);
            if (spot is null) continue;

            placement.X = spot.Value.X;
            placement.Y = spot.Value.Y;
            _placements[placement.Id] = placement;
        }

        SaveLayout();
        LayoutBoard();
        RefreshDrawer();
        Shell.Toast(L.T("Home.Toast.Arranged", "桌面已整理"));
    }

    private (int X, int Y)? FindFreeSpot(int width, int height)
    {
        for (var y = 0; y <= MaxRows - height; y++)
        {
            for (var x = 0; x <= Cols - width; x++)
            {
                if (Fits(null, x, y, width, height)) return (x, y);
            }
        }

        return null;
    }

    private (int X, int Y)? FindNearestFreeSpot(string id, int width, int height, int nearX, int nearY)
    {
        (int X, int Y)? best = null;
        var bestDistance = double.MaxValue;

        for (var y = 0; y <= MaxRows - height; y++)
        {
            for (var x = 0; x <= Cols - width; x++)
            {
                if (!Fits(id, x, y, width, height)) continue;

                var distance = (x - nearX) * (x - nearX) + (y - nearY) * (y - nearY);
                if (distance >= bestDistance) continue;

                bestDistance = distance;
                best = (x, y);
            }
        }

        return best;
    }

    private bool Fits(string? ignoreId, int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || x + width > Cols || y + height > MaxRows) return false;

        foreach (var placement in _placements.Values)
        {
            if (ignoreId is not null && placement.Id == ignoreId) continue;

            var overlaps = x < placement.X + placement.W
                           && x + width > placement.X
                           && y < placement.Y + placement.H
                           && y + height > placement.Y;
            if (overlaps) return false;
        }

        return true;
    }

    // ================= 改大小 =================

    private void OnResizeHandlePressed(WidgetHost widget, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;
        if (!_placements.TryGetValue(widget.Definition.Id, out var placement)) return;

        e.Handled = true;
        CancelPress();
        _resizing = true;
        _resizeHost = widget;
        widget.ResizeHandle.Opacity = 1;
        _pressPoint = e.GetPosition(this);
        _lastPointer = _pressPoint;
        _resizeStartW = placement.W;
        _resizeStartH = placement.H;
        e.Pointer.Capture(widget.ResizeHandle);
    }

    private void UpdateResize(Point point)
    {
        if (_resizeHost is null || !_placements.TryGetValue(_resizeHost.Definition.Id, out var placement)) return;

        var deltaColumns = (point.X - _pressPoint.X) / (_cellWidth + Gap);
        var deltaRows = (point.Y - _pressPoint.Y) / (_rowHeight + Gap);
        var width = (int)Math.Round(_resizeStartW + deltaColumns);
        var height = (int)Math.Round(_resizeStartH + deltaRows);

        width = Math.Clamp(width, _resizeHost.Definition.MinW, Cols - placement.X);
        height = Math.Max(_resizeHost.Definition.MinH, height);

        if (width == placement.W && height == placement.H) return;

        placement.W = width;
        placement.H = height;
        LayoutBoard();
    }

    private void EndResize()
    {
        if (!_resizing) return;
        _resizing = false;
        _resizeHost = null;
        SaveLayout();
        LayoutBoard();
        RefreshDrawer();
    }

    private void ResetWidgetSize(WidgetHost widget)
    {
        if (!_placements.TryGetValue(widget.Definition.Id, out var placement)) return;

        placement.W = widget.Definition.DefaultW;
        placement.H = widget.Definition.DefaultH;
        if (placement.X + placement.W > Cols) placement.X = Math.Max(0, Cols - placement.W);

        SaveLayout();
        LayoutBoard();
        RefreshDrawer();
    }

    // ================= 组件盒 =================

    private void OnWidgetSearchChanged(object? sender, TextChangedEventArgs e)
    {
        _drawerQuery = WidgetSearchBox.Text ?? string.Empty;
        RefreshDrawer();
    }

    private void UpdateDrawerHint()
    {
        var total = _widgets.Count;
        var placed = _placements.Count;
        WidgetDrawerHint.Text = L.T("Home.Library.PlacedHint", "已放 {0} / {1} 个组件 · 拖到桌面放置，或点击添加", placed, total);
    }

    private void RefreshDrawer()
    {
        WidgetDrawerList.Children.Clear();

        var query = _drawerQuery.Trim();
        var matches = _widgets
            .Where(widget => query.Length == 0
                             || widget.Definition.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                             || widget.Definition.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(widget => _placements.ContainsKey(widget.Definition.Id) ? 1 : 0)
            .ThenBy(widget => WidgetRegistry.All.ToList().FindIndex(item => item.Id == widget.Definition.Id))
            .ToList();

        if (matches.Count == 0)
        {
            WidgetDrawerList.Children.Add(WidgetChrome.Empty(query.Length == 0
                ? L.T("Home.Library.None", "没有可用组件")
                : L.T("Home.Library.NoMatch", "没有匹配的组件")));
            UpdateDrawerHint();
            return;
        }

        foreach (var widget in matches)
        {
            var placed = _placements.ContainsKey(widget.Definition.Id);

            var glyph = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(8),
                Background = ThemeTokens.Brush(placed ? "NekoFieldBrush" : "NekoAccentSoftBrush"),
                Child = new IconParkIcon
                {
                    Kind = widget.Definition.IconKey,
                    Width = 15,
                    Height = 15,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            titleRow.Children.Add(new TextBlock
            {
                Text = widget.Definition.Title,
                FontSize = 12.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeTokens.Brush("NekoTextBrush"),
            });
            if (placed) titleRow.Children.Add(WidgetChrome.Pill(L.T("Home.Library.OnDesktop", "已在桌面"), "NekoMutedBrush", "NekoFieldBrush"));

            // 列表行只显示组件名（+ 已放置徽标）；描述只用于搜索匹配，不再展示
            var text = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children = { titleRow },
            };

            var action = new Button
            {
                Classes = { "icon" },
                Width = 28,
                Height = 28,
                Content = new IconParkIcon
                {
                    Kind = placed ? "IconCheck" : "IconPlus",
                    Width = 12,
                    Height = 12,
                },
            };
            ToolTip.SetTip(action, placed
                ? L.T("Home.Library.RemoveTip", "从桌面移除")
                : L.T("Home.Library.AddTip", "添加到桌面"));
            action.Click += (_, _) =>
            {
                if (placed) RemoveWidget(widget);
                else AddWidget(widget);
            };

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            row.Children.Add(glyph);
            Grid.SetColumn(text, 1);
            text.Margin = new Thickness(8, 0, 6, 0);
            row.Children.Add(text);
            Grid.SetColumn(action, 2);
            row.Children.Add(action);

            var rowBorder = new Border
            {
                Background = ThemeTokens.Brush(placed ? "NekoFieldSoftBrush" : "NekoCardSoftBrush"),
                BorderBrush = ThemeTokens.Brush("NekoBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(9, 7),
                Child = row,
                Opacity = placed ? 0.78 : 1,
            };

            if (!placed)
            {
                rowBorder.Cursor = new Cursor(StandardCursorType.Hand);
                rowBorder.PointerPressed += (_, e) => OnLibraryRowPressed(widget, rowBorder, e);
            }

            WidgetDrawerList.Children.Add(rowBorder);
        }

        UpdateDrawerHint();
    }

    /// <summary>「+」= 纯组件添加：直接展开组件盒。</summary>
    private void OnWidgetLibraryClick(object? sender, RoutedEventArgs e) => OpenWidgetDrawer();

    /// <summary>页面管理：切换 / 新建 / 删除集中在这里，与组件添加分开。</summary>
    private void OnPageManageClick(object? sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();

        for (var index = 0; index < _widgetPages.Count; index++)
        {
            int pageIndex = index;
            var item = new MenuItem
            {
                Header = (index == _activeWidgetPage ? "✓ " : string.Empty) + (index + 1).ToString(),
                FontWeight = index == _activeWidgetPage ? FontWeight.SemiBold : FontWeight.Normal,
            };
            item.Click += (_, _) => SwitchWidgetPage(pageIndex);
            flyout.Items.Add(item);
        }

        flyout.Items.Add(new Separator());

        var addPage = new MenuItem { Header = L.T("Home.Page.Add", "新建页面") };
        addPage.Click += (_, _) => AddWidgetPage();
        flyout.Items.Add(addPage);

        var deletePage = new MenuItem
        {
            Header = L.T("Home.Page.Delete", "删除页面"),
            IsEnabled = _widgetPages.Count > 1,
        };
        deletePage.Click += (_, _) => ConfirmDeleteWidgetPage(_activeWidgetPage);
        flyout.Items.Add(deletePage);

        flyout.ShowAt(WidgetPageManageButton);
    }

    private void OpenWidgetDrawer()
    {
        RefreshDrawer();
        WidgetDrawer.RenderTransform = TransformOperations.Parse("translateX(380px)");
        WidgetDrawer.IsVisible = true;
        Dispatcher.UIThread.Post(() =>
            WidgetDrawer.RenderTransform = TransformOperations.Parse("translateX(0px)"),
            DispatcherPriority.Render);
    }

    private async void OnCloseWidgetDrawerClick(object? sender, RoutedEventArgs e) =>
        await CloseWidgetDrawerAsync();

    private async System.Threading.Tasks.Task CloseWidgetDrawerAsync()
    {
        if (!WidgetDrawer.IsVisible) return;

        WidgetDrawer.RenderTransform = TransformOperations.Parse("translateX(380px)");
        await System.Threading.Tasks.Task.Delay(240);
        WidgetDrawer.IsVisible = false;
    }

}
