using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Platform;
using Mofumofu.Launcher.Services;

namespace Mofumofu.Launcher.Localization;

/// <summary>
/// 界面本地化：按语言加载 <c>avares://Mofumofu.Launcher/Localization/&lt;lang&gt;/*.json</c>
/// 里的扁平键表（键形如 <c>Settings.Game.Title</c>），并把每个键写进
/// <see cref="Application.Resources"/> 的 <c>L.&lt;key&gt;</c>：
/// XAML 用 <c>{DynamicResource L.Settings.Game.Title}</c> 实时跟随，
/// code-behind 用 <see cref="T(string)"/> 取（切语言时主窗口会重建页面刷新）。
/// 缺键时回退顺序：当前语言 → 简体中文 → 键名本身。
/// </summary>
public static class Localization
{
    private const string AssetRoot = "avares://Mofumofu.Launcher/Localization/";
    private const string ResourcePrefix = "L.";
    public const string DefaultLanguage = "zh-CN";
    public const string FallbackLanguage = "zh-CN";
    public const string SystemLanguage = "system";

    private static readonly Dictionary<string, Dictionary<string, string>> Tables =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly List<string> AppliedKeys = [];
    private static readonly object Gate = new();

    /// <summary>语言选项：代码 / 显示名（显示名自身不翻译）/ 旗帜。</summary>
    public sealed record LanguageOption(string Code, string Name, string FlagImage);

    public static IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new(DefaultLanguage, "简体中文", "avares://Mofumofu.Launcher/Assets/Flags/zh-CN.png"),
        new("zh-TW", "繁體中文", "avares://Mofumofu.Launcher/Assets/Flags/zh-TW.png"),
        new("de-DE", "Deutsch", "avares://Mofumofu.Launcher/Assets/Flags/de-DE.png"),
        new("ja-JP", "日本語", "avares://Mofumofu.Launcher/Assets/Flags/ja-JP.png"),
        new("ru-RU", "Русский", "avares://Mofumofu.Launcher/Assets/Flags/ru-RU.png"),
        new("eo", "Esperanto", "avares://Mofumofu.Launcher/Assets/Flags/eo.png"),
        new("en-US", "English", "avares://Mofumofu.Launcher/Assets/Flags/en-US.png"),
        new(SystemLanguage, "跟随系统", "avares://Mofumofu.Launcher/Assets/Flags/system.png"),
    ];

    /// <summary>最近一次生效语言（已解析，不会是 <c>system</c>）。</summary>
    public static string CurrentLanguage { get; private set; } = DefaultLanguage;

    /// <summary>语言切换完成后触发（主窗口用它重建页面）。</summary>
    public static event Action? LanguageChanged;

    /// <summary>载入全部语言表并应用当前设置里的语言；启动时调用一次。</summary>
    public static void Initialize()
    {
        LoadTables();
        Apply(LocalSettings.Current.Language, notify: false);
    }

    /// <summary>切换语言并存进本地设置；<c>system</c> 表示跟随系统。</summary>
    public static void SetLanguage(string? code)
    {
        Apply(code, notify: true);
        LocalSettings.Current.Language = string.IsNullOrWhiteSpace(code) ? DefaultLanguage : code;
        LocalSettings.Save();
    }

    public static void CycleToNext()
    {
        var codes = Languages.Select(option => option.Code).ToList();
        var index = codes.IndexOf(LocalSettings.Current.Language);
        SetLanguage(codes[(index + 1 + codes.Count) % codes.Count]);
    }

    /// <summary>取翻译；缺键返回键名（便于一眼看出漏翻）。</summary>
    public static string T(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;

        lock (Gate)
        {
            if (Tables.TryGetValue(CurrentLanguage, out var table) && table.TryGetValue(key, out var value))
                return value;
            if (Tables.TryGetValue(FallbackLanguage, out var fallback) && fallback.TryGetValue(key, out var zh))
                return zh;
        }

        return key;
    }

    /// <summary>取翻译并格式化（<c>{0}</c> 占位）。</summary>
    public static string T(string key, params object?[] args) => Format(T(key), args);

    /// <summary>带兜底文案的取翻译：键不存在时用调用方给的原文字。</summary>
    public static string T(string key, string fallback)
    {
        var value = T(key);
        return value == key ? fallback : value;
    }

    public static string T(string key, string fallback, params object?[] args) =>
        Format(T(key, fallback), args);

    /// <summary>该键是否在任一语言表里存在。</summary>
    public static bool Has(string key)
    {
        lock (Gate)
        {
            foreach (var table in Tables.Values)
            {
                if (table.ContainsKey(key)) return true;
            }
        }

        return false;
    }

    /// <summary>当前语言是否中文（个别地方需要按语言微调排版时用）。</summary>
    public static bool IsChinese => CurrentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>把一个键解析后的文本写进任意对象属性（代码里动态建的控件用）。</summary>
    public static string ResourceKey(string key) => ResourcePrefix + key;

    private static string Format(string template, object?[] args)
    {
        if (args.Length == 0 || string.IsNullOrEmpty(template)) return template;

        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    private static void Apply(string? code, bool notify)
    {
        var resolved = Resolve(code);
        CurrentLanguage = resolved;

        if (Application.Current is { } app)
        {
            lock (Gate)
            {
                foreach (var key in AppliedKeys)
                {
                    app.Resources.Remove(ResourcePrefix + key);
                }

                AppliedKeys.Clear();

                foreach (var language in new[] { resolved, FallbackLanguage })
                {
                    if (!Tables.TryGetValue(language, out var table)) continue;

                    foreach (var (key, value) in table)
                    {
                        var resourceKey = ResourcePrefix + key;
                        // 主语言先写；回退语言只补缺，不覆盖主语言
                        if (app.Resources.ContainsKey(resourceKey) && language != resolved) continue;
                        app.Resources[resourceKey] = value;
                        if (language == resolved) AppliedKeys.Add(key);
                    }

                    if (language != resolved) continue;
                    foreach (var (key, _) in table)
                    {
                        if (!AppliedKeys.Contains(key)) AppliedKeys.Add(key);
                    }
                }
            }
        }

        if (notify)
        {
            LanguageChanged?.Invoke();
        }
    }

    private static string Resolve(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return DefaultLanguage;
        if (code.Equals(SystemLanguage, StringComparison.OrdinalIgnoreCase)) return DetectSystemLanguage();

        if (Tables.ContainsKey(code)) return code;

        // 容错：zh / zh-Hans / en 这类简写
        var twoLetter = code.Length >= 2 ? code[..2] : code;
        foreach (var known in Tables.Keys)
        {
            if (known.StartsWith(twoLetter, StringComparison.OrdinalIgnoreCase)) return known;
        }

        return DefaultLanguage;
    }

    private static string DetectSystemLanguage()
    {
        try
        {
            var culture = CultureInfo.CurrentUICulture;
            var twoLetter = culture.TwoLetterISOLanguageName;
            foreach (var known in Tables.Keys)
            {
                if (known.StartsWith(twoLetter, StringComparison.OrdinalIgnoreCase)) return known;
            }
        }
        catch
        {
            // 取系统语言失败时用默认
        }

        return DefaultLanguage;
    }

    private static void LoadTables()
    {
        lock (Gate)
        {
            Tables.Clear();

            foreach (var option in Languages)
            {
                if (option.Code == SystemLanguage) continue;
                Tables[option.Code] = LoadLanguage(option.Code);
            }

            if (!Tables.ContainsKey(DefaultLanguage))
            {
                Tables[DefaultLanguage] = new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }
    }

    private static Dictionary<string, string> LoadLanguage(string code)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var folder = new Uri($"{AssetRoot}{code}/");
            foreach (var asset in AssetLoader.GetAssets(folder, null))
            {
                if (!asset.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    using var stream = AssetLoader.Open(asset);
                    using var reader = new StreamReader(stream);
                    var json = reader.ReadToEnd();
                    var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (entries is null) continue;

                    foreach (var (key, value) in entries)
                    {
                        if (string.IsNullOrWhiteSpace(key)) continue;
                        result[key] = value;
                    }
                }
                catch
                {
                    // 单个文件坏掉不影响其它语言包
                }
            }
        }
        catch
        {
            // 语言目录缺失时返回空表（全部回退到键名）
        }

        return result;
    }
}
