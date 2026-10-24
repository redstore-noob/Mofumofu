using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Mofumofu.Launcher.Controls;
using Mofumofu.Launcher.Mock;
using Mofumofu.Launcher.Services;
using Mofumofu.Launcher.Services.Music;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views.Widgets;

/// <summary>内容 / 趣味类小组件卡片：音乐 / RSS / 运势 / 看板娘 / 一言 / 问候 / 倒计时 / 时钟 / 日历 / 截图 / 目标。</summary>
internal static partial class WidgetCards
{
    private static readonly string[] WeekdayKeys =
    {
        "Home.Weekday.Sun", "Home.Weekday.Mon", "Home.Weekday.Tue", "Home.Weekday.Wed",
        "Home.Weekday.Thu", "Home.Weekday.Fri", "Home.Weekday.Sat",
    };

    private static readonly string[] WeekdayFallbacks = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

    private static string WeekdayName(DayOfWeek day) =>
        L.T(WeekdayKeys[(int)day], WeekdayFallbacks[(int)day]);

    // ================= 音乐控制（真实播放状态，走全局 MusicService） =================

    public static Control Music()
    {
        var service = MusicService.Instance;

        var emptyHint = WidgetChrome.Empty(L.T("Home.Music.EmptyHint", "还没有在播放音乐，去音乐页挑一首吧"));

        var titleText = WidgetChrome.Text("—", WidgetChrome.BodySize, "NekoTextBrush", FontWeight.SemiBold);
        titleText.TextTrimming = TextTrimming.CharacterEllipsis;
        titleText.MaxLines = 1;

        var metaText = WidgetChrome.Muted("—");
        metaText.TextTrimming = TextTrimming.CharacterEllipsis;
        metaText.MaxLines = 1;

        // 播放/暂停按钮要换图标，不用 IconButton 的封装，自己持有图标引用
        var playIcon = WidgetChrome.Icon("IconPlay", 13, "NekoTextBrush");
        playIcon.HorizontalAlignment = HorizontalAlignment.Center;
        playIcon.VerticalAlignment = VerticalAlignment.Center;
        var playButton = new Button
        {
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            Background = ThemeTokens.Brush("NekoAccentSoftBrush"),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(15),
            Content = playIcon,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(playButton, L.T("Pages.Music.Play", "播放"));
        playButton.Click += (_, _) =>
        {
            if (service.HasTrack) service.Toggle();
            else Shell.Go("music");
        };

        var prevButton = WidgetChrome.IconButton("IconMediaPrevious",
            L.T("Pages.Music.Prev", "上一首"), () => service.Previous());
        var nextButton = WidgetChrome.IconButton("IconMediaNext",
            L.T("Pages.Music.Next", "下一首"), () => service.Next());

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { prevButton, playButton, nextButton },
        };

        var playingBody = WidgetChrome.Column(6, titleText, metaText, controls);
        var root = WidgetChrome.Column(0, emptyHint, playingBody);

        void Update()
        {
            MusicItem? item = service.Current;
            bool hasContent = item is not null || service.HasTrack;
            emptyHint.IsVisible = !hasContent;
            playingBody.IsVisible = hasContent;
            if (item is null) return;

            titleText.Text = item.Title;
            metaText.Text = string.IsNullOrWhiteSpace(item.Artist) ? item.FileInfo : item.Artist;
            bool playing = service.IsPlaying;
            ((IconParkIcon)playIcon).Kind = playing ? "IconPause" : "IconPlay";
            ToolTip.SetTip(playButton, playing
                ? L.T("Pages.Music.Pause", "暂停")
                : L.T("Pages.Music.Play", "播放"));
        }

        service.StateChanged += Update;
        WidgetChrome.OnDetach(root, () => service.StateChanged -= Update);
        Update();

        return WidgetChrome.Card(
            L.T("Widgets.music.Title", "音乐控制"),
            WidgetChrome.IconButton("IconFluentMusic", L.T("Home.Music.Page", "音乐页"), () => Shell.Go("music")),
            root);
    }

    // ================= RSS 订阅（未接入 → 空态） =================

    public static Control RssFeed() =>
        WidgetChrome.EmptyStateCard(
            "IconFluentRss",
            L.T("Home.Rss.EmptyHint", "RSS 订阅还没有接入真实后端；接入后会在这里显示订阅条目"),
            L.T("Home.Rss.Manage", "管理订阅"),
            () => Shell.Go("plugins"));

    // ================= 今日运气 =================

    private static int _luckRefresh;

    public static Control DailyLuck()
    {
        var now = DateTime.Now;
        var day = now.ToString("yyyy-MM-dd");
        var luck = ComputeFortune(day);

        var body = new StackPanel { Spacing = 8 };
        // 运势是每天固定的小装饰，不是统计值：不显示分数
        body.Children.Add(new TextBlock
        {
            Text = $"{day} {WeekdayName(now.DayOfWeek)}",
            FontSize = WidgetChrome.BodySize,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeTokens.Brush("NekoTextBrush"),
        });

        body.Children.Add(WidgetChrome.Row(6,
            WidgetChrome.Pill(L.T("Home.Luck.Good", "宜"), "NekoSuccessBrush", "NekoSuccessSoftBrush"),
            WidgetChrome.Text(luck.Good, WidgetChrome.BodySize, "NekoMutedBrush")));
        body.Children.Add(WidgetChrome.Row(6,
            WidgetChrome.Pill(L.T("Home.Luck.Bad", "忌"), "NekoDangerBrush", "NekoDangerSoftBrush"),
            WidgetChrome.Text(luck.Bad, WidgetChrome.BodySize, "NekoMutedBrush")));

        var dot = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(5),
            Background = ThemeTokens.FromHex(luck.ColorHex),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var colorLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        colorLine.Children.Add(dot);
        colorLine.Children.Add(WidgetChrome.Caption(luck.ColorName));
        body.Children.Add(WidgetChrome.Line(
            WidgetChrome.InlineIcon("IconFluentSparkle", L.T("Home.Luck.LuckyNumber", "幸运数字 {0}", luck.Number), "NekoDimBrush"),
            colorLine));

        return WidgetChrome.Card(LuckLevelName(luck.Level), null, body);
    }

    /// <summary>运势档位中文（数据层）→ 语言包里的显示名。</summary>
    private static string LuckLevelName(string level) => level switch
    {
        "大吉" => L.T("Home.Luck.Level.Great", "大吉"),
        "中吉" => L.T("Home.Luck.Level.Good", "中吉"),
        "小吉" => L.T("Home.Luck.Level.Small", "小吉"),
        "末吉" => L.T("Home.Luck.Level.Late", "末吉"),
        "凶" => L.T("Home.Luck.Level.Bad", "凶"),
        "大凶" => L.T("Home.Luck.Level.Worst", "大凶"),
        _ => L.T("Home.Luck.Level.Normal", "吉"),
    };

    private sealed record Fortune(string Level, int Score, string Good, string Bad, string ColorName, string ColorHex, int Number);

    /// <summary>日期 + 固定设备码决定当日运势（同一台机器同一天结果恒定）。</summary>
    private static Fortune ComputeFortune(string day)
    {
        var seed = day.Aggregate(2166136261u, (current, ch) => (current ^ ch) * 16777619u) ^ (uint)_luckRefresh;
        var rand = new Random(unchecked((int)seed));

        var levels = new (string Label, int Weight, int Min, int Max)[]
        {
            ("大吉", 12, 90, 100), ("中吉", 20, 75, 89), ("小吉", 20, 60, 74),
            ("吉", 24, 45, 59), ("末吉", 10, 30, 44), ("凶", 10, 12, 29), ("大凶", 4, 0, 11),
        };

        var total = levels.Sum(level => level.Weight);
        var ticket = rand.Next(total);
        var chosen = levels[0];
        foreach (var level in levels)
        {
            ticket -= level.Weight;
            if (ticket < 0)
            {
                chosen = level;
                break;
            }
        }

        var good = WidgetData.GoodAt[rand.Next(WidgetData.GoodAt.Count)];
        var bad = WidgetData.BadAt[rand.Next(WidgetData.BadAt.Count)];
        var color = WidgetData.LuckyColors[rand.Next(WidgetData.LuckyColors.Count)];

        return new Fortune(chosen.Label, rand.Next(chosen.Min, chosen.Max + 1), good, bad, color.Name, color.Hex, rand.Next(1, 100));
    }

    private static Avalonia.Media.Imaging.Bitmap? AssetBitmap(string assetUri)
    {
        try
        {
            return new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri(assetUri)));
        }
        catch
        {
            return null;
        }
    }

    // ================= 一言 =================

    private static int _hitokotoIndex;

    public static Control Hitokoto()
    {
        var quote = WidgetData.Hitokoto[_hitokotoIndex % WidgetData.Hitokoto.Count];
        var quoteText = WidgetChrome.Text(L.T("Home.Hitokoto.Quote", "「{0}」", quote.Text), WidgetChrome.BodySize + 1, "NekoTextBrush");
        quoteText.TextWrapping = TextWrapping.Wrap;

        // 内置句子属于装饰内容，不再挂「来源」字样（避免看起来像真实统计数据）
        var body = new StackPanel { Spacing = 6, Children = { quoteText } };

        return WidgetChrome.Card(L.T("Home.Hitokoto.Title", "一言"),
            WidgetChrome.IconButton("IconFluentArrowClockwise", L.T("Home.Hitokoto.Another", "换一条"), () =>
            {
                _hitokotoIndex = (_hitokotoIndex + 1) % WidgetData.Hitokoto.Count;
                quoteText.Text = L.T("Home.Hitokoto.Quote", "「{0}」", WidgetData.Hitokoto[_hitokotoIndex].Text);
            }),
            body);
    }

    // ================= 问候 =================

    public static Control Greeting()
    {
        var hour = DateTime.Now.Hour;
        var (greeting, emoji) = hour switch
        {
            >= 5 and < 12 => (L.T("Home.Greeting.Morning", "早上好"), "🌅"),
            >= 12 and < 18 => (L.T("Home.Greeting.Noon", "中午好"), "☀️"),
            _ => (L.T("Home.Greeting.Evening", "晚上好"), "🌙"),
        };
        var quote = WidgetData.GreetingQuotes[hour % WidgetData.GreetingQuotes.Count];
        var body = WidgetChrome.Text(quote, WidgetChrome.BodySize + 1, "NekoMutedBrush");

        return WidgetChrome.Card($"{emoji} {greeting}", null, body);
    }

    // ================= 假期倒计时 =================

    public static Control Countdown()
    {
        var now = DateTime.Now;
        var targets = BuildCountdownTargets(now);
        var primary = targets.FirstOrDefault();

        var body = new StackPanel { Spacing = 6 };
        if (primary is not null)
        {
            body.Children.Add(WidgetChrome.Line(
                new TextBlock
                {
                    Text = primary.Name,
                    FontSize = WidgetChrome.BodySize + 1,
                    FontWeight = FontWeight.Bold,
                    Foreground = ThemeTokens.Brush("NekoTextBrush"),
                },
                WidgetChrome.Caption(L.T("Home.Countdown.DateLine", "{0} 月 {1} 日 · {2}",
                    primary.Date.Month, primary.Date.Day, WeekdayName(primary.Date.DayOfWeek)))));
        }

        foreach (var target in targets.Skip(1).Take(3))
        {
            body.Children.Add(WidgetChrome.Line(
                WidgetChrome.Muted(target.Name),
                WidgetChrome.Caption(DaysLabel(target.Days))));
        }

        if (targets.Count == 0)
        {
            body.Children.Add(WidgetChrome.Empty(L.T("Home.Countdown.None", "近期没有节假日")));
        }

        var value = primary is null
            ? (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                ? L.T("Home.Countdown.WeekendNow", "周末进行中")
                : L.T("Home.Countdown.NoTarget", "暂无目标"))
            : DaysLabel(primary.Days);
        return WidgetChrome.Card(value, null, body);
    }

    private sealed record CountdownTarget(string Name, DateTime Date, int Days);

    private static List<CountdownTarget> BuildCountdownTargets(DateTime now)
    {
        var today = now.Date;
        var targets = new List<CountdownTarget>();

        for (var year = now.Year; year <= now.Year + 1; year++)
        {
            foreach (var (month, day, name) in WidgetData.HolidayTable(year))
            {
                var date = new DateTime(year, month, day);
                var days = (int)Math.Round((date - today).TotalDays);
                if (days >= 0) targets.Add(new CountdownTarget(name, date, days));
            }
        }

        var saturday = today.AddDays(((int)DayOfWeek.Saturday - (int)today.DayOfWeek + 7) % 7);
        var saturdayDays = (int)(saturday - today).TotalDays;
        if (saturdayDays > 0) targets.Add(new CountdownTarget(L.T("Home.Countdown.Weekend", "周末"), saturday, saturdayDays));

        targets.Sort((left, right) => left.Days != right.Days ? left.Days.CompareTo(right.Days) : left.Date.CompareTo(right.Date));

        var seen = new HashSet<int>();
        var unique = new List<CountdownTarget>();
        foreach (var target in targets)
        {
            if (!seen.Add(target.Days)) continue;
            unique.Add(target);
            if (unique.Count >= 4) break;
        }

        return unique;
    }

    private static string DaysLabel(int days) => days switch
    {
        <= 0 => L.T("Home.Countdown.Today", "就是今天"),
        1 => L.T("Home.Countdown.Tomorrow", "明天"),
        _ => L.T("Home.Countdown.InDays", "{0} 天后", days),
    };

    // ================= 时钟 =================

    public static Control Clock()
    {
        var timeText = WidgetChrome.Text(DateTime.Now.ToString("HH:mm:ss"), 26, "NekoTextBrush", FontWeight.Bold);
        var dateText = WidgetChrome.Caption(L.T("Home.Clock.Date", "{0} 月 {1} 日 {2}",
            DateTime.Now.Month, DateTime.Now.Day, WeekdayName(DateTime.Now.DayOfWeek)));

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            var now = DateTime.Now;
            timeText.Text = now.ToString("HH:mm:ss");
            dateText.Text = L.T("Home.Clock.Date", "{0} 月 {1} 日 {2}", now.Month, now.Day, WeekdayName(now.DayOfWeek));
        };
        timer.Start();

        var body = new StackPanel { Spacing = 2, Children = { timeText, dateText } };
        var card = WidgetChrome.Card(body);
        WidgetChrome.OnDetach(card, () => timer.Stop());
        return card;
    }

    // ================= 日历 =================

    public static Control Calendar()
    {
        var offset = 0;
        var headerText = WidgetChrome.Text(string.Empty, WidgetChrome.ValueSize - 3, "NekoTextBrush", FontWeight.Bold);
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto"),
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*,*,*"),
        };

        void Refresh()
        {
            var view = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1).AddMonths(offset);
            headerText.Text = L.T("Home.Calendar.MonthTitle", "{0} 年 {1} 月", view.Year, view.Month);

            grid.Children.Clear();
            for (var column = 0; column < 7; column++)
            {
                var label = new TextBlock
                {
                    Text = L.T(CalendarWeekdayKeys[column], CalendarWeekdayFallbacks[column]),
                    FontSize = WidgetChrome.CaptionSize - 0.5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = ThemeTokens.Brush(column is 0 or 6 ? "NekoDangerBrush" : "NekoDimBrush"),
                };
                Grid.SetColumn(label, column);
                grid.Children.Add(label);
            }

            var first = view.AddDays(1 - view.Day);
            var start = first.AddDays(-(int)first.DayOfWeek);
            var today = DateTime.Today;

            for (var index = 0; index < 42; index++)
            {
                var date = start.AddDays(index);
                var isToday = date == today;
                var inMonth = date.Month == view.Month;
                var holiday = WidgetData.HolidayTable(date.Year).Any(item => item.Month == date.Month && item.Day == date.Day);
                var isWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

                var cell = new Border
                {
                    Width = 22,
                    Height = 20,
                    CornerRadius = new CornerRadius(11),
                    Background = isToday ? ThemeTokens.Brush("NekoAccentBrush") : Brushes.Transparent,
                    Child = new TextBlock
                    {
                        Text = date.Day.ToString(),
                        FontSize = WidgetChrome.CaptionSize,
                        FontWeight = isToday ? FontWeight.Bold : FontWeight.Normal,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = ThemeTokens.Brush(
                            isToday ? "NekoAccentOnBrush"
                            : !inMonth ? "NekoDimBrush"
                            : holiday || isWeekend ? "NekoDangerBrush"
                            : "NekoTextBrush"),
                    },
                    HorizontalAlignment = HorizontalAlignment.Center,
                };

                Grid.SetRow(cell, 1 + index / 7);
                Grid.SetColumn(cell, index % 7);
                grid.Children.Add(cell);
            }
        }

        Refresh();

        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        nav.Children.Add(WidgetChrome.IconButton("IconFluentArrowCounterclockwise", L.T("Home.Calendar.PrevMonth", "上个月"), () =>
        {
            offset--;
            Refresh();
        }));
        nav.Children.Add(WidgetChrome.IconButton("IconFluentArrowClockwise", L.T("Home.Calendar.NextMonth", "下个月"), () =>
        {
            offset++;
            Refresh();
        }));
        nav.Children.Add(WidgetChrome.IconButton("IconFluentTarget", L.T("Home.Calendar.Today", "回到今天"), () =>
        {
            offset = 0;
            Refresh();
        }));

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(WidgetChrome.Line(headerText, nav));
        content.Children.Add(grid);

        return WidgetChrome.Card(content);
    }

    private static readonly string[] CalendarWeekdayKeys =
    {
        "Home.Calendar.WeekdaySun", "Home.Calendar.WeekdayMon", "Home.Calendar.WeekdayTue",
        "Home.Calendar.WeekdayWed", "Home.Calendar.WeekdayThu", "Home.Calendar.WeekdayFri",
        "Home.Calendar.WeekdaySat",
    };

    private static readonly string[] CalendarWeekdayFallbacks = { "日", "一", "二", "三", "四", "五", "六" };

    // ================= 截图墙（无截图扫描后端 → 空态） =================

    public static Control Screenshots() =>
        WidgetChrome.EmptyStateCard(
            "IconFluentImage",
            L.T("Home.Screenshots.EmptyHint", "截图墙还没有接入真实扫描；接入后会在这里显示最近截图"),
            L.T("Home.Screenshots.OpenPage", "前往实例页"),
            () => Shell.Go("instances"));

    // ================= 成就目标（无游玩时长统计 → 空态） =================

    public static Control Goal() =>
        WidgetChrome.EmptyStateCard(
            "IconFluentTarget",
            L.T("Home.Goal.EmptyHint", "游玩时长统计还没有接入真实数据，暂时无法计算目标进度"),
            L.T("Home.Goal.Open", "前往实例页"),
            () => Shell.Go("instances"));
}
