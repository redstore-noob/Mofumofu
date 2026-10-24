using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using MofuCore.Config;
using MofuCore.Java;
using MofuCore.Launch;
using MofuCore.Log;
using Mofumofu.Launcher.Mock;
using Mofumofu.Launcher.Models;
using Mofumofu.Launcher.Views;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Services;

/// <summary>启动阶段状态。</summary>
public enum LaunchStageState
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped,
}

/// <summary>终端日志级别（只影响着色，不参与业务）。</summary>
public enum LaunchLogLevel
{
    Info,
    Success,
    Warning,
    Error,
    Command,
}

/// <summary>一行终端日志：时间戳 + 级别 + 文本。</summary>
public sealed record LaunchLogLine(DateTimeOffset Timestamp, LaunchLogLevel Level, string Text)
{
    public string TimeDisplay => Timestamp.ToLocalTime().ToString("HH:mm:ss");

    public string LevelDisplay => Level switch
    {
        LaunchLogLevel.Success => " OK ",
        LaunchLogLevel.Warning => "WARN",
        LaunchLogLevel.Error => "FAIL",
        LaunchLogLevel.Command => "CMD ",
        _ => "INFO",
    };

    /// <summary>令牌名：由视图按主题取色（`ThemeTokens.Brush`）。</summary>
    public string LevelToken => Level switch
    {
        LaunchLogLevel.Success => "NekoSuccessBrush",
        LaunchLogLevel.Warning => "NekoWarningBrush",
        LaunchLogLevel.Error => "NekoDangerBrush",
        LaunchLogLevel.Command => "NekoAccentBrush",
        _ => "NekoTerminalTextBrush",
    };
}

/// <summary>单个启动阶段的可观察状态（由 <see cref="LaunchProgress"/> 更新）。</summary>
public sealed class LaunchStage
{
    private string? _detailOverride;

    public string Id { get; init; } = string.Empty;

    public string TitleFallback { get; init; } = string.Empty;

    public string DetailFallback { get; init; } = string.Empty;

    public double Progress { get; set; }

    public LaunchStageState State { get; set; } = LaunchStageState.Pending;

    public TimeSpan? Duration { get; set; }

    public int Index { get; init; }

    public string TitleKey => "Launch.Stage." + Id + ".Title";

    public string DetailKey => "Launch.Stage." + Id + ".Detail";

    /// <summary>阶段名：按当前语言实时解析（切语言后页面重建即可生效）。</summary>
    public string Title => L.T(TitleKey, TitleFallback);

    /// <summary>阶段说明：未覆盖时取语言包；运行时（真实启动）可写入具体文案。</summary>
    public string Detail
    {
        get => _detailOverride ?? L.T(DetailKey, DetailFallback);
        set => _detailOverride = value;
    }

    public void ResetDetail() => _detailOverride = null;
}

/// <summary>阶段状态变化事件。</summary>
public sealed class LaunchStageChangedEventArgs : EventArgs
{
    public required LaunchStage Stage { get; init; }
    public required double OverallProgress { get; init; }
    public required string Activity { get; init; }
}

/// <summary>启动结束事件（成功 / 失败 / 取消 / 游戏退出）。</summary>
public sealed class LaunchCompletedEventArgs : EventArgs
{
    public bool Success { get; init; }
    public bool Cancelled { get; init; }
    public int? ExitCode { get; init; }
    public string Message { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// 与 UI 解耦的启动进度模型：阶段时间线 + 终端日志 + 完成事件。
/// 真实启动：<see cref="StartReal"/> / <see cref="StartAuto"/> 调用 <c>MofuCore.Launch.GameLaunch</c>，
/// 把进程 stdout/stderr 与退出码映射到阶段与日志。
/// 视图只订阅事件；替换数据源不需要改视图。
/// </summary>
public sealed class LaunchProgress
{
    public static string DefaultInstanceName => L.T("Launch.DefaultInstance", "Minecraft 1.21.4 · Fabric");
    private const int MaxHistoryLines = 600;
    private const int MaxLogTailChars = 16 * 1024;

    private static readonly (string Id, string Title, string Detail)[] StageDefinitions =
    [
        ("java", "准备 Java 运行时", "定位 Mojang 运行时并核对 Java 版本"),
        ("verify", "校验游戏文件", "版本 JSON / 客户端 JAR / 依赖库完整性检查"),
        ("download", "补全缺失资源", "assets 与 libraries 断点续传与并行下载"),
        ("natives", "解压 natives", "按 rules 解压 natives 到版本目录"),
        ("arguments", "拼接启动参数", "classpath / JVM / 游戏参数装配"),
        ("process", "启动游戏进程", "java.exe 进程启动与握手"),
        ("window", "等待游戏窗口", "等待 Minecraft 窗口就绪"),
    ];

    private static readonly string[] WindowReadyHints =
    [
        "Backend library", "OpenGL", "LWJGL", "Setting user", "Render thread",
        "Created:", "GLFW", "Reloading ResourceManager", "Sound engine started",
    ];

    /// <summary>全局共享实例：页面被重建时能接上仍在后台运行的启动。</summary>
    public static LaunchProgress Shared { get; } = new();

    private readonly object _sync = new();
    private readonly List<LaunchStage> _stages = [];
    private readonly List<LaunchLogLine> _history = [];
    private readonly StringBuilder _logTail = new();
    private readonly Stopwatch _stopwatch = new();
    private CancellationTokenSource? _cts;
    private MinecraftLaunchResult? _launchResult;
    private bool _running;
    private bool _windowReady;
    private TimeSpan _lastDuration;
    private LaunchCompletedEventArgs? _lastResult;
    private DateTimeOffset _runStartedAtUtc;

    public LaunchProgress()
    {
        for (var index = 0; index < StageDefinitions.Length; index++)
        {
            _stages.Add(new LaunchStage
            {
                Id = StageDefinitions[index].Id,
                TitleFallback = StageDefinitions[index].Title,
                DetailFallback = StageDefinitions[index].Detail,
                Index = index,
            });
        }
    }

    public IReadOnlyList<LaunchStage> Stages => _stages;

    public string SourceDisplay => L.T("Launch.Source.Real", "真实启动");

    public string InstanceName { get; private set; } = DefaultInstanceName;

    public string VersionLabel { get; private set; } = string.Empty;

    /// <summary>当前启动的版本 id（真实启动时有效）。</summary>
    public string VersionId { get; private set; } = string.Empty;

    public bool IsRunning
    {
        get { lock (_sync) return _running; }
    }

    public TimeSpan Elapsed => _running ? _stopwatch.Elapsed : _lastDuration;

    public CancellationToken CancellationToken
    {
        get { lock (_sync) return _cts?.Token ?? CancellationToken.None; }
    }

    /// <summary>最后一次启动的收尾结果；尚未结束时为 null。</summary>
    public LaunchCompletedEventArgs? LastResult
    {
        get { lock (_sync) return _lastResult; }
    }

    /// <summary>已产生的终端日志（页面重建时用于恢复历史）。</summary>
    public IReadOnlyList<LaunchLogLine> History
    {
        get { lock (_sync) return _history.ToArray(); }
    }

    /// <summary>启动开始时触发（视图据此清空日志 / 重置阶段行 / 开始计时）。</summary>
    public event EventHandler? Started;

    public event EventHandler<LaunchStageChangedEventArgs>? StageChanged;

    public event EventHandler<LaunchLogLine>? LogAppended;

    /// <summary>清屏（终端命令 `clear`）。</summary>
    public event EventHandler? LogCleared;

    public event EventHandler<LaunchCompletedEventArgs>? Completed;

    /// <summary>取消请求（订阅方可据此终止自己的后端；内部同时 Kill 游戏进程）。</summary>
    public event Action? CancelRequested;

    /// <summary>游戏进程成功启动后触发（参数 = 进程 PID）。插件系统等外部订阅方使用。</summary>
    public event Action<int>? ProcessStarted;

    // ---------------------------------------------------------------- 数据源入口

    /// <summary>
    /// 自动启动：有可用的游戏目录 / 版本 / Java 时走真实 <c>GameLaunch</c>，
    /// 否则把失败原因落到阶段上（不会假装启动成功）。已在运行时直接返回。
    /// </summary>
    public void StartAuto()
    {
        if (IsRunning) return;
        if (LaunchRequestResolver.TryResolve(out MinecraftLaunchOptions options, out string displayName,
                out string versionLabel, out string reason))
        {
            StartReal(options, displayName, versionLabel);
            return;
        }

        ResetForRun(LaunchRequestResolver.FallbackDisplayName, null);
        FailStage("verify", reason);
    }

    /// <summary>用宿主给定的 <see cref="MinecraftLaunchOptions"/> 跑真实启动。</summary>
    public bool StartReal(MinecraftLaunchOptions options, string? displayName = null, string? versionLabel = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (IsRunning) return false;

        string versionId = options.VersionId ?? string.Empty;
        string name = string.IsNullOrWhiteSpace(displayName)
            ? (versionId.Length > 0 ? "Minecraft " + versionId : DefaultInstanceName)
            : displayName;
        ResetForRun(name, versionLabel ?? versionId);
        VersionId = versionId;

        CancellationToken token;
        lock (_sync) token = _cts?.Token ?? CancellationToken.None;

        // 把 GameLaunch 的日志与进程输出接到终端；阶段按回调证据推进，不伪造百分比
        options.LogCallback = message => AppendLog(LaunchLogLevel.Info, "[launch] " + message);
        options.GameOutputCallback = HandleGameOutput;

        AppendLog(LaunchLogLevel.Info, L.T("Launch.Log.Real.Start", "[launcher] 游戏目录：{0}", options.MinecraftDirectory));
        AppendLog(LaunchLogLevel.Info, L.T("Launch.Log.Real.Version", "[launcher] 目标版本：{0} · Java：{1}", versionId, options.JavaExecutable));
        SetStage("verify", 0, LaunchStageState.Running,
            L.T("Launch.Detail.PreVerifying", "MofuCore：解析版本 / 继承链 / 依赖库 / natives / Java"));

        _ = RunRealAsync(options, token);
        return true;
    }

    /// <summary>取消当前启动：真实启动会同时取消准备流程并终止游戏进程。</summary>
    public bool Cancel()
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            if (!_running) return false;
            cts = _cts;
        }

        cts?.Cancel();
        CancelRequested?.Invoke();

        MinecraftLaunchResult? launching;
        lock (_sync) launching = _launchResult;
        if (launching is not null)
        {
            try
            {
                if (launching.IsRunning) launching.Kill();
            }
            catch (Exception)
            {
                // 进程可能刚好自行退出
            }
        }

        GameLaunch.Shared.TryStopGame();
        FinishRun(false, true, launching?.ExitCode, L.T("Launch.Status.Cancelled", "已取消"), TimeSpan.Zero);
        return true;
    }

    /// <summary>结束（成功或失败收尾）；退出码为空表示流程被外部中断。</summary>
    public void Complete(int? exitCode = null, string? message = null)
    {
        if (!IsRunning) return;
        foreach (LaunchStage stage in _stages)
        {
            if (stage.State is LaunchStageState.Pending or LaunchStageState.Running)
            {
                SetStage(stage.Id, 1, LaunchStageState.Succeeded, stage.Detail);
            }
        }
        FinishRun(true, false, exitCode, message ?? L.T("Launch.Phase.Finished", "启动已结束"), _stopwatch.Elapsed);
    }

    /// <summary>失败收尾：当前运行阶段标记失败，其余等待阶段标记跳过。</summary>
    public void Fail(string message)
    {
        LaunchStage? running = _stages.FirstOrDefault(stage => stage.State == LaunchStageState.Running);
        FailStage(running?.Id ?? "verify", message);
    }

    /// <summary>指定阶段失败收尾（真实启动用：把失败落到正确的步骤上）。</summary>
    public void FailStage(string stageId, string message)
    {
        if (!IsRunning) return;
        LaunchStage? stage = _stages.FirstOrDefault(item => string.Equals(item.Id, stageId, StringComparison.OrdinalIgnoreCase));
        if (stage is not null)
        {
            SetStage(stage.Id, Math.Max(stage.Progress, 0.01), LaunchStageState.Failed, message);
            foreach (LaunchStage pending in _stages.Where(item => item.State == LaunchStageState.Pending))
            {
                SetStage(pending.Id, 0, LaunchStageState.Skipped, L.T("Launch.Detail.Skipped", "跳过"));
            }
        }
        AppendLog(LaunchLogLevel.Error, L.T("Launch.Log.Real.Failed", "[launcher] 启动失败：{0}", message));
        FinishRun(false, false, null, message, _stopwatch.Elapsed);
    }

    // ---------------------------------------------------------------- 后端桥接 API

    /// <summary>更新阶段进度（0..1）；stageId 为 <see cref="Stages"/> 的 Id。</summary>
    public void ReportStage(string stageId, double progress, string? detail = null, LaunchStageState state = LaunchStageState.Running)
        => SetStage(stageId, Math.Clamp(progress, 0, 1), state, detail);

    /// <summary>把阶段标记为成功（进度补到 100%）。</summary>
    public void MarkStage(string stageId, string? detail = null)
        => SetStage(stageId, 1, LaunchStageState.Succeeded, detail);

    /// <summary>跳过阶段。</summary>
    public void SkipStage(string stageId, string? detail = null)
        => SetStage(stageId, 0, LaunchStageState.Skipped, detail ?? L.T("Launch.Detail.Skipped", "跳过"));

    /// <summary>GameLaunch 的 stdout 回调：追加一行终端日志。</summary>
    public void AppendStdout(string line) => AppendLog(LaunchLogLevel.Info, line);

    /// <summary>GameLaunch 的 stderr 回调：追加一行错误日志。</summary>
    public void AppendStderr(string line) => AppendLog(LaunchLogLevel.Error, line);

    public void AppendLog(LaunchLogLevel level, string text)
    {
        LaunchLogLine line = new(DateTimeOffset.Now, level, text);
        lock (_sync)
        {
            _history.Add(line);
            if (_history.Count > MaxHistoryLines) _history.RemoveAt(0);
            AppendLogTail(text);
        }
        LogAppended?.Invoke(this, line);
    }

    // ---------------------------------------------------------------- 终端命令

    /// <summary>处理终端输入；返回是否已被识别（视图只负责回显）。</summary>
    public bool SubmitCommand(string? input)
    {
        string command = (input ?? string.Empty).Trim();
        if (command.Length == 0) return false;

        AppendLog(LaunchLogLevel.Command, "❯ " + command);
        string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case "help":
                AppendLog(LaunchLogLevel.Info, L.T("Launch.Command.Help.Usage", "可用命令：help 帮助 · status 阶段状态 · stop 取消启动 · retry 重新启动 · clear 清屏"));
                AppendLog(LaunchLogLevel.Info, L.T("Launch.Command.Help.Source",
                    "当前来源：{0}；这里显示 MofuCore.GameLaunch 的 Java 进程输出。", SourceDisplay));
                return true;

            case "status":
            {
                foreach (LaunchStage stage in _stages)
                {
                    AppendLog(stage.State switch
                    {
                        LaunchStageState.Succeeded => LaunchLogLevel.Success,
                        LaunchStageState.Failed => LaunchLogLevel.Error,
                        _ => LaunchLogLevel.Info,
                    }, L.T("Launch.Command.Status.Row", "[status] {0}/{1} {2} · {3} · {4}%",
                        stage.Index + 1, _stages.Count, stage.Title, StateDisplay(stage.State), stage.Progress * 100));
                }
                AppendLog(LaunchLogLevel.Info, L.T("Launch.Command.Status.Source", "[status] 来源：{0} · 版本：{1}",
                    SourceDisplay, VersionId.Length > 0 ? VersionId : "—"));
                return true;
            }

            case "stop":
            case "cancel":
                if (!Cancel()) AppendLog(LaunchLogLevel.Warning, L.T("Launch.Command.NoActive", "[launcher] 当前没有正在进行的启动。"));
                return true;

            case "replay":
            case "retry":
                AppendLog(LaunchLogLevel.Info, L.T("Launch.Command.Retry", "[launcher] 重新启动…"));
                StartAuto();
                return true;

            case "clear":
            case "cls":
                LogCleared?.Invoke(this, EventArgs.Empty);
                return true;

            default:
                AppendLog(LaunchLogLevel.Warning, L.T("Launch.Command.Unknown", "未知命令：{0}（输入 help 查看可用命令）", parts[0]));
                return false;
        }
    }

    private static string StateDisplay(LaunchStageState state) => state switch
    {
        LaunchStageState.Running => L.T("Launch.Status.InProgress", "进行中"),
        LaunchStageState.Succeeded => L.T("Launch.Status.Completed", "已完成"),
        LaunchStageState.Failed => L.T("Launch.Status.Error", "失败"),
        LaunchStageState.Skipped => L.T("Launch.Status.Skipped", "跳过"),
        _ => L.T("Launch.Status.Pending", "等待中"),
    };

    // ---------------------------------------------------------------- 真实启动流程

    private async Task RunRealAsync(MinecraftLaunchOptions options, CancellationToken token)
    {
        try
        {
            // 启动前钩子：插件可注入 JVM 参数或拦截启动（ILaunchAction.LaunchingAsync）
            AppendLog(LaunchLogLevel.Info, L.T("Launch.Log.Real.CallingGameLaunch", "[launcher] 调用 MofuCore.GameLaunch 开始启动…"));
            (bool hooksProceed, string? hookReason) = await Services.Plugins.PluginManager
                .RunLaunchHooks(options, VersionId).ConfigureAwait(false);
            if (!hooksProceed)
            {
                AppendLog(LaunchLogLevel.Warning, L.T("Launch.Log.HookBlocked", "[launcher] 插件阻止了启动：{0}", hookReason));
                FailStage("verify", L.T("Launch.Hook.Blocked", "插件阻止了启动：{0}", hookReason));
                return;
            }

            // 新实例（options.txt 里还没有 lang 键）第一次启动：把游戏语言写成系统语言
            if (!string.IsNullOrWhiteSpace(options.GameDirectory))
            {
                GameLaunch.SyncInstanceLanguage(options.GameDirectory,
                    message => AppendLog(LaunchLogLevel.Info,
                        L.T("Launch.Log.Language", "[launcher] {0}", message)));
            }

            MinecraftLaunchResult result = await GameLaunch.Shared.LaunchAsync(options, token).ConfigureAwait(false);
            ProcessStarted?.Invoke(result.Pid);
            lock (_sync) _launchResult = result;

            // GameLaunch 内部一次完成版本解析 / 依赖库 / natives / Java 定位 / 参数装配，
            // 这里以「进程已启动」为证据把这些阶段全部收尾（不伪造中间百分比）。
            string pidText = L.T("Launch.Log.Real.ProcessStarted", "[launcher] Java 进程已启动（PID {0}）", result.Pid);
            MarkStage("java", L.T("Launch.Detail.Java", "由 MofuCore 定位并校验 Java 运行时"));
            MarkStage("verify", L.T("Launch.Detail.Verify", "版本 JSON / 继承链 / 依赖库校验通过"));
            MarkStage("download", L.T("Launch.Detail.Download", "依赖文件已就绪"));
            MarkStage("natives", L.T("Launch.Detail.Natives", "natives 已解压到实例目录"));
            MarkStage("arguments", L.T("Launch.Detail.Arguments", "启动参数已装配"));
            SetStage("process", 1, LaunchStageState.Running, pidText);
            SetStage("window", 0, LaunchStageState.Running, L.T("Launch.Detail.WindowWaiting", "等待游戏窗口输出（渲染线程）"));
            AppendLog(LaunchLogLevel.Success, pidText);

            int exitCode = await result.WaitForExitAsync().ConfigureAwait(false);
            lock (_sync) _launchResult = null;
            if (!IsRunning) return;

            if (result.StoppedManually || token.IsCancellationRequested)
            {
                FinishRun(true, true, exitCode, L.T("Launch.Log.Real.Stopped", "游戏已停止"), _stopwatch.Elapsed);
                return;
            }

            if (exitCode == 0)
            {
                MarkStage("process", L.T("Launch.Detail.ProcessExit", "游戏进程已退出"));
                MarkStage("window", L.T("Launch.Detail.WindowClosed", "游戏窗口已正常关闭"));
                Complete(0, L.T("Launch.Log.Real.Exited", "游戏已正常退出"));
                return;
            }

            string crashSummary = DiagnoseCrash(options, exitCode);
            MarkStage("process", L.T("Launch.Detail.ProcessCrash", "游戏进程异常退出（exit {0}）", exitCode));
            FailStage("window", crashSummary);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            FinishRun(false, true, null, L.T("Launch.Status.Cancelled", "启动已取消"), TimeSpan.Zero);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            AppendLog(LaunchLogLevel.Error, L.T("Launch.Log.Real.Failed", "[launcher] 启动失败：{0}", e.Message));
            FailStage(ResolveFailingStage(), e.Message);
        }
    }

    private void HandleGameOutput(string line, bool isError)
    {
        AppendLog(isError ? LaunchLogLevel.Error : LaunchLogLevel.Info, line);

        if (_windowReady) return;
        foreach (string hint in WindowReadyHints)
        {
            if (line.Contains(hint, StringComparison.OrdinalIgnoreCase))
            {
                _windowReady = true;
                MarkStage("process", L.T("Launch.Detail.ProcessStart", "Java 进程已启动"));
                MarkStage("window", L.T("Launch.Detail.WindowReady", "游戏窗口已就绪（检测到渲染线程输出）"));
                AppendLog(LaunchLogLevel.Success, L.T("Launch.Log.Real.WindowReady", "[launcher] 检测到游戏渲染线程输出，窗口已就绪"));
                break;
            }
        }
    }

    private string DiagnoseCrash(MinecraftLaunchOptions options, int exitCode)
    {
        string message = L.T("Launch.Log.Crash.Exit", "游戏异常退出（exit {0}）", exitCode);
        try
        {
            string gameDirectory = string.IsNullOrWhiteSpace(options.GameDirectory)
                ? options.MinecraftDirectory
                : options.GameDirectory;
            string tail;
            DateTimeOffset startedAt;
            lock (_sync)
            {
                tail = _logTail.ToString();
                startedAt = _runStartedAtUtc;
            }

            // 崩溃即自动分析：日志末尾 + 本次运行新生成的 crash-reports + hs_err_pid，见 MofuCore.Log.LogAnalysis
            LogAnalysisResult analysis = LogAnalysis.Analyze(new LogAnalysisInput
            {
                LogText = tail,
                CrashReportsDirectory = Path.Combine(gameDirectory, "crash-reports"),
                GameDirectory = gameDirectory,
                ExitCode = exitCode,
                MinecraftVersion = options.VersionId,
                MemoryMb = options.MaximumMemoryMb,
                GameStartedAt = startedAt,
            });

            AppendLog(LaunchLogLevel.Warning, L.T("Launch.Log.Diagnosis.Summary", "[诊断] {0}", analysis.Summary));
            foreach (LogAnalysisFinding finding in analysis.Findings)
            {
                AppendLog(LaunchLogLevel.Warning, L.T("Launch.Log.Diagnosis.Reason", "[诊断] 可能原因：{0}", finding.Title));
                foreach (string decision in finding.Decisions)
                {
                    AppendLog(LaunchLogLevel.Warning, L.T("Launch.Log.Diagnosis.Suggestion", "[建议] {0}", decision));
                }

                foreach (string line in finding.Evidence)
                {
                    AppendLog(LaunchLogLevel.Warning, "  " + line);
                }
            }

            foreach (ModDependencyIssue issue in analysis.ModIssues)
            {
                AppendLog(LaunchLogLevel.Warning, L.T("Launch.Log.Diagnosis.ModIssue", "[诊断] 模组问题：{0}", issue.ToString()));
            }

            if (!string.IsNullOrWhiteSpace(analysis.Summary)) message += "：" + analysis.Summary;

            ShowCrashPopup(analysis);
        }
        catch (Exception e)
        {
            AppendLog(LaunchLogLevel.Warning, L.T("Launch.Log.Diagnosis.Failed", "[诊断] 崩溃诊断失败：{0}", e.Message));
        }
        return message;
    }

    /// <summary>游戏崩溃时直接弹分析弹窗（结论 + 证据 + 决策），而不是只把诊断写进终端。</summary>
    private void ShowCrashPopup(LogAnalysisResult analysis)
    {
        string instanceName = InstanceName;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                Shell.Dialog(
                    CrashDialogContent.Build(analysis, instanceName),
                    L.T("Crash.Dialog.Title", "游戏崩溃分析"));
            }
            catch
            {
                // 弹窗构建失败不影响主流程；诊断已写入终端日志
            }
        });
    }

    private string ResolveFailingStage()
    {
        LaunchStage? running = _stages.FirstOrDefault(stage => stage.State == LaunchStageState.Running);
        return running?.Id ?? "verify";
    }

    private void AppendLogTail(string text)
    {
        _logTail.Append(text).Append('\n');
        if (_logTail.Length > MaxLogTailChars)
        {
            _logTail.Remove(0, _logTail.Length - MaxLogTailChars);
        }
    }

    // ---------------------------------------------------------------- 内部实现

    private void ResetForRun(string? instanceName, string? versionLabel)
    {
        CancellationTokenSource? previous;
        lock (_sync)
        {
            previous = _cts;
            _launchResult = null;
        }
        previous?.Cancel();

        CancellationTokenSource cts = new();
        lock (_sync)
        {
            _cts = cts;
            _running = true;
            _windowReady = false;
            _lastDuration = TimeSpan.Zero;
            _stopwatch.Restart();
            _runStartedAtUtc = DateTimeOffset.UtcNow;
            _history.Clear();
            _logTail.Clear();
            _lastResult = null;
            InstanceName = string.IsNullOrWhiteSpace(instanceName) ? DefaultInstanceName : instanceName.Trim();
            VersionLabel = string.IsNullOrWhiteSpace(versionLabel) ? string.Empty : versionLabel.Trim();
            VersionId = string.Empty;
            foreach (LaunchStage stage in _stages)
            {
                stage.Progress = 0;
                stage.Duration = null;
                stage.ResetDetail();
                stage.State = LaunchStageState.Pending;
            }
        }

        Started?.Invoke(this, EventArgs.Empty);
        AppendLog(LaunchLogLevel.Info, L.T("Launch.Log.Init", "[launcher] 初始化启动流程 · {0}", InstanceName));
    }

    private void SetStage(string stageId, double progress, LaunchStageState state, string? detail)
    {
        LaunchStage? stage = _stages.FirstOrDefault(item => string.Equals(item.Id, stageId, StringComparison.OrdinalIgnoreCase));
        if (stage is null) return;

        double overall;
        string activity;
        lock (_sync)
        {
            stage.State = state;
            stage.Progress = progress;
            if (!string.IsNullOrWhiteSpace(detail)) stage.Detail = detail;
            if (state is LaunchStageState.Succeeded or LaunchStageState.Failed or LaunchStageState.Skipped)
            {
                stage.Duration ??= TimeSpan.Zero;
            }
            overall = _stages.Sum(item => item.Progress) / _stages.Count * 100;
            activity = string.IsNullOrWhiteSpace(detail) ? stage.Title : detail;
        }

        StageChanged?.Invoke(this, new LaunchStageChangedEventArgs
        {
            Stage = stage,
            OverallProgress = overall,
            Activity = activity,
        });
    }

    private void FinishRun(bool success, bool cancelled, int? exitCode, string message, TimeSpan duration)
    {
        TimeSpan total;
        lock (_sync)
        {
            if (!_running) return;
            _running = false;
            _lastDuration = duration > TimeSpan.Zero ? duration : _stopwatch.Elapsed;
            _stopwatch.Stop();
            foreach (LaunchStage stage in _stages)
            {
                if (stage.State == LaunchStageState.Running)
                {
                    stage.State = cancelled ? LaunchStageState.Skipped : LaunchStageState.Succeeded;
                    stage.Duration ??= TimeSpan.Zero;
                }
            }
            total = _lastDuration;
        }

        AppendLog(cancelled ? LaunchLogLevel.Warning : success ? LaunchLogLevel.Success : LaunchLogLevel.Error,
            L.T("Launch.Log.Finish", "[launcher] {0}（用时 {1:0.0}s）", message, total.TotalSeconds));

        LaunchCompletedEventArgs completed = new()
        {
            Success = success,
            Cancelled = cancelled,
            ExitCode = exitCode,
            Message = message,
            Duration = total,
        };
        lock (_sync) _lastResult = completed;
        Completed?.Invoke(this, completed);
    }
}

/// <summary>
/// 从本地配置解析一次真实启动所需的 <see cref="MinecraftLaunchOptions"/>：
/// 游戏目录（config.minecraftPath）→ 选中实例（versions/&lt;实例名&gt;）→ Java（全局/实例配置/JAVA_HOME）。
/// 任一项缺失都返回 false 并给出可读原因，由调用方把失败原因展示在启动控制台。
/// </summary>
internal static class LaunchRequestResolver
{
    public static string FallbackDisplayName
    {
        get
        {
            InstanceInfo? instance = SelectedInstance();
            return instance is null ? LaunchProgress.DefaultInstanceName : $"Minecraft {instance.Version} · {instance.Loader}";
        }
    }

    public static bool TryResolve(
        out MinecraftLaunchOptions options,
        out string displayName,
        out string versionLabel,
        out string reason)
    {
        options = new MinecraftLaunchOptions();
        InstanceInfo? instance = SelectedInstance();
        displayName = instance is null ? LaunchProgress.DefaultInstanceName : $"Minecraft {instance.Version} · {instance.Loader}";
        versionLabel = instance?.Name ?? string.Empty;
        reason = string.Empty;

        string minecraftDirectory = (YamlRead.GameDirectory ?? string.Empty).Trim();
        if (minecraftDirectory.Length == 0)
        {
            reason = L.T("Launch.Reason.NoGameDirectory", "未配置 Minecraft 游戏目录");
            return false;
        }
        if (!Directory.Exists(minecraftDirectory))
        {
            reason = L.T("Launch.Reason.DirectoryMissing", "游戏目录不存在：{0}", minecraftDirectory);
            return false;
        }
        if (instance is null || versionLabel.Length == 0)
        {
            reason = L.T("Launch.Reason.NoInstance", "没有可用的实例");
            return false;
        }

        string versionJson = Path.Combine(minecraftDirectory, "versions", versionLabel, versionLabel + ".json");
        if (!File.Exists(versionJson))
        {
            reason = L.T("Launch.Reason.VersionMissing", "未找到版本 {0}（{1}）", versionLabel, versionJson);
            return false;
        }

        GlobalLaunchSettings settings = YamlRead.LoadGlobalLaunchSettings();
        string javaExecutable = ResolveJavaExecutable(settings.JavaExecutable, instance.JavaPath);

        string gameDirectory = minecraftDirectory;
        if (YamlRead.DefaultVersionIsolation)
        {
            // 隔离目录缺失时创建而不是静默退回共根目录，否则存档/mods 会落回公共目录
            gameDirectory = Path.Combine(minecraftDirectory, "versions", versionLabel);
            try
            {
                Directory.CreateDirectory(gameDirectory);
                foreach (string sub in (string[])["mods", "saves", "resourcepacks", "config"])
                {
                    Directory.CreateDirectory(Path.Combine(gameDirectory, sub));
                }
            }
            catch (Exception)
            {
                gameDirectory = minecraftDirectory;
            }
        }

        int minimumMemory = Math.Max(512, settings.MinimumMemoryMb);
        int maximumMemory = instance.MemoryMb > 0 ? instance.MemoryMb : settings.MaximumMemoryMb;
        if (maximumMemory < minimumMemory) maximumMemory = minimumMemory;

        string username = LocalSettings.Current.SelectedAccountName?.Trim() ?? string.Empty;
        if (!GameLaunch.IsValidOfflineUsername(username)) username = "Mofumofu";

        options = new MinecraftLaunchOptions
        {
            MinecraftDirectory = minecraftDirectory,
            GameDirectory = gameDirectory,
            VersionId = versionLabel,
            Account = OfflineAccount.Create(username),
            JavaExecutable = javaExecutable,
            JavaRuntimeDirectory = JavaDownload.GetRuntimeDirectory(minecraftDirectory),
            MinimumMemoryMb = minimumMemory,
            MaximumMemoryMb = maximumMemory,
            WindowWidth = settings.WindowWidth,
            WindowHeight = settings.WindowHeight,
            ProcessPriority = settings.ProcessPriority,
            WrapperCommand = settings.WrapperCommand,
            LaunchFullscreen = settings.LaunchFullscreen,
            AdditionalJvmArguments = [.. settings.AdditionalJvmArguments],
            AdditionalGameArguments = [.. settings.AdditionalGameArguments],
        };
        foreach ((string key, string value) in ParseEnvironmentVariables(settings.AdditionalEnvironmentVariables))
        {
            options.EnvironmentVariables[key] = value;
        }
        return true;
    }

    /// <summary>从磁盘上的 versions/ 找启动目标（实例名 = 版本目录名）。</summary>
    private static InstanceInfo? SelectedInstance()
    {
        try
        {
            string root = (YamlRead.GameDirectory ?? string.Empty).Trim();
            if (root.Length == 0 || !Directory.Exists(root)) return null;

            string versionsRoot = Path.Combine(root, "versions");
            if (!Directory.Exists(versionsRoot)) return null;

            string selectedId = LocalSettings.Current.SelectedInstanceId;
            // 只扫一遍：没有 <id>.json 的目录（备份/半成品）不算实例；
            // 选中项优先，其 JSON 坏掉时再依次尝试其余实例
            List<string> candidateIds = [];
            foreach (string directory in Directory.GetDirectories(versionsRoot))
            {
                string? name = Path.GetFileName(directory);
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!File.Exists(Path.Combine(directory, name + ".json"))) continue;
                if (string.Equals(name, selectedId, StringComparison.OrdinalIgnoreCase))
                {
                    candidateIds.Insert(0, name);
                }
                else
                {
                    candidateIds.Add(name);
                }
            }

            MofuCore.Launch.VersionLoader versionLoader = new();
            MofuCore.Launch.LaunchProfile? profile = null;
            string id = string.Empty;
            foreach (string candidate in candidateIds)
            {
                profile = versionLoader.TryLoad(root, candidate);
                if (profile is not null)
                {
                    id = candidate;
                    break;
                }
            }

            if (profile is null) return null;
            string probe = (profile.InheritsFrom + " " + profile.SourceId + " " + id).ToLowerInvariant();
            string loader = probe.Contains("neoforge") ? "NeoForge"
                : probe.Contains("quilt") ? "Quilt"
                : probe.Contains("fabric") ? "Fabric"
                : probe.Contains("forge") ? "Forge"
                : "Vanilla";
            string version = string.IsNullOrWhiteSpace(profile.InheritsFrom) ? id : profile.InheritsFrom;

            return new InstanceInfo(
                id,
                id,
                version,
                loader,
                "avares://Mofumofu.Launcher/Assets/instance-icons/vanilla.png",
                string.Empty,
                string.Empty,
                YamlRead.JavaExecutable,
                YamlRead.LoadGlobalLaunchSettings().MaximumMemoryMb,
                0,
                0,
                true);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ResolveJavaExecutable(string configured, string instanceJava)
    {
        string trimmed = (configured ?? string.Empty).Trim().Trim('"');
        if (trimmed.Length > 0 &&
            !string.Equals(trimmed, YamlRead.Keys.AutomaticJavaValue, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(trimmed))
        {
            return trimmed;
        }

        string instanceTrimmed = (instanceJava ?? string.Empty).Trim().Trim('"');
        if (instanceTrimmed.Length > 0 && File.Exists(instanceTrimmed))
        {
            return instanceTrimmed;
        }

        string? javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            string candidate = Path.Combine(javaHome, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
            if (File.Exists(candidate)) return candidate;
        }

        return string.Empty;
    }

    private static (string Key, string Value)[] ParseEnvironmentVariables(IEnumerable<string>? entries)
    {
        List<(string Key, string Value)> result = [];
        foreach (string? entry in entries ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            int index = entry.IndexOf('=');
            if (index <= 0 || index >= entry.Length - 1) continue;
            string key = entry[..index].Trim();
            if (key.Length == 0) continue;
            result.Add((key, entry[(index + 1)..].Trim()));
        }
        return result.ToArray();
    }
}
