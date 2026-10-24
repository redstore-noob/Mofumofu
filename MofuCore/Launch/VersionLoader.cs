using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MofuCore.Launch;

/// <summary>
/// 加载Minecraft实例的版本信息等。
/// 沿 inheritsFrom 向上追溯（深度上限防环），把参数/库/资源等声明合并为最终启动档案。
/// </summary>
public class VersionLoader
{
    /// <summary>继承链深度上限（防环）。</summary>
    public const int MaximumInheritanceDepth = 16;

    /// <summary>异步加载并合并版本继承链。</summary>
    public async Task<LaunchProfile> LoadAsync(
        string minecraftDirectory,
        string versionId,
        CancellationToken cancellationToken = default)
    {
        string checkedVersionId = ValidateVersionId(versionId);
        List<VersionChainEntry> chain = [];
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        string currentId = checkedVersionId;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(currentId))
            {
                throw new MinecraftLaunchException("版本继承出现循环：" + currentId);
            }

            if (chain.Count >= MaximumInheritanceDepth)
            {
                throw new MinecraftLaunchException("版本继承层级过深，已停止解析。");
            }

            string jsonPath = VersionJsonPath(minecraftDirectory, currentId);
            if (!File.Exists(jsonPath))
            {
                throw new MinecraftLaunchException("找不到版本配置：" + jsonPath);
            }

            using JsonDocument document = await ParseFileAsync(jsonPath, cancellationToken).ConfigureAwait(false);
            JsonElement root = document.RootElement.Clone();
            chain.Add(new VersionChainEntry(currentId, root));

            string? parentId = TryGetString(root, "inheritsFrom");
            if (string.IsNullOrWhiteSpace(parentId))
            {
                break;
            }

            currentId = ValidateVersionId(parentId);
        }

        // 自最顶层依赖（原版）向实例方向合并
        chain.Reverse();
        LaunchProfile profile = MergeProfiles(checkedVersionId, chain);
        profile.MinecraftDirectory = Path.GetFullPath(minecraftDirectory);
        return profile;
    }

    /// <summary>
    /// 尽力加载：完整链解析失败（父版本 JSON 缺失、下载中的半成品文件等）时，
    /// 降级为只含 id / inheritsFrom / type 的部分档案，让实例仍出现在列表里。
    /// 仅当实例自身的 JSON 完全不可读时才返回 null。
    /// </summary>
    public LaunchProfile? TryLoad(string minecraftDirectory, string versionId)
    {
        try
        {
            return Load(minecraftDirectory, versionId);
        }
        catch (MinecraftLaunchException)
        {
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        string checkedVersionId;
        try
        {
            checkedVersionId = ValidateVersionId(versionId);
        }
        catch (MinecraftLaunchException)
        {
            return null;
        }

        string jsonPath = VersionJsonPath(minecraftDirectory, checkedVersionId);
        if (!File.Exists(jsonPath))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(jsonPath));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? declaredId = TryGetString(root, "id");
            return new LaunchProfile
            {
                Id = checkedVersionId,
                SourceId = string.IsNullOrWhiteSpace(declaredId) ? checkedVersionId : declaredId.Trim(),
                InheritsFrom = (TryGetString(root, "inheritsFrom") ?? string.Empty).Trim(),
                VersionType = TryGetString(root, "type") ?? "release",
                MinecraftDirectory = Path.GetFullPath(minecraftDirectory),
                IsPartial = true,
            };
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>同步加载（实例列表扫描等轻量场景使用）。</summary>
    public LaunchProfile Load(string minecraftDirectory, string versionId)
    {
        string checkedVersionId = ValidateVersionId(versionId);
        List<VersionChainEntry> chain = [];
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        string currentId = checkedVersionId;

        while (true)
        {
            if (!visited.Add(currentId))
            {
                throw new MinecraftLaunchException("版本继承出现循环：" + currentId);
            }

            if (chain.Count >= MaximumInheritanceDepth)
            {
                throw new MinecraftLaunchException("版本继承层级过深，已停止解析。");
            }

            string jsonPath = VersionJsonPath(minecraftDirectory, currentId);
            if (!File.Exists(jsonPath))
            {
                throw new MinecraftLaunchException("找不到版本配置：" + jsonPath);
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(jsonPath));
            JsonElement root = document.RootElement.Clone();
            chain.Add(new VersionChainEntry(currentId, root));

            string? parentId = TryGetString(root, "inheritsFrom");
            if (string.IsNullOrWhiteSpace(parentId))
            {
                break;
            }

            currentId = ValidateVersionId(parentId);
        }

        chain.Reverse();
        LaunchProfile profile = MergeProfiles(checkedVersionId, chain);
        profile.MinecraftDirectory = Path.GetFullPath(minecraftDirectory);
        return profile;
    }

    /// <summary>直接合并已读出的版本链（下载器复用）。</summary>
    internal static LaunchProfile MergeProfiles(string requestedVersionId, List<VersionChainEntry> chain)
    {
        string mainClass = string.Empty;
        string clientJarVersionId = string.Empty;
        string assetsId = string.Empty;
        string versionType = string.Empty;
        string legacyArguments = string.Empty;
        string sourceId = string.Empty;
        string loggingArgument = string.Empty;
        string loggingFileId = string.Empty;
        string inheritsFrom = string.Empty;
        int? javaMajorVersion = null;
        List<JsonElement> jvmArguments = [];
        List<JsonElement> gameArguments = [];
        List<JsonElement> libraries = [];
        Dictionary<string, int> libraryIndexes = new(StringComparer.Ordinal);

        foreach (VersionChainEntry entry in chain)
        {
            JsonElement root = entry.Root;
            mainClass = TryGetString(root, "mainClass") ?? mainClass;
            assetsId = TryGetString(root, "assets") ?? assetsId;
            versionType = TryGetString(root, "type") ?? versionType;
            legacyArguments = TryGetString(root, "minecraftArguments") ?? legacyArguments;

            string? declaredId = TryGetString(root, "id");
            if (!string.IsNullOrWhiteSpace(declaredId))
            {
                sourceId = declaredId;
            }

            string? declaredParent = TryGetString(root, "inheritsFrom");
            if (!string.IsNullOrWhiteSpace(declaredParent))
            {
                inheritsFrom = declaredParent.Trim();
            }

            if (root.TryGetProperty("assetIndex", out JsonElement assetIndex) &&
                TryGetString(assetIndex, "id") is { Length: > 0 } assetIndexId)
            {
                assetsId = assetIndexId;
            }

            if (root.TryGetProperty("javaVersion", out JsonElement javaVersion) &&
                javaVersion.ValueKind == JsonValueKind.Object &&
                javaVersion.TryGetProperty("majorVersion", out JsonElement majorVersion) &&
                majorVersion.TryGetInt32(out int parsedMajorVersion))
            {
                javaMajorVersion = parsedMajorVersion;
            }

            if (root.TryGetProperty("logging", out JsonElement logging) &&
                logging.ValueKind == JsonValueKind.Object &&
                logging.TryGetProperty("client", out JsonElement loggingClient) &&
                loggingClient.ValueKind == JsonValueKind.Object)
            {
                string? argument = TryGetString(loggingClient, "argument");
                if (!string.IsNullOrWhiteSpace(argument))
                {
                    loggingArgument = argument;
                }

                if (loggingClient.TryGetProperty("file", out JsonElement file) &&
                    TryGetString(file, "id") is { Length: > 0 } fileId)
                {
                    loggingFileId = fileId.Trim();
                }
            }

            if (root.TryGetProperty("downloads", out JsonElement downloads) &&
                downloads.ValueKind == JsonValueKind.Object &&
                downloads.TryGetProperty("client", out _))
            {
                clientJarVersionId = entry.Id;
            }

            string? explicitJarVersion = TryGetString(root, "jar");
            if (explicitJarVersion is not null)
            {
                clientJarVersionId = explicitJarVersion;
            }

            if (root.TryGetProperty("arguments", out JsonElement arguments) &&
                arguments.ValueKind == JsonValueKind.Object)
            {
                jvmArguments.AddRange(EnumerateArray(arguments, "jvm"));
                gameArguments.AddRange(EnumerateArray(arguments, "game"));
            }

            foreach (JsonElement library in EnumerateArray(root, "libraries"))
            {
                string key = LibraryCoordinateKey(library);
                if (libraryIndexes.TryGetValue(key, out int existingIndex))
                {
                    libraries[existingIndex] = library;
                }
                else
                {
                    libraryIndexes[key] = libraries.Count;
                    libraries.Add(library);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(mainClass))
        {
            throw new MinecraftLaunchException("版本配置缺少 mainClass。");
        }

        if (string.IsNullOrWhiteSpace(clientJarVersionId))
        {
            throw new MinecraftLaunchException("版本配置未指定可用的客户端 JAR。");
        }

        return new LaunchProfile
        {
            Id = requestedVersionId,
            SourceId = string.IsNullOrWhiteSpace(sourceId) ? requestedVersionId : sourceId,
            InheritsFrom = inheritsFrom,
            MainClass = mainClass,
            ClientJarVersionId = clientJarVersionId,
            AssetsId = string.IsNullOrWhiteSpace(assetsId) ? "legacy" : assetsId,
            VersionType = string.IsNullOrWhiteSpace(versionType) ? "release" : versionType,
            RequiredJavaMajorVersion = javaMajorVersion,
            LegacyGameArguments = legacyArguments,
            JvmArguments = jvmArguments,
            GameArguments = gameArguments,
            Libraries = libraries,
            LoggingArgument = loggingArgument,
            LoggingFileId = loggingFileId,
        };
    }

    internal static string VersionJsonPath(string minecraftDirectory, string versionId)
    {
        return Path.Combine(minecraftDirectory, "versions", versionId, versionId + ".json");
    }

    /// <summary>版本 ID 不能为空、不能包含路径分隔符或非法文件名字符。</summary>
    internal static string ValidateVersionId(string versionId)
    {
        string trimmed = (versionId ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.IndexOfAny(['/', '\\']) >= 0 ||
            trimmed is "." or ".." || trimmed.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0)
        {
            throw new MinecraftLaunchException("无效的版本 ID：" + versionId);
        }

        return trimmed;
    }

    private static async Task<JsonDocument> ParseFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = File.OpenRead(path);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException e)
        {
            throw new MinecraftLaunchException("版本配置不是有效 JSON：" + path, e);
        }
        catch (IOException e)
        {
            throw new MinecraftLaunchException("读取版本配置失败：" + path, e);
        }
    }

    internal static IEnumerable<JsonElement> EnumerateArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (JsonElement element in array.EnumerateArray())
        {
            yield return element.Clone();
        }
    }

    internal static string? TryGetString(JsonElement parent, string name)
    {
        return parent.ValueKind == JsonValueKind.Object &&
               parent.TryGetProperty(name, out JsonElement value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>库坐标键：group:artifact:classifier@ext。</summary>
    private static string LibraryCoordinateKey(JsonElement library)
    {
        string? name = TryGetString(library, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return Guid.NewGuid().ToString("N");
        }

        string extension = string.Empty;
        string coordinate = name;
        int atIndex = name.IndexOf('@');
        if (atIndex >= 0)
        {
            extension = name[atIndex..];
            coordinate = name[..atIndex];
        }

        string[] parts = coordinate.Split(':');
        if (parts.Length < 3)
        {
            return name;
        }

        string classifier = parts.Length >= 4 ? parts[3] : string.Empty;
        return parts[0] + ":" + parts[1] + ":" + classifier + extension;
    }

    internal sealed record VersionChainEntry(string Id, JsonElement Root);
}

/// <summary>版本 JSON 库/参数条目的 rules 评估器（JsonElement 版本）。</summary>
public static class MinecraftRuleEvaluator
{
    /// <summary>Mojang 约定的系统名：windows / osx / linux。</summary>
    public static string OperatingSystemName =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";

    /// <summary>创建标准 Minecraft 特性字典（用于库规则评估）。</summary>
    public static Dictionary<string, bool> CreateDefaultFeatures(bool hasCustomResolution)
    {
        return new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["has_custom_resolution"] = hasCustomResolution,
            ["is_demo_user"] = false,
            ["has_quick_plays_support"] = false,
            ["is_quick_play_singleplayer"] = false,
            ["is_quick_play_multiplayer"] = false,
            ["is_quick_play_realms"] = false,
        };
    }

    /// <summary>判断版本 JSON 条目（library / argument）的 rules 是否允许当前环境。</summary>
    public static bool IsAllowed(JsonElement item, IReadOnlyDictionary<string, bool> features)
    {
        if (!item.TryGetProperty("rules", out JsonElement rules) ||
            rules.ValueKind != JsonValueKind.Array ||
            rules.GetArrayLength() == 0)
        {
            return true;
        }

        bool allowed = false;
        foreach (JsonElement rule in rules.EnumerateArray())
        {
            if (!RuleMatches(rule, features))
            {
                continue;
            }

            allowed = string.Equals(VersionLoader.TryGetString(rule, "action"), "allow",
                StringComparison.Ordinal);
        }

        return allowed;
    }

    private static bool RuleMatches(JsonElement rule, IReadOnlyDictionary<string, bool> features)
    {
        if (rule.TryGetProperty("os", out JsonElement os) && os.ValueKind == JsonValueKind.Object)
        {
            string? osName = VersionLoader.TryGetString(os, "name");
            if (!string.IsNullOrEmpty(osName) &&
                !string.Equals(osName, OperatingSystemName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string? osArch = VersionLoader.TryGetString(os, "arch");
            if (!string.IsNullOrEmpty(osArch) && !MatchesArchitecture(osArch))
            {
                return false;
            }

            string? osVersion = VersionLoader.TryGetString(os, "version");
            if (!string.IsNullOrEmpty(osVersion))
            {
                try
                {
                    return Regex.IsMatch(Environment.OSVersion.VersionString, osVersion);
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }
        }

        if (!rule.TryGetProperty("features", out JsonElement required) ||
            required.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        foreach (JsonProperty feature in required.EnumerateObject())
        {
            bool expected = feature.Value.ValueKind == JsonValueKind.True;
            bool actual = features.TryGetValue(feature.Name, out bool value) && value;
            if (actual != expected)
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesArchitecture(string expected)
    {
        string actual = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.X64 => "x86_64",
            Architecture.Arm => "arm",
            Architecture.Arm64 => "aarch64",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        };

        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase) ||
               (expected == "x64" && actual == "x86_64") ||
               (expected == "arm64" && actual == "aarch64");
    }
}
