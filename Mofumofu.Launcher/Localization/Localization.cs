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

    public static string CurrentLanguage { get; private set; } = DefaultLanguage;

    public static event Action? LanguageChanged;

    public static void Initialize()
    {
        LoadTables();
        Apply(LocalSettings.Current.Language, notify: false);
    }

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

    public static string T(string key, params object?[] args) => Format(T(key), args);

    public static string T(string key, string fallback)
    {
        var value = T(key);
        return value == key ? fallback : value;
    }

    public static string T(string key, string fallback, params object?[] args) =>
        Format(T(key, fallback), args);

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

    public static bool IsChinese => CurrentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

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
