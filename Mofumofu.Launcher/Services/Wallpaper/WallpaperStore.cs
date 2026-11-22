using System;
using System.Collections.Generic;
using System.IO;

namespace Mofumofu.Launcher.Services.Wallpaper;

public static class WallpaperStore
{
    private static readonly YamlDotNet.Serialization.ISerializer Serializer =
        new YamlDotNet.Serialization.SerializerBuilder().WithIndentedSequences().Build();

    private static readonly YamlDotNet.Serialization.IDeserializer Deserializer =
        new YamlDotNet.Serialization.DeserializerBuilder().IgnoreUnmatchedProperties().Build();

    public static string SettingsPath => Path.Combine(
        MofuCore.Config.YamlRead.ConfigDirectory,
        "wallpaper.yaml");

    public static WallpaperSettings Load()
    {
        if (File.Exists(SettingsPath))
        {
            try
            {
                var parsed = Deserializer.Deserialize<WallpaperSettings>(File.ReadAllText(SettingsPath));
                if (parsed is not null)
                {
                    return Normalize(parsed);
                }
            }
            catch
            {
            }

            return new WallpaperSettings();
        }

        var defaults = new WallpaperSettings();
        string seeded = WallpaperDefaults.EnsureSeeded();
        if (seeded.Length > 0)
        {
            defaults.Enabled = true;
            defaults.Mode = WallpaperModes.Image;
            defaults.Images = [seeded];
            Save(defaults);
        }

        return Normalize(defaults);
    }

    public static void Save(WallpaperSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            var yaml = Serializer.Serialize(Normalize(settings));
            var temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, yaml);
            File.Move(temporary, SettingsPath, true);
        }
        catch
        {
        }
    }

    private static WallpaperSettings Normalize(WallpaperSettings settings)
    {
        settings.Mode = WallpaperModes.Normalize(settings.Mode);
        settings.Images ??= new List<string>();
        settings.IntervalSeconds = Math.Clamp(settings.IntervalSeconds, 5, 3600);
        settings.BlurPercent = Math.Clamp(settings.BlurPercent, 0, 100);
        settings.DimPercent = Math.Clamp(settings.DimPercent, 0, 100);

        settings.Images.RemoveAll(string.IsNullOrWhiteSpace);
        return settings;
    }
}
