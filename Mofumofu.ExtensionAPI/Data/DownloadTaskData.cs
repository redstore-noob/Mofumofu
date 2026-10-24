namespace Mofumofu.ExtensionAPI.Data;

/// <summary>下载任务状态（与宿主下载页展示一致）。</summary>
public enum DownloadTaskState
{
    Downloading,
    Paused,
    Done,
    Cancelled,
    Failed,
}

/// <summary>
/// 一个下载任务的快照。用 <see cref="Number"/>（4 位数字编号，展示补零如 0007）
/// 在 IDownloadAction 里定位与操作任务。
/// </summary>
/// <param name="Number">4 位数字任务编号（会话内唯一）</param>
/// <param name="Title">任务标题</param>
/// <param name="State">任务状态</param>
/// <param name="Percent">进度 0~100</param>
/// <param name="SpeedText">速度文本（宿主已格式化）</param>
/// <param name="SizeText">大小文本（宿主已格式化）</param>
/// <param name="CanPause">该任务是否支持暂停/继续</param>
public sealed record DownloadTaskData(
    int Number,
    string Title,
    DownloadTaskState State,
    double Percent,
    string SpeedText,
    string SizeText,
    bool CanPause);
