using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Mofumofu.Launcher.Services;

/// <summary>命令生成器的字段类型。</summary>
public enum CommandFieldKind
{
    /// <summary>自由文本。</summary>
    Text,
    /// <summary>整数文本（带范围夹取）。</summary>
    Number,
    /// <summary>可输入的下拉选择（自动补全）。</summary>
    Choice,
    /// <summary>严格下拉选择（不可输入）。</summary>
    Pick,
    /// <summary>开关。</summary>
    Toggle,
}

/// <summary>一个命令生成表单里的字段描述。</summary>
public sealed record CommandField(
    string Key,
    string Label,
    CommandFieldKind Kind = CommandFieldKind.Text,
    string[]? Options = null,
    string Default = "",
    string? Placeholder = null,
    string? Hint = null,
    int Min = 0,
    int Max = int.MaxValue,
    /// <summary>接入 <see cref="ItemCatalog"/> 的分类（item/block/effect/enchant/entity）；
    /// 设置后自动补全用全量注册表 + 中文名搜索，Options 仅作目录为空时的兜底。</summary>
    string? Catalog = null);

/// <summary>一条命令的完整规格：表单字段 + 由字段值拼出命令行的构建函数。</summary>
public sealed record CommandSpec(
    string Id,
    string Command,
    string Title,
    string Usage,
    string Category,
    bool NeedsTarget,
    string DefaultTarget,
    CommandField[] Fields,
    Func<IReadOnlyDictionary<string, string>, IEnumerable<string>> Build);

/// <summary>
/// 命令生成器的数据目录：17 条常用命令的规格与构建逻辑，加上语法高亮用的分词器。
/// 全部为纯字符串逻辑，不依赖 UI；页面层把控好的字段值塞进字典，这里拼出最终命令。
/// </summary>
public static class CommandSpecs
{
    // ---------------- 常用选项表 ----------------

    public static readonly string[] Items =
    {
        "minecraft:diamond_sword", "minecraft:netherite_sword", "minecraft:diamond_pickaxe",
        "minecraft:netherite_pickaxe", "minecraft:golden_apple", "minecraft:enchanted_golden_apple",
        "minecraft:elytra", "minecraft:totem_of_undying", "minecraft:diamond", "minecraft:emerald",
        "minecraft:netherite_ingot", "minecraft:oak_log", "minecraft:bread", "minecraft:cooked_beef",
        "minecraft:torch", "minecraft:ender_pearl", "minecraft:shulker_box", "minecraft:trident",
        "minecraft:mace", "minecraft:wind_charge", "minecraft:experience_bottle",
    };

    public static readonly string[] Blocks =
    {
        "minecraft:stone", "minecraft:dirt", "minecraft:grass_block", "minecraft:oak_log",
        "minecraft:oak_planks", "minecraft:cobblestone", "minecraft:deepslate", "minecraft:obsidian",
        "minecraft:bedrock", "minecraft:glass", "minecraft:glowstone", "minecraft:sea_lantern",
        "minecraft:chest", "minecraft:furnace", "minecraft:tnt", "minecraft:command_block",
        "minecraft:repeating_command_block", "minecraft:air", "minecraft:water", "minecraft:lava",
    };

    public static readonly string[] Effects =
    {
        "speed", "slowness", "haste", "mining_fatigue", "strength", "instant_health",
        "instant_damage", "jump_boost", "nausea", "regeneration", "resistance",
        "fire_resistance", "water_breathing", "invisibility", "blindness", "night_vision",
        "hunger", "weakness", "poison", "wither", "health_boost", "absorption", "saturation",
        "glowing", "levitation", "luck", "unluck", "slow_falling", "conduit_power",
        "dolphins_grace", "bad_omen", "hero_of_the_village", "darkness",
    };

    public static readonly string[] Enchantments =
    {
        "sharpness", "smite", "bane_of_arthropods", "knockback", "fire_aspect", "looting",
        "sweeping_edge", "unbreaking", "mending", "efficiency", "fortune", "silk_touch",
        "protection", "fire_protection", "blast_protection", "projectile_protection",
        "thorns", "respiration", "aqua_affinity", "depth_strider", "frost_walker",
        "soul_speed", "swift_sneak", "power", "punch", "flame", "infinity", "loyalty",
        "channeling", "riptide", "impaling", "multishot", "piercing", "quick_charge",
        "density", "breach", "wind_burst",
    };

    public static readonly string[] Entities =
    {
        "minecraft:pig", "minecraft:cow", "minecraft:sheep", "minecraft:chicken",
        "minecraft:zombie", "minecraft:skeleton", "minecraft:creeper", "minecraft:spider",
        "minecraft:enderman", "minecraft:villager", "minecraft:iron_golem", "minecraft:wolf",
        "minecraft:cat", "minecraft:horse", "minecraft:armor_stand", "minecraft:item_display",
        "minecraft:block_display", "minecraft:text_display", "minecraft:lightning_bolt",
        "minecraft:firework_rocket", "minecraft:arrow", "minecraft:experience_orb",
    };

    public static readonly string[] Particles =
    {
        "heart", "flame", "soul_fire_flame", "end_rod", "crit", "enchanted_hit",
        "happy_villager", "angry_villager", "witch", "bubble", "cloud", "dragon_breath",
        "dust", "effect", "electric_spark", "explosion", "firework", "glow", "gust",
        "lava", "note", "portal", "rain", "smoke", "snowflake", "sonic_boom", "wax_on",
    };

    public static readonly string[] GameRules =
    {
        "keepInventory", "doDaylightCycle", "doMobSpawning", "mobGriefing", "doFireTick",
        "naturalRegeneration", "showDeathMessages", "doInsomnia", "announceAdvancements",
        "doWeatherCycle", "doTraderSpawning", "fallDamage", "fireDamage", "drowningDamage",
        "doImmediateRespawn", "doLimitedCrafting", "randomTickSpeed", "spawnRadius",
        "maxEntityCramming", "commandBlockOutput", "logAdminCommands", "sendCommandFeedback",
        "disableElytraMovementCheck", "disableRaids", "doPatrolSpawning", "forgiveDeadPlayers",
        "universalAnger", "playersSleepingPercentage", "spectatorsGenerateChunks",
    };

    /// <summary>规则名 → 值类型提示（true/false 或整数）。</summary>
    public static string GameruleValueHint(string rule) => rule switch
    {
        "randomTickSpeed" or "spawnRadius" or "maxEntityCramming" or "playersSleepingPercentage"
            => "int",
        _ => "bool",
    };

    // ---------------- 目录本体 ----------------

    /// <summary>分类顺序：玩家 / 世界 / 方块 / 实体 / 消息。</summary>
    public static readonly string[] Categories = { "player", "world", "block", "entity", "message" };

    public static readonly IReadOnlyList<CommandSpec> All = BuildCatalog();

    private static List<CommandSpec> BuildCatalog() => new()
    {
        // ---------- 玩家 ----------

        new("give", "give", "给物品",
            "give <目标> <物品> [数量] [组件]",
            "player", true, "@p",
            new[]
            {
                new CommandField("item", "物品 ID", CommandFieldKind.Choice, Items, "minecraft:diamond_sword",
                    Placeholder: "diamond_sword / 钻石剑", Catalog: "item",
                    Hint: "支持中文或英文搜索全部物品与方块"),
                new CommandField("count", "数量", CommandFieldKind.Number, Default: "1", Min: 1, Max: 6456,
                    Hint: "新版允许超过一组"),
            },
            v =>
            {
                var components = Raw(v, "components");
                var count = IntOf(v, "count", 1, 1, 6456);
                var tail = components.Length > 0
                    ? $" {count} {components}"
                    : count != 1 ? $" {count}" : string.Empty;
                return One($"/give {Target(v)} {IdOf(v, "item", "minecraft:diamond_sword")}{tail}");
            }),

        new("effect", "effect", "状态效果",
            "effect give <目标> <效果> [秒] [等级] [隐藏粒子]",
            "player", true, "@a",
            new[]
            {
                new CommandField("mode", "子命令", CommandFieldKind.Pick, new[] { "give", "clear" }),
                new CommandField("effect", "效果 ID", CommandFieldKind.Choice, Effects, "speed",
                    Placeholder: "speed / 迅捷", Catalog: "effect"),
                new CommandField("seconds", "持续秒数", CommandFieldKind.Number, Default: "30", Min: 0, Max: 1000000,
                    Hint: "0 = infinite"),
                new CommandField("amplifier", "等级（0 起）", CommandFieldKind.Number, Default: "0", Min: 0, Max: 255),
                new CommandField("hideParticles", "隐藏粒子", CommandFieldKind.Toggle),
            },
            v =>
            {
                var target = Target(v);
                if (Raw(v, "mode") == "clear")
                {
                    var effectId = Raw(v, "effect");
                    return One(effectId.Length > 0
                        ? $"/effect clear {target} {IdText(v, effectId)}"
                        : $"/effect clear {target}");
                }

                var seconds = Raw(v, "seconds") == "0" ? "infinite" : IntOf(v, "seconds", 30, 0, 1000000).ToString();
                var amplifier = IntOf(v, "amplifier", 0, 0, 255);
                var hide = Raw(v, "hideParticles") == bool.TrueString;
                return One($"/effect give {target} {IdOf(v, "effect", "speed")} {seconds} {amplifier} {hide.ToString().ToLowerInvariant()}");
            }),

        new("enchant", "enchant", "附魔",
            "enchant <目标> <附魔> [等级]",
            "player", true, "@p",
            new[]
            {
                new CommandField("enchant", "附魔 ID", CommandFieldKind.Choice, Enchantments, "sharpness",
                    Placeholder: "sharpness / 锋利", Catalog: "enchant"),
                new CommandField("level", "等级", CommandFieldKind.Number, Default: "1", Min: 1, Max: 10),
            },
            v => One($"/enchant {Target(v)} {IdOf(v, "enchant", "sharpness")} {IntOf(v, "level", 1, 1, 10)}")),

        new("xp", "xp", "经验",
            "xp add <目标> <数量> [levels|points]",
            "player", true, "@p",
            new[]
            {
                new CommandField("mode", "子命令", CommandFieldKind.Pick, new[] { "add", "set" }),
                new CommandField("amount", "数量", CommandFieldKind.Number, Default: "10", Min: -1000000, Max: 1000000),
                new CommandField("unit", "单位", CommandFieldKind.Pick, new[] { "levels", "points" }),
            },
            v => One($"/xp {Raw(v, "mode", "add")} {Target(v)} {IntOf(v, "amount", 10, -1000000, 1000000)} {Raw(v, "unit", "levels")}")),

        new("gamemode", "gamemode", "游戏模式",
            "gamemode <模式> [目标]",
            "player", true, "@s",
            new[]
            {
                new CommandField("mode", "模式", CommandFieldKind.Pick,
                    new[] { "survival", "creative", "adventure", "spectator" }),
            },
            v => One($"/gamemode {Raw(v, "mode", "survival")} {Target(v)}")),

        new("clear", "clear", "清空背包",
            "clear [目标] [物品]",
            "player", true, "@a",
            new[]
            {
                new CommandField("item", "只清这种物品（可选）", CommandFieldKind.Choice, Items, "",
                    Placeholder: "留空 = 全部", Catalog: "item"),
            },
            v =>
            {
                var item = Raw(v, "item");
                return One(item.Trim().Length > 0
                    ? $"/clear {Target(v)} {IdText(v, item.Trim())}"
                    : $"/clear {Target(v)}");
            }),

        new("tp", "tp", "传送",
            "tp <目标> <坐标> [朝向]",
            "player", true, "@s",
            new[]
            {
                new CommandField("coords", "目的地坐标", Default: "~ ~ ~",
                    Placeholder: "~ ~ ~（支持 ^ 局部坐标）"),
                new CommandField("facing", "朝向", CommandFieldKind.Pick,
                    new[] { "none", "entity", "rotation" }),
                new CommandField("facingTarget", "朝向的目标", Default: "@p", Placeholder: "@p / 玩家名"),
                new CommandField("yaw", "偏航角", CommandFieldKind.Number, Default: "0", Min: -180, Max: 180),
                new CommandField("pitch", "俯仰角", CommandFieldKind.Number, Default: "0", Min: -90, Max: 90),
            },
            v =>
            {
                var facing = Raw(v, "facing", "none");
                var tail = facing switch
                {
                    "entity" => $" facing entity {Raw(v, "facingTarget", "@p")}",
                    "rotation" => $" {IntOf(v, "yaw", 0, -180, 180)} {IntOf(v, "pitch", 0, -90, 90)}",
                    _ => string.Empty,
                };
                return One($"/tp {Target(v)} {Raw(v, "coords", "~ ~ ~")}{tail}");
            }),

        new("kill", "kill", "击杀实体",
            "kill [目标]",
            "player", true, "@e",
            Array.Empty<CommandField>(),
            v => One($"/kill {Target(v)}")),

        // ---------- 世界 ----------

        new("time", "time", "时间",
            "time set <值> / time add <刻>",
            "world", false, "",
            new[]
            {
                new CommandField("mode", "子命令", CommandFieldKind.Pick, new[] { "set", "add" }),
                new CommandField("preset", "预设时刻", CommandFieldKind.Pick,
                    new[] { "day", "noon", "night", "midnight", "sunrise", "sunset" }),
                new CommandField("ticks", "游戏刻（preset = day 等可留空）", CommandFieldKind.Number,
                    Default: "1000", Min: 0, Max: 24000),
            },
            v =>
            {
                if (Raw(v, "mode") == "add")
                    return One($"/time add {IntOf(v, "ticks", 1000, 0, 24000)}");

                var preset = Raw(v, "preset", "day");
                var ticks = Raw(v, "ticks");
                var value = preset == "day" && int.TryParse(ticks, out var parsed) && parsed > 0
                    ? parsed.ToString()
                    : preset;
                return One($"/time set {value}");
            }),

        new("weather", "weather", "天气",
            "weather <类型> [持续刻]",
            "world", false, "",
            new[]
            {
                new CommandField("type", "天气", CommandFieldKind.Pick, new[] { "clear", "rain", "thunder" }),
                new CommandField("duration", "持续刻（可选）", CommandFieldKind.Number, Default: "",
                    Min: 1, Max: 1000000, Hint: "留空 = 由系统随机"),
            },
            v =>
            {
                var duration = Raw(v, "duration");
                return One(int.TryParse(duration, out var parsed) && parsed > 0
                    ? $"/weather {Raw(v, "type", "clear")} {parsed}"
                    : $"/weather {Raw(v, "type", "clear")}");
            }),

        new("gamerule", "gamerule", "游戏规则",
            "gamerule <规则> [值]",
            "world", false, "",
            new[]
            {
                new CommandField("rule", "规则名", CommandFieldKind.Choice, GameRules, "keepInventory"),
                new CommandField("value", "值", Default: "true",
                    Placeholder: "true / false 或整数"),
            },
            v =>
            {
                var rule = Raw(v, "rule", "keepInventory");
                var value = Raw(v, "value").Trim();
                return One(value.Length > 0 ? $"/gamerule {rule} {value}" : $"/gamerule {rule}");
            }),

        new("difficulty", "difficulty", "难度",
            "difficulty <难度>",
            "world", false, "",
            new[]
            {
                new CommandField("mode", "难度", CommandFieldKind.Pick,
                    new[] { "peaceful", "easy", "normal", "hard" }),
            },
            v => One($"/difficulty {Raw(v, "mode", "normal")}")),

        // ---------- 方块 ----------

        new("setblock", "setblock", "放置方块",
            "setblock <坐标> <方块> [模式]",
            "block", false, "",
            new[]
            {
                new CommandField("coords", "坐标", Default: "~ ~ ~", Placeholder: "~ ~ ~"),
                new CommandField("block", "方块 ID", CommandFieldKind.Choice, Blocks, "minecraft:stone",
                    Catalog: "block"),
                new CommandField("mode", "替换模式", CommandFieldKind.Pick,
                    new[] { "replace", "destroy", "keep" }),
            },
            v => One($"/setblock {Raw(v, "coords", "~ ~ ~")} {IdOf(v, "block", "minecraft:stone")} {Raw(v, "mode", "replace")}")),

        new("fill", "fill", "填充区域",
            "fill <起点> <终点> <方块> [模式]",
            "block", false, "",
            new[]
            {
                new CommandField("from", "起点坐标", Default: "~ ~ ~", Placeholder: "~ ~ ~"),
                new CommandField("to", "终点坐标", Default: "~10 ~ ~10", Placeholder: "~10 ~ ~10"),
                new CommandField("block", "方块 ID", CommandFieldKind.Choice, Blocks, "minecraft:stone",
                    Catalog: "block"),
                new CommandField("mode", "填充模式", CommandFieldKind.Pick,
                    new[] { "replace", "destroy", "hollow", "keep", "outline" }),
            },
            v => One($"/fill {Raw(v, "from", "~ ~ ~")} {Raw(v, "to", "~10 ~ ~10")} {IdOf(v, "block", "minecraft:stone")} {Raw(v, "mode", "replace")}")),

        // ---------- 实体 ----------

        new("summon", "summon", "召唤实体",
            "summon <实体> [坐标] [NBT]",
            "entity", false, "",
            new[]
            {
                new CommandField("entity", "实体 ID", CommandFieldKind.Choice, Entities, "minecraft:pig",
                    Catalog: "entity"),
                new CommandField("coords", "坐标", Default: "~ ~ ~", Placeholder: "~ ~ ~"),
            },
            v =>
            {
                var nbt = Raw(v, "nbt");
                return One($"/summon {IdOf(v, "entity", "minecraft:pig")} {Raw(v, "coords", "~ ~ ~")}{(nbt.Length > 0 ? $" {nbt}" : string.Empty)}");
            }),

        new("particle", "particle", "粒子效果",
            "particle <粒子> <坐标> [数量] [速度]",
            "entity", false, "",
            new[]
            {
                new CommandField("particle", "粒子 ID", CommandFieldKind.Choice, Particles, "heart"),
                new CommandField("coords", "坐标", Default: "~ ~1 ~", Placeholder: "~ ~1 ~"),
                new CommandField("count", "数量（可选）", CommandFieldKind.Number, Default: "20", Min: 0, Max: 10000),
                new CommandField("speed", "速度", CommandFieldKind.Text, Default: "0"),
            },
            v =>
            {
                var count = IntOf(v, "count", 20, 0, 10000);
                var speed = Raw(v, "speed", "0");
                return One(count > 0
                    ? $"/particle {IdOf(v, "particle", "heart")} {Raw(v, "coords", "~ ~1 ~")} 0 0 0 {speed} {count}"
                    : $"/particle {IdOf(v, "particle", "heart")} {Raw(v, "coords", "~ ~1 ~")}");
            }),

        // ---------- 消息 ----------

        new("title", "title", "全屏标题",
            "title <目标> title <文本>",
            "message", true, "@a",
            new[]
            {
                new CommandField("slot", "显示位置", CommandFieldKind.Pick,
                    new[] { "title", "subtitle", "actionbar" }),
                new CommandField("text", "文本内容", Default: "Mofumofu", Placeholder: "支持中文"),
                new CommandField("useTimes", "设置停留时间", CommandFieldKind.Toggle, Default: bool.FalseString),
                new CommandField("fadeIn", "淡入（刻）", CommandFieldKind.Number, Default: "10", Min: 0, Max: 1000),
                new CommandField("stay", "停留（刻）", CommandFieldKind.Number, Default: "70", Min: 0, Max: 10000),
                new CommandField("fadeOut", "淡出（刻）", CommandFieldKind.Number, Default: "20", Min: 0, Max: 1000),
            },
            v =>
            {
                var lines = new List<string>();
                if (Raw(v, "useTimes") == bool.TrueString)
                {
                    lines.Add($"/title {Target(v)} times {IntOf(v, "fadeIn", 10, 0, 1000)} {IntOf(v, "stay", 70, 0, 10000)} {IntOf(v, "fadeOut", 20, 0, 1000)}");
                }

                lines.Add($"/title {Target(v)} {Raw(v, "slot", "title")} {JsonText(Raw(v, "text", "Mofumofu"))}");
                return lines;
            }),
    };

    // ---------------- 组件 / NBT 拼接 ----------------

    /// <summary>拼 1.20.5+ 物品组件参数（give 的第 5 段）。内容为空时返回空串。</summary>
    public static string BuildComponents(
        string? customName,
        IReadOnlyList<(string Enchant, int Level)> enchantments,
        bool unbreakable,
        IReadOnlyList<string> loreLines,
        string customModelData,
        bool includeNamespace = true)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(customName))
            parts.Add($"minecraft:custom_name={SnbtString(customName.Trim())}");

        if (enchantments.Count > 0)
        {
            string Key(string id)
            {
                var raw = id.Trim();
                return includeNamespace && !raw.Contains(':') ? "minecraft:" + raw : raw;
            }

            var levels = string.Join(",", enchantments
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Enchant))
                .Select(pair => $"\"{Key(pair.Enchant)}\":{pair.Level}"));
            if (levels.Length > 0)
                parts.Add($"minecraft:enchantments={{levels={{{levels}}}}}");
        }

        if (unbreakable)
            parts.Add("minecraft:unbreakable={}");

        if (loreLines.Count > 0)
            parts.Add($"minecraft:lore=[{string.Join(",", loreLines.Select(SnbtString))}]");

        if (int.TryParse(customModelData?.Trim(), out var modelData) && modelData != 0)
            parts.Add($"minecraft:custom_model_data={modelData}");

        return parts.Count > 0 ? $"[{string.Join(",", parts)}]" : string.Empty;
    }

    /// <summary>拼 summon 的 NBT（1.21.5 前字段名）。内容为空时返回空串。</summary>
    public static string BuildSummonNbt(
        string? customName,
        bool noAi,
        bool invulnerable,
        bool glowing,
        bool silent,
        bool noGravity,
        string rawSnbt)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(customName))
            parts.Add($"CustomName:'{JsonText(customName.Trim())}'");
        if (noAi) parts.Add("NoAI:1b");
        if (invulnerable) parts.Add("Invulnerable:1b");
        if (glowing) parts.Add("Glowing:1b");
        if (silent) parts.Add("Silent:1b");
        if (noGravity) parts.Add("NoGravity:1b");

        var extra = rawSnbt?.Trim();
        if (!string.IsNullOrWhiteSpace(extra))
            parts.Add(extra);

        return parts.Count > 0 ? $"{{{string.Join(",", parts)}}}" : string.Empty;
    }

    /// <summary>SNBT 字符串字面量：<c>"文字"</c>（把反斜杠与引号转义掉）。</summary>
    public static string SnbtString(string text) =>
        $"\"{(text ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    /// <summary>JSON 文本组件：<c>{"text":"文字"}</c>。</summary>
    public static string JsonText(string text) =>
        "{\"text\":" + JsonSerializerEscape(text ?? string.Empty) + "}";

    private static string JsonSerializerEscape(string text)
    {
        var builder = new StringBuilder("\"");
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default: builder.Append(ch); break;
            }
        }

        return builder.Append('"').ToString();
    }

    // ---------------- 目标选择器 ----------------

    /// <summary>由基础选择器（@a/@p/@s/@e/@r 或玩家名）+ 筛选条件拼出目标选择器。</summary>
    public static string BuildSelector(
        string baseSelector,
        string? limit, string? sort, string? distance,
        string? tag, string? gamemode, string? levelMin, string? levelMax,
        string? x, string? y, string? z,
        string? dx, string? dy, string? dz)
    {
        if (!baseSelector.StartsWith('@')) return baseSelector;

        var conditions = new List<string>();
        void Add(string? value, string prefix)
        {
            var trimmed = value?.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
                conditions.Add(prefix + trimmed);
        }

        Add(limit, "limit=");
        Add(sort, "sort=");
        Add(distance, "distance=");
        Add(tag, "tag=");
        Add(gamemode, "gamemode=");
        Add(x, "x=");
        Add(y, "y=");
        Add(z, "z=");
        Add(dx, "dx=");
        Add(dy, "dy=");
        Add(dz, "dz=");

        // level=..10 / level=5.. / level=3..10 / level=5（两端相同只写一个数）
        var min = levelMin?.Trim();
        var max = levelMax?.Trim();
        var hasMin = !string.IsNullOrEmpty(min);
        var hasMax = !string.IsNullOrEmpty(max);
        if (hasMin && hasMax) conditions.Add(min == max ? $"level={min}" : $"level={min}..{max}");
        else if (hasMin) conditions.Add($"level={min}..");
        else if (hasMax) conditions.Add($"level=..{max}");

        return conditions.Count == 0
            ? baseSelector
            : $"{baseSelector}[{string.Join(",", conditions)}]";
    }

    // ---------------- 取值小工具 ----------------

    private static IEnumerable<string> One(string line) => new[] { line };

    public static string Raw(IReadOnlyDictionary<string, string> values, string key, string fallback = "")
    {
        if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            return value.Trim();

        return fallback;
    }

    public static int IntOf(IReadOnlyDictionary<string, string> values, string key, int fallback, int min, int max)
    {
        if (!int.TryParse(Raw(values, key), out var parsed)) parsed = fallback;
        return Math.Clamp(parsed, min, max);
    }

    /// <summary>目标选择器值：表单没给时用规格里的默认目标。</summary>
    public static string Target(IReadOnlyDictionary<string, string> values) =>
        Raw(values, "target").Length > 0 ? Raw(values, "target") : "@s";

    /// <summary>
    /// 取物品/实体等 ID 字段的值并归一：补 minecraft: 前缀；
    /// 输出卡片关掉命名空间时再去掉（值来自 "__ns" 键，由页面写入）。
    /// </summary>
    public static string IdOf(IReadOnlyDictionary<string, string> values, string key, string fallback)
    {
        var raw = Raw(values, key, fallback);
        return ApplyNamespace(values, raw);
    }

    /// <summary>对已经拼好的 ID 文本做同样的命名空间处理。</summary>
    public static string IdText(IReadOnlyDictionary<string, string> values, string raw) =>
        ApplyNamespace(values, raw);

    private static string ApplyNamespace(IReadOnlyDictionary<string, string> values, string raw)
    {
        var id = raw.Contains(':') ? raw : "minecraft:" + raw;
        var includeNamespace = values.TryGetValue("__ns", out var flag) && flag == "1";
        if (!includeNamespace && id.StartsWith("minecraft:", StringComparison.Ordinal))
            id = id["minecraft:".Length..];
        return id;
    }

    // ---------------- 语法高亮分词 ----------------

    public enum TokenType { Head, Selector, Id, Number, Nbt, Text, Plain }

    public readonly record struct Token(string Text, TokenType Type);

    private static readonly Regex IdPattern =
        new("^[a-z0-9_.+-]+:[a-z0-9_./-]+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NumberPattern =
        new("^[~^]?-?\\d+(\\.\\d+)?$", RegexOptions.Compiled);

    /// <summary>
    /// 把一行命令拆成用于着色的词元：按顶层空格切分，
    /// 引号与 [] {} 括号内的空格不会切断。
    /// </summary>
    public static IReadOnlyList<Token> Tokenize(string line)
    {
        var tokens = new List<Token>();
        var builder = new StringBuilder();
        var depth = 0;
        var quote = '\0';

        void Flush()
        {
            if (builder.Length == 0) return;
            tokens.Add(Classify(builder.ToString()));
            builder.Clear();
        }

        foreach (var ch in line)
        {
            if (quote != '\0')
            {
                builder.Append(ch);
                if (ch == quote) quote = '\0';
                continue;
            }

            switch (ch)
            {
                case '"' or '\'':
                    quote = ch;
                    builder.Append(ch);
                    break;
                case '[' or '{':
                    depth++;
                    builder.Append(ch);
                    break;
                case ']' or '}':
                    depth = Math.Max(0, depth - 1);
                    builder.Append(ch);
                    break;
                case ' ' when depth == 0:
                    Flush();
                    break;
                default:
                    builder.Append(ch);
                    break;
            }
        }

        Flush();
        return tokens;
    }

    private static Token Classify(string text)
    {
        if (text.StartsWith('/')) return new Token(text, TokenType.Head);
        if (text.StartsWith('@')) return new Token(text, TokenType.Selector);
        if ((text.StartsWith('[') || text.StartsWith('{')) && depth0(text)) return new Token(text, TokenType.Nbt);
        if (text.Length > 1 && (text.StartsWith('"') || text.StartsWith('\''))) return new Token(text, TokenType.Text);
        if (NumberPattern.IsMatch(text)) return new Token(text, TokenType.Number);
        if (IdPattern.IsMatch(text)) return new Token(text, TokenType.Id);
        return new Token(text, TokenType.Plain);
    }

    private static bool depth0(string text)
    {
        var depth = 0;
        var quote = '\0';
        foreach (var ch in text)
        {
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
                continue;
            }

            switch (ch)
            {
                case '"' or '\'':
                    quote = ch;
                    break;
                case '[' or '{':
                    depth++;
                    break;
                case ']' or '}':
                    depth--;
                    break;
            }
        }

        return depth == 0;
    }
}
