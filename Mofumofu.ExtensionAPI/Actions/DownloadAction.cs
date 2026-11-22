using Mofumofu.ExtensionAPI.Data;

namespace Mofumofu.ExtensionAPI.Actions;

/// <summary>
/// 下载任务的动作接口。任务编号会话内唯一，任务结束后会被回收复用，不要长期缓存。
/// 写操作需 <see cref="PluginPermissionZones.Network"/>，只读查询与通知需
/// <see cref="PluginPermissionZones.NetworkReadonly"/>；未声明权限则收不到通知。
/// </summary>
public interface IDownloadAction
{
    Task<DownloadTaskData?> GetTaskAsync(int taskNumber, CancellationToken cancellationToken = default)
        => Task.FromResult<DownloadTaskData?>(null);

    bool Cancel(int taskNumber) => false;

    bool SetPaused(int taskNumber, bool paused) => false;

    void DownloadCompletedHook(int taskNumber, string title) { }

    void DownloadFailedHook(int taskNumber, string title, string reason) { }
}
