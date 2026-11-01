using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Mofumofu.Launcher.Services;

/// <summary>
/// 极简外壳服务：页面导航请求 + 全局提示（Toast）。
/// 由 MainWindow 订阅并负责真正的切换/弹提示，页面只负责喊话。
/// </summary>
public static class Shell
{
    public static event Action<string>? PageRequested;
    public static event Action<string>? ToastRequested;

    /// <summary>弹窗请求：标题、内容、触发控件（anchor 可空，空则弹窗从窗口中心蹦出来）。</summary>
    public static event Action<string, object, Control?>? DialogRequested;

    public static event Action<string>? ToolRequested;
    public static event Action? ExitRequested;

    public static void Go(string pageId) => PageRequested?.Invoke(pageId);

    /// <summary>打开开始菜单里的工具（资源包/整合包/命令等）。</summary>
    public static void GoTool(string toolId) => ToolRequested?.Invoke(toolId);

    public static void Toast(string message) => ToastRequested?.Invoke(message);

    /// <summary>强制退出启动器（绕过关闭询问/托盘），用于重置设置后直接进入下次启动流程。</summary>
    public static void Exit() => ExitRequested?.Invoke();

    public static void Dialog(object content) => Dialog(content, "提示", null);

    public static void Dialog(object content, string title) => Dialog(content, title, null);

    /// <summary>
    /// 弹出居中弹窗。<paramref name="anchor"/> 是触发这次弹窗的控件，
    /// 弹窗从它的方向蹦出来；不传、控件不在视觉树时从窗口中心蹦出来。
    /// </summary>
    public static void Dialog(object content, string title, Control? anchor) =>
        DialogRequested?.Invoke(title, content, anchor);

    /// <summary>页面顶部的返回键统一回桌面。</summary>
    public static void BackToDesktop() => Go("home");
}
