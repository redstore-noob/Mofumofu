using System;
using System.Collections.Generic;

namespace Mofumofu.Launcher.Mock;

/// <summary>
/// 桌面小组件的内容素材（一言 / 问候 / 今日运气 / 看板娘台词）。
/// 这些是「内容」而不是示例数据；所有真实状态由各卡片的 MofuCore 后端提供。
/// </summary>
public static class WidgetData
{
    /// <summary>一言卡片：内置句子（原版在没有网络时也用这套兜底）。</summary>
    public static readonly IReadOnlyList<(string Text, string From)> Hitokoto = new[]
    {
        ("把该挖的矿挖完，把该做的梦做完。", "Mofumofu"),
        ("世界很大，先去把存档备份了。", "Mofumofu"),
        ("慢慢来，比较快。", "Mofumofu"),
        ("每一个方块，都从第一镐开始。", "Mofumofu"),
    };

    /// <summary>问候卡片：按时段抽一句。</summary>
    public static readonly IReadOnlyList<string> GreetingQuotes = new[]
    {
        "别害怕苦难，那是成为强者的阶梯喵",
        "挖矿的时候不要往下挖呢，会掉到熔岩里的~",
        "在黑暗的地下世界，也要闪闪发光喵",
        "把该挖的矿挖完，把该做的梦做完喵",
        "世界很大，先去把存档备份了",
        "慢慢来，比较快喵",
        "夜晚来临前，别忘记点上火把",
        "漆黑的矿洞里，火把就是希望的光",
    };

    /// <summary>今日运气：宜 / 忌 / 幸运色。</summary>
    public static readonly IReadOnlyList<string> GoodAt = new[]
    {
        "下矿挖钻石", "附魔装备", "精修农场", "与村民交易", "搭建铁路",
        "钓鱼", "探索地牢", "挑战末影龙", "盖一栋新房子", "研究红石机关",
    };

    public static readonly IReadOnlyList<string> BadAt = new[]
    {
        "空手走悬崖", "招惹末影人", "雷雨天举三叉戟", "对着苦力帕贴脸",
        "不带火把下矿", "岩浆边上泡脚", "用木镐挖黑曜石", "跟凋灵比嗓门",
    };

    public static readonly IReadOnlyList<(string Name, string Hex)> LuckyColors = new[]
    {
        ("草方块绿", "#7CBD4C"), ("钻石青", "#5DECDB"), ("下界红", "#B02E26"),
        ("末地紫", "#A64EA4"), ("金苹果黄", "#F5C33B"), ("海洋蓝", "#3C44A9"),
    };

    /// <summary>日历卡片的法定节假日（月, 日, 名称），按年显式列出以免农历误差。</summary>
    public static readonly IReadOnlyDictionary<int, (int Month, int Day, string Name)[]> Holidays = new Dictionary<int, (int, int, string)[]>
    {
        [2025] = new[] { (1, 1, "元旦"), (1, 29, "春节"), (4, 4, "清明节"), (5, 1, "劳动节"), (5, 31, "端午节"), (10, 1, "国庆节"), (10, 6, "中秋节") },
        [2026] = new[] { (1, 1, "元旦"), (2, 17, "春节"), (4, 5, "清明节"), (5, 1, "劳动节"), (6, 19, "端午节"), (9, 25, "中秋节"), (10, 1, "国庆节") },
        [2027] = new[] { (1, 1, "元旦"), (2, 6, "春节"), (4, 5, "清明节"), (5, 1, "劳动节"), (6, 9, "端午节"), (9, 15, "中秋节"), (10, 1, "国庆节") },
        [2028] = new[] { (1, 1, "元旦"), (1, 26, "春节"), (4, 4, "清明节"), (5, 1, "劳动节"), (6, 22, "端午节"), (10, 1, "国庆节"), (10, 3, "中秋节") },
    };

    public static IReadOnlyList<(int Month, int Day, string Name)> HolidayTable(int year) =>
        Holidays.TryGetValue(year, out var table) ? table : Array.Empty<(int, int, string)>();
}
