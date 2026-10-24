namespace MofuCore.Download;

/// <summary>
/// 用于匹配对应资源的中文名。
/// 数据来源为mcmod，所有中文名归mcmod.cn版权所有。
/// </summary>
public class ChineseResourceName
{
    // 键为 Modrinth / CurseForge 常见的英文标题或 slug（小写），值为 mcmod 上的中文名。
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        // 优化 / 前置
        ["sodium"] = "钠",
        ["lithium"] = "锂",
        ["phosphor"] = "磷",
        ["iris shaders"] = "虹膜光影",
        ["iris"] = "虹膜光影",
        ["optifine"] = "高清修复",
        ["fabric api"] = "Fabric API",
        ["forge"] = "Forge",
        ["neoforge"] = "NeoForge",
        ["quilt loader"] = "Quilt 加载器",
        ["architectury api"] = "Architectury API",
        ["cloth config"] = "布匹配置",
        ["mod menu"] = "模组菜单",
        ["indium"] = "铟",
        ["ferritecore"] = "铁氧体核心",
        ["memoryleakfix"] = "内存泄漏修复",
        ["entityculling"] = "实体剔除",
        ["cull leaves"] = "树叶剔除",
        ["modernfix"] = "现代修复",
        ["embeddium"] = "Embeddium",
        ["rubidium"] = "铷",
        ["oculus"] = "眼罩",
        ["starlight"] = "星光",
        ["krypton"] = "氪",
        ["lazydfu"] = "LazyDFU",

        // 内容 / 科技 / 魔法
        ["jei"] = "JEI 物品管理器",
        ["just enough items"] = "JEI 物品管理器",
        ["rei"] = "REI 物品管理器",
        ["roughly enough items"] = "REI 物品管理器",
        ["emi"] = "EMI 物品管理器",
        ["create"] = "机械动力",
        ["create: astral"] = "机械动力：星界",
        ["create crafts & additions"] = "机械动力：工艺与附加",
        ["botania"] = "植物魔法",
        ["thermal expansion"] = "热力膨胀",
        ["thermal foundation"] = "热力基础",
        ["mekanism"] = "通用机械",
        ["applied energistics 2"] = "应用能源2",
        ["ae2"] = "应用能源2",
        ["refined storage"] = "精致存储",
        ["industrial foregoing"] = "工业先锋",
        ["immersive engineering"] = "沉浸工程",
        ["ender io"] = "末影接口",
        ["tinkers' construct"] = "匠魂",
        ["tinkers construct"] = "匠魂",
        ["twilight forest"] = "暮色森林",
        ["aether"] = "天境",
        ["ars nouveau"] = "新生魔艺",
        ["blood magic"] = "血魔法",
        ["occultism"] = "神秘学",
        ["farmers delight"] = "农夫乐事",
        ["farmer's delight"] = "农夫乐事",
        ["cooking for blockheads"] = "懒人厨房",
        ["journeymap"] = "旅行地图",
        ["xaero's minimap"] = "Xaero 小地图",
        ["xaeros minimap"] = "Xaero 小地图",
        ["xaero's world map"] = "Xaero 世界地图",
        ["minemenu"] = "MineMenu",
        ["appleskin"] = "苹果皮",
        ["jade"] = "玉 🔍",
        ["the one probe"] = "探针",
        ["wthit"] = "WTHIT",
        ["hwyla"] = "Hwyla",
        ["curios"] = "饰品栏",
        ["trinkets"] = "饰品",
        ["patchouli"] = "帕秋莉手册",
        ["geckolib"] = "GeckoLib",
        ["citadel"] = "Citadel",
        ["alex's mobs"] = "Alex 的生物",
        ["alexs mobs"] = "Alex 的生物",
        ["mowzie's mobs"] = "Mowzie 的生物",
        ["ice and fire"] = "冰火传说",
        ["epic fight"] = "史诗战斗",
        ["paraglider"] = "滑翔伞",
        ["waystones"] = "传送石碑",
        ["sophisticated backpacks"] = "精致背包",
        ["sophisticated storage"] = "精致存储",
        ["building gadgets"] = "建筑小工具",
        ["construction wand"] = "建筑魔杖",
        ["litematica"] = "投影",
        ["minihud"] = "迷你 HUD",
        ["tweakeroo"] = "Tweakeroo",
        ["itemscroller"] = "Item Scroller",
        ["carpet"] = "地毯",
        ["servux"] = "Servux",
        ["spark"] = "Spark 性能分析",
        ["chunky"] = "Chunky 预生成",
        ["worldedit"] = "创世神",
        ["worldedit cui"] = "WorldEdit CUI",

        // 整合包
        ["fabulously optimized"] = "极致优化",
        ["all of fabric 7"] = "All of Fabric 7",
        ["all the mods 9"] = "All the Mods 9",
        ["atm9"] = "All the Mods 9",
        ["better mc"] = "更好的 Minecraft",
        ["rlcraft"] = "RLCraft",
        ["vault hunters"] = "宝库猎人",
        ["dawncraft"] = "黎明工艺",
        ["prominence"] = "Prominence",
        ["deceased craft"] = "亡者工艺",
        ["create above and beyond"] = "机械动力：超越",

        // 光影
        ["complementary reimagined"] = "互补幻想",
        ["complementary shaders"] = "互补光影",
        ["bsl shaders"] = "BSL 光影",
        ["shader bsl"] = "BSL 光影",
        ["seus shaders"] = "SEUS 光影",
        ["sildur's vibrant shaders"] = "Sildur 鲜艳光影",
        ["sildurs vibrant shaders"] = "Sildur 鲜艳光影",
        ["makeup ultra fast"] = "MakeUp 极速光影",
        ["kappa shader"] = "Kappa 光影",
        ["nostalgia shader"] = "怀旧光影",
        ["rethinking voxels"] = "Rethinking Voxels",
        ["photon shader"] = "Photon 光影",

        // 材质
        ["faithful 32x"] = "Faithful 32x",
        ["faithful"] = "Faithful",
        ["stay true"] = "Stay True",
        ["fresh animations"] = "Fresh Animations",
        ["bare bones"] = "简约风格",
        ["mizuno's 16 craft"] = "水野 16x",
        ["mizunos 16 craft"] = "水野 16x",
        ["john smith legacy"] = "John Smith 传承",
        ["soartex fanver"] = "Soartex Fanver",
        ["sphax purebdcraft"] = "Sphax 五边形",
        ["purebdcraft"] = "Sphax 五边形",
    };

    // 中文名 → 英文检索词（搜索时把中文关键词换成 Modrinth 认得的英文）
    private static readonly Dictionary<string, string> Reverse = BuildReverse();

    private static Dictionary<string, string> BuildReverse()
    {
        Dictionary<string, string> reverse = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in Names)
        {
            if (!reverse.ContainsKey(value)) reverse[value] = key;
        }

        return reverse;
    }

    /// <summary>按英文标题或 slug 查中文名。</summary>
    public static bool TryGet(string? nameOrSlug, out string chinese)
    {
        chinese = string.Empty;
        if (string.IsNullOrWhiteSpace(nameOrSlug)) return false;

        string key = Normalize(nameOrSlug);
        if (Names.TryGetValue(key, out chinese!)) return true;

        // 标题里带后缀（如 "Sodium 0.6.0"）时取最长前缀匹配
        string? best = null;
        int bestLength = 0;
        foreach ((string candidate, string value) in Names)
        {
            if (candidate.Length <= bestLength) continue;
            if (!key.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Length > candidate.Length && char.IsLetterOrDigit(key[candidate.Length])) continue;
            best = value;
            bestLength = candidate.Length;
        }

        if (best is not null)
        {
            chinese = best;
            return true;
        }

        chinese = string.Empty;
        return false;
    }

    /// <summary>没有中文名时返回原标题。</summary>
    public static string Of(string? nameOrSlug)
    {
        if (TryGet(nameOrSlug, out string chinese)) return chinese;
        return nameOrSlug?.Trim() ?? string.Empty;
    }

    /// <summary>优先用标题查中文名，其次 slug。</summary>
    public static string Display(string? title, string? slug)
    {
        if (TryGet(title, out string chinese)) return chinese;
        if (TryGet(slug, out chinese)) return chinese;
        return string.IsNullOrWhiteSpace(title) ? slug?.Trim() ?? string.Empty : title.Trim();
    }

    /// <summary>把中文搜索词换成英文；不是已知中文名时原样返回。</summary>
    public static string ToSearchKeyword(string? keyword)
    {
        string trimmed = (keyword ?? string.Empty).Trim();
        if (trimmed.Length == 0) return string.Empty;
        return Reverse.TryGetValue(trimmed, out string? english) ? english : trimmed;
    }

    /// <summary>标题 / slug / 中文名任一命中关键词。</summary>
    public static bool Matches(string? title, string? slug, string? keyword)
    {
        string query = (keyword ?? string.Empty).Trim();
        if (query.Length == 0) return true;

        if (title is not null && title.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (slug is not null && slug.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        return TryGet(title, out string chinese) && chinese.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>全部内置条目（只读，按英文键排序）。</summary>
    public static IReadOnlyDictionary<string, string> All =>
        Names.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    private static string Normalize(string value)
    {
        return value.Trim()
            .Replace('’', '\'')
            .Replace("  ", " ");
    }
}
