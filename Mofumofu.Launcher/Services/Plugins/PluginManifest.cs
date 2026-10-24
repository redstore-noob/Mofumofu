using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Mofumofu.Launcher.Services.Plugins;

/// <summary>
/// 插件清单：统一读取开发态散目录的 plugin.yaml 与 .nekoex 解包后的 info.yaml。
/// 字段：id / name / version / author / description / apiVersion / permissions（逗号分隔）。
/// </summary>
public sealed class PluginManifest
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string ApiVersion { get; init; } = string.Empty;
    public List<string> Permissions { get; init; } = [];

    /// <summary>插件所在目录（散目录或 .nekoex 解包目录）。</summary>
    public string Directory { get; init; } = string.Empty;

    /// <summary>来源 .nekoex 文件路径（散目录插件为 null；卸载时一并删除）。</summary>
    public string? SourcePackagePath { get; set; }

    public string DllPath => Path.Combine(Directory, "extension.dll");
    public string IconPath => Path.Combine(Directory, "icon.png");
    public string DescriptionPath => Path.Combine(Directory, "description.md");

    public bool HasDll => File.Exists(DllPath);
    public bool HasIcon => File.Exists(IconPath);

    /// <summary>description.md 全文；不存在时为空串。</summary>
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

    /// <summary>
    /// 从插件目录读取清单：依次尝试 plugin.yaml / plugin.yml / info.yaml / info.yml。
    /// 目录里一个清单都没有时返回 null。
    /// </summary>
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

    /// <summary>极简 YAML 行解析：key: value，支持 # 注释与成对引号（与清单文件的平面结构匹配）。</summary>
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
            // 清单不可读按无清单处理
        }

        return values;
    }

    private static IEnumerable<string> SplitPermissions(string raw)
    {
        return raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
