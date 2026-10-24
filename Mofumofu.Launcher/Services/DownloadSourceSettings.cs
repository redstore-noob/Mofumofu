using System;
using MofuCore.Download.Minecraft;

namespace Mofumofu.Launcher.Services;

/// <summary>下载源设置：主源 / 回退源 / 自定义源统一应用。</summary>
public static class DownloadSourceSettings
{
    public const string Bmcl = "bmcl";
    public const string Official = "official";
    public const string Custom = "custom";
    public const string None = "none";

    public static bool IsCustom(string? key) =>
        string.Equals(key?.Trim(), Custom, StringComparison.OrdinalIgnoreCase);

    public static DownloadSource? CreateCustom(LocalSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.CustomDownloadSourceUrl)) return null;

        string baseUrl = settings.CustomDownloadSourceUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string name = string.IsNullOrWhiteSpace(settings.CustomDownloadSourceName)
            ? "Custom"
            : settings.CustomDownloadSourceName.Trim();

        return new DownloadSource
        {
            Name = name,
            LauncherMeta = baseUrl + "/mc/game/version_manifest_v2.json",
            Meta = baseUrl,
            Libraries = baseUrl + "/maven",
            Resources = baseUrl + "/assets",
            Maven = baseUrl + "/maven",
        };
    }

    public static DownloadSource? Resolve(string? key, LocalSettings settings) =>
        key?.Trim().ToLowerInvariant() switch
        {
            Official => DownloadSources.Official,
            Bmcl => DownloadSources.Bmcl,
            Custom => CreateCustom(settings),
            _ => null,
        };

    public static void Apply(LocalSettings settings)
    {
        DownloadSource active = Resolve(settings.DownloadSource, settings) ?? DownloadSources.Bmcl;
        DownloadSource? fallback = Resolve(settings.DownloadFallbackSource, settings);

        if (fallback is not null &&
            string.Equals(fallback.Name, active.Name, StringComparison.OrdinalIgnoreCase))
        {
            fallback = null;
        }

        DownloadSourceProvider.SetActive(active);
        DownloadSourceProvider.SetFallback(fallback);
    }
}
