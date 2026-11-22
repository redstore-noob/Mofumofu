using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Mofumofu.Launcher.Services.Plugins;

public sealed class PluginManifest
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string ApiVersion { get; init; } = string.Empty;
    public List<string> Permissions { get; init; } = [];

    public string Directory { get; init; } = string.Empty;

    public string? SourcePackagePath { get; set; }

    public string DllPath => Path.Combine(Directory, "extension.dll");
    public string IconPath => Path.Combine(Directory, "icon.png");
    public string DescriptionPath => Path.Combine(Directory, "description.md");

    public bool HasDll => File.Exists(DllPath);
    public bool HasIcon => File.Exists(IconPath);

    internal static bool IsValidPluginId(string id)
    {
        if (string.IsNullOrEmpty(id) || id is "." or "..") return false;
        foreach (char c in id)
        {
            bool allowed = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                           || (c >= '0' && c <= '9')
                           || c == '.' || c == '_' || c == '-';
            if (allowed == false) return false;
        }
        return true;
    }

    public string ReadDescriptionMarkdown()
    {
        try
        {
            return File.Exists(DescriptionPath) ? File.ReadAllText(DescriptionPath) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static PluginManifest? TryLoad(string directory)
    {
        foreach (string name in new[] { "plugin.yaml", "plugin.yml", "info.yaml", "info.yml" })
        {
            string path = Path.Combine(directory, name);
            if (!File.Exists(path)) continue;

            Dictionary<string, string> values = ParseYamlLines(path);
            if (values.Count == 0) continue;

            string id = values.TryGetValue("id", out string? declared) && declared.Length > 0
                ? declared
                : Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar));
            if (IsValidPluginId(id) == false) continue;
            return new PluginManifest
            {
                Id = id,
                Name = values.TryGetValue("name", out string? nameValue) && nameValue.Length > 0 ? nameValue : id,
                Version = values.TryGetValue("version", out string? version) ? version : string.Empty,
                Author = values.TryGetValue("author", out string? author) ? author : string.Empty,
                Description = values.TryGetValue("description", out string? description) ? description : string.Empty,
                ApiVersion = values.TryGetValue("apiVersion", out string? api)
                    ? api
                    : values.TryGetValue("api", out string? apiShort) ? apiShort : string.Empty,
                Permissions = [.. SplitPermissions(values.TryGetValue("permissions", out string? permissions)
                    ? permissions
                    : string.Empty)],
                Directory = directory,
            };
        }

        return null;
    }

    public static Dictionary<string, string> ParseYamlLines(string path)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string line in File.ReadLines(path))
            {
                if (line.TrimStart().StartsWith('#')) continue;
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;

                string key = line[..colon].Trim();
                string value = line[(colon + 1)..].Trim().Trim('"', '\'');
                if (key.Length > 0 && value.Length > 0) values[key] = value;
            }
        }
        catch
        {
        }

        return values;
    }

    private static IEnumerable<string> SplitPermissions(string raw)
    {
        return raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
