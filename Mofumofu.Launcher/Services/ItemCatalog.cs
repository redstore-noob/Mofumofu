using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Avalonia.Platform;

namespace Mofumofu.Launcher.Services;

public sealed record ItemOption(string Id, string Zh, string En)
{
    public static ItemOption OfId(string id) => new(id, string.Empty, string.Empty);
}

/// <summary>
/// 命令生成器的注册表目录：内嵌自原版 client.jar 的语言文件（zh_cn / en_us），
/// 覆盖方块、物品、状态效果、附魔、实体的全部注册项与官方译名。
/// 数据由构建脚本从对应版本的 jar / 资源对象库提取，版本号见 <see cref="Version"/>。
/// 加载失败时各分类为空表，自动补全回退到命令规格里的预设选项。
/// </summary>
public static class ItemCatalog
{
    private static readonly Dictionary<string, List<ItemOption>> Categories = new();

    public static string Version { get; private set; } = string.Empty;

    private static readonly List<ItemOption> GiveItems = new();

    static ItemCatalog()
    {
        try
        {
            using var stream = AssetLoader.Open(
                new Uri("avares://Mofumofu.Launcher/Assets/command-catalog.json"));
            using var json = JsonDocument.Parse(stream);
            var root = json.RootElement;
            Version = root.GetProperty("version").GetString() ?? string.Empty;

            foreach (var (category, entries) in new[]
                     {
                         ("block", root.GetProperty("blocks")),
                         ("item", root.GetProperty("items")),
                         ("effect", root.GetProperty("effects")),
                         ("enchant", root.GetProperty("enchantments")),
                         ("entity", root.GetProperty("entities")),
                     })
            {
                Categories[category] = entries.EnumerateArray()
                    .Select(entry => new ItemOption(
                        entry.GetProperty("id").GetString() ?? string.Empty,
                        entry.GetProperty("zh").GetString() ?? string.Empty,
                        entry.GetProperty("en").GetString() ?? string.Empty))
                    .Where(option => option.Id.Length > 0)
                    .ToList();
            }
        }
        catch
        {
            // 目录缺失或损坏时保持空表：自动补全回退到规格里的预设选项
        }

        GiveItems.AddRange(BuildGiveItems());
    }

    private static IEnumerable<ItemOption> BuildGiveItems()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in Categories.GetValueOrDefault("item", new List<ItemOption>())
                     .Concat(Categories.GetValueOrDefault("block", new List<ItemOption>())))
        {
            if (seen.Add(option.Id)) yield return option;
        }
    }

    public static int Count(string category) => Resolve(category).Count;

    private static List<ItemOption> Resolve(string category) => category switch
    {
        "item" => GiveItems,
        _ => Categories.GetValueOrDefault(category, new List<ItemOption>()),
    };

    public static IReadOnlyList<ItemOption> Search(string category, string? query, int limit = 12)
    {
        var source = Resolve(category);
        var keyword = (query ?? string.Empty).Trim();
        if (keyword.Length == 0)
        {
            return source.OrderBy(option => option.Id, StringComparer.Ordinal).Take(limit).ToList();
        }

        return source
            .Select(option => (Option: option, Score: Score(option, keyword)))
            .Where(pair => pair.Score < int.MaxValue)
            .OrderBy(pair => pair.Score)
            .ThenBy(pair => pair.Option.Id.Length)
            .ThenBy(pair => pair.Option.Id, StringComparer.Ordinal)
            .Select(pair => pair.Option)
            .Take(limit)
            .ToList();
    }

    private static int Score(ItemOption option, string keyword)
    {
        if (option.Id.Equals(keyword, StringComparison.OrdinalIgnoreCase)) return 0;
        if (option.Zh.Equals(keyword, StringComparison.Ordinal)) return 0;
        if (option.Id.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)) return 1;
        if (option.Zh.StartsWith(keyword, StringComparison.Ordinal)) return 2;
        if (option.Id.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return 3;
        if (option.Zh.Contains(keyword, StringComparison.Ordinal)) return 4;
        if (option.En.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return 5;

        return int.MaxValue;
    }
}
