using System;

namespace Mofumofu.Launcher.Services;

/// <summary>下载/安装完成后通知主窗口刷新底部版本栏。</summary>
public static class LaunchBarRefresh
{
    public static event Action? Requested;

    public static void Request() => Requested?.Invoke();
}
