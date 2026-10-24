using Mofumofu.ExtensionAPI.Data;

namespace Mofumofu.ExtensionAPI.Actions;

/// <summary>
/// 下载任务的动作接口：按 4 位数字编号查询与操作启动器里的下载任务，
/// 并在任务完成 / 失败时收到通知。
/// 编号从任务创建起会话内唯一；任务结束后编号会被回收复用，不要长期缓存。
/// </summary>
/// 全部成员都有默认空实现：插件只需重写自己关心的成员。
/// </summary>
public interface IDownloadAction
{
    /// <summary>
    /// 按编号查询任务快照。
    /// </summary>
    /// <param name="taskNumber">4 位数字任务编号</param>
    /// <returns>任务快照；编号不存在（已结束被清理 / 传错）时返回 null</returns>
    Task<DownloadTaskData?> GetTaskAsync(int taskNumber, CancellationToken cancellationToken = default)
        => Task.FromResult<DownloadTaskData?>(null);

    /// <summary>按编号取消任务（含已登记文件的清理）。需要 network 权限。</summary>
    /// <returns>true = 找到并已取消；false = 编号不存在或任务已结束</returns>
    bool Cancel(int taskNumber) => false;

    /// <summary>按编号暂停 / 继续任务（仅支持声明了可暂停的任务）。需要 network 权限。</summary>
    /// <returns>true = 找到并已执行；false = 编号不存在 / 任务不支持暂停 / 状态不允许</returns>
    bool SetPaused(int taskNumber, bool paused) => false;

    /// <summary>下载任务完成时被调用（需要 networkReadonly 权限，未声明则不会收到通知）。</summary>
    void DownloadCompletedHook(int taskNumber, string title) { }

    /// <summary>下载任务失败时被调用（reason 为失败原因）。需要 networkReadonly 权限。</summary>
    void DownloadFailedHook(int taskNumber, string title, string reason) { }
}
