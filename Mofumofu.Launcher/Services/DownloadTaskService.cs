using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using MofuCore.Download.Minecraft;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Services;

/// <summary>下载任务的统一状态。</summary>
public enum DownloadTaskState
{
    Downloading,
    Paused,
    Done,
    Cancelled,
    Failed,
}

/// <summary>下载任务的一次快照（事件参数 / 列表读取）。</summary>
public sealed record DownloadTaskSnapshot(
    string Id,
    string ShortId,
    string Title,
    string Detail,
    double Percent,
    string SpeedText,
    string SizeText,
    DownloadTaskState State,
    bool CanPause);

/// <summary>
/// 一个下载任务的句柄：由下载发起方（任何页面）持有，用来汇报进度与收尾。
/// 任务会自动出现在下载页的「下载任务」列表里，无论发起自哪个页面。
/// </summary>
public sealed class DownloadTaskHandle
{
    private readonly DownloadTaskCore _owner;

    internal DownloadTaskHandle(DownloadTaskCore owner, string id, int shortId, CancellationToken token)
    {
        _owner = owner;
        Id = id;
        ShortId = shortId;
        Token = token;
    }

    public string Id { get; }

    /// <summary>4 位数字任务编号（会话内唯一，如 7 → "0007"），展示用。</summary>
    public int ShortId { get; }

    /// <summary>任务取消令牌：下载方观察它来响应下载页的「取消」按钮。</summary>
    public CancellationToken Token { get; }

    /// <summary>汇报进度（0..100）；detail/speed/size 传 null 表示保持不变。
    /// detail 变化会作为一行写入任务日志（下载页点卡片可看）。</summary>
    public void Report(double percent, string? detail = null, string? speed = null, string? size = null)
        => _owner.Report(Id, percent, detail, speed, size);

    public void Complete(string? detail = null) => _owner.Finish(Id, DownloadTaskState.Done, detail);

    public void Fail(string message) => _owner.Finish(Id, DownloadTaskState.Failed, message);

    /// <summary>下载流程观察到取消令牌后调用；若用户先点了取消，这里是幂等的。</summary>
    public void MarkCancelled() => _owner.Finish(Id, DownloadTaskState.Cancelled, null);

    /// <summary>往任务日志写一行（时间戳自动加，下载页点开任务卡片即可查看）。</summary>
    public void Log(string message) => _owner.AppendLog(Id, message);

    /// <summary>
    /// 登记取消时需要清理的路径：文件直接删，目录递归删。
    /// 只应登记本次任务新生成的目标（登记前用 File.Exists / Directory.Exists 过滤已有文件，
    /// 避免取消时误删用户原有的安装）。
    /// </summary>
    public void RegisterCleanup(params string[] paths) => _owner.RegisterCleanup(Id, paths);
}

/// <summary>
/// 全局下载任务中心：启动器里所有下载（Minecraft/实例、Modrinth 资源、Java、
/// 服务端核心、联机组件、整合包导入……）都从这里登记，下载页统一渲染。
/// 每个任务带环形日志（点任务卡查看）；取消时按登记清理已下载的文件。
/// </summary>
public static class DownloadTaskService
{
    private static readonly DownloadTaskCore Core = new();

    /// <summary>新任务出现（按发起顺序排在列表末尾）。</summary>
    public static Action<DownloadTaskSnapshot>? Added;

    /// <summary>任务进度 / 状态变化。</summary>
    public static Action<DownloadTaskSnapshot>? Changed;

    /// <summary>下载页用它感知「清理已完成」（其它订阅方忽略）。</summary>
    public static Action? FinishedCleared;

    /// <summary>开始一个下载任务并登记到列表。</summary>
    public static DownloadTaskHandle Start(string title, string detail, bool canPause = false)
        => Core.Start(title, detail, canPause);

    /// <summary>用户请求取消（下载页「取消」按钮）。返回是否存在该任务。</summary>
    public static bool TryCancel(string id) => Core.TryCancel(id);

    /// <summary>用户暂停 / 继续某个任务。有任意任务处于暂停时全局下载闸门合上，
    /// 全部恢复后重新打开（MofuCore 的下载流程统一走全局闸门）。</summary>
    public static bool TrySetPaused(string id, bool paused) => Core.TrySetPaused(id, paused);

    /// <summary>移除全部已完成任务。</summary>
    public static void RemoveFinished()
    {
        Core.RemoveFinished();
        FinishedCleared?.Invoke();
    }

    /// <summary>取一个任务的日志快照（时间戳 + 事件行）；任务不存在时返回空表。</summary>
    public static IReadOnlyList<string> GetLog(string id) => Core.GetLog(id);

    public static IReadOnlyList<DownloadTaskSnapshot> Snapshots => Core.Snapshots();
}

/// <summary>任务中心的内部实现（事件统一在 UI 线程上触发）。</summary>
internal sealed class DownloadTaskCore
{
    private const int LogCapacity = 400;

    private sealed class Entry
    {
        public string Id = string.Empty;
        public int ShortId;
        public string Title = string.Empty;
        public bool CanPause;
        public string Detail = string.Empty;
        public double Percent;
        public string SpeedText = "—";
        public string SizeText = "—";
        public DownloadTaskState State = DownloadTaskState.Downloading;
        public CancellationTokenSource? Cancellation;
        public List<string> Log = new();
        public string LastDetail = string.Empty;
        public List<string> CleanupPaths = new();
        public bool CleanupStarted;

        public string ShortIdText => ShortId.ToString("0000");

        public DownloadTaskSnapshot Snapshot() =>
            new(Id, ShortIdText, Title, Detail, Percent, SpeedText, SizeText, State, CanPause);
    }

    private readonly object _sync = new();
    private readonly List<Entry> _tasks = [];
    private int _sequence;
    private int _nextShortId = 1;

    public DownloadTaskHandle Start(string title, string detail, bool canPause)
    {
        Entry entry = new()
        {
            Id = "task-" + Interlocked.Increment(ref _sequence),
            Title = title,
            Detail = detail,
            CanPause = canPause,
            Cancellation = new CancellationTokenSource(),
            LastDetail = detail,
        };

        lock (_sync)
        {
            entry.ShortId = AllocateShortId();
            _tasks.Add(entry);
            AddLine(entry, L.T("Download.Log.Start", "任务开始"));
            if (detail.Length > 0) AddLine(entry, detail);
        }

        Post(() => DownloadTaskService.Added?.Invoke(entry.Snapshot()));
        SyncPauseGate();
        return new DownloadTaskHandle(this, entry.Id, entry.ShortId, entry.Cancellation?.Token ?? CancellationToken.None);
    }

    /// <summary>分配 4 位数字任务编号（0001~9999）：跳过在用号码；绕满一圈后复用已结束任务的号码。须持有 _sync。</summary>
    private int AllocateShortId()
    {
        var used = _tasks.Select(item => item.ShortId).ToHashSet();
        for (var i = 0; i < 9999; i++)
        {
            int candidate = _nextShortId;
            _nextShortId = _nextShortId >= 9999 ? 1 : _nextShortId + 1;
            if (used.Add(candidate)) return candidate;
        }

        return _nextShortId; // 理论不可达：同时存活任务超过 9999 个才可能
    }

    public void Report(string id, double percent, string? detail, string? speed, string? size)
    {
        Entry? entry;
        lock (_sync)
        {
            entry = _tasks.FirstOrDefault(item => item.Id == id);
            if (entry is null || entry.State is not (DownloadTaskState.Downloading or DownloadTaskState.Paused))
            {
                return;
            }

            entry.Percent = Math.Clamp(percent, 0, 100);
            if (detail is not null) entry.Detail = detail;
            if (speed is not null) entry.SpeedText = speed;
            if (size is not null) entry.SizeText = size;

            // 阶段 / 文件名变化记一行日志（速度与字节数变化太频繁，不入日志）
            if (detail is not null && detail != entry.LastDetail)
            {
                entry.LastDetail = detail;
                AddLine(entry, detail);
            }
        }

        Post(() => DownloadTaskService.Changed?.Invoke(entry.Snapshot()));
    }

    public void Finish(string id, DownloadTaskState state, string? detail)
    {
        Entry? entry;
        lock (_sync)
        {
            entry = _tasks.FirstOrDefault(item => item.Id == id);
            if (entry is null || entry.State is DownloadTaskState.Done or DownloadTaskState.Cancelled
                or DownloadTaskState.Failed)
            {
                return;
            }

            entry.State = state;
            if (state == DownloadTaskState.Done) entry.Percent = 100;
            if (detail is not null) entry.Detail = detail;
            entry.SpeedText = "—";
            entry.Cancellation = null;

            switch (state)
            {
                case DownloadTaskState.Done:
                    AddLine(entry, L.T("Download.Log.Done", "下载完成"));
                    break;
                case DownloadTaskState.Failed:
                    AddLine(entry, detail is null
                        ? L.T("Download.Log.Failed", "下载失败")
                        : L.T("Download.Log.FailedReason", "下载失败：{0}", detail));
                    break;
                case DownloadTaskState.Cancelled:
                    AddLine(entry, L.T("Download.Log.Cancelled", "已取消下载"));
                    break;
            }
        }

        if (state == DownloadTaskState.Cancelled) RunCleanup(entry);
        SyncPauseGate();
        Post(() => DownloadTaskService.Changed?.Invoke(entry.Snapshot()));
    }

    public bool TryCancel(string id)
    {
        CancellationTokenSource? cts;
        Entry? entry;
        lock (_sync)
        {
            entry = _tasks.FirstOrDefault(item => item.Id == id);
            if (entry is null) return false;
            if (entry.State is DownloadTaskState.Done or DownloadTaskState.Cancelled
                or DownloadTaskState.Failed)
            {
                return true;
            }

            entry.State = DownloadTaskState.Cancelled;
            entry.SpeedText = "—";
            cts = entry.Cancellation;
            entry.Cancellation = null;
            AddLine(entry, L.T("Download.Log.UserCancel", "用户取消了下载"));
        }

        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 任务已结束
            }
        }

        RunCleanup(entry);
        SyncPauseGate();
        Post(() => DownloadTaskService.Changed?.Invoke(entry.Snapshot()));
        return true;
    }

    public bool TrySetPaused(string id, bool paused)
    {
        Entry? entry;
        lock (_sync)
        {
            entry = _tasks.FirstOrDefault(item => item.Id == id);
            if (entry is null || !entry.CanPause) return false;
            if (paused && entry.State == DownloadTaskState.Downloading)
            {
                entry.State = DownloadTaskState.Paused;
                AddLine(entry, L.T("Download.Log.Paused", "已暂停"));
            }
            else if (!paused && entry.State == DownloadTaskState.Paused)
            {
                entry.State = DownloadTaskState.Downloading;
                AddLine(entry, L.T("Download.Log.Resumed", "继续下载"));
            }
            else
            {
                return true;
            }
        }

        SyncPauseGate();
        Post(() => DownloadTaskService.Changed?.Invoke(entry.Snapshot()));
        return true;
    }

    public void RemoveFinished()
    {
        lock (_sync)
        {
            for (var index = _tasks.Count - 1; index >= 0; index--)
            {
                if (_tasks[index].State == DownloadTaskState.Done)
                {
                    _tasks.RemoveAt(index);
                }
            }
        }

        SyncPauseGate();
    }

    public IReadOnlyList<string> GetLog(string id)
    {
        lock (_sync)
        {
            return _tasks.FirstOrDefault(item => item.Id == id)?.Log.ToArray() ?? Array.Empty<string>();
        }
    }

    public IReadOnlyList<DownloadTaskSnapshot> Snapshots()
    {
        lock (_sync) return _tasks.Select(item => item.Snapshot()).ToArray();
    }

    public void AppendLog(string id, string message)
    {
        Entry? entry;
        lock (_sync)
        {
            entry = _tasks.FirstOrDefault(item => item.Id == id);
            if (entry is null) return;
            AddLine(entry, message);
        }
    }

    public void RegisterCleanup(string id, string[] paths)
    {
        lock (_sync)
        {
            var entry = _tasks.FirstOrDefault(item => item.Id == id);
            if (entry is null) return;

            foreach (string path in paths)
            {
                string trimmed = path.Trim();
                if (trimmed.Length == 0) continue;
                if (entry.CleanupPaths.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) continue;
                entry.CleanupPaths.Add(trimmed);
            }
        }
    }

    /// <summary>取消后在后台线程删除登记的文件 / 目录，结果写进任务日志。</summary>
    private void RunCleanup(Entry entry)
    {
        string[] paths;
        lock (_sync)
        {
            if (entry.CleanupStarted) return;
            entry.CleanupStarted = true;
            paths = [.. entry.CleanupPaths];
        }

        if (paths.Length == 0)
        {
            AppendLog(entry.Id, L.T("Download.Log.NoCleanup",
                "本次任务没有登记可清理的文件，已下载的部分会保留"));
            return;
        }

        AppendLog(entry.Id, L.T("Download.Log.Cleaning", "正在清理已下载的文件…"));

        _ = Task.Run(() =>
        {
            foreach (string path in paths)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        AppendLog(entry.Id, L.T("Download.Log.DeletedFile", "已删除文件 {0}", path));
                    }
                    else if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: true);
                        AppendLog(entry.Id, L.T("Download.Log.DeletedDirectory", "已删除目录 {0}", path));
                    }
                }
                catch (Exception ex)
                {
                    AppendLog(entry.Id, L.T("Download.Log.CleanupFailed", "清理失败 {0}：{1}", path, ex.Message));
                }
            }

            AppendLog(entry.Id, L.T("Download.Log.CleanupDone", "清理结束"));
        });
    }

    /// <summary>任一可暂停任务处于暂停 → 合上全局下载闸门；全部恢复 → 打开。</summary>
    private void SyncPauseGate()
    {
        bool anyPaused;
        lock (_sync)
        {
            anyPaused = _tasks.Any(item => item.CanPause && item.State == DownloadTaskState.Paused);
        }

        if (anyPaused) DownloadPauseGate.Pause();
        else DownloadPauseGate.Resume();
    }

    private static void AddLine(Entry entry, string message)
    {
        entry.Log.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        if (entry.Log.Count > LogCapacity)
        {
            entry.Log.RemoveRange(0, entry.Log.Count - LogCapacity);
        }
    }

    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}
