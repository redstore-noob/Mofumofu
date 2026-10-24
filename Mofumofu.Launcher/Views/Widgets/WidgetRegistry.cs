using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views.Widgets;

/// <summary>
/// 一个桌面小组件的定义。
/// 标题/描述在访问时才查语言包，这样切语言后重建页面能立刻拿到新文案。
/// </summary>
public sealed class WidgetDefinition
{
    private readonly string _titleFallback;
    private readonly string _descriptionFallback;

    public WidgetDefinition(
        string id,
        string titleFallback,
        string descriptionFallback,
        string iconKey,
        int defaultW,
        int defaultH,
        int minW,
        int minH,
        Func<Control> build)
    {
        Id = id;
        _titleFallback = titleFallback;
        _descriptionFallback = descriptionFallback;
        IconKey = iconKey;
        DefaultW = defaultW;
        DefaultH = defaultH;
        MinW = minW;
        MinH = minH;
        Build = build;
    }

    public string Id { get; }

    public string Title => L.T($"Widgets.{Id}.Title", _titleFallback);

    public string Description => L.T($"Widgets.{Id}.Desc", _descriptionFallback);

    public string IconKey { get; }

    public int DefaultW { get; }

    public int DefaultH { get; }

    public int MinW { get; }

    public int MinH { get; }

    public Func<Control> Build { get; }
}

/// <summary>默认摆放在桌面上的组件（含占格大小）。</summary>
public sealed record DefaultPlacement(string Id, int X, int Y, int W, int H);

/// <summary>
/// 小组件注册表：id / 标题 / 描述 / 图标 / 默认占格统一登记，卡片内容由 <see cref="WidgetCards"/> 构建。
/// 标题与描述走语言包 <c>Widgets.&lt;id&gt;.Title</c> / <c>Widgets.&lt;id&gt;.Desc</c>。
/// </summary>
public static class WidgetRegistry
{
    private static readonly List<WidgetDefinition> BuiltIn = Build();

    /// <summary>插件动态注册的小组件（运行期可增删改）。</summary>
    private static readonly Dictionary<string, DynamicWidget> Dynamic = new(StringComparer.Ordinal);

    /// <summary>动态注册表变化（增/删/改）时触发；HomeView 监听后增量重建宿主。</summary>
    public static event Action? Changed;

    /// <summary>全部小组件 = 内置 + 插件动态（顺序即组件盒里的顺序）。</summary>
    public static IReadOnlyList<WidgetDefinition> All
    {
        get
        {
            List<WidgetDefinition> all = [.. BuiltIn];
            foreach (DynamicWidget widget in Dynamic.Values)
            {
                all.Add(widget.ToDefinition());
            }

            return all;
        }
    }

    /// <summary>当前动态注册的组件 id 列表。</summary>
    public static IReadOnlyList<string> DynamicIds => [.. Dynamic.Keys];

    /// <summary>
    /// 注册（或替换）一个插件小组件，返回撤销句柄。id 会被打上插件前缀，无需调用方去重。
    /// </summary>
    public static IDisposable RegisterDynamic(
        string id, string title, string description, Func<Control> build, int defaultW, int defaultH)
    {
        Dynamic[id] = new DynamicWidget(id, title, description, build, defaultW, defaultH);
        Changed?.Invoke();
        return new DynamicWidgetToken(id);
    }

    /// <summary>修改动态组件标题（立即生效并持久化到语言缓存之外的显示）。</summary>
    public static void EditDynamicTitle(string id, string newTitle)
    {
        if (Dynamic.TryGetValue(id, out DynamicWidget? widget) == false) return;
        Dynamic[id] = widget with { Title = newTitle };
        Changed?.Invoke();
    }

    /// <summary>替换动态组件的内容工厂。</summary>
    public static void EditDynamicContent(string id, Func<Control> newBuild)
    {
        if (Dynamic.TryGetValue(id, out DynamicWidget? widget) == false) return;
        Dynamic[id] = widget with { Build = newBuild };
        Changed?.Invoke();
    }

    private static void UnregisterDynamic(string id)
    {
        if (Dynamic.Remove(id)) Changed?.Invoke();
    }

    private sealed record DynamicWidget(
        string Id,
        string Title,
        string Description,
        Func<Control> Build,
        int DefaultW,
        int DefaultH)
    {
        public WidgetDefinition ToDefinition() => new(
            Id, Title, Description, "IconPlugin", DefaultW, DefaultH, 2, 2, Build);
    }

    private sealed class DynamicWidgetToken(string id) : IDisposable
    {
        public void Dispose() => UnregisterDynamic(id);
    }

    /// <summary>初次进入时摆上桌面的组件（一屏之内）。</summary>
    public static IReadOnlyList<DefaultPlacement> DefaultLayout { get; } = new[]
    {
        new DefaultPlacement("greeting", 0, 0, 5, 2),
        new DefaultPlacement("clock", 16, 0, 4, 2),
        new DefaultPlacement("calendar", 11, 0, 5, 4),
        new DefaultPlacement("countdown", 1, 4, 4, 3),
        new DefaultPlacement("luck", 16, 3, 4, 3),
    };

    public static WidgetDefinition? ById(string id) =>
        All.FirstOrDefault(widget => string.Equals(widget.Id, id, StringComparison.Ordinal));

    private static List<WidgetDefinition> Build() => new List<WidgetDefinition>
    {
        new("worlds", "最近存档", "按上次游玩时间列出存档，可直接接着玩",
            "IconFluentGlobe", 6, 4, 3, 2, WidgetCards.RecentWorlds),
        new("hosted-server", "托管服务器", "本机托管的服务器：选择查看状态，可直接启停",
            "IconFluentServer", 5, 3, 3, 3, WidgetCards.HostedServer),
        new("quick-servers", "收藏服务器", "收藏多台外部服务器：实时人数、彩色 MOTD、服务器图标，点击直接进服",
            "IconFluentServer", 6, 5, 3, 3, WidgetCards.QuickServers),
        new("log", "运行日志", "实时输出，退出后推测可能的原因",
            "IconFluentDocument", 7, 3, 3, 2, WidgetCards.LaunchLog),
        new("memory", "内存监控", "启动器与 JVM 内存占用、Java 进程数",
            "IconFluentDesktop", 4, 3, 3, 2, WidgetCards.Memory),
        new("rss", "RSS 订阅", "订阅任意 RSS / Atom 源，点击条目打开原文",
            "IconFluentRss", 6, 4, 3, 2, WidgetCards.RssFeed),
        new("performance", "性能监控", "CPU / GPU / 内存占用折线图，任务管理器同款",
            "IconFluentTarget", 7, 4, 4, 3, WidgetCards.Performance),
        new("luck", "今日运气", "日期 + 设备码决定的每日运势，每天 0 点重摇",
            "IconFluentSparkle", 4, 3, 3, 2, WidgetCards.DailyLuck),
        new("music", "音乐控制", "当前播放曲目与快捷播放控制",
            "IconFluentMusic", 5, 3, 3, 2, WidgetCards.Music),
        new("skin", "皮肤展示", "当前账号的 3D 皮肤预览（双层皮肤 / 披风）",
            "IconFluentPerson", 4, 4, 3, 3, WidgetCards.Skin),
        new("download", "下载任务", "主页查看 Minecraft/实例下载进度、速度与暂停控制",
            "IconFluentDownload", 6, 3, 3, 2, WidgetCards.Download),
        new("java", "Java 环境", "已检测的 Java 版本一览，一键前往 Java 下载页",
            "IconFluentCoffee", 5, 3, 3, 2, WidgetCards.Java),
        new("disk", "磁盘空间", "监控 .minecraft 所在硬盘的剩余空间，快满时变红提醒",
            "IconFluentBox", 4, 3, 3, 2, WidgetCards.Disk),
        new("network", "网络状态", "BMCLAPI / 官方源延迟与可用性，点进下载设置",
            "IconFluentGlobe", 4, 3, 3, 2, WidgetCards.Network),
        new("hitokoto", "一言", "Hitokoto 每日一言，可刷新换一条",
            "IconFluentChat", 5, 3, 3, 2, WidgetCards.Hitokoto),
        new("greeting", "问候", "早上好/中午好/晚上好，配一句 MC 小句子",
            "IconFluentSun", 5, 3, 3, 2, WidgetCards.Greeting),
        new("countdown", "假期倒计时", "距离周末还有几天，以及最近几个假期",
            "IconFluentCalendar", 4, 3, 3, 2, WidgetCards.Countdown),
        new("clock", "时钟", "数字时钟与日期星期，秒级跳动",
            "IconFluentClock", 4, 2, 3, 2, WidgetCards.Clock),
        new("calendar", "日历", "月历视图，节假日红字标记",
            "IconFluentCalendar", 5, 4, 4, 3, WidgetCards.Calendar),
        new("favorites", "常用实例", "手动置顶的实例快捷启动，可收藏当前实例",
            "IconFluentStar", 6, 3, 3, 2, WidgetCards.Favorites),
        new("goal", "成就目标", "自定义游玩时长目标（累计/本周），进度条跟进",
            "IconFluentTarget", 5, 3, 3, 2, WidgetCards.Goal),
        new("rewind", "时光回溯", "快照总数、存储占用与最近快照时间线，直达实例页管理",
            "IconFluentHistory", 4, 3, 3, 2, WidgetCards.Rewind),
        new("screenshots", "截图墙", "轮播当前实例的截图，点击可打开",
            "IconFluentImage", 6, 4, 3, 2, WidgetCards.Screenshots),
    }.Where(widget => widget.Id is not ("playtime" or "rss"
        or "network" or "goal" or "screenshots")).ToList();
}
