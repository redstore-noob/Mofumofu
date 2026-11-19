using System.Diagnostics;
using MofuCore.Config;
using MofuCore.Launch;
using MofuCore.Server;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mofumofu.Console.Pages;

public sealed class HomePage : MofuPage
{
    private readonly TextView _summary;
    private readonly Label _hint;
    private readonly InstanceService _instances;

    public HomePage(IMofuShell shell) : base(shell, "主页")
    {
        _instances = new InstanceService(shell.Accounts, shell.Settings);

        _summary = new TextView
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = Dim.Fill(5),
            ReadOnly = true,
            WordWrap = false,
            Text = "正在读取启动器状态…",
        };

        _hint = new Label
        {
            X = 1,
            Y = Pos.AnchorEnd(4),
            Width = Dim.Fill(1),
            Text = "提示：F2 实例 · F3 下载 · F4 账号 · F5 服务器 · F6 回档 · F7 联机 · F8 工具 · F9 设置",
        };

        Button launch = new() { Text = "启动选中实例", X = 1, Y = Pos.AnchorEnd(2) };
        Button open = new() { Text = "打开游戏目录", X = 22, Y = Pos.AnchorEnd(2) };
        Button refresh = new() { Text = "刷新", X = 42, Y = Pos.AnchorEnd(2) };

        launch.Accepted += (_, _) => LaunchSelected();
        open.Accepted += (_, _) => OpenFolder(YamlRead.GameDirectory);
        refresh.Accepted += (_, _) => Refresh();

        Add(_summary, _hint, launch, open, refresh);
    }

    public override void Refresh()
    {
        Shell.SetStatus("正在刷新主页…");
        Task.Run(() =>
        {
            string gameDirectory = YamlRead.GameDirectory;
            IReadOnlyList<InstanceItem> instances = _instances.Scan();
            IReadOnlyList<ServerInfo> servers = ServerManage.ListServerInfos();
            int running = servers.Count(server => server.Status is ServerManage.StatusRunning or ServerManage.StatusStarting);
            string account = Shell.Accounts.Entries
                .FirstOrDefault(entry => entry.Key == Shell.Settings.SelectedAccount)?.Display ?? "（未选择）";
            InstanceItem? selected = instances.FirstOrDefault(item => item.Id == Shell.Settings.SelectedInstance);
            ServerInfo? latestServer = servers.FirstOrDefault();
            string java = YamlRead.JavaExecutable;

            string text = string.Join(Environment.NewLine, new[]
            {
                "Mofumofu TUI · 终端启动器",
                string.Empty,
                $"游戏目录    {gameDirectory}",
                $"下载源      {Shell.Settings.DownloadSource}",
                $"当前账号    {account}",
                $"实例        {instances.Count} 个" + (selected is null ? string.Empty : $" · 选中：{selected.Id}"),
                $"服务器      {servers.Count} 个 · 运行中 {running} 个" + (latestServer is null ? string.Empty : $" · 最近：{latestServer.Name}"),
                $"Java        {(string.IsNullOrWhiteSpace(java) ? "自动检测 / 启动时下载" : java)}",
                string.Empty,
                "说明",
                "  · 自定义小组件、插件、音乐、MofuAgent 不包含在 TUI 版中。",
                "  · 用 F1–F8 或左侧导航切换页面；Enter 执行当前列表项。",
                "  · 服务器与实例数据与桌面版共用 MofuCore 配置。",
            });

            Ui.Post(() =>
            {
                _summary.Text = text;
                Shell.SetStatus($"主页就绪 · 实例 {instances.Count} 个 · 服务器 {servers.Count} 个");
            });
        });
    }

    private void LaunchSelected()
    {
        IReadOnlyList<InstanceItem> instances = _instances.Scan();
        if (instances.Count == 0)
        {
            Ui.Info("启动游戏", "还没有任何实例，请先到「下载」页安装一个游戏版本。");
            return;
        }

        string? instanceId = Shell.Settings.SelectedInstance;
        if (instances.All(item => item.Id != instanceId))
        {
            instanceId = Ui.Select("选择要启动的实例", instances.Select(item => item.Display).ToList());
            if (instanceId is null)
            {
                return;
            }

            InstanceItem? picked = instances.FirstOrDefault(item => item.Display == instanceId);
            if (picked is null)
            {
                return;
            }

            instanceId = picked.Id;
            Shell.Settings.SelectedInstance = picked.Id;
            Shell.Settings.Save();
        }

        _ = LaunchAsync(instanceId!);
    }

    private async Task LaunchAsync(string instanceId)
    {
        (bool ok, MinecraftLaunchResult? result) = await Ui.RunTaskAsync(
            "启动 " + instanceId,
            "正在准备启动…",
            (progress, token) => _instances.LaunchAsync(instanceId, progress, token)).ConfigureAwait(false);
        if (ok && result is not null)
        {
            Shell.SetStatus($"游戏已启动：{instanceId} (PID {result.Process.Id})");
            Ui.Info("启动成功", $"Minecraft 已启动。\nPID：{result.Process.Id}\n\n关闭游戏后可从「实例」页查看状态。");
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            Ui.Error("打开目录失败", exception.Message);
        }
    }
}
