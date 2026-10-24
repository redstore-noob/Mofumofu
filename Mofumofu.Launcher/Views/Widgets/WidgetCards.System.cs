using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MofuCore.Config;
using MofuCore.Java;
using MofuCore.Tools;
using Mofumofu.Launcher.Mock;
using Mofumofu.Launcher.Services;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views.Widgets;

/// <summary>系统类小组件卡片：内存 / 性能 / Java / 磁盘 / 网络 / 皮肤。</summary>
internal static partial class WidgetCards
{
    // ================= 内存监控 =================

    public static Control Memory()
    {
        var launcher = BuildMemoryRow(L.T("Home.Memory.Launcher", "启动器"));
        var jvm = BuildMemoryRow("JVM");

        var javaPill = WidgetChrome.Pill(string.Empty, "NekoDimBrush", "NekoFieldSoftBrush");
        var javaPillText = (TextBlock)javaPill.Child!;

        var body = new StackPanel { Spacing = 9 };
        body.Children.Add(launcher.Row);
        body.Children.Add(jvm.Row);
        body.Children.Add(javaPill);

        var (card, valueText) = WidgetChrome.CardWithValue(L.T("Home.Memory.Loading", "读取中…"), null, body);

        var busy = false;
        async void Refresh()
        {
            if (busy) return;
            busy = true;
            try
            {
                var usage = await MemoryRead.SystemUsageNowAsync();
                var snapshot = await MemoryRead.SnapshotAsync();

                valueText.Text = $"{usage.MemoryUsedGb:0.#} / {usage.MemoryTotalGb:0} GB";

                var scale = Math.Max(Math.Max(snapshot.LauncherMemoryMb, snapshot.JvmMemoryMb), 1);
                launcher.Value.Text = FormatMemory(snapshot.LauncherMemoryMb);
                jvm.Value.Text = FormatMemory(snapshot.JvmMemoryMb);
                SetBar(launcher.Bar, snapshot.LauncherMemoryMb / scale * 100);
                SetBar(jvm.Bar, snapshot.JvmMemoryMb / scale * 100);

                var count = snapshot.JavaProcessCount;
                javaPillText.Text = count > 0
                    ? L.T("Home.Memory.JavaProcesses", "{0} 个 Java 进程", count)
                    : L.T("Home.Memory.NoJava", "没有 Java 进程在运行");
                javaPill.Background = ThemeTokens.Brush(count > 0 ? "NekoSuccessSoftBrush" : "NekoFieldSoftBrush");
                javaPillText.Foreground = ThemeTokens.Brush(count > 0 ? "NekoSuccessBrush" : "NekoDimBrush");
            }
            catch
            {
                // 采集失败保留上一次数值
            }
            finally
            {
                busy = false;
            }
        }

        // 真实采样：每 2 秒一次；组件不可见（在组件盒里 / 被盖住）时跳过，避免白跑 IO
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            if (card.IsEffectivelyVisible) Refresh();
        };
        timer.Start();
        Refresh();
        WidgetChrome.OnDetach(card, timer.Stop);

        return card;
    }

    private static (StackPanel Row, TextBlock Value, Grid Bar) BuildMemoryRow(string label)
    {
        var value = WidgetChrome.Caption("—");
        var bar = WidgetChrome.Bar(0);
        var row = new StackPanel { Spacing = 3 };
        row.Children.Add(WidgetChrome.Line(
            new TextBlock
            {
                Text = label,
                FontSize = WidgetChrome.BodySize,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeTokens.Brush("NekoTextBrush"),
            },
            value));
        row.Children.Add(bar);
        return (row, value, bar);
    }

    /// <summary>更新自绘进度条的比例（Bar 用两段星列当宽度，改列定义即改比例）。</summary>
    private static void SetBar(Grid bar, double percent)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        bar.ColumnDefinitions = new ColumnDefinitions(
            $"{clamped.ToString("0.##", CultureInfo.InvariantCulture)}*," +
            $"{(100 - clamped).ToString("0.##", CultureInfo.InvariantCulture)}*");
    }

    private static string FormatMemory(double megabytes) =>
        megabytes >= 1024 ? $"{megabytes / 1024:0.00} GB" : $"{megabytes:0} MB";

    // ================= 性能监控 =================

    public static Control Performance()
    {
        const int window = 30;

        var cpuValue = WidgetChrome.Caption("—");
        var memoryValue = WidgetChrome.Caption("—");

        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        legend.Children.Add(BuildLegend(L.T("Home.Performance.Cpu", "CPU"), cpuValue, "NekoInfoBrush"));
        legend.Children.Add(BuildLegend(L.T("Home.Performance.Memory", "内存"), memoryValue, "NekoAccentBrush"));

        // 折线图：CPU 与物理内存占用共用一个 0~100 的归一化坐标系
        var chart = new Grid { Height = 92, ClipToBounds = true };
        for (var tick = 0; tick <= 4; tick++)
        {
            var line = new Border
            {
                Height = 1,
                Background = ThemeTokens.Brush(tick is 0 or 4 ? "NekoBorderBrush" : "NekoFieldBrush"),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, tick * 22),
            };
            chart.Children.Add(line);
        }

        var cpuPath = CreateSeriesPath("NekoInfoBrush");
        var memoryPath = CreateSeriesPath("NekoAccentBrush");
        chart.Children.Add(cpuPath);
        chart.Children.Add(memoryPath);

        var caption = WidgetChrome.Caption(L.T("Home.Performance.SampleHint", "每 2 秒采样 · 最近 {0} 秒", window * 2));

        var body = new StackPanel { Spacing = 8, Children = { legend, chart, caption } };
        var (card, valueText) = WidgetChrome.CardWithValue(
            "—", WidgetChrome.Pill(L.T("Home.Performance.Live", "实时")), body);

        var cpuSamples = new List<double>(window);
        var memorySamples = new List<double>(window);
        var busy = false;

        async void Refresh()
        {
            if (busy) return;
            busy = true;
            try
            {
                var usage = await MemoryRead.SystemUsageNowAsync();

                AppendSample(cpuSamples, usage.CpuPercent, window);
                AppendSample(memorySamples, usage.MemoryPercent, window);

                valueText.Text = $"{usage.CpuPercent:0}%";
                cpuValue.Text = $"{usage.CpuPercent:0}%";
                memoryValue.Text = $"{usage.MemoryPercent:0}%";
                cpuPath.Data = BuildSeriesGeometry(cpuSamples);
                memoryPath.Data = BuildSeriesGeometry(memorySamples);
            }
            catch
            {
                // 采样失败保留上一条曲线
            }
            finally
            {
                busy = false;
            }
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            if (card.IsEffectivelyVisible) Refresh();
        };
        timer.Start();
        Refresh();
        WidgetChrome.OnDetach(card, timer.Stop);

        return card;
    }

    private static Control BuildLegend(string label, TextBlock valueText, string brushKey)
    {
        var dot = new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = ThemeTokens.Brush(brushKey),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(dot);
        stack.Children.Add(WidgetChrome.Caption(label));
        stack.Children.Add(valueText);
        return stack;
    }

    private static void AppendSample(List<double> samples, double value, int window)
    {
        samples.Add(Math.Clamp(value, 0, 100));
        if (samples.Count > window) samples.RemoveAt(0);
    }

    /// <summary>把一条 0~100 的序列画成折线（Stretch=Fill 自适应卡片宽度）。</summary>
    private static Path CreateSeriesPath(string brushKey) => new()
    {
        Stroke = ThemeTokens.Brush(brushKey),
        StrokeThickness = 1.4,
        StrokeLineCap = PenLineCap.Round,
        StrokeJoin = PenLineJoin.Round,
        Stretch = Stretch.Fill,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        Margin = new Thickness(0, 2),
        Data = new StreamGeometry(),
    };

    private static StreamGeometry BuildSeriesGeometry(IReadOnlyList<double> values)
    {
        var geometry = new StreamGeometry();
        if (values.Count < 2) return geometry;

        using var context = geometry.Open();
        var step = 100d / (values.Count - 1);
        context.BeginFigure(new Point(0, 100 - Math.Clamp(values[0], 0, 100)), false);
        for (var index = 1; index < values.Count; index++)
        {
            context.LineTo(new Point(index * step, 100 - Math.Clamp(values[index], 0, 100)));
        }

        return geometry;
    }

    // ================= Java 环境 =================

    public static Control Java()
    {
        var rowsHost = new StackPanel { Spacing = 5 };

        var body = new StackPanel { Spacing = 5 };
        body.Children.Add(rowsHost);
        body.Children.Add(WidgetChrome.LinkButton(L.T("Home.Java.DownloadPage", "Java 下载页"), () => Shell.Go("download"), "IconDownload"));

        var action = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        action.Children.Add(WidgetChrome.IconButton("IconDownload", L.T("Home.Java.OpenDownloadTip", "打开 Java 下载页"), () => Shell.Go("download")));

        var (card, valueText) = WidgetChrome.CardWithValue(L.T("Home.Memory.Loading", "读取中…"), action, body);

        var busy = false;
        async void Refresh()
        {
            if (busy) return;
            busy = true;
            try
            {
                var runtimes = await JavaPathSearch.FindAllJavaAsync();

                rowsHost.Children.Clear();
                valueText.Text = L.T("Home.Java.Count", "{0} 个 Java", runtimes.Count);

                if (runtimes.Count == 0)
                {
                    rowsHost.Children.Add(WidgetChrome.Empty(L.T("Home.Java.None", "没有检测到 Java")));
                    return;
                }

                for (var index = 0; index < Math.Min(runtimes.Count, 6); index++)
                {
                    var runtime = runtimes[index];
                    var title = $"Java {runtime.MajorVersion?.ToString(CultureInfo.InvariantCulture) ?? "?"} · {runtime.VendorName}";
                    var version = string.IsNullOrWhiteSpace(runtime.VersionString) ? string.Empty : runtime.VersionString;

                    var trailing = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 4,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    if (index == 0) trailing.Children.Add(WidgetChrome.Pill(L.T("Home.Java.Default", "默认")));
                    if (!string.IsNullOrEmpty(version))
                    {
                        trailing.Children.Add(new TextBlock
                        {
                            Text = version,
                            FontSize = WidgetChrome.CaptionSize + 0.5,
                            Foreground = ThemeTokens.Brush("NekoMutedBrush"),
                            VerticalAlignment = VerticalAlignment.Center,
                        });
                    }

                    rowsHost.Children.Add(WidgetChrome.ListItem("IconFluentCoffee", title, PathTail(runtime.JavaPath), trailing, 24));
                }
            }
            catch
            {
                rowsHost.Children.Clear();
                rowsHost.Children.Add(WidgetChrome.Empty(L.T("Home.Java.None", "没有检测到 Java")));
                valueText.Text = L.T("Home.Java.Count", "{0} 个 Java", 0);
            }
            finally
            {
                busy = false;
            }
        }

        action.Children.Add(WidgetChrome.IconButton("IconFluentArrowClockwise", L.T("Home.Java.RefreshTip", "刷新 Java 列表"),
            () =>
            {
                Refresh();
                Shell.Toast(L.T("Home.Java.Refreshed", "Java 列表已刷新"));
            }));

        // Java 探测开销大：进入桌面（每次可见）时跑一次，其余靠手动刷新
        WidgetChrome.OnBecameVisible(card, Refresh);
        Refresh();
        return card;
    }

    /// <summary>路径尾部两段（避免长路径撑爆卡片）。</summary>
    private static string PathTail(string path)
    {
        var segments = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return path;
        return segments.Length <= 2 ? string.Join('\\', segments) : string.Join('\\', segments[^2..]);
    }

    // ================= 磁盘空间 =================

    public static Control Disk()
    {
        var bar = WidgetChrome.Bar(0, "NekoAccentBrush", 6);
        var usedText = WidgetChrome.Caption("—");
        var totalText = WidgetChrome.Caption("—");
        var pathText = WidgetChrome.Caption(string.Empty);
        pathText.TextTrimming = TextTrimming.CharacterEllipsis;
        pathText.MaxLines = 1;

        var noticeText = new TextBlock
        {
            FontSize = WidgetChrome.CaptionSize + 0.5,
            TextWrapping = TextWrapping.Wrap,
        };
        var notice = new Border
        {
            IsVisible = false,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(9, 6),
            Child = noticeText,
        };

        var body = new StackPanel { Spacing = 7 };
        body.Children.Add(bar);
        body.Children.Add(WidgetChrome.Line(usedText, totalText));
        body.Children.Add(pathText);
        body.Children.Add(notice);

        var action = WidgetChrome.IconButton("IconFolder", L.T("Home.Disk.OpenTip", "打开游戏目录"),
            () => OpenGameDirectory(ResolveGameDirectory()));

        var (card, valueText) = WidgetChrome.CardWithValue(L.T("Home.Memory.Loading", "读取中…"), action, body);

        var busy = false;
        async void Refresh()
        {
            if (busy) return;
            busy = true;
            try
            {
                // 每次采样都重新解析游戏目录：设置里改过目录后不用重建卡片
                var usage = await MemoryRead.GetDiskUsageAsync(ResolveGameDirectory());
                var total = usage.TotalBytes;
                var free = Math.Max(0, usage.FreeBytes);
                var freeRatio = total > 0 ? free / (double)total : 1;
                var used = Math.Max(0, total - free);

                valueText.Text = L.T("Home.Disk.Free", "剩余 {0}", MemoryRead.FormatBytes(free));
                SetBar(bar, usage.UsedPercent);
                usedText.Text = L.T("Home.Disk.Used", "已用 {0}", MemoryRead.FormatBytes(used));
                totalText.Text = L.T("Home.Disk.Total", "共 {0}", MemoryRead.FormatBytes(total));
                pathText.Text = usage.Path;

                var low = freeRatio < 0.2;
                notice.IsVisible = low;
                if (low)
                {
                    var danger = freeRatio < 0.08;
                    notice.Background = ThemeTokens.Brush(danger ? "NekoDangerSoftBrush" : "NekoWarningSoftBrush");
                    noticeText.Foreground = ThemeTokens.Brush(danger ? "NekoDangerBrush" : "NekoWarningBrush");
                    noticeText.Text = danger
                        ? L.T("Home.Disk.LowSpaceDanger", "空间即将耗尽，游戏可能无法写入存档，请尽快清理")
                        : L.T("Home.Disk.LowSpace", "剩余空间偏少，建议清理下载缓存与旧快照");
                }
            }
            catch
            {
                // 读取失败保留上一次数值
            }
            finally
            {
                busy = false;
            }
        }

        // 磁盘占用变化慢：30 秒采样一次即可；不可见时跳过
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += (_, _) =>
        {
            if (card.IsEffectivelyVisible) Refresh();
        };
        timer.Start();
        Refresh();
        WidgetChrome.OnDetach(card, timer.Stop);

        return card;
    }

    private static string ResolveGameDirectory()
    {
        try
        {
            var directory = YamlRead.GameDirectory;
            if (!string.IsNullOrWhiteSpace(directory) && System.IO.Directory.Exists(directory)) return directory;
        }
        catch
        {
            // 配置不可读时用程序目录
        }

        return AppContext.BaseDirectory;
    }

    private static void OpenGameDirectory(string directory)
    {
        try
        {
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            Shell.Toast(L.T("Home.Disk.Opened", "已打开游戏目录"));
        }
        catch
        {
            Shell.Toast(L.T("Home.Disk.OpenFailed", "打开游戏目录失败"));
        }
    }

    // ================= 网络状态（无真实测速后端 → 空态） =================

    public static Control Network() =>
        WidgetChrome.EmptyStateCard(
            "IconFluentGlobe",
            L.T("Home.Network.EmptyHint", "下载源测速还没有接入真实后端；接入后会在这里显示各源延迟"),
            L.T("Home.Network.Open", "前往下载设置"),
            () => Shell.Go("settings"));

    // ================= 皮肤展示（真实：SkinHeadService 本地皮肤缓存） =================

    public static Control Skin()
    {
        var headHost = new Border
        {
            Width = 96,
            Height = 96,
            CornerRadius = new CornerRadius(10),
            Background = ThemeTokens.Brush("NekoFieldBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = WidgetChrome.IconTile("IconFluentPerson", 40, "NekoDimBrush"),
        };

        var hintText = WidgetChrome.Caption(string.Empty);

        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(headHost);
        body.Children.Add(hintText);
        body.Children.Add(WidgetChrome.Line(
            WidgetChrome.Caption(L.T("Home.Skin.Line", "头像来自本地皮肤缓存")),
            WidgetChrome.LinkButton(L.T("Home.Skin.Account", "账户页"), () => Shell.Go("account"), "IconAccount")));

        var account = GameState.SelectedAccount;
        var (card, valueText) = WidgetChrome.CardWithValue(
            string.IsNullOrWhiteSpace(account)
                ? L.T("Home.Launch.AccountMissing", "未登录账号")
                : account,
            null,
            body);

        async void LoadAsync()
        {
            var accountName = GameState.SelectedAccount;
            valueText.Text = string.IsNullOrWhiteSpace(accountName)
                ? L.T("Home.Launch.AccountMissing", "未登录账号")
                : accountName;

            if (string.IsNullOrWhiteSpace(accountName))
            {
                headHost.Child = WidgetChrome.IconTile("IconFluentPerson", 40, "NekoDimBrush");
                hintText.Text = L.T("Home.Skin.NoAccount", "还没有选择账号，登录后这里会显示皮肤头像");
                return;
            }

            var head = await SkinHeadService.LoadHeadAsync(accountName);
            if (head is null)
            {
                headHost.Child = WidgetChrome.IconTile("IconFluentPerson", 40, "NekoDimBrush");
                hintText.Text = L.T("Home.Skin.NoHead", "还没有该账号的皮肤缓存；在账户页登录或上传皮肤后显示");
                return;
            }

            var image = new Image
            {
                Source = head,
                Width = 88,
                Height = 88,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // 32×32 像素头像放大：关掉插值保持像素感
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.None);
            headHost.Child = image;
            hintText.Text = L.T("Home.Skin.SourceLocal", "来自本地皮肤缓存");
        }

        WidgetChrome.OnBecameVisible(card, LoadAsync);
        GameState.Changed += LoadAsync;
        WidgetChrome.OnDetach(card, () => GameState.Changed -= LoadAsync);

        LoadAsync();
        return card;
    }
}
