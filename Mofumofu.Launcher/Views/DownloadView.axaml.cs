using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MofuCore.Config;
using MofuCore.Download;
using MofuCore.Download.CurseForge;
using MofuCore.Download.Minecraft;
using MofuCore.Download.Modrinth;
using MofuCore.Java;
using MofuCore.Tools;
using Mofumofu.Launcher.Controls;
using Mofumofu.Launcher.Mock;
using Mofumofu.Launcher.Models;
using Mofumofu.Launcher.Services;
using Mofumofu.Launcher.Services.Plugins;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views;

public sealed class DownloadIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return new Bitmap(AssetLoader.Open(new Uri(path)));
        }
        catch
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class VersionBadgeBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool background = parameter as string == "bg";
        return value as string switch
        {
            "snapshot" => ThemeTokens.Brush(background ? "NekoDangerSoftBrush" : "NekoDangerBrush"),
            "old_beta" or "old_alpha" => ThemeTokens.Brush(background ? "NekoTextBrush" : "NekoSurfaceBrush"),
            _ => ThemeTokens.Brush(background ? "NekoSuccessSoftBrush" : "NekoSuccessBrush"),
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public partial class DownloadView : UserControl
{
    private const int TaskListExitDelayMs = 160;

    private sealed class TaskCard
    {
        public string TaskId { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string ShortId { get; init; } = string.Empty;
        public DownloadTaskState State { get; set; } = DownloadTaskState.Downloading;
        public Border Root { get; init; } = null!;
        public ProgressBar Bar { get; init; } = null!;
        public TextBlock DetailText { get; init; } = null!;
        public TextBlock StatusText { get; init; } = null!;
        public TextBlock SizeText { get; init; } = null!;
        public Border StatusBadge { get; init; } = null!;
        public TextBlock SpeedText { get; init; } = null!;
        public TextBlock PercentText { get; init; } = null!;
        public Button Toggle { get; init; } = null!;
        public Button Cancel { get; init; } = null!;

        public bool LogOpen;
        public TextBlock LogHint { get; init; } = null!;
        public Border LogPanel { get; init; } = null!;
        public ScrollViewer LogScroll { get; init; } = null!;
        public SelectableTextBlock LogText { get; init; } = null!;
    }

    private const int VersionPageSize = 50;

    private static string StateLabel(DownloadTaskState state) => state switch
    {
        DownloadTaskState.Done => L.T("Download.Task.Status.Done", "已完成"),
        DownloadTaskState.Paused => L.T("Download.Task.Status.Paused", "暂停中"),
        DownloadTaskState.Cancelled => L.T("Download.Task.Status.Cancelled", "已取消"),
        DownloadTaskState.Failed => L.T("Download.Task.Status.Failed", "失败"),
        _ => L.T("Download.Task.Status.Downloading", "下载中"),
    };

    private readonly List<Button> _tabButtons = new();
    private readonly List<Control> _tabPanels = new();

    private static event Action? ExtensionTabRequested;

    public static void OpenExtensionTab() => ExtensionTabRequested?.Invoke();

    private void OnExtensionTabRequested()
    {
        var button = _tabButtons.FirstOrDefault(b => string.Equals(b.Tag as string, "extension", StringComparison.Ordinal));
        if (button is not null) OnTabClick(button, new RoutedEventArgs());
    }

    private readonly Dictionary<string, TaskCard> _taskCards = new();
    private readonly Dictionary<string, MinecraftVersion> _minecraftVersions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ResourceItem, ModrinthProject> _resourceProjects = new();
    private readonly Dictionary<ResourceItem, string> _resourceTypes = new();

    private const int ResourcePageSize = 20;

    private List<GameVersion> _allVersions = [];
    private VendorJavaKind _javaVendorKind = VendorJavaKind.Zulu;
    private IReadOnlyList<VendorJavaCandidate> _javaCandidates = [];
    private List<ModrinthVersion> _detailVersions = [];
    private List<string> _detailGameVersions = [];
    private ModrinthProject? _detailProject;
    private ModrinthVersion? _detailSelectedVersion;
    private string _detailType = "mod";
    private string? _detailGameVersion;
    private bool _detailLoading;
    private bool _detailSuppress;
    private bool _ready;
    private bool _started;
    private int _activeTabIndex;
    private int _versionPage;
    private string _gameDirectory = string.Empty;
    private string _minecraftInstallVersionId = string.Empty;
    private IReadOnlyList<ModLoaderVersionInfo> _minecraftLoaderVersions = [];
    private CancellationTokenSource? _minecraftLoaderCts;
    private bool _minecraftInstallReady;
    private CancellationTokenSource? _versionCts;
    private CancellationTokenSource? _modCts;
    private CancellationTokenSource? _modpackCts;
    private CancellationTokenSource? _shaderCts;
    private CancellationTokenSource? _resourcePackCts;
    private CancellationTokenSource? _detailCts;

    private static string CacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mofumofu.Launcher",
        "cache");

    private static string VersionsCachePath => Path.Combine(CacheDirectory, "versions.json");

    public DownloadView()
    {
        InitializeComponent();

        _gameDirectory = EnsureGameDirectory();
        VersionLoadingOverlay.IsVisible = true;

        _tabButtons.AddRange(new[]
        {
            TabMinecraft, TabMod, TabModpack, TabShader, TabResourcePack, TabJava, TabExtension, TabTasks,
        });
        _tabPanels.AddRange(new Control[]
        {
            MinecraftPanel, ModPanel, ModpackPanel, ShaderPanel,
            ResourcePackPanel, JavaPanel, ExtensionPanel, TasksPanel,
        });

        ExtensionTabRequested += OnExtensionTabRequested;
        DetachedFromVisualTree += (_, _) => ExtensionTabRequested -= OnExtensionTabRequested;

        DownloadTaskService.Added += OnTaskAdded;
        DownloadTaskService.Changed += OnTaskChanged;
        DownloadTaskService.FinishedCleared += OnFinishedTasksCleared;
        foreach (DownloadTaskSnapshot snapshot in DownloadTaskService.Snapshots)
        {
            CreateTaskCard(snapshot);
        }

        ModList.ItemsSource = Array.Empty<ResourceItem>();
        ModpackList.ItemsSource = Array.Empty<ResourceItem>();
        ShaderList.ItemsSource = Array.Empty<ResourceItem>();
        ResourcePackList.ItemsSource = Array.Empty<ResourceItem>();

        VersionPager.PageChanged += page =>
        {
            _versionPage = page;
            RefreshVersions();
        };
        ModPager.PageChanged += page => OnResourcePageChanged("mod", page);
        ModpackPager.PageChanged += page => OnResourcePageChanged("modpack", page);
        ShaderPager.PageChanged += page => OnResourcePageChanged("shader", page);
        ResourcePackPager.PageChanged += page => OnResourcePageChanged("resourcepack", page);

        ShowResourceLoading("mod");
        ShowResourceLoading("modpack");
        ShowResourceLoading("shader");
        ShowResourceLoading("resourcepack");

        RefreshVersions();

        _ready = true;
    }

    private bool TryLoadVersionCache(out DateTimeOffset savedAt)
    {
        savedAt = default;
        try
        {
            if (!File.Exists(VersionsCachePath)) return false;
            VersionCacheFile? cache = JsonSerializer.Deserialize<VersionCacheFile>(
                File.ReadAllText(VersionsCachePath), ServerJsonOptions);
            if (cache is null || cache.Versions.Count == 0) return false;

            _allVersions = cache.Versions
                .Select(entry => new GameVersion(entry.Id, entry.Type, entry.ReleaseDate, entry.Installed, entry.Icon,
                    VersionTypeKey(entry.TypeKey.Length > 0 ? entry.TypeKey : entry.Type)))
                .ToList();
            savedAt = cache.SavedAt;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void WriteVersionCache(IReadOnlyList<GameVersion> versions)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            VersionCacheFile cache = new()
            {
                SavedAt = DateTimeOffset.UtcNow,
                Versions = versions.Select(version => new VersionCacheEntry
                {
                    Id = version.Id,
                    Type = version.Type,
                    TypeKey = version.TypeKey,
                    ReleaseDate = version.ReleaseDate,
                    Installed = version.Installed,
                    Icon = version.Icon,
                }).ToList(),
            };
            string temporary = VersionsCachePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(cache, ServerJsonOptions));
            File.Move(temporary, VersionsCachePath, true);
        }
        catch (Exception)
        {
        }
    }

    private static readonly JsonSerializerOptions ServerJsonOptions = new() { WriteIndented = false };

    private static string VersionIcon(string type) => type == "snapshot"
        ? "avares://Mofumofu.Launcher/Assets/instance-icons/snapshot_version.png"
        : "avares://Mofumofu.Launcher/Assets/instance-icons/vanilla.png";

    private void OnRetryVersionsClick(object? sender, RoutedEventArgs e)
    {
        VersionRetryButton.IsVisible = false;
        VersionOfflineHint.IsVisible = false;
        _ = LoadVersionsAsync();
    }

    private void ShowVersionError(string message)
    {
        VersionOfflineHint.Text = L.T("Download.Version.Error", "获取版本列表失败：{0}（可重试）", message);
        VersionOfflineHint.IsVisible = true;
        VersionRetryButton.IsVisible = true;
        VersionLoadingOverlay.IsVisible = false;
        RefreshVersions();
    }

    private void ShowResourceLoading(string type)
    {
        var (_, _, hint, _, _) = ResourceControls(type);
        hint.Text = L.T("Download.Resource.Loading", "正在加载列表…");
        hint.IsVisible = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeManager.ThemeChanged += OnThemeChanged;
        OnThemeChanged();

        MinecraftInstallPanel.IsVisible = false;
        ResourceDetailPanel.IsVisible = false;
        TabHost.IsVisible = true;

        if (!_started)
        {
            _started = true;

            // 换页时这一帧要先把页面画出来：读缓存、拉列表、建控件这些重活全部排到页面出帧之后，
            // 先让用户看到界面，再慢慢填内容——否则点击和页面出现之间会卡一下。
            Dispatcher.UIThread.Post(() => _ = LoadInitialDataAsync(), DispatcherPriority.Background);
        }
    }

    private async Task LoadInitialDataAsync()
    {
        try
        {
            TryLoadVersionCache(out _);
            if (_allVersions.Count > 0)
            {
                RefreshVersions();
                VersionLoadingOverlay.IsVisible = false;
            }

            // 让页面先完成一次真正的布局和首帧，再开始网络请求。
            await Task.Delay(30);
            if (!IsEffectivelyVisible) return;

            await LoadVersionsAsync();

            foreach (string type in new[] { "mod", "modpack", "shader", "resourcepack" })
            {
                if (!IsEffectivelyVisible) return;
                await LoadResourcesAsync(type);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine("[download] 初始化失败: " + exception.Message);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ThemeManager.ThemeChanged -= OnThemeChanged;
        CancelCts(ref _versionCts);
        CancelCts(ref _minecraftLoaderCts);
        CancelCts(ref _modCts);
        CancelCts(ref _modpackCts);
        CancelCts(ref _shaderCts);
        CancelCts(ref _resourcePackCts);
        CancelCts(ref _detailCts);
        base.OnDetachedFromVisualTree(e);
    }

    private static void CancelCts(ref CancellationTokenSource? source)
    {
        var current = source;
        source = null;
        if (current is null) return;
        try
        {
            current.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnThemeChanged()
    {
        foreach (var card in _taskCards.Values)
        {
            ApplyTaskStatus(card);
        }
    }

    private void OnTaskAdded(DownloadTaskSnapshot snapshot)
    {
        if (!_taskCards.ContainsKey(snapshot.Id))
        {
            CreateTaskCard(snapshot);
        }
    }

    private void OnTaskChanged(DownloadTaskSnapshot snapshot)
    {
        if (_taskCards.TryGetValue(snapshot.Id, out TaskCard? card))
        {
            ApplySnapshot(card, snapshot);
            if (card.LogOpen) RefreshTaskLog(card);
        }
    }

    private async void OnFinishedTasksCleared()
    {
        var finished = _taskCards.Values.Where(c => c.State == DownloadTaskState.Done).ToList();
        if (finished.Count == 0) return;

        // 逐卡淡出后再真正摘掉，避免整列表「唰」地跳一下
        foreach (TaskCard card in finished)
        {
            UiAnimations.FadeSlideOut(card.Root, 0.96, 6, 150);
        }

        if (UiAnimations.Enabled) await Task.Delay(TaskListExitDelayMs);

        foreach (TaskCard card in finished)
        {
            DownloadTaskList.Children.Remove(card.Root);
            _taskCards.Remove(card.TaskId);
        }
    }

    private TaskCard CreateTaskCard(DownloadTaskSnapshot snapshot)
    {
        var detailText = new TextBlock
        {
            Text = snapshot.Detail,
            FontSize = 11,
            Foreground = ThemeTokens.Brush("NekoMutedBrush"),
        };
        var statusText = new TextBlock { Text = StateLabel(snapshot.State), FontSize = 10 };
        var badge = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 2, 7, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = statusText,
        };
        var logHint = new TextBlock
        {
            Text = L.T("Download.Task.LogHint", "日志 ▸"),
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0),
            Foreground = ThemeTokens.Brush("NekoMutedBrush"),
        };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new StackPanel { Spacing = 1, Children =
{
    new TextBlock
    {
        Text = $"#{snapshot.ShortId}  {snapshot.Title}",
        FontSize = 13,
        FontWeight = FontWeight.SemiBold,
        Foreground = ThemeTokens.Brush("NekoAccentBrush"),
    },
    detailText,
} });
        var headerActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { logHint, badge },
        };
        Grid.SetColumn(headerActions, 1);
        header.Children.Add(headerActions);
        ToolTip.SetTip(header, L.T("Download.Task.LogTooltip", "点击卡片展开 / 收起下载日志"));

        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = snapshot.Percent,
            Height = 6,
            Margin = new Thickness(0, 10, 0, 8),
        };

        var sizeText = new TextBlock
        {
            Text = snapshot.SizeText,
            FontSize = 11,
            Foreground = ThemeTokens.Brush("NekoMutedBrush"),
        };
        var speedText = new TextBlock
        {
            Text = snapshot.SpeedText,
            FontSize = 11,
            Foreground = ThemeTokens.Brush("NekoMutedBrush"),
        };
        var percentText = new TextBlock
        {
            Text = $"{snapshot.Percent:0}%",
            FontSize = 11,
            Foreground = ThemeTokens.Brush("NekoAccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var infoRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        infoRow.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { sizeText, speedText },
        });
        Grid.SetColumn(percentText, 1);
        infoRow.Children.Add(percentText);

        var toggle = new Button
        {
            Content = snapshot.State == DownloadTaskState.Done
                ? L.T("Download.Task.ToggleDone", "已完成")
                : L.T("Download.Task.Pause", "暂停"),
            Classes = { "ghost" },
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            IsEnabled = snapshot.State != DownloadTaskState.Done && snapshot.CanPause,
        };
        var cancel = new Button
        {
            Content = L.T("Download.Task.Cancel", "取消"),
            Classes = { "ghost" },
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            IsEnabled = snapshot.State != DownloadTaskState.Done,
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { toggle, cancel },
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        bottom.Children.Add(infoRow);
        Grid.SetColumn(actions, 1);
        bottom.Children.Add(actions);

        var logText = new SelectableTextBlock
        {
            FontFamily = new FontFamily("Consolas, Cascadia Mono, Microsoft YaHei"),
            FontSize = 10.5,
            Foreground = ThemeTokens.Brush("NekoMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
        var logScroll = new ScrollViewer
        {
            Content = logText,
            MaxHeight = 190,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var logPanel = new Border
        {
            IsVisible = false,
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(8),
            Background = ThemeTokens.Brush("NekoFieldSoftBrush"),
            BorderBrush = ThemeTokens.Brush("NekoBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = logScroll,
        };

        var root = new Border
        {
            Classes = { "card-soft" },
            Padding = new Thickness(14),
            Child = new StackPanel
            {
                Spacing = 0,
                Children = { header, bar, bottom, logPanel },
            },
        };

        var card = new TaskCard
        {
            TaskId = snapshot.Id,
            Title = snapshot.Title,
            ShortId = snapshot.ShortId,
            State = snapshot.State,
            Root = root,
            Bar = bar,
            DetailText = detailText,
            StatusText = statusText,
            SizeText = sizeText,
            StatusBadge = badge,
            SpeedText = speedText,
            PercentText = percentText,
            Toggle = toggle,
            Cancel = cancel,
            LogHint = logHint,
            LogPanel = logPanel,
            LogScroll = logScroll,
            LogText = logText,
        };

        toggle.Tag = card;
        cancel.Tag = card;
        toggle.Click += OnTaskToggleClick;
        cancel.Click += OnTaskCancelClick;

        header.Cursor = new Cursor(StandardCursorType.Hand);
        header.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            ToggleTaskLog(card);
        };

        ApplyTaskStatus(card);
        _taskCards[card.TaskId] = card;
        DownloadTaskList.Children.Add(root);
        UiAnimations.FadeSlideIn(root, 0.97, 8, 200);
        return card;
    }

    private void ToggleTaskLog(TaskCard card)
    {
        card.LogOpen = !card.LogOpen;
        _ = UiAnimations.SetExpandedAsync(card.LogPanel, card.LogOpen, 180);
        card.LogHint.Text = card.LogOpen
            ? L.T("Download.Task.LogHintOpen", "日志 ▾")
            : L.T("Download.Task.LogHint", "日志 ▸");
        if (card.LogOpen) RefreshTaskLog(card);
    }

    private void RefreshTaskLog(TaskCard card)
    {
        IReadOnlyList<string> lines = DownloadTaskService.GetLog(card.TaskId);
        card.LogText.Text = lines.Count == 0
            ? L.T("Download.Task.LogEmpty", "暂无日志")
            : string.Join("\n", lines);
        card.LogScroll.ScrollToEnd();
    }

    private void ApplySnapshot(TaskCard card, DownloadTaskSnapshot snapshot)
    {
        card.State = snapshot.State;
        card.Bar.Value = snapshot.Percent;
        card.PercentText.Text = $"{snapshot.Percent:0}%";
        card.DetailText.Text = snapshot.Detail;
        card.SizeText.Text = snapshot.SizeText;
        card.SpeedText.Text = snapshot.SpeedText;
        switch (snapshot.State)
        {
            case DownloadTaskState.Done:
                card.Toggle.Content = L.T("Download.Task.ToggleDone", "已完成");
                card.Toggle.IsEnabled = false;
                card.Cancel.IsEnabled = false;
                break;
            case DownloadTaskState.Paused:
                card.Toggle.Content = L.T("Download.Task.Resume", "继续");
                break;
            case DownloadTaskState.Downloading:
                card.Toggle.Content = L.T("Download.Task.Pause", "暂停");
                break;
            case DownloadTaskState.Cancelled or DownloadTaskState.Failed:
                card.Toggle.IsEnabled = false;
                card.Cancel.IsEnabled = false;
                break;
        }

        ApplyTaskStatus(card);
    }

    private void ApplyTaskStatus(TaskCard card)
    {
        card.StatusText.Text = StateLabel(card.State);
        var (background, foreground) = card.State switch
        {
            DownloadTaskState.Done => (ThemeTokens.Brush("NekoSuccessSoftBrush"), ThemeTokens.Brush("NekoSuccessBrush")),
            DownloadTaskState.Paused => (ThemeTokens.Brush("NekoWarningSoftBrush"), ThemeTokens.Brush("NekoWarningBrush")),
            DownloadTaskState.Cancelled or DownloadTaskState.Failed => (ThemeTokens.Brush("NekoDangerSoftBrush"), ThemeTokens.Brush("NekoDangerBrush")),
            _ => (ThemeTokens.Brush("NekoAccentSoftBrush"), ThemeTokens.Brush("NekoAccentBrush")),
        };
        card.StatusBadge.Background = background;
        card.StatusText.Foreground = foreground;
    }

    private void OnTaskToggleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TaskCard card }) return;

        switch (card.State)
        {
            case DownloadTaskState.Done:
                Shell.Toast(L.T("Download.Task.DoneToast", "「{0}」已完成，无需操作", card.TaskId));
                return;
            case DownloadTaskState.Cancelled:
                Shell.Toast(L.T("Download.Task.CancelledToast", "「{0}」已取消，请重新创建任务", card.TaskId));
                return;
            case DownloadTaskState.Failed:
                Shell.Toast(L.T("Download.Task.FailedToast", "「{0}」下载失败，请重试", card.TaskId));
                return;
            case DownloadTaskState.Paused:
                DownloadTaskService.TrySetPaused(card.TaskId, paused: false);
                Shell.Toast(L.T("Download.Task.Resumed", "已继续下载「{0}」", card.Title));
                return;
            default:
                DownloadTaskService.TrySetPaused(card.TaskId, paused: true);
                Shell.Toast(L.T("Download.Task.PausedToast", "已暂停「{0}」", card.Title));
                return;
        }
    }

    private void OnTaskCancelClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TaskCard card }) return;

        DownloadTaskService.TryCancel(card.TaskId);
        Shell.Toast(L.T("Download.Task.CancelToast", "已取消下载「{0}」", card.Title));
    }

    private void OnClearFinishedTasksClick(object? sender, RoutedEventArgs e)
    {
        var finishedCount = _taskCards.Values.Count(c => c.State == DownloadTaskState.Done);
        DownloadTaskService.RemoveFinished();

        Shell.Toast(finishedCount == 0
            ? L.T("Download.Task.ClearNone", "没有已完成的任务需要清理")
            : L.T("Download.Task.Cleared", "已清理 {0} 个已完成任务", finishedCount));
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        Shell.Toast(L.T("Download.Toast.Refreshing", "正在刷新下载源与版本列表…"));
        await Task.WhenAll(LoadVersionsAsync(), LoadResourcesAsync("mod"), LoadResourcesAsync("modpack"));
    }

    private void OnTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tab }) return;

        if (MinecraftInstallPanel.IsVisible || ResourceDetailPanel.IsVisible)
        {
            CancelCts(ref _minecraftLoaderCts);
            MinecraftInstallPanel.IsVisible = false;
            ResourceDetailPanel.IsVisible = false;
            TabHost.IsVisible = true;
        }

        var next = -1;
        for (var i = 0; i < _tabButtons.Count; i++)
        {
            if (string.Equals((string?)_tabButtons[i].Tag, tab, StringComparison.Ordinal))
            {
                next = i;
                break;
            }
        }
        if (next < 0 || next == _activeTabIndex) return;

        var direction = next >= _activeTabIndex ? 1 : -1;
        _activeTabIndex = next;

        for (var i = 0; i < _tabButtons.Count; i++)
        {
            var active = i == next;
            _tabButtons[i].Classes.Set("active", active);
            _tabPanels[i].IsVisible = active;
        }

        if (_tabPanels[next] is ScrollViewer scroll)
        {
            scroll.Offset = default;
        }
        AnimateTabSwitch(_tabPanels[next], direction);

        if (string.Equals(tab, "java", StringComparison.Ordinal))
        {
            JavaCandidateScroll.Offset = default;
            _ = LoadJavaCandidatesAsync();
        }

        if (string.Equals(tab, "extension", StringComparison.Ordinal))
        {
            _ = RefreshExtensionEntriesAsync();
        }
    }

    private async Task LoadVersionsAsync()
    {
        CancelCts(ref _versionCts);
        _versionCts = new CancellationTokenSource();
        var token = _versionCts.Token;
        VersionLoadingOverlay.IsVisible = _allVersions.Count == 0;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        // 默认 BMCL 源本机可能卡 ~29s；后端已加 8s 单源超时 + Mojang 回退，45s 只是兜底。
        timeout.CancelAfter(TimeSpan.FromSeconds(45));

        try
        {
            IReadOnlyList<MinecraftVersion> versions = await MinecraftDownload.GetVersionsAsync(timeout.Token);
            if (token.IsCancellationRequested) return;

            List<GameVersion> list = versions.Select(ToGameVersion).ToList();
            Dispatcher.UIThread.Post(() =>
            {
                _minecraftVersions.Clear();
                foreach (MinecraftVersion version in versions)
                {
                    _minecraftVersions[version.Id] = version;
                }

                if (list.Count > 0)
                {
                    _allVersions = list;
                    _versionPage = 0;
                }

                VersionOfflineHint.IsVisible = false;
                VersionRetryButton.IsVisible = false;
                VersionLoadingOverlay.IsVisible = false;
                RefreshVersions();
            });

            if (list.Count > 0)
            {
                WriteVersionCache(list);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Dispatcher.UIThread.Post(() => ShowVersionError(
                L.T("Download.Version.Timeout", "请求超时")));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => ShowVersionError(ex.Message));
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_allVersions.Count > 0) VersionLoadingOverlay.IsVisible = false;
            });
        }
    }

    private GameVersion ToGameVersion(MinecraftVersion version)
    {
        bool installed = _gameDirectory.Length > 0
                         && Directory.Exists(Path.Combine(_gameDirectory, "versions", version.Id));
        return new GameVersion(
            version.Id,
            LocalizedVersionType(version.Type),
            version.ReleaseTime.LocalDateTime.ToString("yyyy-MM-dd"),
            installed,
            VersionIcon(version.Type),
            version.Type);
    }

    private static string VersionTypeKey(string value) => value switch
    {
        "release" or "snapshot" or "old_beta" or "old_alpha" => value,
        _ when value == L.T("Download.Version.Type.Release", "正式版") => "release",
        _ when value == L.T("Download.Version.Type.Snapshot", "快照") => "snapshot",
        _ when value == L.T("Download.Version.Type.Old", "远古") => "old_alpha",
        _ => "release",
    };

    private static string LocalizedVersionType(string type) => type switch
    {
        "release" => L.T("Download.Version.Type.Release", "正式版"),
        "snapshot" => L.T("Download.Version.Type.Snapshot", "快照"),
        "old_beta" or "old_alpha" => L.T("Download.Version.Type.Old", "远古"),
        _ => type,
    };

    private void RefreshVersions()
    {
        var query = VersionSearchBox.Text?.Trim() ?? string.Empty;

        IEnumerable<GameVersion> items = _allVersions;
        if (query.Length > 0)
        {
            items = items.Where(v => v.Id.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        string release = L.T("Download.Version.Type.Release", "正式版");
        string snapshot = L.T("Download.Version.Type.Snapshot", "快照");
        string old = L.T("Download.Version.Type.Old", "远古");
        bool showRelease = ShowReleaseCheck.IsChecked == true;
        bool showSnapshot = ShowSnapshotCheck.IsChecked == true;
        bool showOld = ShowOldCheck.IsChecked == true;

        items = items.Where(v =>
            v.Type switch
            {
                _ when string.Equals(v.Type, release, StringComparison.Ordinal) => showRelease,
                _ when string.Equals(v.Type, snapshot, StringComparison.Ordinal) => showSnapshot,
                _ when string.Equals(v.Type, old, StringComparison.Ordinal) => showOld,
                _ => true,
            });

        var filtered = items.ToList();
        int pageCount = Math.Max(1, (filtered.Count + VersionPageSize - 1) / VersionPageSize);
        _versionPage = Math.Clamp(_versionPage, 0, pageCount - 1);
        var pageItems = filtered
            .Skip(_versionPage * VersionPageSize)
            .Take(VersionPageSize)
            .ToList();

        VersionList.ItemsSource = pageItems;
        VersionCountText.Text = L.T("Download.Version.Count", "共 {0} 个版本", filtered.Count);
        VersionPager.SetPageCount(pageCount, _versionPage);
        VersionListScroll.Offset = new Vector(0, 0);
        AnimateListRefresh(VersionList);
    }

    private void OnVersionSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        _versionPage = 0;
        RefreshVersions();
    }

    private void OnVersionFilterChanged(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _versionPage = 0;
        RefreshVersions();
    }

    private void OnDownloadVersionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string versionId })
        {
            OpenMinecraftInstallPanel(versionId);
        }
    }

    private static readonly ModLoaderKind[] LoaderChoices =
    [
        ModLoaderKind.Vanilla,
        ModLoaderKind.Fabric,
        ModLoaderKind.Quilt,
        ModLoaderKind.Forge,
        ModLoaderKind.NeoForge,
        ModLoaderKind.OptiFine,
    ];

    private void OpenMinecraftInstallPanel(string versionId)
    {
        CancelCts(ref _minecraftLoaderCts);
        _minecraftInstallVersionId = versionId;

        _minecraftInstallReady = false;
        MinecraftLoaderCombo.ItemsSource = LoaderChoices
            .Select(kind => kind == ModLoaderKind.Vanilla
                ? L.T("Download.Minecraft.Vanilla", "原版")
                : ModLoaderInstaller.DisplayName(kind))
            .ToList();
        MinecraftLoaderCombo.SelectedIndex = 0;
        MinecraftLoaderVersionCombo.ItemsSource = null;
        MinecraftLoaderVersionRow.IsVisible = false;
        MinecraftLoaderLoadingText.IsVisible = false;
        _minecraftLoaderVersions = [];
        MinecraftInstanceNameBox.Text = versionId;
        MinecraftInstanceNameBox.IsReadOnly = true;
        MinecraftInstallStatus.Text = string.Empty;
        MinecraftInstallButton.IsEnabled = true;
        _minecraftInstallReady = true;

        MinecraftInstallPanel.IsVisible = true;
        UiAnimations.Reset(MinecraftInstallPanel);
        UiAnimations.FadeSlideIn(MinecraftInstallPanel, 0.99, 6, 170);
        TabHost.IsVisible = false;
    }

    private void OnCloseMinecraftInstallClick(object? sender, RoutedEventArgs e)
    {
        CancelCts(ref _minecraftLoaderCts);
        MinecraftInstallPanel.IsVisible = false;
        TabHost.IsVisible = true;
    }

    private async void OnMinecraftLoaderChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_minecraftInstallReady) return;

        ModLoaderKind kind = CurrentMinecraftLoaderKind();
        CancelCts(ref _minecraftLoaderCts);
        _minecraftLoaderVersions = [];
        MinecraftLoaderVersionCombo.ItemsSource = null;
        MinecraftLoaderVersionRow.IsVisible = kind != ModLoaderKind.Vanilla;
        MinecraftInstallStatus.Text = string.Empty;

        if (kind == ModLoaderKind.Vanilla)
        {
            MinecraftLoaderLoadingText.IsVisible = false;
            MinecraftInstanceNameBox.Text = _minecraftInstallVersionId;
            MinecraftInstanceNameBox.IsReadOnly = true;
            MinecraftInstallButton.IsEnabled = true;
            return;
        }

        MinecraftInstanceNameBox.IsReadOnly = false;
        MinecraftInstanceNameBox.Text = DefaultMinecraftInstanceName(kind, string.Empty);
        MinecraftLoaderVersionRow.IsVisible = true;
        MinecraftLoaderLoadingText.IsVisible = true;
        MinecraftInstallButton.IsEnabled = false;
        await LoadMinecraftLoaderVersionsAsync(kind);
    }

    private void OnMinecraftLoaderVersionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_minecraftInstallReady) return;
        ModLoaderKind kind = CurrentMinecraftLoaderKind();
        if (kind == ModLoaderKind.Vanilla) return;

        MinecraftInstallStatus.Text = string.Empty;
        ModLoaderVersionInfo? version = CurrentMinecraftLoaderVersion();
        MinecraftInstanceNameBox.Text = DefaultMinecraftInstanceName(kind, version?.LoaderVersion ?? string.Empty);
    }

    private async Task LoadMinecraftLoaderVersionsAsync(ModLoaderKind kind)
    {
        CancellationTokenSource cts = new();
        _minecraftLoaderCts = cts;
        try
        {
            IReadOnlyList<ModLoaderVersionInfo> versions = await ModLoaderInstaller.GetVersionsAsync(
                kind, _minecraftInstallVersionId, cts.Token);
            if (cts.IsCancellationRequested) return;

            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested) return;
                _minecraftLoaderVersions = versions;
                MinecraftLoaderLoadingText.IsVisible = false;
                MinecraftLoaderVersionCombo.ItemsSource = versions
                    .Select(version => version.IsStable
                        ? version.DisplayName
                        : version.DisplayName + " " + L.T("Download.Minecraft.Install.TestBuild", "（测试版）"))
                    .ToList();
                MinecraftLoaderVersionCombo.SelectedIndex = versions.Count > 0 ? 0 : -1;
                MinecraftInstallButton.IsEnabled = versions.Count > 0;
                if (versions.Count == 0)
                {
                    MinecraftInstallStatus.Text = L.T("Download.Minecraft.Install.NoLoaderVersion",
                        "没有找到适配 Minecraft {0} 的 {1} 版本。", _minecraftInstallVersionId,
                        ModLoaderInstaller.DisplayName(kind));
                }
                else
                {
                    MinecraftInstanceNameBox.Text = DefaultMinecraftInstanceName(
                        kind, versions[0].LoaderVersion);
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested) return;
                MinecraftLoaderLoadingText.IsVisible = false;
                MinecraftInstallButton.IsEnabled = false;
                MinecraftInstallStatus.Text = L.T("Download.Minecraft.Install.LoaderFailed",
                    "获取加载器版本失败：{0}", ex.Message);
            });
        }
        finally
        {
            if (ReferenceEquals(_minecraftLoaderCts, cts)) _minecraftLoaderCts = null;
            cts.Dispose();
        }
    }

    private ModLoaderKind CurrentMinecraftLoaderKind()
    {
        int index = MinecraftLoaderCombo.SelectedIndex;
        return index >= 0 && index < LoaderChoices.Length ? LoaderChoices[index] : ModLoaderKind.Vanilla;
    }

    private ModLoaderVersionInfo? CurrentMinecraftLoaderVersion()
    {
        int index = MinecraftLoaderVersionCombo.SelectedIndex;
        return index >= 0 && index < _minecraftLoaderVersions.Count
            ? _minecraftLoaderVersions[index]
            : null;
    }

    private string DefaultMinecraftInstanceName(ModLoaderKind kind, string loaderVersion)
    {
        if (kind == ModLoaderKind.Vanilla) return _minecraftInstallVersionId;
        if (loaderVersion.Length == 0)
        {
            return kind switch
            {
                ModLoaderKind.Fabric => $"fabric-loader-{_minecraftInstallVersionId}",
                ModLoaderKind.Quilt => $"quilt-loader-{_minecraftInstallVersionId}",
                ModLoaderKind.NeoForge => $"neoforge-{_minecraftInstallVersionId}",
                ModLoaderKind.OptiFine => $"OptiFine_{_minecraftInstallVersionId}",
                _ => $"forge-{_minecraftInstallVersionId}",
            };
        }

        return ModLoaderInstaller.DefaultInstanceName(kind, _minecraftInstallVersionId, loaderVersion);
    }

    private async void OnMinecraftInstallClick(object? sender, RoutedEventArgs e)
    {
        if (!_minecraftInstallReady) return;

        ModLoaderKind kind = CurrentMinecraftLoaderKind();
        ModLoaderVersionInfo? loader = kind == ModLoaderKind.Vanilla ? null : CurrentMinecraftLoaderVersion();
        if (kind != ModLoaderKind.Vanilla && loader is null)
        {
            Shell.Toast(L.T("Download.Minecraft.Install.PickLoader", "请先选择加载器版本"));
            return;
        }

        string instanceName = kind == ModLoaderKind.Vanilla
            ? _minecraftInstallVersionId
            : (MinecraftInstanceNameBox.Text ?? string.Empty).Trim();
        if (instanceName.Length == 0)
        {
            instanceName = DefaultMinecraftInstanceName(kind, loader?.LoaderVersion ?? string.Empty);
        }

        try
        {
            MinecraftDownload.ValidateVersionId(instanceName);
            if (kind != ModLoaderKind.Vanilla &&
                string.Equals(instanceName, _minecraftInstallVersionId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    L.T("Download.Minecraft.Install.SameName", "实例名称不能与原版版本号相同。"));
            }
        }
        catch (Exception ex)
        {
            MinecraftInstallStatus.Text = ex.Message;
            return;
        }

        string versionId = _minecraftInstallVersionId;
        MinecraftInstallPanel.IsVisible = false;
        TabHost.IsVisible = true;

        string title = kind == ModLoaderKind.Vanilla
            ? $"Minecraft {versionId}"
            : $"Minecraft {versionId} · {ModLoaderInstaller.DisplayName(kind)} {loader!.LoaderVersion}";

        var task = DownloadTaskService.Start(title,
            L.T("Download.Version.Preparing", "准备下载"), canPause: true);

        try
        {
            string gameDirectory = EnsureGameDirectory();

            string installDirectory = Path.Combine(
                gameDirectory, "versions", kind == ModLoaderKind.Vanilla ? versionId : instanceName);
            if (!Directory.Exists(installDirectory)) task.RegisterCleanup(installDirectory);
            task.Log(L.T("Download.Log.TargetDir", "安装目录：{0}", installDirectory));
            if (kind == ModLoaderKind.Vanilla)
            {
                var progress = new Progress<MinecraftInstallProgress>(value => task.Report(
                    value.Percentage,
                    string.IsNullOrWhiteSpace(value.Detail) ? value.StageName : $"{value.StageName} · {value.Detail}",
                    value.BytesPerSecond > 0 ? MemoryRead.FormatBytes((long)value.BytesPerSecond) + "/s" : null,
                    value.TotalBytes > 0
                        ? $"{MemoryRead.FormatBytes(value.CompletedBytes)} / {MemoryRead.FormatBytes(value.TotalBytes)}"
                        : null));

                var download = new MinecraftDownload(gameDirectory);
                if (_minecraftVersions.TryGetValue(versionId, out MinecraftVersion? version))
                {
                    await download.InstallAsync(version, progress, task.Token);
                }
                else
                {
                    string? metadataUrl = await MinecraftDownload.FindVersionMetadataUrlAsync(
                        versionId, task.Token);
                    if (string.IsNullOrWhiteSpace(metadataUrl))
                    {
                        throw new InvalidOperationException(L.T("Download.Version.NoMetadata",
                            "版本清单中没有找到该版本的元数据地址。"));
                    }

                    await download.InstallAsync(versionId, metadataUrl, progress, task.Token);
                }
            }
            else
            {
                var progress = new Progress<ModLoaderInstallProgress>(value => task.Report(
                    value.Percentage,
                    value.StageName,
                    value.BytesPerSecond > 0 ? MemoryRead.FormatBytes((long)value.BytesPerSecond) + "/s" : null,
                    value.TotalBytes > 0
                        ? $"{MemoryRead.FormatBytes(value.CompletedBytes)} / {MemoryRead.FormatBytes(value.TotalBytes)}"
                        : null));
                await ModLoaderInstaller.InstallAsync(
                    loader!, gameDirectory, instanceName, progress, task.Token);
            }

            task.Complete();
            GameState.SetVersion(instanceName);
            RefreshVersions();
            LaunchBarRefresh.Request();
            Shell.Toast(L.T("Download.Version.InstallDone", "Minecraft {0} 安装完成", instanceName));
        }
        catch (OperationCanceledException)
        {
            task.MarkCancelled();
            Shell.Toast(L.T("Download.Version.CancelDone", "已取消下载 Minecraft {0}", versionId));
        }
        catch (Exception ex)
        {
            task.Fail(ex.Message);
            Shell.Toast(L.T("Download.Version.Failed", "下载 Minecraft {0} 失败：{1}", versionId, ex.Message));
        }
    }

    private void OnResourceSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_ready) return;

        if (ReferenceEquals(sender, ModSearchBox))
        {
            _ = SearchResourcesAsync("mod", true);
        }
        else if (ReferenceEquals(sender, ModpackSearchBox))
        {
            _ = SearchResourcesAsync("modpack", true);
        }
        else if (ReferenceEquals(sender, ShaderSearchBox))
        {
            _ = SearchResourcesAsync("shader", true);
        }
        else if (ReferenceEquals(sender, ResourcePackSearchBox))
        {
            _ = SearchResourcesAsync("resourcepack", true);
        }
    }

    private async Task LoadResourcesAsync(string type)
    {
        CancellationTokenSource cts = ReplaceResourceCts(type);
        await SearchResourcesCoreAsync(type, SearchBoxText(type), cts.Token, false, 0);
    }

    private async Task SearchResourcesAsync(string type, bool debounce)
    {
        CancellationTokenSource cts = ReplaceResourceCts(type);
        await SearchResourcesCoreAsync(type, SearchBoxText(type), cts.Token, debounce, 0);
    }

    private void OnResourcePageChanged(string type, int page)
    {
        CancellationTokenSource cts = ReplaceResourceCts(type);
        _ = SearchResourcesCoreAsync(type, SearchBoxText(type), cts.Token, false, page);
    }

    private CancellationTokenSource ReplaceResourceCts(string type)
    {
        var cts = new CancellationTokenSource();
        switch (type)
        {
            case "modpack":
                CancelCts(ref _modpackCts);
                _modpackCts = cts;
                break;
            case "shader":
                CancelCts(ref _shaderCts);
                _shaderCts = cts;
                break;
            case "resourcepack":
                CancelCts(ref _resourcePackCts);
                _resourcePackCts = cts;
                break;
            default:
                CancelCts(ref _modCts);
                _modCts = cts;
                break;
        }

        return cts;
    }

    private string? SearchBoxText(string type) => type switch
    {
        "modpack" => ModpackSearchBox.Text,
        "shader" => ShaderSearchBox.Text,
        "resourcepack" => ResourcePackSearchBox.Text,
        _ => ModSearchBox.Text,
    };

    private (ItemsControl List, TextBlock Count, TextBlock Hint, MofuPager Pager, ScrollViewer Scroll)
        ResourceControls(string type) => type switch
        {
            "modpack" => (ModpackList, ModpackCountText, ModpackOfflineHint, ModpackPager, ModpackListScroll),
            "shader" => (ShaderList, ShaderCountText, ShaderOfflineHint, ShaderPager, ShaderListScroll),
            "resourcepack" => (ResourcePackList, ResourcePackCountText, ResourcePackOfflineHint,
                ResourcePackPager, ResourcePackListScroll),
            _ => (ModList, ModCountText, ModOfflineHint, ModPager, ModListScroll),
        };

    private async Task SearchResourcesCoreAsync(string type, string? query, CancellationToken token, bool debounce, int page)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            // 输入法连续输入时合并请求
            if (debounce) await Task.Delay(350, timeout.Token);

            int offset = Math.Max(0, page) * ResourcePageSize;
            ModrinthSearchResult result;
            if (string.IsNullOrWhiteSpace(query))
            {
                result = await ModrinthResourceSearch.BrowseAsync(
                    type, null, null, ResourcePageSize, offset, "downloads", timeout.Token);
            }
            else
            {
                string keyword = ChineseResourceName.ToSearchKeyword(query);
                result = await ModrinthResourceSearch.SearchPageAsync(
                    keyword, type, null, null, ResourcePageSize, offset, "relevance", timeout.Token);
            }
            if (timeout.IsCancellationRequested) return;

            Dispatcher.UIThread.Post(() => ApplyResources(type, result.Hits, result.TotalHits, page));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Dispatcher.UIThread.Post(() => ShowResourceOffline(type,
                L.T("Download.Resource.Timeout", "请求超时")));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => ShowResourceOffline(type, ex.Message));
        }
    }

    private void ApplyResources(string type, IReadOnlyList<ModrinthProject> projects, int totalHits, int page)
    {
        List<ResourceItem> items = projects.Select(project => ToResourceItem(project, type)).ToList();
        for (var index = 0; index < items.Count && index < projects.Count; index++)
        {
            _resourceProjects[items[index]] = projects[index];
            _resourceTypes[items[index]] = type;
        }

        var (list, count, hint, pager, scroll) = ResourceControls(type);
        int pageCount = Math.Max(1, (totalHits + ResourcePageSize - 1) / ResourcePageSize);
        list.ItemsSource = items;
        count.Text = L.T("Download.Resource.Count", "共 {0} 项", totalHits);
        hint.IsVisible = false;
        pager.SetPageCount(pageCount, page);
        scroll.Offset = new Vector(0, 0);
        AnimateListRefresh(list);
    }

    private void ShowResourceOffline(string type, string message)
    {
        var (_, _, hint, pager, _) = ResourceControls(type);
        hint.Text = type switch
        {
            "modpack" => L.T("Download.Resource.OfflineModpack", "无法获取整合包列表（离线或网络错误），请稍后重试"),
            "shader" => L.T("Download.Resource.OfflineShader", "无法获取光影包列表（离线或网络错误），请稍后重试"),
            "resourcepack" => L.T("Download.Resource.OfflineResourcePack", "无法获取材质包列表（离线或网络错误），请稍后重试"),
            _ => L.T("Download.Resource.OfflineMod", "无法获取 Mod 列表（离线或网络错误），请稍后重试"),
        };
        hint.IsVisible = true;
        pager.SetPageCount(1, 0);
        Shell.Toast(L.T("Download.Resource.SearchFailed", "Modrinth 搜索失败：{0}", message));
    }

    private static ResourceItem ToResourceItem(ModrinthProject project, string type)
    {
        string icon = type switch
        {
            "modpack" => "IconFluentCube",
            "shader" => "IconFluentSparkle",
            "resourcepack" => "IconFluentImage",
            _ => "IconFluentBox",
        };
        string author = string.IsNullOrWhiteSpace(project.Slug) ? "Modrinth" : project.Slug;
        return new ResourceItem(
            ChineseResourceName.Display(project.Title, project.Slug),
            author,
            project.Description,
            project.DownloadsDisplay,
            project.FollowsDisplay,
            project.TypeDisplay,
            icon,
            ResourceBadges(project, type));
    }

    private static readonly string[] LoaderOrder =
        ["fabric", "neoforge", "forge", "quilt", "liteloader", "rift"];

    private static IReadOnlyList<string> ResourceBadges(ModrinthProject project, string type)
    {
        const int categoryLimit = 2;
        List<string> badges = [];
        List<string> categories = project.DisplayCategories.Count > 0
            ? project.DisplayCategories
            : project.Categories;

        foreach (string category in categories)
        {
            if (badges.Count >= categoryLimit) break;
            if (LoaderOrder.Contains(category, StringComparer.OrdinalIgnoreCase)) continue;
            string display = LocalizedModCategory(category);
            if (!badges.Contains(display)) badges.Add(display);
        }

        if (type is "mod" or "modpack")
        {
            foreach (string loader in LoaderOrder)
            {
                if (project.Categories.Contains(loader, StringComparer.OrdinalIgnoreCase))
                    badges.Add(LoaderBadge(loader));
            }
        }

        return badges;
    }

    private static string LoaderBadge(string loader) => loader switch
    {
        "fabric" => "Fabric",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        "quilt" => "Quilt",
        "liteloader" => "LiteLoader",
        "rift" => "Rift",
        _ => loader,
    };

    private static string LocalizedModCategory(string category) => category switch
    {
        "adventure" => L.T("Download.Resource.Category.Adventure", "冒险"),
        "cursed" => L.T("Download.Resource.Category.Cursed", "诅咒"),
        "decoration" => L.T("Download.Resource.Category.Decoration", "装饰"),
        "economy" => L.T("Download.Resource.Category.Economy", "经济"),
        "equipment" => L.T("Download.Resource.Category.Equipment", "装备"),
        "food" => L.T("Download.Resource.Category.Food", "食物"),
        "game-mechanics" => L.T("Download.Resource.Category.GameMechanics", "游戏机制"),
        "library" => L.T("Download.Resource.Category.Library", "前置库"),
        "magic" => L.T("Download.Resource.Category.Magic", "魔法"),
        "management" => L.T("Download.Resource.Category.Management", "管理"),
        "minigame" => L.T("Download.Resource.Category.Minigame", "小游戏"),
        "mobs" => L.T("Download.Resource.Category.Mobs", "生物"),
        "optimization" => L.T("Download.Resource.Category.Optimization", "优化"),
        "social" => L.T("Download.Resource.Category.Social", "社交"),
        "storage" => L.T("Download.Resource.Category.Storage", "存储"),
        "technology" => L.T("Download.Resource.Category.Technology", "科技"),
        "transportation" => L.T("Download.Resource.Category.Transportation", "交通"),
        "utility" => L.T("Download.Resource.Category.Utility", "实用"),
        "worldgen" => L.T("Download.Resource.Category.WorldGen", "世界生成"),
        "atmosphere" => L.T("Download.Resource.Category.Atmosphere", "大气"),
        "audio" => L.T("Download.Resource.Category.Audio", "音频"),
        "blocks" => L.T("Download.Resource.Category.Blocks", "方块"),
        "bloom" => L.T("Download.Resource.Category.Bloom", "泛光"),
        "colored-lighting" => L.T("Download.Resource.Category.ColoredLighting", "彩色光照"),
        "combat" => L.T("Download.Resource.Category.Combat", "战斗"),
        "core-shaders" => L.T("Download.Resource.Category.CoreShaders", "核心光影"),
        "entities" => L.T("Download.Resource.Category.Entities", "实体"),
        "environment" => L.T("Download.Resource.Category.Environment", "环境"),
        "fantasy" => L.T("Download.Resource.Category.Fantasy", "奇幻"),
        "foliage" => L.T("Download.Resource.Category.Foliage", "植被"),
        "fonts" => L.T("Download.Resource.Category.Fonts", "字体"),
        "gui" => L.T("Download.Resource.Category.Gui", "GUI"),
        "high" => L.T("Download.Resource.Category.High", "高配"),
        "items" => L.T("Download.Resource.Category.Items", "物品"),
        "locale" => L.T("Download.Resource.Category.Locale", "语言"),
        "low" => L.T("Download.Resource.Category.Low", "低配"),
        "medium" => L.T("Download.Resource.Category.Medium", "中配"),
        "modded" => L.T("Download.Resource.Category.Modded", "模组"),
        "models" => L.T("Download.Resource.Category.Models", "模型"),
        "path-tracing" => L.T("Download.Resource.Category.PathTracing", "光追"),
        "pbr" => L.T("Download.Resource.Category.Pbr", "PBR"),
        "potato" => L.T("Download.Resource.Category.Potato", "土豆机"),
        "realistic" => L.T("Download.Resource.Category.Realistic", "写实"),
        "reflections" => L.T("Download.Resource.Category.Reflections", "反射"),
        "semi-realistic" => L.T("Download.Resource.Category.SemiRealistic", "半写实"),
        "shadows" => L.T("Download.Resource.Category.Shadows", "阴影"),
        "simplified" => L.T("Download.Resource.Category.Simplified", "简化"),
        "themed" => L.T("Download.Resource.Category.Themed", "主题"),
        "tweaks" => L.T("Download.Resource.Category.Tweaks", "微调"),
        "vanilla-like" => L.T("Download.Resource.Category.VanillaLike", "原版风"),
        _ => CapitalizedCategory(category),
    };

    private static string CapitalizedCategory(string category)
    {
        string text = category.Replace('-', ' ');
        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }

    private async void OnResourceIconLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Image { Tag: ResourceItem item } image) return;
        if (!_resourceProjects.TryGetValue(item, out ModrinthProject? project)) return;

        if (string.IsNullOrWhiteSpace(project.IconUrl))
        {
            IconPlaceholder.SetVisible(image, true);
            return;
        }

        try
        {
            Bitmap? bitmap = await ResourceIconCache.LoadAsync(project.IconUrl, $"modrinth:{project.ProjectId}");
            if (bitmap is null)
            {
                IconPlaceholder.SetVisible(image, true);
                return;
            }

            image.Source = bitmap;
            IconPlaceholder.SetVisible(image, false);
        }
        catch (Exception)
        {
            IconPlaceholder.SetVisible(image, true);
        }
    }

    private async Task InstallResourceAsync(
        ModrinthProject project,
        string type,
        ModrinthVersion version,
        string? gameVersion,
        string? loader,
        string displayName,
        string? targetInstanceId = null)
    {
        string gameDirectory = EnsureGameDirectory();
        var task = DownloadTaskService.Start(displayName,
            L.T("Download.Resource.InstallDetail", "正在准备安装…"));

        try
        {
            var progress = new Progress<ModrinthDownloadProgress>(value =>
            {
                double percent = value.Total > 0
                    ? Math.Clamp(value.Completed * 100.0 / value.Total, 0, 100)
                    : 0;
                task.Report(percent, detail: value.Detail);
            });

            if (type == "modpack")
            {
                await InstallModpackAsync(version, gameDirectory, progress, task.Token, task);
                LaunchBarRefresh.Request();
            }
            else
            {
                string contentRoot = gameDirectory;
                if (!string.IsNullOrWhiteSpace(targetInstanceId) && YamlRead.DefaultVersionIsolation)
                {
                    contentRoot = Path.Combine(gameDirectory, "versions", targetInstanceId);
                }

                string targetDirectory = Path.Combine(contentRoot, type switch
                {
                    "resourcepack" => "resourcepacks",
                    "shader" => "shaderpacks",
                    _ => "mods",
                });
                Directory.CreateDirectory(targetDirectory);

                foreach (ModrinthVersionFile file in version.Files)
                {
                    string targetPath = Path.Combine(targetDirectory, file.Filename);
                    if (!File.Exists(targetPath)) task.RegisterCleanup(targetPath);
                }

                await ModrinthResourceDownload.DownloadWithDependenciesAsync(
                    version.Id,
                    targetDirectory,
                    includeDependencies: type == "mod",
                    gameVersion,
                    loader,
                    progress,
                    task.Token);
            }

            task.Complete();
            Shell.Toast(L.T("Download.Resource.InstallDone", "已安装「{0}」", displayName));
        }
        catch (OperationCanceledException)
        {
            task.MarkCancelled();
            Shell.Toast(L.T("Download.Resource.InstallCancelled", "已取消安装「{0}」", displayName));
        }
        catch (Exception ex)
        {
            task.Fail(ex.Message);
            Shell.Toast(L.T("Download.Resource.InstallFailed", "安装「{0}」失败：{1}", displayName, ex.Message));
        }
    }

    private static async Task InstallModpackAsync(
        ModrinthVersion version,
        string gameDirectory,
        IProgress<ModrinthDownloadProgress> progress,
        CancellationToken cancellationToken,
        DownloadTaskHandle task)
    {
        ModrinthVersionFile? primary = version.Files.FirstOrDefault(file => file.Primary)
                                       ?? version.Files.FirstOrDefault();
        if (primary is null) throw new InvalidOperationException("整合包没有可下载的主文件");

        string temporary = Path.Combine(gameDirectory, "downloads");
        Directory.CreateDirectory(temporary);

        string mrpackPath = Path.Combine(temporary, primary.Filename);
        if (!File.Exists(mrpackPath)) task.RegisterCleanup(mrpackPath);

        string instanceName = SanitizeDirectoryName(
            version.Name.Length > 0 ? version.Name : version.VersionNumber);
        string instanceDirectory = Path.Combine(gameDirectory, "versions", instanceName);
        bool instanceExisted = Directory.Exists(instanceDirectory);
        Directory.CreateDirectory(instanceDirectory);
        if (!instanceExisted) task.RegisterCleanup(instanceDirectory);

        string mrpack = await ModrinthResourceDownload.DownloadVersionFileAsync(
            primary, temporary, null, cancellationToken);

        var installProgress = new Progress<ModpackInstallProgress>(value => progress.Report(
            new ModrinthDownloadProgress(value.CompletedFiles, Math.Max(1, value.TotalFiles), value.Phase)));

        ModpackInstallResult result = await ModrinthBundleInstall.InstallAsync(
            mrpack,
            instanceDirectory,
            new ModpackInstallOptions
            {
                InstallLoader = true,
                JavaExecutable = YamlRead.JavaExecutable,
            },
            installProgress,
            cancellationToken);

        string instanceVersions = Path.Combine(instanceDirectory, "versions");
        if (Directory.Exists(instanceVersions))
        {
            string gameVersions = Path.Combine(gameDirectory, "versions");
            Directory.CreateDirectory(gameVersions);
            foreach (string directory in Directory.GetDirectories(instanceVersions))
            {
                CopyDirectory(directory, Path.Combine(gameVersions, Path.GetFileName(directory)));
            }
        }

        if (result.Errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join("；", result.Errors));
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
    }

    private static string SanitizeDirectoryName(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        name = name.Trim().Trim('.');
        return name.Length == 0 ? "modpack" : name;
    }

    private static string EnsureGameDirectory()
    {
        try
        {
            string directory = (YamlRead.GameDirectory ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = Path.GetFullPath(".minecraft");
                YamlRead.SaveGameDirectory(directory);
            }

            Directory.CreateDirectory(directory);
            return directory;
        }
        catch (Exception)
        {
            return Path.GetFullPath(".minecraft");
        }
    }

    private static string? SelectedGameVersion()
    {
        try
        {
            string gameDirectory = YamlRead.GameDirectory;
            string versionsRoot = Path.Combine(gameDirectory, "versions");
            if (!Directory.Exists(versionsRoot)) return null;

            List<string> ids = [];
            foreach (string directory in Directory.GetDirectories(versionsRoot))
            {
                string json = Path.Combine(directory, Path.GetFileName(directory) + ".json");
                if (File.Exists(json)) ids.Add(Path.GetFileName(directory));
            }
            if (ids.Count == 0) return null;

            string? selected = LocalSettings.Current.SelectedInstanceId;
            if (!string.IsNullOrWhiteSpace(selected))
            {
                string? match = ids.FirstOrDefault(id =>
                    string.Equals(id, selected, StringComparison.OrdinalIgnoreCase));
                if (match is not null) return match;
                match = ids.FirstOrDefault(id =>
                    selected.Contains(id, StringComparison.OrdinalIgnoreCase));
                if (match is not null) return match;
            }

            return ids[0];
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? SelectedLoader()
    {
        string? version = SelectedGameVersion();
        if (string.IsNullOrWhiteSpace(version)) return null;
        string lower = version.ToLowerInvariant();
        if (lower.Contains("fabric")) return "fabric";
        if (lower.Contains("quilt")) return "quilt";
        if (lower.Contains("neoforge")) return "neoforge";
        if (lower.Contains("forge")) return "forge";
        return "vanilla";
    }

    private sealed class DetailVersionRow
    {
        public required ModrinthVersion Version { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Meta { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public string Size { get; init; } = string.Empty;
    }

    private sealed class TargetInstance
    {
        public string Id { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public override string ToString() => DisplayName;
    }

    private void OnOpenResourceDetailClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ResourceItem item }) return;
        OpenResourceDetail(item);
    }

    private void OpenResourceDetail(ResourceItem item)
    {
        if (!_resourceProjects.TryGetValue(item, out ModrinthProject? project)) return;

        _detailProject = project;
        _detailType = _resourceTypes.TryGetValue(item, out string? type) ? type : "mod";
        _detailSelectedVersion = null;
        _detailGameVersion = null;

        DetailTitleText.Text = ChineseResourceName.Display(project.Title, project.Slug);
        DetailMetaText.Text = $"{project.TypeDisplay} · {project.DownloadsDisplay} · {project.FollowsDisplay}";
        DetailDescriptionText.Text = project.Description;
        DetailIcon.Source = null;
        DetailStatusText.Text = string.Empty;
        DetailVersionList.ItemsSource = null;
        DetailVersionsLoading.IsVisible = true;
        DetailVersionCountText.Text = string.Empty;

        ResourceDetailPanel.IsVisible = true;
        UiAnimations.Reset(ResourceDetailPanel);
        UiAnimations.FadeSlideIn(ResourceDetailPanel, 0.99, 6, 170);
        TabHost.IsVisible = false;
        _ = LoadDetailIconAsync(project);
        LoadTargetInstances();
        _ = LoadDetailVersionsAsync(project.ProjectId);
    }

    private async Task LoadDetailIconAsync(ModrinthProject project)
    {
        if (string.IsNullOrWhiteSpace(project.IconUrl)) return;
        try
        {
            Bitmap? bitmap = await ResourceIconCache.LoadAsync(project.IconUrl, $"modrinth:{project.ProjectId}");
            if (bitmap is not null && ReferenceEquals(_detailProject, project))
            {
                DetailIcon.Source = bitmap;
            }
        }
        catch (Exception)
        {
        }
    }

    private void OnCloseResourceDetailClick(object? sender, RoutedEventArgs e)
    {
        CancelCts(ref _detailCts);
        ResourceDetailPanel.IsVisible = false;
        TabHost.IsVisible = true;
        _detailProject = null;
        _detailVersions = [];
        _detailSelectedVersion = null;
        DetailVersionList.ItemsSource = null;
        DetailIcon.Source = null;
    }

    private void LoadTargetInstances()
    {
        List<TargetInstance> targets = [];
        if (_allVersions.Count > 0)
        {
            targets.AddRange(_allVersions
                .Where(version => version.Installed)
                .Select(version => new TargetInstance { Id = version.Id, DisplayName = version.Id }));
        }
        else
        {
            try
            {
                string versionsRoot = Path.Combine(_gameDirectory, "versions");
                if (Directory.Exists(versionsRoot))
                {
                    targets.AddRange(Directory.GetDirectories(versionsRoot)
                        .Select(Path.GetFileName)
                        .Where(id => !string.IsNullOrWhiteSpace(id) &&
                                     File.Exists(Path.Combine(versionsRoot, id!, id! + ".json")))
                        .Select(id => new TargetInstance { Id = id!, DisplayName = id! }));
                }
            }
            catch (Exception)
            {
            }
        }

        targets.Insert(0, new TargetInstance
        {
            Id = string.Empty,
            DisplayName = L.T("Download.Detail.RootTarget", "游戏根目录"),
        });

        _detailSuppress = true;
        DetailTargetCombo.ItemsSource = targets;
        string? selectedId = SelectedGameVersion();
        DetailTargetCombo.SelectedItem = targets.FirstOrDefault(target =>
            string.Equals(target.Id, selectedId, StringComparison.OrdinalIgnoreCase)) ?? targets[0];
        _detailSuppress = false;
    }

    private async Task LoadDetailVersionsAsync(string projectId)
    {
        CancelCts(ref _detailCts);
        _detailCts = new CancellationTokenSource();
        CancellationToken token = _detailCts.Token;

        _detailLoading = true;
        DetailVersionsLoading.IsVisible = true;
        try
        {
            _detailVersions = (await ModrinthResourceDownload.GetVersionsAsync(projectId, null, null, token))
                .OrderByDescending(version => ParseDate(version.DatePublished))
                .ToList();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _detailVersions = [];
            Dispatcher.UIThread.Post(() => DetailStatusText.Text =
                L.T("Download.Detail.LoadFailed", "版本列表加载失败：{0}", ex.Message));
        }
        finally
        {
            _detailLoading = false;
            Dispatcher.UIThread.Post(() => DetailVersionsLoading.IsVisible = false);
        }

        if (token.IsCancellationRequested) return;
        Dispatcher.UIThread.Post(BuildDetailGameVersions);
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.TryParse(value, out DateTimeOffset parsed) ? parsed : DateTimeOffset.MinValue;

    private void BuildDetailGameVersions()
    {
        IEnumerable<string> versions = _detailVersions.SelectMany(version => version.GameVersions);
        if (DetailReleaseOnlyCheck.IsChecked == true)
        {
            versions = versions.Where(IsReleaseVersion);
        }

        _detailGameVersions = versions
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(version => version, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string keyword = (DetailGameVersionSearch.Text ?? string.Empty).Trim();
        if (keyword.Length > 0)
        {
            _detailGameVersions = _detailGameVersions
                .Where(version => version.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        _detailSuppress = true;
        DetailGameVersionList.ItemsSource = _detailGameVersions;
        string? keep = _detailGameVersion is not null && _detailGameVersions.Contains(_detailGameVersion, StringComparer.OrdinalIgnoreCase)
            ? _detailGameVersions.First(version => string.Equals(version, _detailGameVersion, StringComparison.OrdinalIgnoreCase))
            : _detailGameVersions.FirstOrDefault();
        DetailGameVersionList.SelectedItem = keep;
        _detailSuppress = false;

        _detailGameVersion = keep;
        RefreshDetailVersions();
    }

    private static bool IsReleaseVersion(string version)
    {
        if (version.Length == 0) return false;
        bool hasDot = false;
        foreach (char character in version)
        {
            if (character == '.')
            {
                hasDot = true;
                continue;
            }

            if (!char.IsDigit(character)) return false;
        }

        return hasDot;
    }

    private void OnDetailGameVersionSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_ready || _detailProject is null) return;
        BuildDetailGameVersions();
    }

    private void OnDetailGameVersionFilterChanged(object? sender, RoutedEventArgs e)
    {
        if (!_ready || _detailProject is null) return;
        BuildDetailGameVersions();
    }

    private void OnDetailLoaderChanged(object? sender, RoutedEventArgs e)
    {
        if (!_ready || _detailSuppress) return;
        RefreshDetailVersions();
    }

    private void OnDetailGameVersionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_detailSuppress) return;
        _detailGameVersion = DetailGameVersionList.SelectedItem as string;
        RefreshDetailVersions();
    }

    private HashSet<string> SelectedLoaders()
    {
        HashSet<string> loaders = new(StringComparer.OrdinalIgnoreCase);
        if (DetailFabricCheck.IsChecked == true) loaders.Add("fabric");
        if (DetailForgeCheck.IsChecked == true) loaders.Add("forge");
        if (DetailNeoForgeCheck.IsChecked == true) loaders.Add("neoforge");
        if (DetailQuiltCheck.IsChecked == true) loaders.Add("quilt");
        return loaders;
    }

    private void RefreshDetailVersions()
    {
        if (_detailProject is null) return;

        HashSet<string> loaders = SelectedLoaders();
        IEnumerable<ModrinthVersion> filtered = _detailVersions;

        if (!string.IsNullOrWhiteSpace(_detailGameVersion))
        {
            filtered = filtered.Where(version =>
                version.GameVersions.Contains(_detailGameVersion!, StringComparer.OrdinalIgnoreCase));
        }

        if (_detailType is "mod" or "modpack" && loaders.Count > 0)
        {
            filtered = filtered.Where(version =>
                version.Loaders.Count == 0 || version.Loaders.Any(loaders.Contains));
        }

        List<DetailVersionRow> rows = filtered.Select(version => new DetailVersionRow
        {
            Version = version,
            Name = string.IsNullOrWhiteSpace(version.Name) ? version.VersionNumber : version.Name,
            Meta = $"{version.VersionNumber} · {version.GameVersionsDisplay} · {version.LoaderDisplay}",
            Type = version.VersionTypeDisplay,
            Size = version.PrimaryFile()?.SizeDisplay ?? string.Empty,
        }).ToList();

        DetailVersionList.ItemsSource = rows;
        DetailVersionCountText.Text = L.T("Download.Detail.Count", "共 {0} 个可用版本", rows.Count);
        if (rows.Count == 0)
        {
            DetailStatusText.Text = L.T("Download.Detail.NoVersion",
                "没有匹配的版本，换个游戏版本或加载器试试。");
            _detailSelectedVersion = null;
        }
        else
        {
            _detailSelectedVersion = rows[0].Version;
            DetailStatusText.Text = L.T("Download.Detail.Selected", "已选择 {0}", rows[0].Name);
        }
    }

    private void OnDetailVersionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DetailVersionRow row }) return;
        _detailSelectedVersion = row.Version;
        DetailStatusText.Text = L.T("Download.Detail.Selected", "已选择 {0}", row.Name);
    }

    private void OnDetailInstallClick(object? sender, RoutedEventArgs e)
    {
        if (_detailProject is null || _detailSelectedVersion is null)
        {
            DetailStatusText.Text = L.T("Download.Detail.NoVersion",
                "没有匹配的版本，换个游戏版本或加载器试试。");
            return;
        }

        string? targetId = (DetailTargetCombo.SelectedItem as TargetInstance)?.Id;
        string? gameVersion = string.IsNullOrWhiteSpace(targetId)
            ? _detailGameVersion
            : GameVersionOfInstance(targetId) ?? _detailGameVersion;
        string? loader = LoaderOfInstance(targetId);
        _ = InstallResourceAsync(_detailProject, _detailType, _detailSelectedVersion, gameVersion, loader,
            DetailTitleText.Text ?? ChineseResourceName.Display(_detailProject.Title, _detailProject.Slug),
            targetId);
    }

    private static string? GameVersionOfInstance(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        foreach (string part in id.Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsReleaseVersion(part)) return part;
        }

        int space = id.IndexOf(' ');
        return space > 0 ? id[..space] : null;
    }

    private static string? LoaderOfInstance(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        string lower = id.ToLowerInvariant();
        if (lower.Contains("neoforge")) return "neoforge";
        if (lower.Contains("quilt")) return "quilt";
        if (lower.Contains("fabric")) return "fabric";
        if (lower.Contains("forge")) return "forge";
        return "vanilla";
    }

    private async Task LoadJavaCandidatesAsync()
    {
        JavaPlatformText.Text = VendorJavaInstaller.GetPlatformDisplayName();
        JavaCandidateStatus.Text = L.T("Download.Java.CandidatesLoading", "正在查询可用 Java 版本…");
        JavaCandidateList.ItemsSource = null;
        JavaEmptyOverlay.IsVisible = false;
        JavaLoadingOverlay.IsVisible = true;

        try
        {
            IReadOnlyList<VendorJavaCandidate> candidates =
                await VendorJavaInstaller.QueryAvailableVersionsAsync(_javaVendorKind);
            _javaCandidates = candidates;
            JavaCandidateList.ItemsSource = candidates;
            JavaEmptyOverlay.IsVisible = candidates.Count == 0;
            UiAnimations.Reset(JavaCandidateList);
            UiAnimations.FadeSlideIn(JavaCandidateList, 0.995, 4, 140);
            JavaCandidateStatus.Text = candidates.Count == 0
                ? string.Empty
                : L.T("Download.Java.CandidatesCount", "{0} 个可用版本 · 将自动选择当前平台安装包",
                    candidates.Count);
        }
        catch (Exception error)
        {
            JavaCandidateStatus.Text = L.T("Download.Java.CandidatesFailed",
                "查询 Java 版本失败：{0}", error.Message);
        }
        finally
        {
            JavaLoadingOverlay.IsVisible = false;
        }
    }

    private void OnRefreshJavaCandidatesClick(object? sender, RoutedEventArgs e) =>
        _ = LoadJavaCandidatesAsync();

    private void OnJavaVendorPillClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse(tag, out VendorJavaKind kind))
        {
            return;
        }

        if (_javaVendorKind == kind && _javaCandidates.Count > 0)
        {
            return;
        }

        _javaVendorKind = kind;
        ApplyJavaVendorSelection();
        if (_ready)
        {
            _ = LoadJavaCandidatesAsync();
        }
    }

    private void ApplyJavaVendorSelection()
    {
        JavaVendorZuluPill.Classes.Set("active", _javaVendorKind == VendorJavaKind.Zulu);
        JavaVendorTemurinPill.Classes.Set("active", _javaVendorKind == VendorJavaKind.Temurin);
        JavaVendorOraclePill.Classes.Set("active", _javaVendorKind == VendorJavaKind.Oracle);
    }

    private async void OnDownloadJavaCandidateClick(object? sender, RoutedEventArgs e)
    {
        VendorJavaCandidate? candidate = (sender as Button)?.Tag as VendorJavaCandidate;
        if (candidate is null)
        {
            Shell.Toast(L.T("Download.Java.SelectVersion", "请先选择一个 Java 版本"));
            return;
        }

        string gameDirectory = EnsureGameDirectory();
        var installer = new VendorJavaInstaller(gameDirectory);
        var task = DownloadTaskService.Start(
            $"{L.T("Download.Java.Runtime", "Java 运行时")} · {candidate.DisplayName}",
            L.T("Download.Java.Preparing", "正在准备下载…"));
        JavaDownloadProgress.IsVisible = true;
        JavaDownloadProgress.Value = 0;
        JavaDownloadStatus.IsVisible = true;
        JavaDownloadStatus.Text = L.T("Download.Java.Preparing", "正在准备下载…");

        try
        {
            var progress = new Progress<VendorJavaProgress>(value =>
            {
                double percent = value.TotalBytes > 0
                    ? Math.Clamp(value.CompletedBytes * 100.0 / value.TotalBytes, 0, 100)
                    : 0;
                task.Report(percent, value.Phase,
                    null,
                    value.TotalBytes > 0
                        ? $"{MemoryRead.FormatBytes(value.CompletedBytes)} / {MemoryRead.FormatBytes(value.TotalBytes)}"
                        : null);
                Dispatcher.UIThread.Post(() =>
                {
                    JavaDownloadProgress.Value = percent;
                    if (!string.IsNullOrWhiteSpace(value.Phase))
                    {
                        JavaDownloadStatus.Text = $"{value.Phase} · {percent:0}%";
                    }
                });
            });

            VendorJavaRuntime runtime = await installer.InstallCandidateAsync(candidate, progress);
            YamlRead.SaveJava(runtime.JavaExecutablePath, runtime.DisplayName);
            task.Complete(L.T("Download.Java.InstallDone", "已安装并启用 {0}", runtime.DisplayName));
            JavaDownloadProgress.Value = 100;
            JavaDownloadStatus.Text = L.T("Download.Java.InstallDone", "已安装并启用 {0}", runtime.DisplayName);
            Shell.Toast(L.T("Download.Java.InstallDone", "已安装并启用 {0}", runtime.DisplayName));
        }
        catch (Exception error)
        {
            task.Fail(error.Message);
            JavaDownloadStatus.Text = L.T("Download.Java.InstallFailed",
                "Java 运行时安装失败：{0}", error.Message);
            Shell.Toast(JavaDownloadStatus.Text);
        }
    }

    private static string SafeGameDirectory()
    {
        try
        {
            string directory = YamlRead.GameDirectory;
            return string.IsNullOrWhiteSpace(directory) ? string.Empty : directory.Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static readonly TimeSpan TabSwitchDuration = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan ListRefreshDuration = TimeSpan.FromMilliseconds(180);

    private void AnimateTabSwitch(Control? panel, int direction)
    {
        ResetMotion(panel);
        if (!UiAnimations.Enabled || panel is null) return;

        panel.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TabSwitchDuration,
                Easing = new CubicEaseOut(),
            },
            new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = TabSwitchDuration,
                Easing = new CubicEaseOut(),
            },
        };

        panel.Opacity = 0;
        panel.RenderTransform = TransformOperations.Parse(
            $"translateX({(direction >= 0 ? 22 : -22).ToString(CultureInfo.InvariantCulture)}px)");
        Dispatcher.UIThread.Post(() =>
        {
            panel.Opacity = 1;
            panel.RenderTransform = TransformOperations.Parse("translateX(0px)");
        }, DispatcherPriority.Render);
    }

    private void AnimateListRefresh(Control? target)
    {
        ResetMotion(target);
        if (!UiAnimations.Enabled || target is null) return;

        target.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = ListRefreshDuration,
                Easing = new CubicEaseOut(),
            },
            new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = ListRefreshDuration,
                Easing = new CubicEaseOut(),
            },
        };

        target.Opacity = 0;
        target.RenderTransform = TransformOperations.Parse("translateY(6px)");
        Dispatcher.UIThread.Post(() =>
        {
            target.Opacity = 1;
            target.RenderTransform = TransformOperations.Parse("translateY(0px)");
        }, DispatcherPriority.Render);
    }

    private static void ResetMotion(Control? target)
    {
        if (target is null) return;
        target.Opacity = 1;
        target.RenderTransform = TransformOperations.Parse("translateX(0px) scale(1)");
    }

    private void RenderExtensionRepos()
    {
        ExtensionRepoList.Children.Clear();
        foreach (string repo in LocalSettings.Current.PluginRepos)
        {
            var reference = PluginStore.ParseRepo(repo);
            string display = reference?.Slug ?? repo;

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };

            if (reference is not null)
            {
                var badge = new Border
                {
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(7, 1),
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Background = ThemeTokens.Brush(HostSoftBrushOf(reference.HostName)),
                    Child = new TextBlock
                    {
                        Text = reference.HostName,
                        FontSize = 10,
                        Foreground = ThemeTokens.Brush(HostBrushOf(reference.HostName)),
                    },
                };
                row.Children.Add(badge);
            }

            var name = new TextBlock
            {
                Text = display,
                FontSize = 12.5,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Foreground = ThemeTokens.Brush("NekoTextBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(name, reference is null ? 0 : 1);
            Grid.SetColumnSpan(name, reference is null ? 3 : 2);
            row.Children.Add(name);

            ToolTip.SetTip(name, repo);

            string captured = repo;
            var remove = new Button
            {
                Content = L.T("Download.Extension.Remove", "移除"),
                Classes = { "ghost" },
                Height = 28,
                Padding = new Thickness(10, 0),
            };
            remove.Click += (_, _) =>
            {
                LocalSettings.Current.PluginRepos.Remove(captured);
                LocalSettings.Save();
                RenderExtensionRepos();
                _ = RefreshExtensionEntriesAsync();
            };
            Grid.SetColumn(remove, 3);
            row.Children.Add(remove);
            ExtensionRepoList.Children.Add(row);
        }
    }

    private void OnExtensionRepoAddClick(object? sender, RoutedEventArgs e)
    {
        string raw = (ExtensionRepoBox.Text ?? string.Empty).Trim();
        var reference = PluginStore.ParseRepo(raw);
        if (reference is null)
        {
            Shell.Toast(L.T("Download.Extension.BadRepo",
                "无法识别的仓库地址，试试 github.com / gitee.com / gitcode.com 的仓库链接"));
            return;
        }

        if (LocalSettings.Current.PluginRepos.Any(existing =>
                PluginStore.ParseRepo(existing)?.HomeUrl.Equals(reference.HomeUrl, StringComparison.OrdinalIgnoreCase) == true))
        {
            Shell.Toast(L.T("Download.Extension.Duplicate", "这个仓库已经添加过了"));
            return;
        }

        LocalSettings.Current.PluginRepos.Add(reference.HomeUrl);
        LocalSettings.Save();
        ExtensionRepoBox.Text = string.Empty;
        RenderExtensionRepos();
        _ = RefreshExtensionEntriesAsync();
    }

    private void OnExtensionRefreshClick(object? sender, RoutedEventArgs e) => _ = RefreshExtensionEntriesAsync();

    private async Task RefreshExtensionEntriesAsync()
    {
        RenderExtensionRepos();

        var repos = LocalSettings.Current.PluginRepos.ToList();
        if (repos.Count == 0)
        {
            ExtensionEntryList.Children.Clear();
            ExtensionEntryList.Children.Add(new TextBlock
            {
                Text = L.T("Download.Extension.NoRepos", "先添加一个插件仓库（GitHub 仓库地址）。"),
                Classes = { "caption" },
            });
            return;
        }

        ExtensionEntryList.Children.Clear();
        ExtensionEntryList.Children.Add(new TextBlock
        {
            Text = L.T("Download.Extension.Loading", "正在读取仓库 Release…"),
            Classes = { "caption" },
        });

        foreach (string repo in repos)
        {
            PluginStoreEntry entry;
            try
            {
                entry = await PluginStore.FetchLatestAsync(repo);
            }
            catch (Exception error)
            {
                ExtensionEntryList.Children.Add(new TextBlock
                {
                    Text = L.T("Download.Extension.RepoFailed", "读取 {0} 失败：{1}", repo, error.Message),
                    Classes = { "caption" },
                    Foreground = ThemeTokens.Brush("NekoDangerBrush"),
                    TextWrapping = TextWrapping.Wrap,
                });
                continue;
            }

            ExtensionEntryList.Children.Add(BuildExtensionEntryCard(entry));
        }
    }

    private static (string Brush, string SoftBrush) HostBrushes(string hostName) => hostName switch
    {
        "Gitee" => ("NekoDangerBrush", "NekoDangerSoftBrush"),
        "GitCode" => ("NekoWarningBrush", "NekoWarningSoftBrush"),
        _ => ("NekoInfoBrush", "NekoInfoSoftBrush"),
    };

    private static string HostBrushOf(string hostName) => HostBrushes(hostName).Brush;

    private static string HostSoftBrushOf(string hostName) => HostBrushes(hostName).SoftBrush;

    private Control BuildExtensionEntryCard(PluginStoreEntry entry)
    {
        bool installed = PluginManager.ById(PluginStore.RepoId(entry)) is not null;
        bool upToDate = PluginStore.IsUpToDate(entry);

        string action = entry.AssetUrl is null
            ? L.T("Download.Extension.NoAsset", "无安装包")
            : installed && upToDate
                ? L.T("Download.Extension.UpToDate", "已是最新")
                : installed
                    ? L.T("Download.Extension.Update", "更新")
                    : L.T("Download.Extension.Install", "安装");

        var card = new Border
        {
            Classes = { "card" },
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 12),
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var info = new StackPanel { Spacing = 3 };

        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        titleRow.Children.Add(new TextBlock
        {
            Text = entry.Name + "  v" + entry.Version,
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeTokens.Brush("NekoTextBrush"),
        });
        titleRow.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 1),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Background = ThemeTokens.Brush(HostSoftBrushOf(entry.HostName)),
            Child = new TextBlock
            {
                Text = entry.HostName,
                FontSize = 10,
                Foreground = ThemeTokens.Brush(HostBrushOf(entry.HostName)),
            },
        });
        info.Children.Add(titleRow);
        info.Children.Add(new TextBlock
        {
            Text = entry.AssetUrl is null
                ? L.T("Download.Extension.NoAssetHint", "最新 Release 没有 .nekoex 资产")
                : entry.RepoName + (entry.AssetSize > 0 ? " · " + (entry.AssetSize / 1024.0 / 1024.0).ToString("0.0") + " MB" : ""),
            Classes = { "caption" },
        });
        if (string.IsNullOrWhiteSpace(entry.Notes) == false)
        {
            string notes = entry.Notes.Length > 160 ? entry.Notes[..160] + "…" : entry.Notes;
            info.Children.Add(new TextBlock
            {
                Text = notes.Replace("\r", " ").Replace("\n", " "),
                FontSize = 12,
                Foreground = ThemeTokens.Brush("NekoMutedBrush"),
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
            });
        }

        var actionButton = new Button
        {
            Content = action,
            Classes = { entry.AssetUrl is null || (installed && upToDate) ? "ghost" : "primary" },
            Height = 32,
            Padding = new Thickness(14, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            IsEnabled = entry.AssetUrl is not null && (installed && upToDate) == false,
        };
        actionButton.Click += (_, _) => _ = InstallExtensionEntryAsync(entry);

        grid.Children.Add(info);
        Grid.SetColumn(actionButton, 1);
        grid.Children.Add(actionButton);
        card.Child = grid;
        return card;
    }

    private async Task InstallExtensionEntryAsync(PluginStoreEntry entry)
    {
        var task = DownloadTaskService.Start(
            L.T("Download.Extension.TaskTitle", "插件 {0} v{1}", entry.Name, entry.Version),
            L.T("Download.Version.Preparing", "准备下载"));
        try
        {
            var progress = new Progress<long>(bytes => task.Report(
                entry.AssetSize > 0 ? Math.Clamp(bytes * 100.0 / entry.AssetSize, 0, 100) : 0,
                null,
                null,
                MemoryRead.FormatBytes(bytes)));
            string id = await PluginStore.InstallAsync(entry, progress);
            task.Complete(L.T("Download.Extension.Installed", "已安装 {0}", id));
            Shell.Toast(L.T("Pages.Plugins.Install.Done", "已安装插件 {0}", id));
        }
        catch (Exception error)
        {
            task.Fail(error.Message);
            Shell.Toast(L.T("Pages.Plugins.Install.Failed", "安装失败：{0}", error.Message));
        }

        _ = RefreshExtensionEntriesAsync();
    }

    private void Control_OnLoaded(object? sender, RoutedEventArgs e)
    {
        TimeZoneInfo ChinaUTC = TimeZoneInfo.Local;
        if (ChinaUTC.Id != "China Standard Time" && ChinaUTC.Id != "Asia/Shanghai")
        {
            ChineseMainlandInfoBar.IsVisible = false;
        }
    }
}

public sealed class VersionCacheFile
{
    public DateTimeOffset SavedAt { get; set; }

    public List<VersionCacheEntry> Versions { get; set; } = [];
}

public sealed class VersionCacheEntry
{
    public string Id { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string TypeKey { get; set; } = string.Empty;

    public string ReleaseDate { get; set; } = string.Empty;

    public bool Installed { get; set; }

    public string Icon { get; set; } = string.Empty;
}
