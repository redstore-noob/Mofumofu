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

public enum DownloadTaskState
{
    Downloading,
    Paused,
    Done,
    Cancelled,
    Failed,
}

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

    public int ShortId { get; }

    public CancellationToken Token { get; }

    public void Report(double percent, string? detail = null, string? speed = null, string? size = null)
        => _owner.Report(Id, percent, detail, speed, size);

    public void Complete(string? detail = null) => _owner.Finish(Id, DownloadTaskState.Done, detail);

    public void Fail(string message) => _owner.Finish(Id, DownloadTaskState.Failed, message);

    /// <summary>下载流程观察到取消令牌后调用；若用户先点了取消，这里是幂等的。</summary>
    public void MarkCancelled() => _owner.Finish(Id, DownloadTaskState.Cancelled, null);

    public void Log(string message) => _owner.AppendLog(Id, message);

    public void RegisterCleanup(params string[] paths) => _owner.RegisterCleanup(Id, paths);
}

public static class DownloadTaskService
{
    private static readonly DownloadTaskCore Core = new();

    public static Action<DownloadTaskSnapshot>? Added;

    public static Action<DownloadTaskSnapshot>? Changed;

    public static Action? FinishedCleared;

    public static DownloadTaskHandle Start(string title, string detail, bool canPause = false)
        => Core.Start(title, detail, canPause);

    public static bool TryCancel(string id) => Core.TryCancel(id);

    public static bool TrySetPaused(string id, bool paused) => Core.TrySetPaused(id, paused);

    public static void RemoveFinished()
    {
        Core.RemoveFinished();
        FinishedCleared?.Invoke();
    }

    public static IReadOnlyList<string> GetLog(string id) => Core.GetLog(id);

    public static IReadOnlyList<DownloadTaskSnapshot> Snapshots => Core.Snapshots();
}

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

        DownloadTaskSnapshot addedSnapshot;
        lock (_sync)
        {
            entry.ShortId = AllocateShortId();
            _tasks.Add(entry);
            AddLine(entry, L.T("Download.Log.Start", "任务开始"));
            if (detail.Length > 0) AddLine(entry, detail);
            addedSnapshot = entry.Snapshot();
        }

        Post(() => DownloadTaskService.Added?.Invoke(addedSnapshot));
        SyncPauseGate();
        return new DownloadTaskHandle(this, entry.Id, entry.ShortId, entry.Cancellation?.Token ?? CancellationToken.None);
    }

    private int AllocateShortId()
    {
        var used = _tasks.Select(item => item.ShortId).ToHashSet();
        for (var i = 0; i < 9999; i++)
        {
            int candidate = _nextShortId;
            _nextShortId = _nextShortId >= 9999 ? 1 : _nextShortId + 1;
            if (used.Add(candidate)) return candidate;
        }

        return _nextShortId;
    }

    public void Report(string id, double percent, string? detail, string? speed, string? size)
    {
        DownloadTaskSnapshot snapshot;
        lock (_sync)
        {
            var entry = _tasks.FirstOrDefault(item => item.Id == id);
            if (entry is null || entry.State is not (DownloadTaskState.Downloading or DownloadTaskState.Paused))
            {
                return;
            }

            entry.Percent = Math.Clamp(percent, 0, 100);
            if (detail is not null) entry.Detail = detail;
            if (speed is not null) entry.SpeedText = speed;
            if (size is not null) entry.SizeText = size;

            if (detail is not null && detail != entry.LastDetail)
            {
                entry.LastDetail = detail;
                AddLine(entry, detail);
            }

            snapshot = entry.Snapshot();
        }

        Post(() => DownloadTaskService.Changed?.Invoke(snapshot));
    }

    public void Finish(string id, DownloadTaskState state, string? detail)
    {
        Entry? entry;
        DownloadTaskSnapshot snapshot;
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

            snapshot = entry.Snapshot();
        }

        if (state == DownloadTaskState.Cancelled) RunCleanup(entry);
        SyncPauseGate();
        Post(() => DownloadTaskService.Changed?.Invoke(snapshot));
    }

    public bool TryCancel(string id)
    {
        CancellationTokenSource? cts;
        Entry? entry;
        DownloadTaskSnapshot snapshot;
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
            snapshot = entry.Snapshot();
        }

        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        RunCleanup(entry);
        SyncPauseGate();
        Post(() => DownloadTaskService.Changed?.Invoke(snapshot));
        return true;
    }

    public bool TrySetPaused(string id, bool paused)
    {
        DownloadTaskSnapshot snapshot;
        lock (_sync)
        {
            var entry = _tasks.FirstOrDefault(item => item.Id == id);
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

            snapshot = entry.Snapshot();
        }

        SyncPauseGate();
        Post(() => DownloadTaskService.Changed?.Invoke(snapshot));
        return true;
    }

    public void RemoveFinished()
    {
        lock (_sync)
        {
            for (var index = _tasks.Count - 1; index >= 0; index--)
            {
                if (_tasks[index].State is DownloadTaskState.Done or DownloadTaskState.Cancelled
                    or DownloadTaskState.Failed)
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
            try
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
            }
            catch (Exception ex)
            {
                // 兜底：任何漏出的异常都写日志，绝不成为未观察异常。
                AppendLog(entry.Id, L.T("Download.Log.CleanupFailed", "清理失败 {0}：{1}", "清理任务", ex.Message));
            }
        }).ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);
    }

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
