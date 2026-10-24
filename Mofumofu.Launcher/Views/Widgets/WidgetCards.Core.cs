using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MofuCore.Config;
using MofuCore.Launch;
using MofuCore.Log;
using MofuCore.Server;
using MofuCore.Tools;
using Mofumofu.Launcher.Mock;
using Mofumofu.Launcher.Services;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views.Widgets;

/// <summary>
/// 桌面小组件的具体卡片（启动 / 存档 / 进服 / 服务器 / 日志 / 收藏 / 快照 / 下载）。
/// 每张卡片统一的结构：大号数值 + 状态胶囊 + 可操作列表。
/// </summary>
internal static partial class WidgetCards
{
    private static string SelectedInstanceName => GameState.SelectedVersion;

    // ================= 最近存档（真实：扫描所有实例的 saves/*，点击直接进存档） =================

    public static Control RecentWorlds()
    {
        var rowsHost = new StackPanel { Spacing = 4 };

        var busy = false;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var (card, valueText) = WidgetChrome.CardWithValue(
            L.T("Home.Worlds.Loading", "扫描中…"),
            actions,
            rowsHost);

        async void Refresh()
        {
            if (busy) return;
            busy = true;
            try
            {
                var worlds = await WidgetBackend.LoadAllWorldsAsync();
                valueText.Text = L.T("Home.Worlds.Count", "{0} 个世界", worlds.Count);
                rowsHost.Children.Clear();

                if (worlds.Count == 0)
                {
                    rowsHost.Children.Add(WidgetChrome.Empty(L.T("Home.Worlds.EmptyHint", "在游戏里创建世界后，这里会显示最近存档")));
                    rowsHost.Children.Add(WidgetChrome.LinkButton(L.T("Home.Worlds.OpenFolder", "打开游戏目录"), OpenGameFolder));
                    return;
                }

                foreach (var item in worlds.Take(5))
                {
                    // 隔离实例的存档显示归属实例；共享目录的存档跟随当前选中实例启动
                    var subtitle = item.InstanceId is { } instanceId
                        ? L.T("Home.Worlds.SubtitleInstance", "{0} · {1} · {2}",
                            instanceId, item.World.LastWrite.ToString("MM-dd HH:mm"), WidgetBackend.FormatSize(item.World.SizeBytes))
                        : L.T("Home.Worlds.Subtitle", "{0} · {1}",
                            item.World.LastWrite.ToString("MM-dd HH:mm"), WidgetBackend.FormatSize(item.World.SizeBytes));

                    var owner = item;
                    var play = WidgetChrome.IconButton("IconPlay", L.T("Home.Worlds.PlayTip", "启动并进入「{0}」", owner.World.Name),
                        () => WidgetQuickLaunch.Launch(
                            owner.InstanceId,
                            GameLaunch.BuildWorldArguments(owner.World.Name),
                            L.T("Home.Worlds.Entering", "正在进入「{0}」", owner.World.Name)), 24);

                    rowsHost.Children.Add(WidgetChrome.ListItem("IconFluentGlobe", owner.World.Name, subtitle, play));
                }
            }
            catch
            {
                valueText.Text = L.T("Home.Worlds.Count", "{0} 个世界", 0);
                rowsHost.Children.Clear();
                rowsHost.Children.Add(WidgetChrome.Empty(L.T("Home.Worlds.EmptyHint", "在游戏里创建世界后，这里会显示最近存档")));
            }
            finally
            {
                busy = false;
            }
        }

        actions.Children.Add(WidgetChrome.IconButton("IconFluentArrowClockwise",
            L.T("Home.Worlds.RefreshTip", "重新扫描存档"), () => Refresh()));

        WidgetChrome.OnBecameVisible(card, Refresh);
        Refresh();
        return card;
    }

    /// <summary>打开本机游戏目录（真实文件管理器）。</summary>
    private static void OpenGameFolder()
    {
        try
        {
            var directory = WidgetBackend.GameDirectory;
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory) { UseShellExecute = true });
            }
            else
            {
                Shell.Toast(L.T("Home.Worlds.NoGameDir", "还没有配置游戏目录"));
            }
        }
        catch
        {
            Shell.Toast(L.T("Home.Worlds.OpenFolderFailed", "打开游戏目录失败"));
        }
    }

    // ================= 托管服务器（真实：MofuCore.Server.ServerManage） =================

    public static Control HostedServer()
    {
        var rowsHost = new StackPanel { Spacing = 8 };

        var busy = false;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var (card, valueText) = WidgetChrome.CardWithValue(
            L.T("Home.Hosted.Loading", "读取中…"),
            actions,
            rowsHost);

        async void Refresh()
        {
            if (busy) return;
            busy = true;
            try
            {
                var servers = await Task.Run(() => ServerManage.ListServerInfos());
                valueText.Text = L.T("Home.Hosted.Count", "{0} 台托管", servers.Count);
                rowsHost.Children.Clear();

                if (servers.Count == 0)
                {
                    rowsHost.Children.Add(WidgetChrome.Empty(L.T("Home.Hosted.EmptyHint", "还没有本机托管服务器；到服务器页创建后这里会显示")));
                    rowsHost.Children.Add(WidgetChrome.LinkButton(L.T("Home.Hosted.Open", "前往服务器页"),
                        () => Shell.Go("servers"), "IconFluentServer"));
                    return;
                }

                foreach (var server in servers.Take(3))
                {
                    rowsHost.Children.Add(BuildHostedServerRow(server, Refresh));
                }

                rowsHost.Children.Add(WidgetChrome.LinkButton(L.T("Common.Manage", "管理"),
                    () => Shell.Go("servers"), "IconFluentServer"));
            }
            catch
            {
                valueText.Text = L.T("Home.Hosted.Count", "{0} 台托管", 0);
                rowsHost.Children.Clear();
                rowsHost.Children.Add(WidgetChrome.Empty(L.T("Home.Hosted.EmptyHint", "还没有本机托管服务器；到服务器页创建后这里会显示")));
            }
            finally
            {
                busy = false;
            }
        }

        actions.Children.Add(WidgetChrome.IconButton("IconFluentArrowClockwise",
            L.T("Home.Hosted.RefreshTip", "刷新服务器列表"), () => Refresh()));

        WidgetChrome.OnBecameVisible(card, Refresh);
        Refresh();
        return card;
    }

    /// <summary>一台真实托管服务器：名字/版本/端口/在线数来自 ServerManage.ListServerInfos()。</summary>
    private static Control BuildHostedServerRow(ServerInfo server, Action refresh)
    {
        var running = server.Status == ServerManage.StatusRunning;
        var changing = server.Status is ServerManage.StatusStarting or ServerManage.StatusStopping;

        var statusToken = running ? "NekoSuccessBrush" : changing ? "NekoWarningBrush" : "NekoDimBrush";
        var statusSoft = running ? "NekoSuccessSoftBrush" : changing ? "NekoWarningSoftBrush" : "NekoFieldSoftBrush";
        var statusLabel = running
            ? L.T("Home.Hosted.Running", "运行中")
            : changing
                ? L.T("Home.Hosted.Changing", "启动/停止中")
                : L.T("Home.Hosted.Stopped", "已停止");

        var statusPill = WidgetChrome.Pill(statusLabel, statusToken, statusSoft);
        if (running)
        {
            ToolTip.SetTip(statusPill, L.T("Home.Hosted.PlayersValue", "{0}/{1} 在线", server.Players, server.MaxPlayers));
        }

        var toggle = WidgetChrome.LinkButton(
            running ? L.T("Home.Hosted.Stop", "停止") : L.T("Home.Hosted.Start", "启动"),
            () => _ = ToggleHostedServerAsync(server, running, refresh),
            running ? "IconStop" : "IconPlay");

        var actions = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(statusPill);
        actions.Children.Add(toggle);

        var row = WidgetChrome.ListItem(
            "IconFluentServer",
            server.Name,
            L.T("Home.Hosted.Meta", "{0} · 端口 {1}", server.McVersion, server.Port),
            actions,
            26);

        return new Border
        {
            Background = ThemeTokens.Brush("NekoFieldSoftBrush"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 6),
            Child = row,
        };
    }

    private static async Task ToggleHostedServerAsync(ServerInfo server, bool running, Action refresh)
    {
        try
        {
            if (running)
            {
                await ServerManage.StopAsync(server.Id);
                Shell.Toast(L.T("Home.Hosted.StoppedToast", "{0} 已停止", server.Name));
            }
            else
            {
                await ServerManage.StartAsync(server.Id);
                Shell.Toast(L.T("Home.Hosted.Started", "{0} 已启动", server.Name));
            }
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Home.Hosted.Failed", "操作失败：{0}", ex.Message));
        }

        refresh();
    }

    // ================= 运行日志 =================

    public static Control LaunchLog()
    {
        var rowsHost = new StackPanel { Spacing = 2 };
        var body = new StackPanel { Spacing = 3 };
        body.Children.Add(rowsHost);

        var busy = false;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var (card, valueText) = WidgetChrome.CardWithValue(
            GameState.IsRunning
                ? L.T("Home.Log.Running", "运行中")
                : L.T("Home.Log.NotStarted", "未启动"),
            actions,
            body);

        async void Refresh()
        {
            if (busy) return;
            busy = true;
            try
            {
                var text = await LogWrite.ReadCurrentAsync();
                rowsHost.Children.Clear();

                var lines = text
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.TrimEnd('\r'))
                    .Where(line => line.Length > 0)
                    .TakeLast(6)
                    .ToList();

                if (lines.Count == 0)
                {
                    rowsHost.Children.Add(WidgetChrome.Empty(L.T("Home.Log.Empty", "还没有日志")));
                }
                else
                {
                    foreach (var line in lines)
                    {
                        rowsHost.Children.Add(BuildLogRow(line));
                    }
                }
            }
            catch
            {
                rowsHost.Children.Clear();
                rowsHost.Children.Add(WidgetChrome.Empty(L.T("Home.Log.Empty", "还没有日志")));
            }
            finally
            {
                busy = false;
            }

            valueText.Text = GameState.IsRunning
                ? L.T("Home.Log.Running", "运行中")
                : L.T("Home.Log.NotStarted", "未启动");
        }

        // 右上角：导出 + 刷新（此时 Refresh / valueText / busy 都已就绪，避免 CS0165）
        actions.Children.Add(WidgetChrome.IconButton("IconFluentDocument", L.T("Home.Log.ExportTip", "导出当前日志"),
            () => _ = ExportLogAsync()));
        actions.Children.Add(WidgetChrome.IconButton("IconFluentArrowClockwise", L.T("Home.Log.RefreshTip", "刷新日志"), Refresh));

        body.Children.Add(WidgetChrome.LinkButton(L.T("Home.Log.Export", "导出日志"), () => _ = ExportLogAsync(), "IconFluentDocument"));

        // 每次回到桌面页、以及游戏状态变化时刷新一次日志
        WidgetChrome.OnBecameVisible(card, Refresh);
        GameState.Changed += Refresh;
        WidgetChrome.OnDetach(card, () => GameState.Changed -= Refresh);

        Refresh();
        return card;
    }

    private static Control BuildLogRow(string line)
    {
        var (time, type, message) = ParseLogLine(line);
        var typeBrush = type switch
        {
            "WARN" => "NekoWarningBrush",
            "ERROR" => "NekoDangerBrush",
            _ => "NekoDimBrush",
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        row.Children.Add(new TextBlock
        {
            Text = time,
            FontSize = WidgetChrome.CaptionSize,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei"),
            Foreground = ThemeTokens.Brush("NekoDimBrush"),
        });

        var typeText = new TextBlock
        {
            Text = type,
            FontSize = WidgetChrome.CaptionSize,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei"),
            Foreground = ThemeTokens.Brush(typeBrush),
            Margin = new Thickness(6, 0, 6, 0),
            MinWidth = 34,
        };
        Grid.SetColumn(typeText, 1);
        row.Children.Add(typeText);

        var messageText = new TextBlock
        {
            Text = message,
            FontSize = WidgetChrome.CaptionSize + 0.5,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei"),
            Foreground = ThemeTokens.Brush("NekoMutedBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(messageText, 2);
        row.Children.Add(messageText);

        return row;
    }

    /// <summary>解析 <c>[time][TYPE]message</c> 格式的日志行。</summary>
    private static (string Time, string Type, string Message) ParseLogLine(string line)
    {
        if (!line.StartsWith("[", StringComparison.Ordinal)) return (string.Empty, string.Empty, line);

        var timeEnd = line.IndexOf(']');
        if (timeEnd <= 1) return (string.Empty, string.Empty, line);

        var time = line[1..timeEnd];
        if (time.Length >= 8) time = time[^8..];

        var typeEnd = line.IndexOf(']', timeEnd + 1);
        if (typeEnd < 0) return (time, string.Empty, line[(timeEnd + 1)..].Trim());

        var type = line[(timeEnd + 2)..typeEnd].Trim();
        var message = line[(typeEnd + 1)..].Trim();
        return (time, type, message);
    }

    private static async Task ExportLogAsync()
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Mofumofu.Launcher",
                "logs-export");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"mofumofu-{DateTime.Now:yyyyMMdd-HHmmss}.log");

            var ok = await LogWrite.ExportCurrentAsync(path);
            Shell.Toast(ok
                ? L.T("Home.Log.Exported", "日志已导出：{0}", path)
                : L.T("Home.Log.ExportFailed", "日志导出失败"));
        }
        catch
        {
            Shell.Toast(L.T("Home.Log.ExportFailed", "日志导出失败"));
        }
    }

    // ================= 常用实例（真实：扫描 versions/*） =================

    public static Control Favorites()
    {
        var rowsHost = new StackPanel { Spacing = 4 };

        var busy = false;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var (card, valueText) = WidgetChrome.CardWithValue(
            L.T("Home.Favorites.Loading", "扫描中…"),
            actions,
            rowsHost);

        async void Refresh()
        {
            if (busy) return;
            busy = true;
            try
            {
                var instances = await WidgetBackend.LoadInstancesAsync();
                valueText.Text = L.T("Home.Favorites.Count", "{0} 个实例", instances.Count);
                rowsHost.Children.Clear();

                if (instances.Count == 0)
                {
                    rowsHost.Children.Add(WidgetChrome.Empty(L.T("Home.Favorites.Empty", "还没有安装实例；到下载页安装后这里会显示")));
                    rowsHost.Children.Add(WidgetChrome.LinkButton(L.T("Home.Favorites.Open", "前往下载页"), () => Shell.Go("download"), "IconDownload"));
                    return;
                }

                foreach (var instance in instances.Take(6))
                {
                    var select = WidgetChrome.IconButton("IconPlay", L.T("Home.Favorites.LaunchTip", "选择 {0}", instance.Id),
                        () =>
                        {
                            GameState.SetVersion(instance.Id);
                            Shell.Toast(L.T("Home.Favorites.Selected", "已选择 {0}（在桌面启动卡启动）", instance.Id));
                        }, 24);

                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
                    var icon = new Border
                    {
                        Width = 26,
                        Height = 26,
                        CornerRadius = new CornerRadius(7),
                        Background = ThemeTokens.Brush("NekoFieldBrush"),
                        Child = WidgetChrome.AssetImage(instance.Icon, 20, "IconFluentCube"),
                    };
                    row.Children.Add(icon);

                    var texts = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                    var title = WidgetChrome.Text(instance.Id, WidgetChrome.BodySize, "NekoTextBrush", FontWeight.SemiBold);
                    title.TextTrimming = TextTrimming.CharacterEllipsis;
                    title.MaxLines = 1;
                    texts.Children.Add(title);
                    texts.Children.Add(WidgetChrome.Caption(L.T("Home.Favorites.Subtitle", "{0} · {1}",
                        instance.Loader, instance.LastWrite.ToString("yyyy-MM-dd HH:mm"))));
                    Grid.SetColumn(texts, 1);
                    texts.Margin = new Thickness(8, 0, 6, 0);
                    row.Children.Add(texts);

                    Grid.SetColumn(select, 2);
                    select.VerticalAlignment = VerticalAlignment.Center;
                    row.Children.Add(select);

                    rowsHost.Children.Add(row);
                }
            }
            catch
            {
                valueText.Text = L.T("Home.Favorites.Count", "{0} 个实例", 0);
                rowsHost.Children.Clear();
                rowsHost.Children.Add(WidgetChrome.Empty(L.T("Home.Favorites.Empty", "还没有安装实例；到下载页安装后这里会显示")));
            }
            finally
            {
                busy = false;
            }
        }

        actions.Children.Add(WidgetChrome.IconButton("IconFluentArrowClockwise",
            L.T("Home.Favorites.RefreshTip", "重新扫描已安装实例"), () => Refresh()));

        WidgetChrome.OnBecameVisible(card, Refresh);
        Refresh();
        return card;
    }

    // ================= 时光回溯（横向迷你时间轴） =================

    /// <summary>时间轴上的一个节点：时间为空表示只知道"有快照"（无法定位到具体目录）。</summary>
    private sealed record RewindPoint(DateTimeOffset? Time, string Color, bool IsLatest);

    public static Control Rewind()
    {
        var timelineHost = new Border { VerticalAlignment = VerticalAlignment.Center };
        var hintText = WidgetChrome.Caption(string.Empty);

        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(timelineHost);
        body.Children.Add(hintText);
        body.Children.Add(WidgetChrome.Row(6,
            WidgetChrome.LinkButton(L.T("Home.Rewind.NewSnapshot", "新建快照"),
                () => Shell.Toast(L.T("Home.Rewind.Created", "已创建快照")), "IconPlus"),
            WidgetChrome.LinkButton(L.T("Home.Rewind.RollbackLatest", "回滚最近"), () => Shell.Dialog(RewindDialogContent.Build(SelectedInstanceName), "Rewind"), "IconFluentArrowCounterclockwise")));

        var (card, valueText) = WidgetChrome.CardWithValue(
            L.T("Home.Rewind.Value", "快照"),
            WidgetChrome.LinkButton(L.T("Common.Manage", "管理"), () => Shell.Dialog(RewindDialogContent.Build(SelectedInstanceName), "Rewind")),
            body);
        valueText.FontSize = 14;
        valueText.MaxLines = 1;

        void Apply(IReadOnlyList<RewindPoint> points, int count, long bytes, string hint)
        {
            void Update()
            {
                timelineHost.Child = BuildRewindTimeline(points);
                valueText.Text = count <= 0
                    ? L.T("Home.Rewind.EmptyValue", "暂无快照")
                    : L.T("Home.Rewind.Summary", "{0} 个快照 · {1}", count, FormatRewindBytes(bytes));
                hintText.Text = hint;
                hintText.IsVisible = hint.Length > 0;
            }

            // 数据从后台线程回来，统一切回 UI 线程再改控件
            if (Dispatcher.UIThread.CheckAccess()) Update();
            else Dispatcher.UIThread.Post(Update);
        }

        // 不预填假快照：真实数据回来前显示空态，读不到就一直是空态
        Apply(Array.Empty<RewindPoint>(), 0, 0, L.T("Home.Rewind.EmptyHint", "在实例页的 Rewind 弹窗里新建快照"));
        _ = LoadRewindAsync(Apply);

        return card;
    }

    /// <summary>横向迷你时间轴：一条主轴 + 从左（旧）到右（新）的圆点，最新一个带光环与「最新」。</summary>
    private static Control BuildRewindTimeline(IReadOnlyList<RewindPoint> points)
    {
        if (points.Count == 0) return WidgetChrome.Empty(L.T("Home.Rewind.Empty", "还没有快照"));

        // 只画最近 12 个（数据已按从旧到新排序）
        var visible = points.Count > 12 ? points.Skip(points.Count - 12).ToList() : points;
        var columns = visible.Count;

        var grid = new Grid
        {
            // 左右各留一点边距：最边上的圆点/时间标签不会被卡片边缘裁掉
            Margin = new Thickness(14, 0, 14, 0),
            RowDefinitions = new RowDefinitions("Auto,28,Auto"),
            ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", columns))),
            ClipToBounds = false,
        };

        // 主轴：一条细线横穿全部节点
        var axis = new Border
        {
            Height = 1,
            Margin = new Thickness(5, 0, 5, 0),
            Background = ThemeTokens.Brush("NekoAccentLineBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(axis, 1);
        Grid.SetColumnSpan(axis, columns);
        grid.Children.Add(axis);

        var latestRing = ThemeTokens.WithAlpha("NekoAccent", 0.45);
        var labelStep = columns <= 4 ? 1 : (int)Math.Ceiling(columns / 4d);

        for (var index = 0; index < columns; index++)
        {
            var point = visible[index];
            // 越久远的点越小；最新一个稍大
            var size = point.IsLatest ? 11 : Math.Clamp(4.5 + index * 0.7, 4.5, 9);

            if (point.IsLatest)
            {
                var ring = new Border
                {
                    Width = size + 7,
                    Height = size + 7,
                    CornerRadius = new CornerRadius((size + 7) / 2),
                    BorderThickness = new Thickness(1),
                    BorderBrush = latestRing,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetRow(ring, 1);
                Grid.SetColumn(ring, index);
                grid.Children.Add(ring);

                var latestLabel = new TextBlock
                {
                    Text = L.T("Home.Rewind.Latest", "最新"),
                    FontSize = 9.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeTokens.Brush("NekoAccentBrush"),
                    // 贴最右一列：右对齐，避免「最新」被卡片右边缘裁掉
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    TextAlignment = TextAlignment.Right,
                };
                Grid.SetRow(latestLabel, 0);
                Grid.SetColumn(latestLabel, index);
                grid.Children.Add(latestLabel);
            }

            var dot = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size / 2),
                Background = RewindColorBrush(point.Color),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetRow(dot, 1);
            Grid.SetColumn(dot, index);
            grid.Children.Add(dot);

            // 时间标签稀疏显示：从最新往旧每隔 labelStep 个标一个，避免挤成一团。
            // 时间用显式 "HH:mm" 拼接（不依赖区域设置的 TimeSeparator），
            // 放在轴线下方固定 8px 处，不会压到圆点；最左/最右一列贴边对齐防裁切。
            if (point.Time is { } time && (columns - 1 - index) % labelStep == 0)
            {
                var alignment = index == 0
                    ? HorizontalAlignment.Left
                    : index == columns - 1
                        ? HorizontalAlignment.Right
                        : HorizontalAlignment.Center;
                var alignmentText = alignment switch
                {
                    HorizontalAlignment.Left => TextAlignment.Left,
                    HorizontalAlignment.Right => TextAlignment.Right,
                    _ => TextAlignment.Center,
                };

                var label = new TextBlock
                {
                    Text = $"{time.LocalDateTime.Hour:00}:{time.LocalDateTime.Minute:00}",
                    FontSize = 9.5,
                    Foreground = ThemeTokens.Brush("NekoDimBrush"),
                    HorizontalAlignment = alignment,
                    VerticalAlignment = VerticalAlignment.Top,
                    TextAlignment = alignmentText,
                    Margin = new Thickness(0, 8, 0, 0),
                    // 列宽可能比 "HH:mm" 还窄：给一个最小宽度并允许溢出绘制，避免被裁成 "21:1"
                    MinWidth = 34,
                    ClipToBounds = false,
                    TextTrimming = TextTrimming.None,
                };
                Grid.SetRow(label, 2);
                Grid.SetColumn(label, index);
                grid.Children.Add(label);
            }
        }

        return grid;
    }

    /// <summary>快照的标记色键 → 主题令牌画刷（内容色只做语义映射，不写死色值）。</summary>
    private static IBrush RewindColorBrush(string? color) => color switch
    {
        "primary" => ThemeTokens.Brush("NekoAccentBrush"),
        "secondary" => ThemeTokens.Brush("NekoInfoBrush"),
        "success" => ThemeTokens.Brush("NekoSuccessBrush"),
        "warning" => ThemeTokens.Brush("NekoWarningBrush"),
        "danger" => ThemeTokens.Brush("NekoDangerBrush"),
        _ => ThemeTokens.Brush("NekoDimBrush"),
    };

    /// <summary>
    /// 读取真实快照：先按当前实例找目标目录（versions/ → saves/），
    /// 找到就用它的快照列表；否则退到全局汇总（只有颜色没有时间）；都没有就空态。
    /// </summary>
    private static async Task LoadRewindAsync(Action<IReadOnlyList<RewindPoint>, int, long, string> apply)
    {
        try
        {
            var target = FindRewindTarget();
            if (target is not null)
            {
                var snapshots = await RewindSystem.ListSnapshotsAsync(target).ConfigureAwait(true);
                if (snapshots.Count > 0)
                {
                    var points = snapshots
                        .Take(12)
                        .Select(snapshot => new RewindPoint(snapshot.CreatedAt, snapshot.Color, false))
                        .ToList();
                    points.Reverse();
                    points[^1] = points[^1] with { IsLatest = true };

                    apply(points, snapshots.Count,
                        snapshots.Sum(snapshot => snapshot.TotalSize),
                        L.T("Home.Rewind.Recent", "最近 {0}", snapshots[0].CreatedAt.LocalDateTime.ToString("MM-dd HH:mm")));
                    return;
                }
            }

            var summary = await RewindSystem.GetSummaryAsync().ConfigureAwait(true);
            if (summary.SnapshotCount > 0)
            {
                var points = summary.RecentColors
                    .AsEnumerable()
                    .Reverse()
                    .Select(color => new RewindPoint(null, color, false))
                    .ToList();
                if (points.Count > 0) points[^1] = points[^1] with { IsLatest = true };

                apply(points, summary.SnapshotCount, summary.BlobBytes,
                    target is null
                        ? L.T("Home.Rewind.NoGameDir", "未找到游戏目录 · 显示全局快照")
                        : L.T("Home.Rewind.NoInstanceSnapshots", "当前实例还没有快照 · 显示全局快照"));
                return;
            }
        }
        catch
        {
            // 读取失败保持空态，不影响桌面其它组件
        }

        apply(Array.Empty<RewindPoint>(), 0, 0,
            L.T("Home.Rewind.EmptyHint", "在实例页的 Rewind 弹窗里新建快照"));
    }

    /// <summary>按当前选中实例找 Rewind 目标目录（实例目录优先，其次存档目录）。</summary>
    private static string? FindRewindTarget()
    {
        try
        {
            var gameDirectory = YamlRead.GameDirectory;
            if (string.IsNullOrWhiteSpace(gameDirectory)) return null;

            var name = GameState.SelectedVersion;
            if (string.IsNullOrWhiteSpace(name)) return null;
            foreach (var root in new[] { Path.Combine(gameDirectory, "versions"), Path.Combine(gameDirectory, "saves") })
            {
                var directory = Path.Combine(root, name);
                if (Directory.Exists(directory)) return directory;
            }
        }
        catch
        {
            // 游戏目录不可读时按没有处理
        }

        return null;
    }

    private static string FormatRewindBytes(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0.0} MB";
        if (bytes >= 1024) return $"{bytes / 1024d:0} KB";
        return $"{bytes} B";
    }

    // ================= 下载任务（真实：DownloadTaskService 全局任务中心） =================

    public static Control Download()
    {
        var rowsHost = new StackPanel { Spacing = 6 };
        var (card, valueText) = WidgetChrome.CardWithValue(
            L.T("Home.DownloadWidget.Loading", "读取中…"),
            null,
            rowsHost);

        void Render()
        {
            var tasks = DownloadTaskService.Snapshots;
            int active = tasks.Count(item =>
                item.State is DownloadTaskState.Downloading or DownloadTaskState.Paused);
            valueText.Text = active > 0
                ? L.T("Home.DownloadWidget.ActiveCount", "{0} 个进行中", active)
                : L.T("Home.DownloadWidget.Idle", "空闲");

            rowsHost.Children.Clear();
            if (tasks.Count == 0)
            {
                rowsHost.Children.Add(WidgetChrome.Empty(
                    L.T("Home.DownloadWidget.EmptyHint", "还没有下载任务；安装游戏或资源后会显示在这里")));
                rowsHost.Children.Add(WidgetChrome.LinkButton(
                    L.T("Home.Download.Open", "打开下载页"), () => Shell.Go("download"), "IconFluentDownload"));
                return;
            }

            // 进行中的排前面，其余按原顺序收尾；最多显示 4 条
            foreach (DownloadTaskSnapshot task in tasks
                         .OrderBy(item => item.State is DownloadTaskState.Downloading or DownloadTaskState.Paused ? 0 : 1)
                         .Take(4))
            {
                rowsHost.Children.Add(BuildDownloadTaskRow(task));
            }

            rowsHost.Children.Add(WidgetChrome.LinkButton(
                L.T("Home.Download.Open", "打开下载页"), () => Shell.Go("download"), "IconFluentDownload"));
        }

        void OnServiceChanged(DownloadTaskSnapshot _) => Render();
        DownloadTaskService.Added += OnServiceChanged;
        DownloadTaskService.Changed += OnServiceChanged;
        WidgetChrome.OnDetach(card, () =>
        {
            DownloadTaskService.Added -= OnServiceChanged;
            DownloadTaskService.Changed -= OnServiceChanged;
        });

        WidgetChrome.OnBecameVisible(card, Render);
        Render();
        return card;
    }

    private static Control BuildDownloadTaskRow(DownloadTaskSnapshot task)
    {
        bool active = task.State is DownloadTaskState.Downloading or DownloadTaskState.Paused;

        var title = WidgetChrome.Text(task.Title, WidgetChrome.BodySize, "NekoTextBrush", FontWeight.SemiBold);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.MaxLines = 1;

        var subtitle = WidgetChrome.Caption(active
            ? L.T("Home.DownloadWidget.RowActive", "{0:0}% · {1}", task.Percent, task.Detail)
            : task.State switch
            {
                DownloadTaskState.Done => L.T("Download.Task.Status.Done", "已完成"),
                DownloadTaskState.Paused => L.T("Download.Task.Status.Paused", "暂停中"),
                DownloadTaskState.Cancelled => L.T("Download.Task.Status.Cancelled", "已取消"),
                DownloadTaskState.Failed => L.T("Download.Task.Status.Failed", "失败"),
                _ => L.T("Download.Task.Status.Downloading", "下载中"),
            });
        subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
        subtitle.MaxLines = 1;

        var column = new StackPanel { Spacing = 3 };
        column.Children.Add(title);
        if (active)
        {
            column.Children.Add(WidgetChrome.Bar(task.Percent));
        }

        column.Children.Add(subtitle);
        return column;
    }
}
