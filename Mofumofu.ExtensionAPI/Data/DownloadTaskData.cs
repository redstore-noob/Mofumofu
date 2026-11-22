namespace Mofumofu.ExtensionAPI.Data;

/// <summary>下载任务状态。</summary>
/// <remarks>序号显式固定，禁止依赖隐式序号；持久化 / 跨进程传递时不要假设成员顺序。</remarks>
public enum DownloadTaskState
{
    Downloading = 0,
    Paused = 1,
    Done = 2,
    Cancelled = 3,
    Failed = 4,
}

/// <summary>
/// 一个下载任务的快照。用 <see cref="Number"/>（4 位数字编号，展示补零如 0007）在 IDownloadAction 里定位任务。
/// </summary>
public sealed record DownloadTaskData(
    int Number,
    string Title,
    DownloadTaskState State,
    double Percent,
    string SpeedText,
    string SizeText,
    bool CanPause);
