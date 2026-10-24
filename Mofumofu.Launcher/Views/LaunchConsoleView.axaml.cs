using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Mofumofu.Launcher.Services;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views;

/// <summary>终端输出区的一行（供 XAML DataTemplate 反射绑定，必须公开）。</summary>
public sealed class LaunchConsoleLineVm
{
    public string Time { get; init; } = string.Empty;
    public string Level { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public IBrush LevelBrush { get; init; } = Brushes.Transparent;
    public IBrush TextBrush { get; init; } = Brushes.Transparent;
}

/// <summary>
/// 类终端启动进度页：上半部分是阶段时间线，下半部分是 Java 进程的类终端输出区。
/// 数据全部来自 <see cref="LaunchProgress"/>（MofuCore.GameLaunch 真实启动）。
/// </summary>
public partial class LaunchConsoleView : UserControl
{
    private sealed class StageRow
    {
        public Border IconBox { get; init; } = null!;
        public TextBlock IconText { get; init; } = null!;
        public TextBlock TitleText { get; init; } = null!;
        public ProgressBar Bar { get; init; } = null!;
        public TextBlock PercentText { get; init; } = null!;
        public TextBlock DurationText { get; init; } = null!;
    }

    private const int MaxConsoleLines = 600;
    private const double StageTitleWidth = 168;

    private readonly LaunchProgress _progress = LaunchProgress.Shared;
    private readonly Dictionary<string, StageRow> _stageRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<LaunchConsoleLineVm> _lines = [];
    private readonly List<string> _history = [];
    private int _historyIndex;
    private DispatcherTimer? _elapsedTimer;
    private bool _autoExitScheduled;

    public LaunchConsoleView()
    {
        InitializeComponent();

        LogList.ItemsSource = _lines;
        BuildTimeline();
        BindProgress();
        StartElapsedTimer();

        // 打开页面即开一次启动：有可用的游戏目录/版本/Java 则真实启动，否则把失败原因落到阶段上；
        // 若已有启动在后台运行（页面被重建），则接上它的历史与实时事件，不重复启动。
        Dispatcher.UIThread.Post(() =>
        {
            if (_progress.IsRunning || _progress.History.Count > 0)
            {
                SyncFromProgress();
            }
            else
            {
                _progress.StartAuto();
            }
        });
    }

    /// <summary>重新开始一次启动（重复调用会清空上一轮）。</summary>
    public void ReplayLaunch()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            _progress.StartAuto();
        }
        else
        {
            Dispatcher.UIThread.Post(() => _progress.StartAuto());
        }
    }

    /// <summary>将来接 <c>MofuCore.Launch.GameLaunch</c> 时，宿主把 stdout/stderr 回调和取消请求接到这里。</summary>
    public LaunchProgress Progress => _progress;

    // ---------------------------------------------------------------- 构建

    private void BuildTimeline()
    {
        StageList.Children.Clear();
        _stageRows.Clear();

        foreach (LaunchStage stage in _progress.Stages)
        {
            var iconText = new TextBlock
            {
                Text = "·",
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var iconBox = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                VerticalAlignment = VerticalAlignment.Center,
                Child = iconText,
            };

            var indexText = new TextBlock
            {
                Text = (stage.Index + 1).ToString("00", CultureInfo.InvariantCulture),
                FontSize = 10.5,
                FontFamily = new FontFamily("Consolas, Cascadia Mono, Menlo"),
                Foreground = ThemeTokens.Brush("NekoDimBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
            };

            var titleText = new TextBlock
            {
                Text = stage.Title,
                FontSize = 12.5,
                Width = StageTitleWidth,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Height = 4,
                Margin = new Thickness(10, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = ThemeTokens.Brush("NekoBorderBrush"),
                Foreground = ThemeTokens.Brush("NekoAccentBrush"),
            };

            var percentText = new TextBlock
            {
                Text = "0%",
                FontSize = 11,
                Width = 38,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ThemeTokens.Brush("NekoMutedBrush"),
            };

            var durationText = new TextBlock
            {
                Text = "—",
                FontSize = 10.5,
                Width = 46,
                TextAlignment = TextAlignment.Right,
                FontFamily = new FontFamily("Consolas, Cascadia Mono, Menlo"),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ThemeTokens.Brush("NekoDimBrush"),
            };

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto,Auto"),
                Margin = new Thickness(0, 1),
            };
            row.Children.Add(iconBox);
            Grid.SetColumn(indexText, 1);
            row.Children.Add(indexText);
            Grid.SetColumn(titleText, 2);
            row.Children.Add(titleText);
            Grid.SetColumn(bar, 3);
            row.Children.Add(bar);
            Grid.SetColumn(percentText, 4);
            row.Children.Add(percentText);
            Grid.SetColumn(durationText, 5);
            row.Children.Add(durationText);

            StageList.Children.Add(row);
            StageRow stageRow = new()
            {
                IconBox = iconBox,
                IconText = iconText,
                TitleText = titleText,
                Bar = bar,
                PercentText = percentText,
                DurationText = durationText,
            };
            _stageRows[stage.Id] = stageRow;
            ApplyStage(stage);
        }
    }

    private void BindProgress()
    {
        _progress.Started += (_, _) => OnUi(OnStarted);
        _progress.StageChanged += (_, e) => OnUi(() => ApplyStageChanged(e));
        _progress.LogAppended += (_, line) => OnUi(() => AppendLine(line));
        _progress.LogCleared += (_, _) => OnUi(() => _lines.Clear());
        _progress.Completed += (_, e) => OnUi(() => OnCompleted(e));
    }

    private void StartElapsedTimer()
    {
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _elapsedTimer.Tick += (_, _) => ElapsedText.Text = FormatElapsed(_progress.Elapsed);
        _elapsedTimer.Start();

        // 从桌面重新进入（页面被缓存或重建）时，把后台启动的进度接回来
        AttachedToVisualTree += (_, _) =>
        {
            if (_progress.History.Count > 0) OnUi(SyncFromProgress);
        };
    }

    // ---------------------------------------------------------------- 进度更新

    private void OnStarted()
    {
        _lines.Clear();
        foreach (LaunchStage stage in _progress.Stages)
        {
            ApplyStage(stage);
        }
        UpdateHeader();
        PhaseText.Text = L.T("Launch.Phase.Preparing", "正在准备启动…");
        OverallText.Text = L.T("Launch.Format.Percent", "{0:0}%", 0.0);
        OverallText.Foreground = ThemeTokens.Brush("NekoAccentBrush");
        TerminalStatusText.Text = L.T("Launch.Status.Running", "RUNNING");
        TerminalStatusText.Foreground = ThemeTokens.Brush("NekoAccentBrush");
        ElapsedText.Text = FormatElapsed(TimeSpan.Zero);
    }

    /// <summary>页面重建 / 从桌面重新进入时，把仍在后台运行的启动状态接回来。</summary>
    private void SyncFromProgress()
    {
        _lines.Clear();
        foreach (LaunchLogLine line in _progress.History)
        {
            AppendLine(line, scroll: false);
        }
        foreach (LaunchStage stage in _progress.Stages)
        {
            ApplyStage(stage);
        }
        UpdateHeader();

        double overall = _progress.Stages.Count == 0
            ? 0
            : _progress.Stages.Sum(stage => stage.Progress) / _progress.Stages.Count * 100;
        OverallText.Text = L.T("Launch.Format.Percent", "{0:0}%", overall);
        ElapsedText.Text = FormatElapsed(_progress.Elapsed);

        if (_progress.LastResult is { } result)
        {
            OnCompleted(result);
        }
        else
        {
            LaunchStage? running = _progress.Stages.FirstOrDefault(stage => stage.State == LaunchStageState.Running);
            PhaseText.Text = running?.Detail ?? L.T("Launch.Phase.Starting", "正在启动…");
            OverallText.Foreground = ThemeTokens.Brush("NekoAccentBrush");
            TerminalStatusText.Text = _progress.IsRunning
                ? L.T("Launch.Status.Running", "RUNNING")
                : L.T("Launch.Status.Idle", "IDLE");
            TerminalStatusText.Foreground = ThemeTokens.Brush("NekoAccentBrush");
        }

        if (AutoScrollCheck.IsChecked != false)
        {
            Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd(), DispatcherPriority.Background);
        }
    }

    private void UpdateHeader()
    {
        string name = _progress.InstanceName;
        string label = _progress.VersionLabel;
        InstanceText.Text = label.Length > 0 && !name.Contains(label, StringComparison.OrdinalIgnoreCase)
            ? $"{name} · {label}"
            : name;
    }

    /// <summary>终端标题栏的来源标识：全部来自 MofuCore 真实启动，无需徽章。</summary>

    private void ApplyStageChanged(LaunchStageChangedEventArgs e)
    {
        ApplyStage(e.Stage);
        OverallText.Text = L.T("Launch.Format.Percent", "{0:0}%", e.OverallProgress);
        PhaseText.Text = e.Activity;

        // 游戏窗口就绪说明启动器任务已完成：直接回主界面，日志继续在后台记录
        if (!_autoExitScheduled &&
            string.Equals(e.Stage.Id, "window", StringComparison.OrdinalIgnoreCase) &&
            e.Stage.State == LaunchStageState.Succeeded)
        {
            _autoExitScheduled = true;
            DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(900) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Shell.Toast(L.T("Launch.Toast.BackToDesktop", "游戏已启动，启动日志在后台继续记录"));
                Shell.Go("home");
            };
            timer.Start();
        }
    }

    private void ApplyStage(LaunchStage stage)
    {
        if (!_stageRows.TryGetValue(stage.Id, out StageRow? row)) return;

        (string glyph, IBrush iconForeground, IBrush iconBackground, IBrush titleForeground) = stage.State switch
        {
            LaunchStageState.Running => ("…", ThemeTokens.Brush("NekoAccentBrush"), ThemeTokens.Brush("NekoAccentSoftBrush"), ThemeTokens.Brush("NekoTextBrush")),
            LaunchStageState.Succeeded => ("✓", ThemeTokens.Brush("NekoSuccessBrush"), ThemeTokens.Brush("NekoSuccessSoftBrush"), ThemeTokens.Brush("NekoTextBrush")),
            LaunchStageState.Failed => ("✕", ThemeTokens.Brush("NekoDangerBrush"), ThemeTokens.Brush("NekoDangerSoftBrush"), ThemeTokens.Brush("NekoDangerBrush")),
            LaunchStageState.Skipped => ("–", ThemeTokens.Brush("NekoDimBrush"), ThemeTokens.Brush("NekoFieldBrush"), ThemeTokens.Brush("NekoDimBrush")),
            _ => ("·", ThemeTokens.Brush("NekoDimBrush"), ThemeTokens.Brush("NekoFieldBrush"), ThemeTokens.Brush("NekoMutedBrush")),
        };

        row.IconText.Text = glyph;
        row.IconText.Foreground = iconForeground;
        row.IconBox.Background = iconBackground;
        row.TitleText.Foreground = titleForeground;
        row.Bar.Value = Math.Clamp(stage.Progress * 100, 0, 100);
        row.PercentText.Text = stage.State == LaunchStageState.Skipped
            ? "—"
            : L.T("Launch.Format.Percent", "{0:0}%", stage.Progress * 100);
        row.PercentText.Foreground = stage.State switch
        {
            LaunchStageState.Running => ThemeTokens.Brush("NekoAccentBrush"),
            LaunchStageState.Succeeded => ThemeTokens.Brush("NekoSuccessBrush"),
            LaunchStageState.Failed => ThemeTokens.Brush("NekoDangerBrush"),
            _ => ThemeTokens.Brush("NekoMutedBrush"),
        };
        row.DurationText.Text = stage.Duration is { } duration
            ? L.T("Launch.Format.Duration", "{0:0.0}s", duration.TotalSeconds)
            : stage.State == LaunchStageState.Running ? "…" : "—";
    }

    private void OnCompleted(LaunchCompletedEventArgs e)
    {
        TerminalStatusText.Text = e.Cancelled
            ? L.T("Launch.Status.Cancelled", "CANCELLED")
            : e.Success
                ? L.T("Launch.Status.Success", "SUCCESS")
                : L.T("Launch.Status.Failed", "FAILED");
        TerminalStatusText.Foreground = ThemeTokens.Brush(e.Cancelled || !e.Success ? "NekoDangerBrush" : "NekoSuccessBrush");
        OverallText.Foreground = ThemeTokens.Brush(e.Success ? "NekoSuccessBrush" : "NekoDangerBrush");
        PhaseText.Text = e.Message;
        ElapsedText.Text = FormatElapsed(e.Duration);
        GameState.Stop();
    }

    // ---------------------------------------------------------------- 终端输出

    private void AppendLine(LaunchLogLine line, bool scroll = true)
    {
        _lines.Add(new LaunchConsoleLineVm
        {
            Time = line.TimeDisplay,
            Level = line.LevelDisplay,
            Text = line.Text,
            LevelBrush = ThemeTokens.Brush(line.LevelToken),
            TextBrush = line.Level == LaunchLogLevel.Error
                ? ThemeTokens.Brush("NekoDangerBrush")
                : ThemeTokens.Brush("NekoTerminalTextBrush"),
        });

        while (_lines.Count > MaxConsoleLines)
        {
            _lines.RemoveAt(0);
        }

        if (scroll && AutoScrollCheck.IsChecked != false)
        {
            Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd(), DispatcherPriority.Background);
        }
    }

    // ---------------------------------------------------------------- 命令行

    private void ExecuteCommand()
    {
        string text = CommandInput.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return;

        _history.Add(text);
        _historyIndex = _history.Count;
        _progress.SubmitCommand(text);
        CommandInput.Text = string.Empty;
        CommandInput.Focus();
    }

    private void OnCommandKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                ExecuteCommand();
                e.Handled = true;
                break;

            case Key.Up when _history.Count > 0:
                _historyIndex = Math.Max(0, _historyIndex - 1);
                CommandInput.Text = _history[_historyIndex];
                CommandInput.CaretIndex = CommandInput.Text?.Length ?? 0;
                e.Handled = true;
                break;

            case Key.Down when _history.Count > 0:
                _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
                CommandInput.Text = _historyIndex >= _history.Count ? string.Empty : _history[_historyIndex];
                CommandInput.CaretIndex = CommandInput.Text?.Length ?? 0;
                e.Handled = true;
                break;

            case Key.Escape:
                CommandInput.Text = string.Empty;
                e.Handled = true;
                break;
        }
    }

    // ---------------------------------------------------------------- 顶部按钮

    private void OnReplayClick(object? sender, RoutedEventArgs e) => ReplayLaunch();

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (!_progress.Cancel())
        {
            Shell.Toast(L.T("Launch.Toast.NoActive", "当前没有正在进行的启动"));
            return;
        }
        GameState.Stop();
        Shell.Toast(L.T("Launch.Toast.Cancelled", "已取消启动"));
    }

    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        if (_progress.IsRunning)
        {
            Shell.Toast(L.T("Launch.Toast.Background", "启动仍在后台进行，重新进入「启动控制台」可继续查看"));
        }
        Shell.BackToDesktop();
    }

    // ---------------------------------------------------------------- 辅助

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    private static string FormatElapsed(TimeSpan elapsed)
        => L.T("Launch.Format.Elapsed", "{0:00}:{1:00}", (int)elapsed.TotalMinutes, elapsed.Seconds);
}
