using Mofumofu.Launcher.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using Mofumofu.Launcher.Services.Agent;
using Mofumofu.Launcher.Services.Wallpaper;
using Mofumofu.Launcher.Views;
using Mofumofu.Launcher.Web;

namespace Mofumofu.Launcher;

public partial class MainWindow : Window
{
    private const int ToastExitAnimationMs = 260;
    private const int StartMenuAnimationMs = 160;

    private static readonly (string Id, string Label, string IconKey, string Hint)[] PageRegistry =
    {
        ("account", "账户", "IconAccount", "账号、皮肤与披风"),
        ("download", "下载", "IconDownload", "版本与内容下载"),
        ("instances", "实例", "IconCube", "游戏实例与版本"),
        ("music", "音乐", "IconMusic", "本地音乐播放器"),
        ("online", "联机", "IconGlobe", "房间与公网联机"),
        ("servers", "服务器", "IconServer", "本机托管与收藏"),
        ("creator", "工具", "IconCreator", "资源包、命令、文字与皮肤工具"),
        ("plugins", "插件", "IconPlugin", "扩展与权限"),
        ("mofuagent", "MofuAgent", "IconRobot", "内置智能体 · 会操作启动器"),
        ("settings", "设置", "IconSettings", "启动器选项"),
    };

    private static readonly Dictionary<string, (string Label, Control Icon, Func<Control> Factory, bool IsTool)> PluginPages = new();

    private static MainWindow? _mainInstance;

    private readonly Dictionary<string, Control> _pageCache = new();
    private readonly Dictionary<string, Control> _hostCache = new();
    private readonly Dictionary<string, Button> _navButtons = new();

    public MainWindow()
    {
        InitializeComponent();

        DialogHost.Manager.OnDialogShown += (_, _) => WebRegionHost.SuspendAll();
        DialogHost.Manager.OnDialogDismissed += (_, _) => WebRegionHost.ResumeAll();

        Shell.PageRequested += Navigate;
        Shell.AgentTaskRequested += OpenAgentTask;
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

        ThemeManager.ThemeChanged += OnThemeChanged;
        L.LanguageChanged += OnLanguageChanged;
        Title = L.T("Shell.AppTitle", "Mofumofu Launcher");
        GlassEffects.SetEnabled(LocalSettings.Current.PanelBlur);
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

        AddHandler(DragDrop.DragOverEvent, OnFileDragOver, RoutingStrategies.Bubble, true);
        AddHandler(DragDrop.DropEvent, OnFileDrop, RoutingStrategies.Bubble, true);
        AddHandler(DragDrop.DragLeaveEvent, OnFileDragLeave, RoutingStrategies.Bubble, true);
        Deactivated += (_, _) => HideFileDropOverlay();
    }

    private string _dropPreviewKey = string.Empty;
    private IReadOnlyList<DropImportPlan> _dropPreviewPlans = Array.Empty<DropImportPlan>();

    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        bool hasFiles;
        try
        {
            hasFiles = e.DataTransfer.Formats.Contains(DataFormat.File);
        }
        catch
        {
            hasFiles = false;
        }

        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (!hasFiles)
        {
            HideFileDropOverlay();
            return;
        }

        // DragOver 会高频触发，按路径集合缓存识别结果，避免每次移动都去读一遍 zip
        var paths = DragImportService.ExtractPaths(e);
        string key = paths.Count == 0 ? string.Empty : string.Join('\u0001', paths);
        if (key.Length > 0 && key != _dropPreviewKey)
        {
            _dropPreviewKey = key;
            _dropPreviewPlans = DragImportService.Classify(paths);
        }
        else if (key.Length == 0)
        {
            _dropPreviewKey = string.Empty;
            _dropPreviewPlans = Array.Empty<DropImportPlan>();
        }

        string detail = _dropPreviewPlans.Count > 0
            ? L.T("Drop.Overlay.Kinds", "识别到：{0}", DragImportService.Describe(_dropPreviewPlans))
            : L.T("Drop.Overlay.Hint", "整合包 / 模组 / 资源包 / 存档… 松开后自动识别导入位置");

        if (_dropPreviewPlans.Any(plan => plan.Scope == DropImportScope.InstanceContent))
        {
            string? instanceId = ResolveDropInstance(e.Source) ?? BuildDefaultInstanceId();
            if (!string.IsNullOrWhiteSpace(instanceId))
            {
                detail += "\n" + L.T("Drop.Overlay.Instance", "目标实例：{0}", instanceId);
            }
        }

        ShowFileDropOverlay(detail);
    }

    private void OnFileDragLeave(object? sender, DragEventArgs e) => HideFileDropOverlay();

    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        HideFileDropOverlay();
        _dropPreviewKey = string.Empty;
        _dropPreviewPlans = Array.Empty<DropImportPlan>();

        var paths = DragImportService.ExtractPaths(e);
        if (paths.Count == 0)
        {
            Shell.Toast(L.T("Drop.NoFiles", "没有读到可导入的文件"));
            return;
        }

        var plans = DragImportService.Classify(paths);
        DragImportService.ConfirmAndImport(plans, BuildDropImportContext(e.Source));
    }

    private void ShowFileDropOverlay(string detail)
    {
        FileDropDetail.Text = detail;
        FileDropOverlay.IsVisible = true;
    }

    private void HideFileDropOverlay() => FileDropOverlay.IsVisible = false;

    private string? ResolveDropInstance(object? source)
        => _currentPage == "instances" ? InstancesView.ResolveDroppedInstanceId(source) : null;

    private string? BuildDefaultInstanceId()
    {
        if (_pageCache.TryGetValue("instances", out var page) && page is InstancesView view &&
            !string.IsNullOrWhiteSpace(view.CurrentInstanceId))
        {
            return view.CurrentInstanceId;
        }

        string saved = LocalSettings.Current.SelectedInstanceId;
        return string.IsNullOrWhiteSpace(saved) && GameState.HasSelectedVersion
            ? GameState.SelectedVersion
            : saved;
    }

    private DropImportContext BuildDropImportContext(object? source)
    {
        var instances = BuildVersionChoices()
            .Select(choice => new DropInstanceChoice(choice.Id, choice.DisplayName))
            .ToList();

        string? instanceId = ResolveDropInstance(source) ?? BuildDefaultInstanceId();
        return new DropImportContext(_currentPage, instanceId, instances, null);
    }

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
        "mofuagent" => new MofuAgentView(),
        "mofuagent-settings" => new MofuAgentSettingsView(),
        "creator" => new CreatorView(),
        "launchconsole" => new LaunchConsoleView(),
        "settings" => new SettingsView(),
        _ => PluginPages.TryGetValue(id, out var pluginPage)
            ? pluginPage.Factory()
            : new HomeView(),
    };

    private string _currentPage = "home";

    private bool OpenAgentTask(AgentTaskDraft task)
    {
        const string pageId = "mofuagent";
        if (!_pageCache.TryGetValue(pageId, out Control? page))
        {
            page = CreatePage(pageId);
            _pageCache[pageId] = page;
        }
        if (page is not MofuAgentView agent || !agent.TryPrepareTask(task)) return false;
        Navigate(pageId);
        return true;
    }

    private void Navigate(string pageId)
    {
        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();

        bool created = false;
        if (!_pageCache.TryGetValue(pageId, out var page))
        {
            page = CreatePage(pageId);
            _pageCache[pageId] = page;
            created = true;
        }

        if (!_hostCache.TryGetValue(pageId, out var host))
        {
            host = PageComposer.Current.Wrap(pageId, page);
            _hostCache[pageId] = host;
            created = true;
        }

        // 点当前页自己的按钮时不要重设 Content：那会走一遍摘挂载，页面的
        // AttachedToVisualTree / Loaded 全部重跑，看起来就是"点一下卡一下"。
        bool switched = !ReferenceEquals(PageHost.Content, host);

        _currentPage = pageId;
        if (switched)
        {
            PageHost.Content = host;

            // 毛玻璃只要在新页面第一次挂上来时套一次；缓存页面切回来效果没变，
            // 每次都全窗口扫一遍可视树纯属浪费。
            if (created) GlassEffects.Refresh();

            UiAnimations.SlideInFromRight(host);
        }

        StartMenuOverlay.IsVisible = false;
        StartButton.Classes.Set("active", false);

        foreach (var (id, button) in _navButtons)
        {
            button.Classes.Set("active", id == pageId);
        }

        _lastSquareCornerPage = page;
        ApplySquareCorners(LocalSettings.Current.SquareCorners);

        // 切换页超过一帧就记一条，方便以后定位"哪个页面一进来就卡"。
        double elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        if (elapsedMs > 16)
        {
            MofuCore.Log.LogWrite.Write("INFO",
                $"页面切换 {pageId} 同步耗时 {elapsedMs:F1} ms（新建页面={created}）");
        }
    }

    private Control? _lastSquareCornerPage;

    private void OnPluginContentChanged() => Dispatcher.UIThread.Post(RefreshCurrentPageHost);

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

    private static string PageLabel(string id, string fallback)
    {
        var key = "Shell.Page." + (id.Length == 0 ? id : char.ToUpperInvariant(id[0]) + id[1..]);
        return L.T(key, fallback);
    }

    private void OnLanguageChanged()
    {
        // 过渡遮罩负责合并连续触发（Play 内部去重），并在罩住后启动分段异步重建，
        // 重建段间让出 UI 线程，遮罩动画才能持续画帧、不被同步卡死。
        LanguageTransition.Play(() => _ = ReloadForLanguageAsync());
    }

    private async System.Threading.Tasks.Task ReloadForLanguageAsync()
    {
        try
        {
            // The Agent page owns the active run and unsent task drafts; don't replace it on reload.
            _pageCache.TryGetValue("mofuagent", out Control? agent);
            _hostCache.TryGetValue("mofuagent", out Control? agentHost);
            _pageCache.Clear();
            if (agent is not null) _pageCache.Add("mofuagent", agent);
            _hostCache.Clear();
            if (agentHost is not null) _hostCache.Add("mofuagent", agentHost);
            PageHost.Content = null;
            await YieldFrame();

            BuildStartMenu();
            await YieldFrame();

            Navigate(_currentPage);
            await YieldFrame();

            ReloadStartBarChoices();
            Title = L.T("Shell.AppTitle", "Mofumofu Launcher");
        }
        finally
        {
            LanguageTransition.ReportDone();
        }
    }

    // 让出一帧：Background 低于渲染/动画优先级，保证遮罩先画再干重活。
    private static async System.Threading.Tasks.Task YieldFrame()
        => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    private void OnThemeChanged()
    {
        BuildStartMenu();
        SyncLaunchState();
        ApplyWallpaperBackdrop();
        WallpaperService.Instance.NotifyThemeChanged();
    }

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

            // 窗口自身背景：开壁纸时透明（透出 DWM 的透明层），关壁纸时用面板色兜底
            Background = on ? Brushes.Transparent : surface;

            var blurOn = LocalSettings.Current.PanelBlur;
            if (inApp)
            {
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
            InAppWallpaper.IsVisible = false;
            BackdropLayer.IsVisible = true;
            HeroGlowLayer.IsVisible = true;
            RootPanel.Background = (IBrush?)FindRes("NekoSurfaceBrush");
        }
    }

    private void BuildStartMenu()
    {
        StartMenuPanel.Children.Clear();

        AddDesktopRow();

        foreach (var entry in PageRegistry)
        {
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

        AddExitActionsRow();
    }

    private void AddExitActionsRow()
    {
        var quit = new Button
        {
            Classes = { "menu-row" },
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Content = BuildMenuRowContent(L.T("Shell.StartMenu.Quit", "退出启动器"), "IconClose", danger: true),
        };
        quit.Click += (_, _) => ExitApplication();

        var restart = new Button
        {
            Classes = { "menu-row" },
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Content = BuildMenuRowContent(L.T("Shell.StartMenu.Restart", "重启启动器"), "IconRefresh", danger: false),
        };
        restart.Click += (_, _) => RestartLauncher();

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,6,*") };
        Grid.SetColumn(quit, 0);
        Grid.SetColumn(restart, 2);
        row.Children.Add(quit);
        row.Children.Add(restart);
        StartMenuPanel.Children.Add(row);
    }

    /// <summary>重启启动器：先拉起一份新实例（带 --restart 让新实例等旧实例释放单实例锁），再退出当前实例。</summary>
    private void RestartLauncher()
    {
        try
        {
            string? executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable))
            {
                Shell.Toast(L.T("Shell.StartMenu.RestartFailed", "找不到当前启动器，无法重启，将直接退出。"));
                ExitApplication();
                return;
            }

            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };

            if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                string? entryAssembly = Assembly.GetEntryAssembly()?.Location;
                if (!string.IsNullOrEmpty(entryAssembly)) info.ArgumentList.Add(entryAssembly);
            }

            info.ArgumentList.Add("--restart");
            foreach (string argument in Program.LaunchArguments)
            {
                if (string.Equals(argument, "--restart", StringComparison.OrdinalIgnoreCase)) continue;
                info.ArgumentList.Add(argument);
            }

            Process.Start(info);
        }
        catch (Exception ex)
        {
            MofuCore.Log.LogWrite.Write("WARN", "重启启动器失败：" + ex.Message);
            Shell.Toast(L.T("Shell.StartMenu.RestartFailed", "找不到当前启动器，无法重启，将直接退出。"));
        }

        ExitApplication();
    }

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

    private void AddMenuRow(string id, string label, string iconKey)
    {
        var button = new Button
        {
            Classes = { "menu-row" },
            Content = BuildMenuRowContent(label, iconKey),
        };

        button.Click += (_, _) => Navigate(id);

        AttachTaskbarToggleMenu(button, id);
        StartMenuPanel.Children.Add(button);
    }

    private Control BuildMenuRowContent(string label, string iconKey, bool danger = false)
    {
        return new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Children =
            {
                new IconParkIcon
                {
                    Kind = iconKey,
                    Width = 16,
                    Height = 16,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                },
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
    }

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

    /// <summary>
    /// 注册一个插件页面（IPageRegister 的宿主侧入口）；返回撤销句柄。
    /// 线程安全：非 UI 线程调用会自动调度。
    /// </summary>
    public static IDisposable RegisterPluginPage(
        string pageId,
        string label,
        Control icon,
        Func<Control> factory,
        bool isTool = false)
    {
        PluginPages[pageId] = (label, icon, factory, isTool);
        _mainInstance?.RefreshPluginPages();
        return new PluginPageToken(pageId);
    }

    public static IDisposable AppendPluginTrayWidget(string id, Control icon, Action click)
    {
        _mainInstance?.AddTrayWidget(id, icon, click);
        return new TrayWidgetToken(id);
    }

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

    private void OpenCreatorTool(string toolId)
    {
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

        toast.Opacity = 0;
        toast.RenderTransform = TransformOperations.Parse("translateX(-150px)");
        await System.Threading.Tasks.Task.Delay(ToastExitAnimationMs);
        ToastStack.Children.Remove(toast);
    }

    private void ShowDialog(string title, object content, Control? anchor)
    {
        if (DialogHost.Manager is null) return;

        // Rewind 之类的自定义弹窗内容自带标题与不透明底板：不要再让 Suki 画半透明标题带，
        // 否则标题条会透出下层页面、和内容叠在一起。
        var selfTitled = title is "Rewind" or "快照";

        DialogHost.Manager.CreateDialog()
            .WithTitle(selfTitled ? string.Empty : title)
            .WithContent(content)
            .From(anchor)
            .Dismiss()
            .ByClickingBackground()
            .TryShow();
    }

    private bool _suppressStartBarSelection;
    private IReadOnlyList<InstanceSnapshot> _instanceSnapshots = Array.Empty<InstanceSnapshot>();

    private void ReloadStartBarChoices()
    {
        _instanceSnapshots = InstanceCatalog.Scan();

        var desired = FindSelectedInstance() ?? _instanceSnapshots.FirstOrDefault();
        if (desired is not null && !string.Equals(desired.Id, GameState.SelectedVersion, StringComparison.Ordinal))
        {
            GameState.SetVersion(desired.Id);
        }

        UpdateInstanceChip(desired);

        var accounts = BuildAccountChoices();
        var desiredAccount = accounts.FirstOrDefault(a => a.Name == GameState.SelectedAccount)
                             ?? accounts.FirstOrDefault();

        _suppressStartBarSelection = true;
        try
        {
            AccountCombo.ItemsSource = accounts;
            AccountCombo.SelectedItem = desiredAccount;
        }
        finally
        {
            _suppressStartBarSelection = false;
        }

        if (desiredAccount is not null && desiredAccount.Name != GameState.SelectedAccount)
        {
            GameState.SetAccount(desiredAccount.Name);
        }
    }

    private InstanceSnapshot? FindSelectedInstance()
    {
        var selectedId = GameState.SelectedVersion;
        return _instanceSnapshots.FirstOrDefault(item =>
                   string.Equals(item.Id, selectedId, StringComparison.OrdinalIgnoreCase))
               ?? _instanceSnapshots.FirstOrDefault(item =>
                   string.Equals(item.Name, selectedId, StringComparison.OrdinalIgnoreCase));
    }

    private void SyncStartBarSelection()
    {
        _suppressStartBarSelection = true;
        try
        {
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

        UpdateInstanceChip(FindSelectedInstance());
    }

    private void UpdateInstanceChip(InstanceSnapshot? instance)
    {
        if (InstanceChipName is null) return;

        var hasInstance = instance is not null;
        InstanceChipName.Text = hasInstance
            ? instance!.Name
            : L.T("Shell.StartBar.SelectInstance", "选择实例");
        InstanceChipMeta.Text = hasInstance ? BuildInstanceMeta(instance!) : string.Empty;
        InstanceChipMeta.IsVisible = hasInstance;

        InstanceChipIcon.Source = instance?.Icon;
        InstanceChipIcon.IsVisible = instance?.Icon is not null;
        InstanceChipFallback.IsVisible = instance?.Icon is null;
        InstanceChipRunDot.IsVisible = hasInstance && GameState.IsRunning;

        InstanceChip.IsEnabled = !GameState.IsRunning;
        ToolTip.SetTip(InstanceChip, L.T(
            GameState.IsRunning ? "Shell.StartBar.InstanceBusy" : "Shell.StartBar.InstanceTip",
            GameState.IsRunning ? "游戏运行中，无法切换实例" : "切换游戏实例"));
    }

    private static string BuildInstanceMeta(InstanceSnapshot instance)
    {
        var loader = instance.Loader == "Vanilla"
            ? L.T("Download.Minecraft.Vanilla", "原版")
            : instance.Loader;
        return $"{instance.Version} · {loader} · {L.T("Shell.Picker.Mods", "{0} 个模组", instance.ModCount)}";
    }

    private void OnInstanceChipClick(object? sender, RoutedEventArgs e)
    {
        if (GameState.IsRunning) return;

        var picker = new InstancePickerPopover(
            _instanceSnapshots,
            FindSelectedInstance()?.Id,
            GameState.IsRunning ? GameState.SelectedVersion : null,
            SelectInstance,
            () => Shell.Go("instances"),
            () => Shell.Go("download"));

        MofuPopover.Show(InstanceChip, picker, true);
    }

    private void SelectInstance(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        if (!string.Equals(GameState.SelectedVersion, id, StringComparison.Ordinal))
        {
            GameState.SetVersion(id);
        }

        LocalSettings.Current.SelectedInstanceId = id;
        LocalSettings.Save();
        UpdateInstanceChip(_instanceSnapshots.FirstOrDefault(item =>
            string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)));
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
        return InstanceCatalog.Scan()
            .Select(item => new VersionChoice { Id = item.Id, DisplayName = item.Name, Icon = item.Icon })
            .ToList();
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
        }
    }

    private static string ProfileSkinRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mofumofu.Launcher",
        "skins");

    private TrayIcon? _trayIcon;
    private bool _forceClose;
    private bool _closePromptOpen;
    private readonly HashSet<Control> _squareAppliedPages = [];
    private readonly Dictionary<Border, CornerRadius> _squareBorderBackup = new();
    private readonly Dictionary<TextBox, CornerRadius> _squareTextBackup = new();

    public void ApplyAppearanceSettings()
    {
        var settings = LocalSettings.Current;

        try
        {
            Opacity = Math.Clamp(settings.WindowOpacity, 40, 100) / 100.0;
        }
        catch
        {
        }

        if (BackdropLayer is not null)
        {
            try
            {
                BackdropLayer.Opacity = Math.Clamp(settings.BackgroundOpacity, 0, 100) / 100.0;
            }
            catch
            {
            }
        }

        ApplyWallpaperBackdrop();
        RestoreSquareCorners();
        ApplySquareCorners(settings.SquareCorners);
    }

    private void ApplySquareCorners(bool square)
    {
        if (!square) return;
        if (PageHost?.Content is not Control target) return;

        if (!_squareAppliedPages.Add(target)) return;

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
        _squareAppliedPages.Clear();
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

    private NativeMenuItem? _trayExitItem;
    private readonly Dictionary<string, NativeMenuItem> _pluginTrayItems = new();

    public static IDisposable AppendPluginTrayMenuItem(string id, string label, Action click)
    {
        _mainInstance?.AddTrayMenuItem(id, label, click);
        return new PluginTrayToken(id);
    }

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
        LocalSettings.Save();

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
        await System.Threading.Tasks.Task.Delay(StartMenuAnimationMs);

        StartMenuOverlay.IsVisible = false;
        StartButton.Classes.Set("active", false);
        UiAnimations.Reset(StartMenuCard);
        _startMenuClosing = false;
    }

    private async void OnStartMenuOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
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
        Shell.AgentTaskRequested -= OpenAgentTask;
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
