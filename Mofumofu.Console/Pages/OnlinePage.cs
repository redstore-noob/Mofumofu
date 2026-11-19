using System.Collections.ObjectModel;
using MofuCore.OnlineGame;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mofumofu.Console.Pages;

public sealed class OnlinePage : MofuPage
{
    private static readonly string[] Actions =
    [
        "查询服务器状态（Server List Ping）",
        "陶瓦：自动安装 / 更新",
        "陶瓦：创建房间",
        "陶瓦：加入房间",
        "陶瓦：退出房间",
        "陶瓦：结束本机服务",
        "刷新状态",
    ];

    private readonly ListView _actions;
    private readonly TextView _detail;
    private readonly Terracotta _terracotta;
    private object? _timer;

    public OnlinePage(IMofuShell shell) : base(shell, "联机")
    {
        _terracotta = new Terracotta(
            string.IsNullOrWhiteSpace(shell.Settings.TerracottaBinary) ? null : shell.Settings.TerracottaBinary);
        _terracotta.StatusChanged = _ => Ui.Post(UpdateDetail);

        _actions = new ListView
        {
            X = 1,
            Y = 1,
            Width = Dim.Percent(38),
            Height = Dim.Fill(1),
            Title = " 联机 ",
            BorderStyle = LineStyle.Single,
        };
        _actions.SetSource(new ObservableCollection<string>(Actions));
        _actions.SelectedItem = 0;
        _actions.Accepted += (_, _) => Execute(_actions.SelectedItem ?? -1);

        _detail = new TextView
        {
            X = Pos.Percent(38) + 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = Dim.Fill(1),
            ReadOnly = true,
            WordWrap = true,
        };

        Add(_actions, _detail);
    }

    public override void OnShown()
    {
        base.OnShown();
        if (_timer is null)
        {
            _timer = Application.AddTimeout(TimeSpan.FromSeconds(2), () =>
            {
                UpdateDetail();
                return true;
            });
        }
    }

    public override void OnHidden()
    {
        if (_timer is not null)
        {
            Application.RemoveTimeout(_timer);
            _timer = null;
        }
    }

    public override void Refresh()
    {
        UpdateDetail();
        Shell.SetStatus("联机 · 陶瓦房间与服务器状态查询");
    }

    private void UpdateDetail()
    {
        TerracottaStatus status = _terracotta.CurrentStatus;
        TerracottaRuntime runtime = _terracotta.GetRuntime();
        TerracottaProviderInfo info = _terracotta.GetInfo();

        _detail.Text = string.Join(Environment.NewLine, new[]
        {
            "陶瓦联机",
            new string('─', 30),
            $"状态        {StateText(status.State)}",
            $"阶段        {status.Phase}",
            $"房间码      {(string.IsNullOrWhiteSpace(status.Room) ? "（无）" : status.Room)}",
            $"加入地址    {(string.IsNullOrWhiteSpace(status.Address) ? "（无）" : status.Address)}",
            $"本地地址    {(string.IsNullOrWhiteSpace(status.LocalAddress) ? "（无）" : status.LocalAddress)}",
            $"成员        {(status.Players.Count == 0 ? "（无）" : string.Join(", ", status.Players.Select(player => player.Name)))}",
            $"提示        {status.Tip}",
            string.IsNullOrWhiteSpace(status.Error) ? string.Empty : "错误        " + status.Error,
            string.Empty,
            $"本机服务    {(runtime.Running ? "运行中" : "未运行")} · {(string.IsNullOrWhiteSpace(runtime.Version) ? "版本未知" : runtime.Version)}" +
                (runtime.Port > 0 ? $" · 端口 {runtime.Port}" : string.Empty),
            $"可执行文件  {(string.IsNullOrWhiteSpace(runtime.Binary) ? "未安装（可通过「自动安装」获取）" : runtime.Binary)}",
            string.IsNullOrWhiteSpace(info.Hint) ? string.Empty : "说明        " + info.Hint,
            string.Empty,
            "用法：先进入存档 → Esc → 对局域网开放，再创建房间；房客粘贴房间码加入，",
            "连上后在游戏多人列表直连 127.0.0.1 即可。",
        });

        Shell.SetStatus($"联机 · {StateText(status.State)}" +
                        (string.IsNullOrWhiteSpace(status.Room) ? string.Empty : " · 房间 " + status.Room));
    }

    private static string StateText(string state) => state switch
    {
        Terracotta.StateStarting => "正在启动",
        Terracotta.StateHosting => "房间已就绪",
        Terracotta.StateJoined => "已加入房间",
        Terracotta.StateError => "出错",
        _ => "空闲",
    };

    private void Execute(int index)
    {
        switch (index)
        {
            case 0:
                _ = PingServerAsync();
                break;
            case 1:
                _ = InstallAsync();
                break;
            case 2:
                _ = HostAsync();
                break;
            case 3:
                _ = JoinAsync();
                break;
            case 4:
                _ = LeaveAsync();
                break;
            case 5:
                _ = ShutdownAsync();
                break;
            case 6:
                UpdateDetail();
                break;
        }
    }

    private async Task PingServerAsync()
    {
        string? address = Ui.Prompt("服务器状态查询", "地址（host 或 host:port）", "mc.hypixel.net");
        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        (bool ok, ServerPingResult? result) = await Ui.RunTaskAsync(
            "服务器状态查询",
            "正在 Ping " + address + "…",
            async (progress, token) =>
            {
                progress.Report("发送 Server List Ping…");
                ServerPingResult ping = await ServerListPing.PingAsync(address, token).ConfigureAwait(false);
                return ping;
            }).ConfigureAwait(false);

        if (!ok || result is null)
        {
            return;
        }

        if (!result.Online)
        {
            Ui.Error("服务器状态", "无法连接或服务器离线：" + address);
            return;
        }

        Ui.Info("服务器状态",
            $"地址：{address}\n" +
            $"版本：{result.VersionName ?? "未知"}\n" +
            $"玩家：{result.OnlinePlayers}/{result.MaxPlayers}\n\n" +
            $"MOTD：\n{StripColors(result.Motd)}");
    }

    private static string StripColors(string? motd)
    {
        if (string.IsNullOrEmpty(motd))
        {
            return "（无）";
        }

        return System.Text.RegularExpressions.Regex.Replace(motd, "§.", string.Empty);
    }

    private async Task InstallAsync()
    {
        (bool ok, TerracottaInstallResult? result) = await Ui.RunTaskAsync(
            "陶瓦自动安装",
            "正在获取最新发行版…",
            async (progress, token) =>
            {
                progress.Report("查询 GitHub Release…");
                TerracottaInstallResult installed = await _terracotta.InstallAsync(token).ConfigureAwait(false);
                progress.Report(string.IsNullOrWhiteSpace(installed.Path)
                    ? "需要手动安装。"
                    : "已安装：" + installed.Path);
                return installed;
            }).ConfigureAwait(false);

        if (!ok || result is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.ManualHint))
        {
            Ui.Info("需要手动安装", result.ManualHint);
        }
        else if (!string.IsNullOrWhiteSpace(result.Path))
        {
            Shell.Settings.TerracottaBinary = result.Path;
            Shell.Settings.Save();
            _terracotta.BinaryPath = result.Path;
            Ui.Info("安装完成", $"{result.AssetName}\n{result.Path}");
        }

        UpdateDetail();
    }

    private async Task HostAsync()
    {
        bool ok = await Ui.RunTaskAsync("创建联机房间", "正在准备陶瓦联机…", async (progress, token) =>
        {
            progress.Report("首次运行会拉起本机服务，请稍候…");
            await _terracotta.HostAsync(null, token).ConfigureAwait(false);
            progress.Report("已开始扫描局域网世界，等待房间码…");
        }).ConfigureAwait(false);
        if (ok)
        {
            UpdateDetail();
            Ui.Info("创建房间", "陶瓦已开始扫描。若还没有房间码，请进入存档后「对局域网开放」，再回来看状态。");
        }
    }

    private async Task JoinAsync()
    {
        string? room = Ui.Prompt("加入房间", "房主给你的房间码");
        if (string.IsNullOrWhiteSpace(room))
        {
            return;
        }

        bool ok = await Ui.RunTaskAsync("加入联机房间", "正在连接房主…", async (progress, token) =>
        {
            await _terracotta.JoinAsync(room.Trim(), null, token).ConfigureAwait(false);
            progress.Report("已提交加入请求。");
        }).ConfigureAwait(false);
        if (ok)
        {
            UpdateDetail();
            Ui.Info("加入房间", "已接入房间。回到游戏多人列表，直连 127.0.0.1 即可进入。");
        }
    }

    private async Task LeaveAsync()
    {
        bool ok = await Ui.RunTaskAsync("退出房间", "正在退出陶瓦会话…", async (_, _) =>
        {
            await _terracotta.LeaveAsync().ConfigureAwait(false);
        }, false).ConfigureAwait(false);
        if (ok)
        {
            UpdateDetail();
        }
    }

    private async Task ShutdownAsync()
    {
        if (!Ui.Confirm("结束本机服务", "确定结束陶瓦本机守护进程吗？\n如果还有联机房间，房间会断开。"))
        {
            return;
        }

        bool ok = await Ui.RunTaskAsync("结束本机服务", "正在结束陶瓦进程…", async (_, _) =>
        {
            await _terracotta.ShutdownAsync().ConfigureAwait(false);
        }, false).ConfigureAwait(false);
        if (ok)
        {
            UpdateDetail();
        }
    }
}
