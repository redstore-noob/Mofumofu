using System;
using Avalonia.Controls;
using Mofumofu.Launcher.Services.Agent;

namespace Mofumofu.Launcher.Services;

public static class Shell
{
    public static event Action<string>? PageRequested;
    public static event Action<string>? ToastRequested;

    public static event Action<string, object, Control?>? DialogRequested;

    public static event Action<string>? ToolRequested;
    public static event Func<AgentTaskDraft, bool>? AgentTaskRequested;
    public static event Action? ExitRequested;

    public static void Go(string pageId) => PageRequested?.Invoke(pageId);

    public static void GoTool(string toolId) => ToolRequested?.Invoke(toolId);

    public static bool OpenAgentTask(AgentTaskDraft task) => AgentTaskRequested?.Invoke(task) == true;

    public static void Toast(string message) => ToastRequested?.Invoke(message);

    public static void Exit() => ExitRequested?.Invoke();

    public static void Dialog(object content) => Dialog(content, "提示", null);

    public static void Dialog(object content, string title) => Dialog(content, title, null);

    public static void Dialog(object content, string title, Control? anchor) =>
        DialogRequested?.Invoke(title, content, anchor);

    public static void BackToDesktop() => Go("home");
}
