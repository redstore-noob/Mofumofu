using Mofumofu.Launcher.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mofumofu.Launcher.Localization;

using L = Mofumofu.Launcher.Localization.Localization;
using Mofumofu.Launcher.Services;
using Mofumofu.Launcher.Services.Wallpaper;
using Mofumofu.Launcher.Views;
using Mofumofu.Launcher.Web;
using SukiUI.Controls;
using SukiUI.Dialogs;

namespace Mofumofu.Launcher;

public partial class MainWindow : SukiWindow
{
    private static readonly (string Id, string Label, string IconKey, string Hint)[] PageRegistry =
    {
        ("account", "账户", "IconAccount", "账号、皮肤与披风"),
        ("download", "下载", "IconDownload", "版本与内容下载"),
        ("instances", "实例", "IconCube", "游戏实例与版本"),
        ("music", "音乐", "IconMusic", "本地音乐播放器"),
        ("online", "联机", "IconGlobe", "房间与公网联机"),
        ("servers", "服务器", "IconServer", "本机托管与收藏"),
        ("plugins", "插件", "IconPlugin", "扩展与权限"),
        ("settings", "设置", "IconSettings", "启动器选项"),
    };

    /// <summary>插件注册的动态页面（pageId → 标签/图标/工厂）；由 PluginContextImpl 调用。</summary>
    private static readonly Dictionary<string, (string Label, Control Icon, Func<Control> Factory)> PluginPages = new();

    private static MainWindow? _mainInstance;

    private readonly Dictionary<string, Control> _pageCache = new();
    /// <summary>页面 id → 包了一层插件内容宿主的页面控件；页面本体仍在 _pageCache 里。</summary>
    private readonly Dictionary<string, Control> _hostCache = new();
    private readonly Dictionary<string, Button> _navButtons = new();
    private string? _lastCreatorToolId;
    private bool _creatorToolsExpanded;

    public MainWindow()
    {
        InitializeComponent();

        DialogHost.Manager = new SukiDialogManager();
        // WebView2 是独立子窗口，永远盖在 Avalonia 弹窗之上；弹窗期间整体隐藏，关闭后恢复
        DialogHost.Manager.OnDialogShown += (_, _) => WebRegionHost.SuspendAll();
        DialogHost.Manager.OnDialogDismissed += (_, _) => WebRegionHost.ResumeAll();

        Shell.PageRequested += Navigate;
        Shell.ToastRequested += ShowToast;
        Shell.DialogRequested += ShowDialog;
        Shell.ToolRequested += OpenCreatorTool;
        Shell.ExitRequested += ExitApplication;
        PageComposer.Changed += OnPluginContentChanged;
        GameState.Changed += SyncLaunchState;
        LaunchBarRefresh.Requested += OnLaunchBarRefreshRequested;

        _mainInstance = this;

        RebuildTaskbar();

        BuildStartMenu();
        ReloadStartBarChoices();
        SyncLaunchState();
        UpdateMaximizeIcon();
        Navigate("home");
        // 插件系统启动扫描：后台优先级跑在首帧之后，不挡窗口出现
        Dispatcher.UIThread.Post(() => Services.Plugins.PluginManager.Initialize(),
            DispatcherPriority.Background);
        InitializeTrayIcon();
        ApplyAppearanceSettings();
        UpdateBetaBadge();
        Closing += OnWindowClosing;
        // 模板/视觉树要到窗口打开后才完整，壁纸开启时再补一次透明处理
        Opened += (_, _) => ApplyWallpaperBackdrop();
        Activated += (_, _) => GlassEffects.SetSuspended(false);
        Deactivated += (_, _) => GlassEffects.SetSuspended(true);

        ThemeManager.ThemeChanged += OnThemeChanged;
        L.LanguageChanged += OnLanguageChanged;
        Title = L.T("Shell.AppTitle", "Mofumofu Launcher");
        GlassEffects.SetEnabled(LocalSettings.Current.PanelBlur);
        GlassEffects.SetPremiumVisuals(LocalSettings.Current.PremiumVisuals);
        WallpaperService.Instance.Changed += OnWallpaperChanged;
        ApplyWallpaperBackdrop();

        PropertyChanged += (_, args) =>
        {
            if (args.Property == WindowStateProperty)
            {
                UpdateMaximizeIcon();
            }
        };

        KeyDown += OnWindowKeyDown;

    }

    /// <summary>InformationalVersion 含 "beta" 时在标题栏显示 BETA 徽章（正式版自动消失）。</summary>
    private void UpdateBetaBadge()
    {
        var informational = typeof(MainWindow).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? string.Empty;
        BetaBadge.IsVisible = informational.Contains("beta", StringComparison.OrdinalIgnoreCase);
    }

    private object? FindRes(string key) =>
        (this.TryFindResource(key, out var local) ? local : null)
        ?? (Application.Current is { } app && app.TryFindResource(key, out var global) ? global : null);

    private Control CreatePage(string id) => id switch
    {
        "home" => new HomeView(),
        "account" => new AccountView(),
        "download" => new DownloadView(),
        "instances" => new InstancesView(),
        "music" => new MusicView(),
        "online" => new OnlineView(),
        "servers" => new ServersView(),
        "plugins" => new PluginsView(),
        "creator" => new CreatorView(),
        "launchconsole" => new LaunchConsoleView(),
        "settings" => new SettingsView(),
        _ => PluginPages.TryGetValue(id, out var pluginPage)
            ? pluginPage.Factory()
            : new HomeView(),
    };

    private string _currentPage = "home";

    private void Navigate(string pageId)
    {
        if (!_pageCache.TryGetValue(pageId, out var page))
        {
            page = CreatePage(pageId);
            _pageCache[pageId] = page;
        }

        // 页面本体外面再包一层插件内容宿主：顶部条 / 悬浮层 / 底栏都插在这一层里
        if (!_hostCache.TryGetValue(pageId, out var host))
        {
            host = PageComposer.Current.Wrap(pageId, page);
            _hostCache[pageId] = host;
        }

        _currentPage = pageId;
        PageHost.Content = host;
        GlassEffects.Refresh();
        UiAnimations.FadeSlideIn(host);
        StartMenuOverlay.IsVisible = false;
        StartButton.Classes.Set("active", false);

        foreach (var (id, button) in _navButtons)
        {
            button.Classes.Set("active", id == pageId);
        }

        // 页面重建后重新套用「直角模式」（旧页面里的圆角记录作废）
        _lastSquareCornerPage = page;
        ApplySquareCorners(LocalSettings.Current.SquareCorners);
    }

    private Control? _lastSquareCornerPage;

    private void OnPluginContentChanged() => Dispatcher.UIThread.Post(RefreshCurrentPageHost);

    /// <summary>插件注册了新内容后，重建当前页面的注入宿主，让它立刻生效。</summary>
    private void RefreshCurrentPageHost()
    {
        if (!_hostCache.ContainsKey(_currentPage)) return;
        if (!_pageCache.TryGetValue(_currentPage, out var page)) return;

        // 页面本体要从旧宿主的视觉树里摘出来，否则新宿主会拒绝这个已有父级的控件
        if (page.Parent is Panel previousHost) previousHost.Children.Remove(page);

        var host = PageComposer.Current.Wrap(_currentPage, page);
        _hostCache[_currentPage] = host;
        PageHost.Content = host;
        GlassEffects.Refresh();
    }

    /// <summary>开始菜单里的页面名：先用语言包，缺失时用注册表里的中文兜底。</summary>
    private static string PageLabel(string id, string fallback)
    {
        var key = "Shell.Page." + (id.Length == 0 ? id : char.ToUpperInvariant(id[0]) + id[1..]);
        return L.T(key, fallback);
    }

    private void OnLanguageChanged()
    {
        _pageCache.Clear();
        _hostCache.Clear();
        PageHost.Content = null;
        BuildStartMenu();
        Navigate(_currentPage);
        Title = L.T("Shell.AppTitle", "Mofumofu Launcher");
    }
    private void OnThemeChanged()
    {
        BuildStartMenu();
        SyncLaunchState();
        ApplyWallpaperBackdrop();
        WallpaperService.Instance.NotifyThemeChanged();
    }

    /// <summary>
    /// 壁纸层（独立无边框窗口）在主窗口下方，主窗口要透出它：开启时把窗口表面变透明，
    /// 关掉时恢复成原来的不透明渐变底色。
    /// </summary>
    private void OnWallpaperChanged() => ApplyWallpaperBackdrop();

    public InAppWallpaperSurface WallpaperSurface => InAppWallpaper;

    private void ApplyWallpaperBackdrop()
    {
        try
        {
            var on = WallpaperService.Instance.IsEnabled;
            var inApp = on && WallpaperService.Instance.UsesInAppSurface;
            var surface = (IBrush?)FindRes("NekoSurfaceBrush");

            InAppWallpaper.IsVisible = inApp;
            int blurPercent = WallpaperService.Instance.BlurPercent;
            int dimPercent = WallpaperService.Instance.DimPercent;
            InAppWallpaper.SetBlurPercent(inApp ? blurPercent : 0);
            WallpaperDimLayer.IsVisible = on && dimPercent > 0;
            WallpaperDimLayer.Opacity = Math.Clamp(dimPercent, 0, 100) / 100.0;
            BackdropLayer.IsVisible = !on;
            HeroGlowLayer.IsVisible = !on;
            RootPanel.Background = on ? Brushes.Transparent : surface;

            // SukiWindow 自带 SukiBackground 着色背景层：开壁纸时一起隐藏，否则窗口永远不透明
            Background = on ? Brushes.Transparent : surface;
            foreach (var layer in this.GetVisualDescendants()
                         .OfType<Control>()
                         .Where(control => control.GetType().Name == "SukiBackground")
                         .ToList())
            {
                layer.IsVisible = !on;
            }

            var blurOn = LocalSettings.Current.PanelBlur;
            if (inApp)
            {
                // 壁纸已经画在窗口内部，不需要整窗透明
                TransparencyLevelHint = [WindowTransparencyLevel.None];
            }
            else
            {
                TransparencyLevelHint = on
                    ? [WindowTransparencyLevel.Transparent]
                    : blurOn
                        ? [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None]
                        : [WindowTransparencyLevel.None];
            }

            GlassEffects.Refresh();
        }
        catch
        {
            // 壁纸层不可用时保持不透明背景
            InAppWallpaper.IsVisible = false;
            BackdropLayer.IsVisible = true;
            HeroGlowLayer.IsVisible = true;
            RootPanel.Background = (IBrush?)FindRes("NekoSurfaceBrush");
        }
    }

    /// <summary>
    /// 开始菜单：单列列表——图标 + 文字。工具不再有独立「创作中心」页面，
    /// 只在菜单里放一个 ComboBox，展开后选择工具。
    /// </summary>
    private void BuildStartMenu()
    {
        StartMenuPanel.Children.Clear();

        // 列表最前面：回桌面的「桌面」缩略图条目（开始按钮只开关菜单）
        AddDesktopRow();

        foreach (var entry in PageRegistry)
        {
            if (entry.Id == "plugins")
            {
                AddCreatorToolsRow();
            }

            AddMenuRow(entry.Id, PageLabel(entry.Id, entry.Label), entry.IconKey);
        }

        foreach (var (pluginPageId, pluginPage) in PluginPages)
        {
            AddPluginMenuRow(pluginPageId, pluginPage.Label, pluginPage.Icon);
        }

        StartMenuPanel.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(8, 6, 8, 6),
            Background = (IBrush)FindRes("NekoBorderBrush")!,
        });

        AddMenuRow("__quit__", L.T("Shell.StartMenu.Quit", "退出启动器"), "IconClose", danger: true);
    }

    /// <summary>开始菜单第一项：微缩的「桌面」缩略图 + 桌面二字（伪预览，不是真截图）。</summary>
    private void AddDesktopRow()
    {
        var muted = ThemeTokens.Brush("NekoMutedBrush");
        var accent = ThemeTokens.Brush("NekoAccentBrush");

        Border Bar(double width, IBrush brush) => new()
        {
            Width = width,
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            Background = brush,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        };

        var thumb = new Border
        {
            Width = 34,
            Height = 26,
            CornerRadius = new CornerRadius(4),
            Background = ThemeTokens.Brush("NekoFieldSoftBrush"),
            BorderBrush = ThemeTokens.Brush("NekoBorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 3),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Child = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 2,
                        Children = { Bar(14, muted), Bar(8, accent) },
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 2,
                        Children = { Bar(9, muted), Bar(9, muted) },
                    },
                },
            },
        };

        var content = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Children =
            {
                thumb,
                new TextBlock
                {
                    Text = L.T("Shell.StartMenu.Desktop", "桌面"),
                    FontSize = 13,
                    FontWeight = FontWeight.Medium,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Foreground = (IBrush)FindRes("NekoTextBrush")!,
                },
            },
        };

        var button = new Button { Classes = { "menu-row" }, Height = 46, Content = content };
        button.Click += (_, _) => Navigate("home");
        AttachTaskbarToggleMenu(button, "home");
        StartMenuPanel.Children.Add(button);
    }

    private void AddMenuRow(string id, string label, string iconKey, bool danger = false)
    {
        var icon = new IconParkIcon
        {
            Kind = iconKey,
            Width = 16,
            Height = 16,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        var content = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Children =
            {
                icon,
                new TextBlock
                {
                    Text = label,
                    FontSize = 13,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Foreground = danger
                        ? (IBrush)FindRes("NekoDangerBrush")!
                        : (IBrush)FindRes("NekoTextBrush")!,
                },
            },
        };

        var button = new Button
        {
            Classes = { "menu-row" },
            Content = content,
        };

        button.Click += (_, _) =>
        {
            if (id == "__quit__")
            {
                Close();
                return;
            }

            Navigate(id);
        };

        AttachTaskbarToggleMenu(button, id);
        StartMenuPanel.Children.Add(button);
    }

    // ==================== 可自定义任务栏 ====================

    private void RebuildTaskbar()
    {
        if (TaskbarNavPanel is null) return;

        TaskbarNavPanel.Children.Clear();
        _navButtons.Clear();

        foreach (var id in LocalSettings.Current.TaskbarPages.ToList())
        {
            var iconKey = TaskbarIconKey(id);
            if (iconKey is null) continue;

            var button = new Button
            {
                Classes = { "nav" },
                Content = new IconParkIcon
                {
                    Kind = iconKey,
                    Width = 18,
                    Height = 18,
                },
            };
            ToolTip.SetTip(button, TaskbarLabel(id));
            button.Click += (_, _) => Navigate(id);
            button.ContextMenu = BuildTaskbarMenu(id);

            TaskbarNavPanel.Children.Add(button);
            _navButtons[id] = button;
        }

        foreach (var (id, button) in _navButtons)
        {
            button.Classes.Set("active", id == _currentPage);
        }
    }

    private static string? TaskbarIconKey(string id)
    {
        if (id == "home") return "IconHome";
        if (PluginPages.ContainsKey(id)) return "IconPlugin";

        foreach (var entry in PageRegistry)
        {
            if (entry.Id == id) return entry.IconKey;
        }

        return null;
    }

    private static string TaskbarLabel(string id)
    {
        if (id == "home") return PageLabel(id, "桌面");
        if (PluginPages.TryGetValue(id, out var pluginPage)) return pluginPage.Label;

        foreach (var entry in PageRegistry)
        {
            if (entry.Id == id) return PageLabel(id, entry.Label);
        }

        return id;
    }

    // ==================== 插件系统集成点 ====================

    /// <summary>
    /// 注册一个插件页面（IPageRegister 的宿主侧入口）；返回撤销句柄。
    /// 线程安全：非 UI 线程调用会自动调度。
    /// </summary>
    public static IDisposable RegisterPluginPage(string pageId, string label, Control icon, Func<Control> factory)
    {
        PluginPages[pageId] = (label, icon, factory);
        _mainInstance?.RefreshPluginPages();
        return new PluginPageToken(pageId);
    }

    /// <summary>插件往任务栏右侧追加图标（ITaskbarTray 的宿主侧入口）；返回撤销句柄。</summary>
    public static IDisposable AppendPluginTrayWidget(string id, Control icon, Action click)
    {
        _mainInstance?.AddTrayWidget(id, icon, click);
        return new TrayWidgetToken(id);
    }

    /// <summary>插件请求跳转页面（ITaskbarTray.NavigateTo 的宿主侧入口）。</summary>
    public static void NavigateFromPlugin(string pageId) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _mainInstance?.Navigate(pageId));

    private sealed class TrayWidgetToken(string id) : IDisposable
    {
        public void Dispose() => _mainInstance?.RemoveTrayWidget(id);
    }

    private readonly Dictionary<string, Button> _pluginTrayButtons = new();

    private void AddTrayWidget(string id, Control icon, Action click)
    {
        if (Dispatcher.UIThread.CheckAccess() == false)
        {
            Dispatcher.UIThread.Post(() => AddTrayWidget(id, icon, click));
            return;
        }

        if (_pluginTrayButtons.ContainsKey(id))
        {
            RemoveTrayWidget(id);
        }

        icon.Width = 18;
        icon.Height = 18;
        var button = new Button { Classes = { "nav" }, Content = icon };
        ToolTip.SetTip(button, id);
        button.Click += (_, _) =>
        {
            try
            {
                click();
            }
            catch (Exception error)
            {
                Services.Plugins.PluginLog.Host($"托盘组件 {id} 点击回调异常：{error.Message}");
            }
        };
        PluginTrayPanel.Children.Add(button);
        _pluginTrayButtons[id] = button;
    }

    private void RemoveTrayWidget(string id)
    {
        if (Dispatcher.UIThread.CheckAccess() == false)
        {
            Dispatcher.UIThread.Post(() => RemoveTrayWidget(id));
            return;
        }

        if (_pluginTrayButtons.Remove(id, out var button))
        {
            PluginTrayPanel.Children.Remove(button);
        }
    }

    /// <summary>插件接管壁纸（IWallpaperEdit 的宿主侧入口）：内置壁纸隐藏、内容铺满背景层。</summary>
    public static IDisposable TakeOverWallpaper(Control content)
    {
        _mainInstance?.ApplyPluginWallpaper(content);
        return new WallpaperTakeOverToken();
    }

    private sealed class PluginPageToken(string pageId) : IDisposable
    {
        public void Dispose()
        {
            PluginPages.Remove(pageId);
            _mainInstance?.RefreshPluginPages();
        }
    }

    private sealed class WallpaperTakeOverToken : IDisposable
    {
        public void Dispose() => _mainInstance?.ClearPluginWallpaper();
    }

    /// <summary>插件页增删后：清掉失效缓存并重建开始菜单/任务栏。</summary>
    private void RefreshPluginPages()
    {
        if (Dispatcher.UIThread.CheckAccess() == false)
        {
            Dispatcher.UIThread.Post(RefreshPluginPages);
            return;
        }

        foreach (string staleId in _pageCache.Keys.Where(id => id.StartsWith("ext:", StringComparison.Ordinal))
                     .ToList())
        {
            if (PluginPages.ContainsKey(staleId)) continue;
            _pageCache.Remove(staleId);
            _hostCache.Remove(staleId);
        }

        BuildStartMenu();
        RebuildTaskbar();
    }

    private void ApplyPluginWallpaper(Control content)
    {
        if (Dispatcher.UIThread.CheckAccess() == false)
        {
            Dispatcher.UIThread.Post(() => ApplyPluginWallpaper(content));
            return;
        }

        WallpaperSurface.StopSlideshow();
        WallpaperSurface.IsVisible = false;
        PluginWallpaperLayer.Child = content;
        PluginWallpaperLayer.IsVisible = true;
    }

    private void ClearPluginWallpaper()
    {
        if (Dispatcher.UIThread.CheckAccess() == false)
        {
            Dispatcher.UIThread.Post(ClearPluginWallpaper);
            return;
        }

        PluginWallpaperLayer.Child = null;
        PluginWallpaperLayer.IsVisible = false;
        Services.Wallpaper.WallpaperService.Instance.Refresh();
    }

    /// <summary>开始菜单里的插件页行：图标是插件给的控件（不限于 IconPark）。</summary>
    private void AddPluginMenuRow(string id, string label, Control icon)
    {
        icon.Width = 16;
        icon.Height = 16;
        icon.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;

        var content = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Children =
            {
                icon,
                new TextBlock
                {
                    Text = label,
                    FontSize = 13,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Foreground = (IBrush)FindRes("NekoTextBrush")!,
                },
            },
        };

        var button = new Button { Classes = { "menu-row" }, Content = content };
        button.Click += (_, _) => Navigate(id);
        AttachTaskbarToggleMenu(button, id);
        StartMenuPanel.Children.Add(button);
    }

    private ContextMenu BuildTaskbarMenu(string id)
    {
        var item = new MenuItem { Header = L.T("Shell.Taskbar.Remove", "从任务栏移除") };
        item.Click += (_, _) => SetTaskbarPage(id, false);

        var menu = new ContextMenu();
        menu.Items.Add(item);
        return menu;
    }

    /// <summary>开始菜单条目右键：按当前状态切换「添加到任务栏 / 从任务栏移除」。</summary>
    private void AttachTaskbarToggleMenu(Button button, string id)
    {
        if (TaskbarIconKey(id) is null) return;

        var toggle = new MenuItem();
        var menu = new ContextMenu();
        menu.Opening += (_, _) =>
        {
            toggle.Header = LocalSettings.Current.TaskbarPages.Contains(id)
                ? L.T("Shell.Taskbar.Remove", "从任务栏移除")
                : L.T("Shell.Taskbar.Add", "添加到任务栏");
        };
        toggle.Click += (_, _) =>
            SetTaskbarPage(id, !LocalSettings.Current.TaskbarPages.Contains(id));
        menu.Items.Add(toggle);
        button.ContextMenu = menu;
    }

    private void SetTaskbarPage(string id, bool visible)
    {
        var pages = LocalSettings.Current.TaskbarPages;

        if (visible)
        {
            if (!pages.Contains(id)) pages.Add(id);
        }
        else
        {
            pages.RemoveAll(page => page == id);
        }

        LocalSettings.Save();
        RebuildTaskbar();
        if (StartMenuOverlay.IsVisible) BuildStartMenu();

        var label = TaskbarLabel(id);
        Shell.Toast(visible
            ? L.T("Shell.Taskbar.Added", "已添加「{0}」到任务栏", label)
            : L.T("Shell.Taskbar.Removed", "已从任务栏移除「{0}」", label));
    }

    /// <summary>开始菜单里的工具入口：点击整行，工具列表在菜单内联展开 / 收起。</summary>
    private void AddCreatorToolsRow()
    {
        var current = CreatorToolCatalog.Tools.FirstOrDefault(tool => tool.Id == _lastCreatorToolId);

        var glyph = new IconParkIcon
        {
            Kind = current?.IconKey ?? "IconCreator",
            Width = 16,
            Height = 16,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        var label = new TextBlock
        {
            Text = current is null
                ? L.T("Shell.StartMenu.ToolsPick", "选择工具")
                : L.T($"Creator.Tool.{current.Id}.Title", current.Title),
            FontSize = 13,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = (IBrush)FindRes("NekoTextBrush")!,
        };

        var chevron = new IconParkIcon
        {
            Kind = "IconChevronDown",
            Width = 12,
            Height = 12,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        var content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
        };
        content.Children.Add(glyph);
        label.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(label, 1);
        content.Children.Add(label);
        Grid.SetColumn(chevron, 2);
        content.Children.Add(chevron);

        var button = new Button
        {
            Classes = { "menu-row" },
            Height = 46,
            Margin = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Content = content,
        };

        var list = new StackPanel
        {
            Spacing = 2,
            Margin = new Thickness(18, 0, 0, 2),
            IsVisible = _creatorToolsExpanded,
        };

        foreach (var tool in CreatorToolCatalog.Tools)
        {
            var icon = new IconParkIcon
            {
                Kind = tool.IconKey ?? "IconCreator",
                Width = 15,
                Height = 15,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };

            var title = new TextBlock
            {
                Text = L.T($"Creator.Tool.{tool.Id}.Title", tool.Title),
                FontSize = 13,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Foreground = (IBrush)FindRes("NekoTextBrush")!,
            };

            var rowContent = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            rowContent.Children.Add(icon);
            title.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(title, 1);
            rowContent.Children.Add(title);

            var row = new Button
            {
                Classes = { "menu-row" },
                Height = 38,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                Content = rowContent,
            };

            row.Click += (_, _) =>
            {
                _lastCreatorToolId = tool.Id;
                label.Text = L.T($"Creator.Tool.{tool.Id}.Title", tool.Title);
                glyph.Kind = tool.IconKey ?? glyph.Kind;
                StartMenuOverlay.IsVisible = false;
                StartButton.Classes.Set("active", false);
                OpenCreatorTool(tool.Id);
            };

            list.Children.Add(row);
        }

        var container = new StackPanel();
        container.Children.Add(button);
        container.Children.Add(list);

        button.Click += (_, _) =>
        {
            _creatorToolsExpanded = !_creatorToolsExpanded;
            list.IsVisible = _creatorToolsExpanded;
        };

        StartMenuPanel.Children.Add(container);
    }

    private void OpenCreatorTool(string toolId)
    {
        _lastCreatorToolId = toolId;
        Navigate("creator");
        if (_pageCache.TryGetValue("creator", out var page) && page is CreatorView creator)
        {
            creator.OpenToolById(toolId);
        }
    }

    private void ShowToast(string message)
    {
        var accent = ThemeTokens.Brush("NekoAccentBrush");

        var icon = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Background = ThemeTokens.WithAlpha("NekoAccentBrush", 0.15),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Child = new IconParkIcon
            {
                Kind = "IconFluentInfo",
                Width = 14,
                Height = 14,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            },
        };

        var text = new StackPanel
        {
            Spacing = 2,
            Margin = new Thickness(10, 0, 0, 0),
            Children =
            {
                new TextBlock
                {
                    Text = "Mofumofu Launcher",
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeTokens.Brush("NekoMutedBrush"),
                },
                new TextBlock
                {
                    Text = message,
                    FontSize = 12.5,
                    Foreground = ThemeTokens.Brush("NekoTextBrush"),
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        content.Children.Add(icon);
        Grid.SetColumn(text, 1);
        content.Children.Add(text);

        var toast = new Border
        {
            Width = 340,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10),
            Background = ThemeTokens.Brush("NekoDialogBrush"),
            BorderBrush = ThemeTokens.Brush("NekoAccentBrush"),
            BorderThickness = new Thickness(1),
            Opacity = 0,
            RenderTransform = TransformOperations.Parse("translateX(-80px)"),
            Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(180),
                },
                new TransformOperationsTransition
                {
                    Property = Visual.RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(240),
                    Easing = new CubicEaseOut(),
                },
            },
            Child = content,
        };

        while (ToastStack.Children.Count >= 4)
        {
            ToastStack.Children.RemoveAt(0);
        }

        ToastStack.Children.Add(toast);
        Dispatcher.UIThread.Post(() =>
        {
            toast.Opacity = 1;
            toast.RenderTransform = TransformOperations.Parse("translateX(0px)");
        }, DispatcherPriority.Render);

        _ = DismissToastAsync(toast);
    }

    private async System.Threading.Tasks.Task DismissToastAsync(Border toast)
    {
        await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(2.6));

        if (!ToastStack.Children.Contains(toast)) return;

        // 向左滑出
        toast.Opacity = 0;
        toast.RenderTransform = TransformOperations.Parse("translateX(-150px)");
        await System.Threading.Tasks.Task.Delay(260);
        ToastStack.Children.Remove(toast);
    }

    private void ShowDialog(string title, object content)
    {
        if (DialogHost.Manager is null) return;

        // Rewind 之类的自定义弹窗内容自带标题与不透明底板：不要再让 Suki 画半透明标题带，
        // 否则标题条会透出下层页面、和内容叠在一起。
        var selfTitled = title is "Rewind" or "快照";

        DialogHost.Manager.CreateDialog()
            .WithTitle(selfTitled ? string.Empty : title)
            .WithContent(content)
            .Dismiss()
            .ByClickingBackground()
            .TryShow();
    }

    private bool _suppressStartBarSelection;

    /// <summary>重建开始栏的版本 / 账号下拉（版本优先真实扫描游戏目录 versions/，账号来自账号列表 + 皮肤档案缓存）。</summary>
    private void ReloadStartBarChoices()
    {
        var versions = BuildVersionChoices();
        var accounts = BuildAccountChoices();

        var desiredVersion = versions.FirstOrDefault(v => v.Id == GameState.SelectedVersion)
                             ?? versions.FirstOrDefault(v => v.DisplayName == GameState.SelectedVersion)
                             ?? versions.FirstOrDefault();

        var desiredAccount = accounts.FirstOrDefault(a => a.Name == GameState.SelectedAccount)
                             ?? accounts.FirstOrDefault();

        _suppressStartBarSelection = true;
        try
        {
            VersionCombo.ItemsSource = versions;
            VersionCombo.SelectedItem = desiredVersion;
            AccountCombo.ItemsSource = accounts;
            AccountCombo.SelectedItem = desiredAccount;
        }
        finally
        {
            _suppressStartBarSelection = false;
        }

        if (desiredVersion is not null && desiredVersion.DisplayName != GameState.SelectedVersion)
        {
            GameState.SetVersion(desiredVersion.DisplayName);
        }

        if (desiredAccount is not null && desiredAccount.Name != GameState.SelectedAccount)
        {
            GameState.SetAccount(desiredAccount.Name);
        }
    }

    /// <summary>状态变化后（例如账户页登录/切换）把下拉选中项对齐回当前选择。</summary>
    private void SyncStartBarSelection()
    {
        _suppressStartBarSelection = true;
        try
        {
            if (VersionCombo.ItemsSource is IEnumerable<VersionChoice> versions)
            {
                var match = versions.FirstOrDefault(v => v.Id == GameState.SelectedVersion);
                if (match is not null) VersionCombo.SelectedItem = match;
            }

            if (AccountCombo.ItemsSource is IEnumerable<AccountChoice> accounts)
            {
                var match = accounts.FirstOrDefault(a => a.Name == GameState.SelectedAccount);
                if (match is not null) AccountCombo.SelectedItem = match;
            }
        }
        finally
        {
            _suppressStartBarSelection = false;
        }
    }

    private void OnVersionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressStartBarSelection || VersionCombo.SelectedItem is not VersionChoice choice) return;

        GameState.SetVersion(choice.Id);
        LocalSettings.Current.SelectedInstanceId = choice.Id;
        LocalSettings.Save();
    }

    private void OnAccountSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressStartBarSelection || AccountCombo.SelectedItem is not AccountChoice choice) return;

        GameState.SetAccount(choice.Name);
        LocalSettings.Current.SelectedAccountName = choice.Name;
        LocalSettings.Save();
    }

    private static List<VersionChoice> BuildVersionChoices()
    {
        var list = new List<VersionChoice>();

        try
        {
            var versionsDir = Path.Combine(ResolveGameDirectory(), "versions");
            if (Directory.Exists(versionsDir))
            {
                foreach (var directory in Directory.GetDirectories(versionsDir))
                {
                    var id = Path.GetFileName(directory);
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    if (!File.Exists(Path.Combine(directory, id + ".json"))) continue;

                    list.Add(new VersionChoice { Id = id, DisplayName = id, Icon = LoadInstanceIcon(id) });
                }
            }
        }
        catch
        {
            // 游戏目录不可读时回退到内置实例列表
        }

        list.Sort((left, right) => string.CompareOrdinal(left.DisplayName, right.DisplayName));
        return list;
    }

    private List<AccountChoice> BuildAccountChoices()
    {
        var names = new List<string>();

        try
        {
            var handle = new MofuCore.Account.MinecraftTokenHandle();
            foreach (var account in handle.LoadMicrosoftAccounts())
            {
                if (!string.IsNullOrWhiteSpace(account.Username)) names.Add(account.Username);
            }

            foreach (var credential in handle.LoadAuthlibCredentials())
            {
                var name = string.IsNullOrWhiteSpace(credential.ProfileName)
                    ? credential.Username
                    : credential.ProfileName;
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }

            foreach (var name in handle.LoadOfflineAccounts())
            {
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
        }
        catch
        {
            // 账号保险库不可读时没有可显示的账号
        }

        var current = GameState.SelectedAccount;
        if (!string.IsNullOrWhiteSpace(current)) names.Add(current);

        var choices = new List<AccountChoice>();
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var choice = new AccountChoice { Name = name };
            choices.Add(choice);
            _ = FillSkinHeadAsync(choice);
        }

        return choices;
    }

    /// <summary>异步补齐账号皮肤大头（服务内部有内存/磁盘缓存，重复调用无额外开销）。</summary>
    private static async System.Threading.Tasks.Task FillSkinHeadAsync(AccountChoice choice)
    {
        try
        {
            var head = await SkinHeadService.LoadHeadAsync(choice.Name).ConfigureAwait(false);
            if (head is null) return;

            await Dispatcher.UIThread.InvokeAsync(() => choice.Head = head);
        }
        catch
        {
            // 拿不到大头就保留默认图标
        }
    }

    private static string ProfileSkinRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mofumofu.Launcher",
        "skins");

    private static string ResolveGameDirectory()
    {
        try
        {
            var configured = MofuCore.Config.YamlRead.GameDirectory;
            if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        }
        catch
        {
            // 配置不可用时用工作目录下的 .minecraft
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".minecraft"));
    }

    private static Bitmap? LoadInstanceIcon(string versionId)
    {
        var key = versionId.ToLowerInvariant();
        var file =
            key.Contains("neoforge") ? "neoforge.png"
            : key.Contains("forge") ? "forge.png"
            : key.Contains("fabric") ? "fabric.png"
            : key.Contains("lite") ? "liteloader.png"
            : key.StartsWith("2") && key.Contains('w') ? "snapshot_version.png"
            : key.StartsWith("1.7") || key.StartsWith("1.8") || key.StartsWith("1.12") ? "old_version.png"
            : "vanilla.png";

        return LoadAssetBitmap($"avares://Mofumofu.Launcher/Assets/instance-icons/{file}");
    }

    private static Bitmap? LoadAssetBitmap(string uri)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(uri));
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    // ================= 外观 / 窗口行为 / 托盘（设置页假交互的落地处） =================

    private TrayIcon? _trayIcon;
    private bool _forceClose;
    private bool _closePromptOpen;
    private Control? _squareAppliedPage;
    private readonly Dictionary<Border, CornerRadius> _squareBorderBackup = new();
    private readonly Dictionary<TextBox, CornerRadius> _squareTextBackup = new();

    /// <summary>应用外观设置：窗口/背景不透明度、面板毛玻璃、直角模式。设置页改完与启动时都会调用。</summary>
    public void ApplyAppearanceSettings()
    {
        var settings = LocalSettings.Current;

        try
        {
            Opacity = Math.Clamp(settings.WindowOpacity, 40, 100) / 100.0;
        }
        catch
        {
            // 不透明度不可用时保持默认
        }

        if (BackdropLayer is not null)
        {
            try
            {
                BackdropLayer.Opacity = Math.Clamp(settings.BackgroundOpacity, 0, 100) / 100.0;
            }
            catch
            {
                // 背景层不可用时忽略
            }
        }

        ApplyWallpaperBackdrop();
        RestoreSquareCorners();
        ApplySquareCorners(settings.SquareCorners);
    }

    /// <summary>直角模式：把当前页面里的面板/输入框圆角清零，关闭时还原。</summary>
    private void ApplySquareCorners(bool square)
    {
        if (!square) return;
        if (PageHost is null) return;
        if (ReferenceEquals(_squareAppliedPage, PageHost.Content as Control)) return;

        RestoreSquareCorners();
        _squareAppliedPage = PageHost.Content as Control;

        foreach (var visual in PageHost.GetVisualDescendants())
        {
            switch (visual)
            {
                case Border border when border.CornerRadius != default:
                    _squareBorderBackup[border] = border.CornerRadius;
                    border.CornerRadius = default;
                    break;

                case TextBox textBox when textBox.CornerRadius != default:
                    _squareTextBackup[textBox] = textBox.CornerRadius;
                    textBox.CornerRadius = default;
                    break;
            }
        }
    }

    private void RestoreSquareCorners()
    {
        foreach (var (border, radius) in _squareBorderBackup) border.CornerRadius = radius;
        foreach (var (textBox, radius) in _squareTextBackup) textBox.CornerRadius = radius;

        _squareBorderBackup.Clear();
        _squareTextBackup.Clear();
        _squareAppliedPage = null;
    }

    private void InitializeTrayIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Mofumofu.Launcher/Assets/appicon.png"));
            var icon = new WindowIcon(stream);

            var show = new NativeMenuItem { Header = L.T("Shell.Tray.Show", "显示主窗口") };
            show.Click += (_, _) => RestoreFromTray();

            var exit = new NativeMenuItem { Header = L.T("Shell.Tray.Exit", "退出启动器") };
            exit.Click += (_, _) => ExitApplication();
            _trayExitItem = exit;

            _trayIcon = new TrayIcon
            {
                Icon = icon,
                ToolTipText = "Mofumofu Launcher",
                IsVisible = true,
                Menu = new NativeMenu { Items = { show, exit } },
            };

            // TrayIcon 必须挂在 Application.TrayIcons 上才会真正显示到系统托盘区，
            // 独立 new 出来的实例不会出现（表现为最小化到托盘后程序“消失”）。
            TrayIcon.SetIcons(Application.Current!, new TrayIcons { _trayIcon });

            _trayIcon.Clicked += (_, _) => RestoreFromTray();
        }
        catch
        {
            _trayIcon = null;
        }
    }

    private bool HasTray => _trayIcon is not null;

    // ==================== 插件托盘常驻（ISystemTray 宿主侧） ====================

    private NativeMenuItem? _trayExitItem;
    private readonly Dictionary<string, NativeMenuItem> _pluginTrayItems = new();

    /// <summary>插件往托盘菜单追加项（插在「退出」之前）；返回撤销句柄。</summary>
    public static IDisposable AppendPluginTrayMenuItem(string id, string label, Action click)
    {
        _mainInstance?.AddTrayMenuItem(id, label, click);
        return new PluginTrayToken(id);
    }

    /// <summary>插件发托盘气泡；平台不支持气泡时回退启动器内 Toast。</summary>
    public static void ShowPluginTrayNotification(string title, string message)
    {
        if (_mainInstance?._trayIcon is { } tray)
        {
            // Avalonia 12 的 TrayIcon 没有统一气泡 API，用托盘提示承载标题 + Toast 承载正文
            tray.ToolTipText = title;
        }
        Shell.Toast($"[{title}] {message}");
    }

    private sealed class PluginTrayToken(string id) : IDisposable
    {
        public void Dispose() => _mainInstance?.RemoveTrayMenuItem(id);
    }

    private void AddTrayMenuItem(string id, string label, Action click)
    {
        if (Dispatcher.UIThread.CheckAccess() == false)
        {
            Dispatcher.UIThread.Post(() => AddTrayMenuItem(id, label, click));
            return;
        }

        if (_trayIcon?.Menu is not { } menu) return;
        RemoveTrayMenuItem(id);

        var item = new NativeMenuItem { Header = label };
        item.Click += (_, _) =>
        {
            try
            {
                click();
            }
            catch (Exception error)
            {
                Services.Plugins.PluginLog.Host($"托盘菜单 {id} 点击异常：{error.Message}");
            }
        };
        _pluginTrayItems[id] = item;

        int insertAt = _trayExitItem is { } exitItem ? menu.Items.IndexOf(exitItem) : menu.Items.Count;
        if (insertAt < 0) insertAt = menu.Items.Count;
        menu.Items.Insert(insertAt, item);
    }

    private void RemoveTrayMenuItem(string id)
    {
        if (Dispatcher.UIThread.CheckAccess() == false)
        {
            Dispatcher.UIThread.Post(() => RemoveTrayMenuItem(id));
            return;
        }

        if (_pluginTrayItems.Remove(id, out var item))
        {
            _trayIcon?.Menu?.Items.Remove(item);
        }
    }

    private void HideToTray()
    {
        if (!HasTray)
        {
            WindowState = WindowState.Minimized;
            return;
        }

        Hide();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _forceClose = true;
        if (_trayIcon is { } icon)
        {
            TrayIcon.GetIcons(Application.Current!)?.Remove(icon);
            icon.Dispose();
        }
        _trayIcon = null;
        Close();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_forceClose) return;

        var action = LocalSettings.Current.CloseAction;
        if (string.Equals(action, "tray", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        if (string.Equals(action, "ask", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            ShowClosePrompt();
        }
    }

    private void ShowClosePrompt()
    {
        if (_closePromptOpen || DialogHost.Manager is null)
        {
            if (HasTray) HideToTray();
            else ExitApplication();
            return;
        }

        _closePromptOpen = true;
        DispatcherTimer.RunOnce(() => _closePromptOpen = false, TimeSpan.FromSeconds(30));

        try
        {
            var panel = new StackPanel { Spacing = 14, Width = 340 };
            panel.Children.Add(new TextBlock
            {
                Text = L.T("Shell.Close.Prompt", "要退出启动器，还是最小化到系统托盘？"),
                TextWrapping = TextWrapping.Wrap,
            });

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            var minimize = new Button { Content = L.T("Shell.Close.Minimize", "最小化到托盘") };
            minimize.Classes.Add("ghost");
            minimize.Click += (_, _) =>
            {
                _closePromptOpen = false;
                DialogHost.Manager?.DismissDialog();
                HideToTray();
            };

            var exit = new Button { Content = L.T("Shell.Close.Exit", "退出") };
            exit.Classes.Add("primary");
            exit.Click += (_, _) =>
            {
                _closePromptOpen = false;
                DialogHost.Manager?.DismissDialog();
                ExitApplication();
            };

            buttons.Children.Add(minimize);
            buttons.Children.Add(exit);
            panel.Children.Add(buttons);

            DialogHost.Manager.CreateDialog()
                .WithTitle(L.T("Shell.Close.Title", "关闭启动器"))
                .WithContent(panel)
                .Dismiss()
                .ByClickingBackground()
                .TryShow();
        }
        catch
        {
            _closePromptOpen = false;
        }
    }

    private void OnLaunchBarRefreshRequested()
    {
        ReloadStartBarChoices();
        SyncLaunchState();
    }

    private void SyncLaunchState()
    {
        if (LaunchText is null) return;

        LaunchText.Text = GameState.IsRunning ? L.T("Shell.StartBar.Stop", "停止游戏") : L.T("Shell.StartBar.Launch", "启动游戏");
        LaunchIcon.Kind = GameState.IsRunning ? "IconStop" : "IconPlay";
        SyncStartBarSelection();
    }

    private bool _startMenuClosing;

    private async void OnStartButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_startMenuClosing) return;

        if (StartMenuOverlay.IsVisible)
        {
            await CloseStartMenuAsync();
            return;
        }

        OpenStartMenu();
    }

    private void OpenStartMenu()
    {
        UiAnimations.Reset(StartMenuCard);
        StartMenuOverlay.IsVisible = true;
        StartButton.Classes.Set("active", true);

        UiAnimations.PopIn(StartMenuCard, 0.86, 18, 220);
        Dispatcher.UIThread.Post(() =>
        {
            StartMenuCard.Opacity = 1;
            StartMenuCard.RenderTransform = TransformOperations.Parse("translateY(0px) scale(1)");
        }, DispatcherPriority.Render);
    }

    private async System.Threading.Tasks.Task CloseStartMenuAsync()
    {
        if (!StartMenuOverlay.IsVisible || _startMenuClosing) return;

        _startMenuClosing = true;
        UiAnimations.PopOut(StartMenuCard, 0.9, 14, 150);
        await System.Threading.Tasks.Task.Delay(160);

        StartMenuOverlay.IsVisible = false;
        StartButton.Classes.Set("active", false);
        UiAnimations.Reset(StartMenuCard);
        _startMenuClosing = false;
    }

    private async void OnStartMenuOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 只有点在遮罩空白处（不是菜单卡片内部）才关闭
        if (!ReferenceEquals(e.Source, StartMenuOverlay)) return;

        e.Handled = true;
        await CloseStartMenuAsync();
    }


    private void OnOpenLaunchLogClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Navigate("launchconsole");
    }

    private void OnLaunchButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // 启动/停止都走「类终端启动进度」页：启动时逐阶段跑，停止时直接收尾
        if (GameState.IsRunning)
        {
            GameState.Stop();
            if (_pageCache.TryGetValue("launchconsole", out var runningPage)
                && runningPage is LaunchConsoleView console)
            {
                console.Progress.Cancel();
            }

            return;
        }

        if (!GameState.HasSelectedVersion)
        {
            Shell.Toast(L.T("Shell.Toast.NoVersion", "请先在下载页下载或选择一个游戏版本"));
            return;
        }

        GameState.Start();
        Navigate("launchconsole");
    }

    // ---------------- 自绘标题栏 / 无边框窗口 ----------------

    private static bool IsButtonSource(object? source)
    {
        return source is Visual visual &&
               (visual is Button || visual.FindAncestorOfType<Button>(true) is not null);
    }

    private void OnShellTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (IsButtonSource(e.Source)) return;

        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
        }

        BeginMoveDrag(e);
    }

    private void OnShellTitleBarDoubleTap(object? sender, TappedEventArgs e)
    {
        if (IsButtonSource(e.Source)) return;
        ToggleMaximize();
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void UpdateMaximizeIcon()
    {
        if (MaximizeIcon is null) return;
        MaximizeIcon.Kind = WindowState == WindowState.Maximized
            ? "IconRestore"
            : "IconMaximize";
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void Resize(WindowEdge edge, PointerPressedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) return;
        BeginResizeDrag(edge, e);
    }

    private void OnResizeTop(object? sender, PointerPressedEventArgs e) => Resize(WindowEdge.North, e);
    private void OnResizeBottom(object? sender, PointerPressedEventArgs e) => Resize(WindowEdge.South, e);
    private void OnResizeLeft(object? sender, PointerPressedEventArgs e) => Resize(WindowEdge.West, e);
    private void OnResizeRight(object? sender, PointerPressedEventArgs e) => Resize(WindowEdge.East, e);
    private void OnResizeTopLeft(object? sender, PointerPressedEventArgs e) => Resize(WindowEdge.NorthWest, e);
    private void OnResizeTopRight(object? sender, PointerPressedEventArgs e) => Resize(WindowEdge.NorthEast, e);
    private void OnResizeBottomLeft(object? sender, PointerPressedEventArgs e) => Resize(WindowEdge.SouthWest, e);
    private void OnResizeBottomRight(object? sender, PointerPressedEventArgs e) => Resize(WindowEdge.SouthEast, e);

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (StartMenuOverlay.IsVisible)
            {
                _ = CloseStartMenuAsync();
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Shell.PageRequested -= Navigate;
        Shell.ToastRequested -= ShowToast;
        Shell.DialogRequested -= ShowDialog;
        Shell.ToolRequested -= OpenCreatorTool;
        Shell.ExitRequested -= ExitApplication;
        PageComposer.Changed -= OnPluginContentChanged;
        GameState.Changed -= SyncLaunchState;
        LaunchBarRefresh.Requested -= OnLaunchBarRefreshRequested;
        ThemeManager.ThemeChanged -= OnThemeChanged;
        L.LanguageChanged -= OnLanguageChanged;
        WallpaperService.Instance.Changed -= OnWallpaperChanged;
        base.OnClosed(e);
    }
}
