using System.Collections.ObjectModel;
using MofuCore.Tools;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mofumofu.Console.Pages;

public sealed class RewindPage : MofuPage
{
    private readonly ListView _instances;
    private readonly ListView _worlds;
    private readonly TextView _detail;
    private readonly InstanceService _service;
    private List<InstanceItem> _instanceItems = [];
    private List<RewindWorldInfo> _worldItems = [];

    public RewindPage(IMofuShell shell) : base(shell, "回档")
    {
        _service = new InstanceService(shell.Accounts, shell.Settings);

        _instances = new ListView
        {
            X = 1,
            Y = 1,
            Width = Dim.Percent(30),
            Height = Dim.Fill(1),
            Title = " 实例 ",
            BorderStyle = LineStyle.Single,
        };
        _instances.SetSource(new ObservableCollection<string>());
        _instances.ValueChanged += (_, _) => LoadWorlds();

        _worlds = new ListView
        {
            X = Pos.Percent(30) + 1,
            Y = 1,
            Width = Dim.Percent(30),
            Height = Dim.Fill(1),
            Title = " 存档 ",
            BorderStyle = LineStyle.Single,
        };
        _worlds.SetSource(new ObservableCollection<string>());
        _worlds.ValueChanged += (_, _) => UpdateDetail();
        _worlds.Accepted += (_, _) => OpenWorldMenu();

        _detail = new TextView
        {
            X = Pos.Percent(60) + 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = Dim.Fill(4),
            ReadOnly = true,
            WordWrap = true,
            Text = "选择实例与存档后，可创建快照、回滚或清理。",
        };

        Button timeline = new() { Text = "时间线 / 操作", X = 1, Y = Pos.AnchorEnd(2) };
        Button refresh = new() { Text = "刷新", X = 20, Y = Pos.AnchorEnd(2) };
        timeline.Accepted += (_, _) => OpenWorldMenu();
        refresh.Accepted += (_, _) => Refresh();

        Add(_instances, _worlds, _detail, timeline, refresh);
    }

    public override void Refresh()
    {
        Shell.SetStatus("正在扫描回档目标…");
        Task.Run(() =>
        {
            List<InstanceItem> items = _service.Scan().ToList();
            Ui.Post(() =>
            {
                _instanceItems = items;
                ObservableCollection<string> display = new(items.Select(item => item.Id));
                _instances.SetSource(display);
                if (items.Count > 0)
                {
                    int index = items.FindIndex(item => item.Id == Shell.Settings.SelectedInstance);
                    _instances.SelectedItem = index >= 0 ? index : 0;
                }
                else
                {
                    _instances.SelectedItem = null;
                }

                LoadWorlds();
            });
        });
    }

    private InstanceItem? SelectedInstance =>
        _instances.SelectedItem is int index && index >= 0 && index < _instanceItems.Count
            ? _instanceItems[index]
            : null;

    private RewindWorldInfo? SelectedWorld =>
        _worlds.SelectedItem is int index && index >= 0 && index < _worldItems.Count
            ? _worldItems[index]
            : null;

    private void LoadWorlds()
    {
        InstanceItem? instance = SelectedInstance;
        if (instance is null)
        {
            _worlds.SetSource(new ObservableCollection<string>());
            _worldItems = [];
            _detail.Text = "没有可回档的实例。";
            return;
        }

        try
        {
            _worldItems = RewindSystem.ListWorlds(instance.Directory).ToList();
        }
        catch (Exception exception)
        {
            _worldItems = [];
            _detail.Text = "读取存档失败：" + exception.Message;
        }

        ObservableCollection<string> display = new();
        foreach (RewindWorldInfo world in _worldItems)
        {
            string suffix = world.SnapshotCount > 0 ? $" · {world.SnapshotCount} 份快照" : " · 无快照";
            display.Add(world.Name + suffix);
        }

        _worlds.SetSource(display);
        _worlds.SelectedItem = _worldItems.Count > 0 ? 0 : null;
        UpdateDetail();
        Shell.SetStatus($"实例 {_instanceItems.Count} 个 · 存档 {_worldItems.Count} 个");
    }

    private void UpdateDetail()
    {
        InstanceItem? instance = SelectedInstance;
        RewindWorldInfo? world = SelectedWorld;
        if (instance is null || world is null)
        {
            _detail.Text = "选择左侧实例后，存档列表会出现在中间。\n\n" +
                           "回档依赖 MofuCore 的内容寻址快照，与桌面版共用。";
            return;
        }

        RewindTimeline timeline = RewindSystem.GetTimeline(world.DirectoryPath);
        _detail.Text = string.Join(Environment.NewLine, new[]
        {
            world.Name,
            new string('─', Math.Min(36, Math.Max(10, world.Name.Length * 2))),
            string.Empty,
            $"实例        {instance.Id}",
            $"路径        {world.DirectoryPath}",
            $"最后修改    {world.LastModified.LocalDateTime:yyyy-MM-dd HH:mm}",
            $"快照        {timeline.SnapshotCount} 份 · {Ui.FormatBytes(timeline.TotalSize)}",
            $"增量        {Ui.FormatBytes(timeline.AddedSize)}",
            $"最新        {(timeline.LatestAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "无")}",
            $"最早        {(timeline.OldestAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "无")}",
            string.Empty,
            "Enter / 「时间线 / 操作」：创建快照、回滚、删除。",
        });
    }

    private void OpenWorldMenu()
    {
        RewindWorldInfo? world = SelectedWorld;
        if (world is null)
        {
            Ui.Info("回档", "请先选择一个存档。");
            return;
        }

        while (true)
        {
            RewindTimeline timeline = RewindSystem.GetTimeline(world.DirectoryPath);
            List<RewindSnapshot> snapshots = timeline.Days
                .SelectMany(day => day.Snapshots)
                .OrderByDescending(snapshot => snapshot.CreatedAt)
                .ToList();
            string? action = Ui.Select(
                $"回档 · {world.Name} · {snapshots.Count} 份快照",
                ["创建快照", "选择快照操作", "刷新", "关闭"]);
            if (action is null || action == "关闭")
            {
                return;
            }

            if (action == "创建快照")
            {
                _ = CreateSnapshotAsync(world);
                return;
            }

            if (action == "刷新")
            {
                UpdateDetail();
                continue;
            }

            if (snapshots.Count == 0)
            {
                Ui.Info("快照", "这个存档还没有任何快照。");
                continue;
            }

            string? picked = Ui.Select("选择快照", snapshots.Select(snapshot =>
                $"{snapshot.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss} · {RewindReasons.Label(snapshot.Reason)}" +
                (string.IsNullOrWhiteSpace(snapshot.Label) ? string.Empty : " · " + snapshot.Label) +
                $" · {Ui.FormatBytes(snapshot.AddedSize)}").ToList());
            if (picked is null)
            {
                continue;
            }

            RewindSnapshot? chosen = snapshots.FirstOrDefault(snapshot =>
                $"{snapshot.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss} · {RewindReasons.Label(snapshot.Reason)}" +
                (string.IsNullOrWhiteSpace(snapshot.Label) ? string.Empty : " · " + snapshot.Label) +
                $" · {Ui.FormatBytes(snapshot.AddedSize)}" == picked);
            if (chosen is null)
            {
                continue;
            }

            string? operation = Ui.Select("快照操作", ["回滚到此快照", "删除快照", "设置标记颜色", "返回"]);
            if (operation == "回滚到此快照")
            {
                if (Ui.Confirm("回滚存档", $"确定把 {world.Name} 回滚到\n{chosen.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss} 吗？\n\n回滚前会自动创建安全快照。"))
                {
                    _ = RollbackAsync(world, chosen);
                    return;
                }
            }
            else if (operation == "删除快照")
            {
                if (Ui.Confirm("删除快照", "确定删除这份快照吗？"))
                {
                    try
                    {
                        RewindSystem.DeleteSnapshot(world.DirectoryPath, chosen.Id);
                        UpdateDetail();
                    }
                    catch (Exception exception)
                    {
                        Ui.Error("删除快照失败", exception.Message);
                    }
                }
            }
            else if (operation == "设置标记颜色")
            {
                string? color = Ui.Select("标记颜色", ["默认", "primary", "secondary", "success", "warning", "danger"]);
                if (color is not null)
                {
                    try
                    {
                        RewindSystem.SetSnapshotColorAsync(world.DirectoryPath, chosen.Id, color == "默认" ? string.Empty : color)
                            .GetAwaiter().GetResult();
                        UpdateDetail();
                    }
                    catch (Exception exception)
                    {
                        Ui.Error("设置颜色失败", exception.Message);
                    }
                }
            }
        }
    }

    private async Task CreateSnapshotAsync(RewindWorldInfo world)
    {
        await Ui.RunTaskAsync("创建快照", "正在扫描 " + world.Name + "…", async (progress, token) =>
        {
            RewindSnapshot snapshot = await RewindSystem.CreateSnapshotAsync(world.DirectoryPath,
                RewindKind.Save, "TUI 手动快照", null, token).ConfigureAwait(false);
            progress.Report($"完成：{snapshot.FileCount} 个文件 · {Ui.FormatBytes(snapshot.AddedSize)}");
        }).ConfigureAwait(false);
        UpdateDetail();
    }

    private async Task RollbackAsync(RewindWorldInfo world, RewindSnapshot snapshot)
    {
        await Ui.RunTaskAsync("回滚存档", "正在还原 " + world.Name + "…", async (progress, token) =>
        {
            RewindRollbackResult result = await RewindSystem.RollbackAsync(world.DirectoryPath, snapshot.Id,
                RewindKind.Save, token).ConfigureAwait(false);
            progress.Report($"已回滚，安全快照：{result.SafetySnapshot.Id}");
        }).ConfigureAwait(false);
        UpdateDetail();
    }
}
