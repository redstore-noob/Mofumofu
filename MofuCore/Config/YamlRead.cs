using System.Collections;
using System.Text;
using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MofuCore.Config;

/// <summary>
/// Minecraft 运行配置读取类，格式为 YAML。
/// 配置目录为 ./Mofumofu/config，包含 config.yaml（常规配置）与 game.yaml（游戏设置）；
/// 启动器自身的主题/窗口等配置由 Mofumofu.Launcher 的 LocalSettings 负责。
/// </summary>
public class YamlRead
{
    public const string DataDirectoryName = "Mofumofu";
    public const string ConfigDirectoryName = "config";
    public const string ConfigFileName = "config.yaml";
    public const string GameFileName = "game.yaml";

    /// <summary>默认游戏根目录名（相对启动器数据根目录，即 ./.minecraft）。</summary>
    public const string DefaultGameDirectoryName = ".minecraft";

    private const int MinimumWindowWidth = 320;
    private const int MinimumWindowHeight = 240;
    private const int DefaultGameWindowWidth = 854;
    private const int DefaultGameWindowHeight = 480;
    private const int MinimumMemoryMb = 512;
    private const int DefaultMinimumMemoryMb = 2048;
    private const int DefaultMaximumMemoryMb = 4096;

    private static readonly object __syncRoot = new();
    private static readonly Dictionary<YamlDomain, YamlFileStore> __stores = [];

    private static readonly Dictionary<YamlDomain, Dictionary<string, object?>> __defaults = new()
    {
        [YamlDomain.Config] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [Keys.JavaPath] = new List<object?>(),
            [Keys.MinecraftPath] = string.Empty,
            [Keys.DefaultVersionIsolation] = "True",
            [Keys.VerifyFilesBeforeLaunch] = "True",
        },
        [YamlDomain.Game] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [Keys.GameWindowWidth] = DefaultGameWindowWidth.ToString(),
            [Keys.GameWindowHeight] = DefaultGameWindowHeight.ToString(),
            [Keys.GameJavaExecutable] = Keys.AutomaticJavaValue,
            [Keys.GameJvmArguments] = "[]",
            [Keys.GameArguments] = "[]",
            [Keys.GameProcessPriority] = "normal",
            [Keys.GameWrapperCommand] = string.Empty,
            [Keys.GameEnvironmentVariables] = "[]",
            [Keys.GameFullscreen] = "False",
            [Keys.GameSnapshotBeforeLaunch] = "False",
            [Keys.GameMinimumMemoryMb] = DefaultMinimumMemoryMb.ToString(),
            [Keys.GameMaximumMemoryMb] = DefaultMaximumMemoryMb.ToString(),
        },
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly ISerializer YamlSerializer = new SerializerBuilder().Build();
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder().Build();
    private static readonly ISerializer TypedYamlSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults)
        .Build();
    private static readonly IDeserializer TypedYamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static string __dataRoot = AppContext.BaseDirectory;

    /// <summary>
    /// 启动器数据根目录（默认跟随程序目录）；配置写在 {DataRoot}/Mofumofu/config 下。
    /// </summary>
    public static string DataRoot
    {
        get => __dataRoot;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || string.Equals(value, __dataRoot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            lock (__syncRoot)
            {
                __dataRoot = value.Trim();
                __stores.Clear();
            }
        }
    }

    /// <summary>
    /// 配置目录：{DataRoot}/Mofumofu/config。
    /// </summary>
    public static string ConfigDirectory => Path.Combine(__dataRoot, DataDirectoryName, ConfigDirectoryName);

    public static string ConfigFilePath => Path.Combine(ConfigDirectory, ConfigFileName);

    public static string GameFilePath => Path.Combine(ConfigDirectory, GameFileName);

    /// <summary>
    /// 创建配置目录与三份配置文件，并补全缺失的默认配置项。
    /// </summary>
    public static async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(Initialize, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 创建配置目录与三份配置文件，并补全缺失的默认配置项。
    /// </summary>
    public static void Initialize()
    {
        Directory.CreateDirectory(ConfigDirectory);
        foreach (YamlDomain domain in Enum.GetValues<YamlDomain>())
        {
            YamlFileStore store = Store(domain);
            store.EnsureDefaults(__defaults[domain]);
        }
    }

    /// <summary>
    /// 重新从磁盘加载配置；不指定域时重载全部。
    /// </summary>
    public static void Reload(YamlDomain? domain = null)
    {
        lock (__syncRoot)
        {
            if (domain is null)
            {
                __stores.Clear();
                return;
            }

            __stores.Remove(domain.Value);
        }
    }

    /// <summary>
    /// 读取配置项；按内置键表路由到 config.yaml / game.yaml。
    /// </summary>
    public static string GetValue(string key)
    {
        return GetValue(DomainOf(key), key);
    }

    /// <summary>
    /// 写入配置项；按内置键表路由到对应的配置文件。
    /// </summary>
    public static bool SetValue(string key, string value)
    {
        return SetValue(DomainOf(key), key, value);
    }

    /// <summary>
    /// 删除配置项；不存在时返回 false。
    /// </summary>
    public static bool ClearValue(string key)
    {
        return RemoveValue(DomainOf(key), key);
    }

    /// <summary>
    /// 读取指定配置文件中的配置项。
    /// </summary>
    public static string GetValue(YamlDomain domain, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        return Store(domain).Get(key);
    }

    /// <summary>
    /// 写入指定配置文件中的配置项。
    /// </summary>
    public static bool SetValue(YamlDomain domain, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Store(domain).Set(key, value);
    }

    /// <summary>
    /// 删除指定配置文件中的配置项；不存在时返回 false。
    /// </summary>
    public static bool RemoveValue(YamlDomain domain, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        return Store(domain).Remove(key);
    }

    /// <summary>
    /// 读取整份配置文件（深拷贝，调用方修改不会影响内存中的配置）。
    /// </summary>
    public static IReadOnlyDictionary<string, object?> ReadAll(YamlDomain domain)
    {
        return Store(domain).Snapshot();
    }

    /// <summary>
    /// 在单次原子写入中应用多项修改：返回 false 时不落盘，异常时回滚。
    /// </summary>
    public static bool UpdateInTransaction(YamlDomain domain, Func<Dictionary<string, object?>, bool> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        return Store(domain).Update(mutation);
    }

    /// <summary>
    /// 已保存的全部 Java 路径（列表首位为默认 Java）。
    /// </summary>
    public static IReadOnlyList<JavaPathItem> GetJavaPaths()
    {
        YamlFileStore store = Store(YamlDomain.Config);
        lock (store.Gate)
        {
            return ParseJavaPaths(store.Document);
        }
    }

    /// <summary>
    /// 用唯一的首选 Java 替换 Java 路径列表。
    /// </summary>
    public static bool SaveJava(string javaPath, string javaVersion)
    {
        if (string.IsNullOrWhiteSpace(javaPath))
        {
            return false;
        }

        string version = string.IsNullOrWhiteSpace(javaVersion) ? "unknown" : javaVersion.Trim();
        string path = javaPath.Trim();
        return Store(YamlDomain.Config).Update(document =>
        {
            document[Keys.JavaPath] = new List<object?>
            {
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["path"] = path,
                    ["version"] = version,
                },
            };
            return true;
        });
    }

    /// <summary>
    /// 添加一条 Java 路径；路径已存在时更新其版本。
    /// </summary>
    public static bool AddJava(string javaPath, string javaVersion)
    {
        if (string.IsNullOrWhiteSpace(javaPath))
        {
            return false;
        }

        string version = string.IsNullOrWhiteSpace(javaVersion) ? "unknown" : javaVersion.Trim();
        string path = javaPath.Trim();
        return Store(YamlDomain.Config).Update(document =>
        {
            List<object?> entries = JavaEntriesOf(document);
            foreach (object? raw in entries)
            {
                if (raw is not Dictionary<string, object?> entry)
                {
                    continue;
                }

                string existing = entry.TryGetValue("path", out object? value) && value is string text ? text : string.Empty;
                if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
                {
                    entry["version"] = version;
                    return true;
                }
            }

            entries.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["path"] = path,
                ["version"] = version,
            });
            document[Keys.JavaPath] = entries;
            return true;
        });
    }

    /// <summary>
    /// 移除一条 Java 路径；不存在时返回 false。
    /// </summary>
    public static bool RemoveJava(string javaPath)
    {
        if (string.IsNullOrWhiteSpace(javaPath))
        {
            return false;
        }

        string path = javaPath.Trim();
        return Store(YamlDomain.Config).Update(document =>
        {
            List<object?> entries = JavaEntriesOf(document);
            List<object?> kept = [];
            bool removed = false;
            foreach (object? raw in entries)
            {
                if (raw is Dictionary<string, object?> entry)
                {
                    string existing = entry.TryGetValue("path", out object? value) && value is string text ? text : string.Empty;
                    if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
                    {
                        removed = true;
                        continue;
                    }
                }

                kept.Add(raw);
            }

            document[Keys.JavaPath] = kept;
            return removed;
        });
    }

    /// <summary>
    /// 把指定路径设为默认 Java（列表首位）；路径不存在时返回 false。
    /// </summary>
    public static bool SetPrimaryJava(string javaPath)
    {
        if (string.IsNullOrWhiteSpace(javaPath))
        {
            return false;
        }

        string path = javaPath.Trim();
        return Store(YamlDomain.Config).Update(document =>
        {
            List<object?> entries = JavaEntriesOf(document);
            int index = -1;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i] is Dictionary<string, object?> entry)
                {
                    string existing = entry.TryGetValue("path", out object? value) && value is string text ? text : string.Empty;
                    if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
                    {
                        index = i;
                        break;
                    }
                }
            }

            if (index < 0)
            {
                return false;
            }

            if (index == 0)
            {
                return true;
            }

            object? entryToMove = entries[index];
            entries.RemoveAt(index);
            entries.Insert(0, entryToMove);
            document[Keys.JavaPath] = entries;
            return true;
        });
    }

    /// <summary>
    /// 首选 Java 可执行文件（java.exe）路径；未配置时返回空串。
    /// </summary>
    public static string JavaExecutable
    {
        get
        {
            IReadOnlyList<JavaPathItem> items = GetJavaPaths();
            return items.Count == 0 ? string.Empty : items[0].Path;
        }
    }

    /// <summary>
    /// 首选 Java 的版本号；未配置时返回空串。
    /// </summary>
    public static string JavaVersion
    {
        get
        {
            IReadOnlyList<JavaPathItem> items = GetJavaPaths();
            return items.Count == 0 ? string.Empty : items[0].Version;
        }
    }

    /// <summary>
    /// 游戏根目录（.minecraft 或自定义目录）；未配置时默认为 ./.minecraft。
    /// </summary>
    public static string GameDirectory
    {
        get
        {
            try
            {
                string configured = GetValue(YamlDomain.Config, Keys.MinecraftPath);
                string path = string.IsNullOrWhiteSpace(configured) ? DefaultGameDirectory : configured.Trim();
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return DefaultGameDirectory;
            }
        }
    }

    /// <summary>默认游戏根目录（{DataRoot}/.minecraft），路径不可解析时返回原样拼接值。</summary>
    public static string DefaultGameDirectory
    {
        get
        {
            try
            {
                return Path.GetFullPath(Path.Combine(DataRoot, DefaultGameDirectoryName));
            }
            catch (Exception)
            {
                return Path.Combine(DataRoot, DefaultGameDirectoryName);
            }
        }
    }

    /// <summary>确保游戏根目录存在（不存在时创建），返回其绝对路径。</summary>
    public static string EnsureGameDirectory()
    {
        string directory = GameDirectory;
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception)
        {
            // 目录创建失败（权限/磁盘）时仍然返回路径，由调用方在真正读写时报错
        }

        return directory;
    }

    /// <summary>
    /// 保存游戏根目录。
    /// </summary>
    public static bool SaveGameDirectory(string path)
    {
        return !string.IsNullOrWhiteSpace(path) && SetValue(YamlDomain.Config, Keys.MinecraftPath, path.Trim());
    }

    /// <summary>
    /// 清除已保存的游戏根目录（恢复自动检测）。
    /// </summary>
    public static bool ClearGameDirectory()
    {
        return RemoveValue(YamlDomain.Config, Keys.MinecraftPath);
    }

    /// <summary>
    /// 全局默认版本隔离：true = 新实例默认隔离。未配置或值损坏时默认开启。
    /// </summary>
    public static bool DefaultVersionIsolation => ReadBool(Keys.DefaultVersionIsolation, true);

    /// <summary>保存全局版本隔离开关。</summary>
    public static bool SaveDefaultVersionIsolation(bool enabled)
    {
        return SetValue(YamlDomain.Config, Keys.DefaultVersionIsolation, enabled ? "True" : "False");
    }

    /// <summary>
    /// 启动前是否校验游戏文件完整性并自动补全。未配置或值损坏时默认开启。
    /// </summary>
    public static bool VerifyFilesBeforeLaunch => ReadBool(Keys.VerifyFilesBeforeLaunch, true);

    /// <summary>
    /// 加载全局高级启动设置（game.yaml）。
    /// </summary>
    public static GlobalLaunchSettings LoadGlobalLaunchSettings()
    {
        return new GlobalLaunchSettings
        {
            WindowWidth = ReadInt(Keys.GameWindowWidth, DefaultGameWindowWidth, MinimumWindowWidth),
            WindowHeight = ReadInt(Keys.GameWindowHeight, DefaultGameWindowHeight, MinimumWindowHeight),
            JavaExecutable = ReadGameJavaExecutable(),
            AdditionalJvmArguments = ReadArguments(Keys.GameJvmArguments),
            AdditionalGameArguments = ReadArguments(Keys.GameArguments),
            ProcessPriority = NormalizeProcessPriority(GetValue(YamlDomain.Game, Keys.GameProcessPriority)),
            WrapperCommand = GetValue(YamlDomain.Game, Keys.GameWrapperCommand).Trim(),
            AdditionalEnvironmentVariables = ReadArguments(Keys.GameEnvironmentVariables),
            LaunchFullscreen = ReadBool(Keys.GameFullscreen, false),
            SnapshotBeforeLaunch = ReadBool(Keys.GameSnapshotBeforeLaunch, false),
            MinimumMemoryMb = ReadInt(Keys.GameMinimumMemoryMb, DefaultMinimumMemoryMb, MinimumMemoryMb),
            MaximumMemoryMb = ReadInt(Keys.GameMaximumMemoryMb, DefaultMaximumMemoryMb, MinimumMemoryMb),
        };
    }

    /// <summary>
    /// 异步加载全局高级启动设置。
    /// </summary>
    public static Task<GlobalLaunchSettings> LoadGlobalLaunchSettingsAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(LoadGlobalLaunchSettings, cancellationToken);
    }

    /// <summary>
    /// 保存整份全局高级启动设置（参数先去空白并剔除空项）。
    /// </summary>
    public static bool SaveGlobalLaunchSettings(GlobalLaunchSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.WindowWidth < MinimumWindowWidth || settings.WindowHeight < MinimumWindowHeight)
        {
            return false;
        }

        int minimumMemory = Math.Max(MinimumMemoryMb, settings.MinimumMemoryMb);
        int maximumMemory = Math.Max(minimumMemory, settings.MaximumMemoryMb);
        string javaExecutable = settings.JavaExecutable.Trim();
        List<string> jvmArguments = NormalizeArguments(settings.AdditionalJvmArguments);
        List<string> gameArguments = NormalizeArguments(settings.AdditionalGameArguments);
        List<string> environmentVariables = NormalizeArguments(settings.AdditionalEnvironmentVariables);
        string processPriority = NormalizeProcessPriority(settings.ProcessPriority);

        return Store(YamlDomain.Game).Update(document =>
        {
            document[Keys.GameWindowWidth] = settings.WindowWidth.ToString();
            document[Keys.GameWindowHeight] = settings.WindowHeight.ToString();
            document[Keys.GameJavaExecutable] = javaExecutable.Length == 0 ? Keys.AutomaticJavaValue : javaExecutable;
            document[Keys.GameJvmArguments] = SerializeStringList(jvmArguments);
            document[Keys.GameArguments] = SerializeStringList(gameArguments);
            document[Keys.GameProcessPriority] = processPriority;
            document[Keys.GameWrapperCommand] = settings.WrapperCommand.Trim();
            document[Keys.GameEnvironmentVariables] = SerializeStringList(environmentVariables);
            document[Keys.GameFullscreen] = FormatBool(settings.LaunchFullscreen);
            document[Keys.GameSnapshotBeforeLaunch] = FormatBool(settings.SnapshotBeforeLaunch);
            document[Keys.GameMinimumMemoryMb] = minimumMemory.ToString();
            document[Keys.GameMaximumMemoryMb] = maximumMemory.ToString();
            return true;
        });
    }

    /// <summary>
    /// 异步保存整份全局高级启动设置。
    /// </summary>
    public static Task<bool> SaveGlobalLaunchSettingsAsync(GlobalLaunchSettings settings, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => SaveGlobalLaunchSettings(settings), cancellationToken);
    }

    /// <summary>
    /// 归一化进程优先级取值；非法值（含空串）归一为 normal。
    /// </summary>
    public static string NormalizeProcessPriority(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "low" => "low",
            "belownormal" => "belownormal",
            "abovenormal" => "abovenormal",
            "high" => "high",
            _ => "normal",
        };
    }

    /// <summary>
    /// 加载实例目录下的 java_config.yaml；文件不存在时返回空配置（使用全局设置）。
    /// </summary>
    public static InstanceJavaConfig LoadInstanceJavaConfig(string instanceDirectory)
    {
        if (string.IsNullOrWhiteSpace(instanceDirectory))
        {
            return new InstanceJavaConfig();
        }

        string configPath = Path.Combine(instanceDirectory.Trim(), "java_config.yaml");
        try
        {
            if (!File.Exists(configPath))
            {
                return new InstanceJavaConfig();
            }

            string text = File.ReadAllText(configPath, Encoding.UTF8);
            return string.IsNullOrWhiteSpace(text)
                ? new InstanceJavaConfig()
                : TypedYamlDeserializer.Deserialize<InstanceJavaConfig>(new StringReader(text)) ?? new InstanceJavaConfig();
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine("加载实例 Java 配置失败: " + e.Message);
            return new InstanceJavaConfig();
        }
    }

    /// <summary>
    /// 保存实例目录下的 java_config.yaml。
    /// </summary>
    public static bool SaveInstanceJavaConfig(string instanceDirectory, InstanceJavaConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(instanceDirectory))
        {
            return false;
        }

        string configPath = Path.Combine(instanceDirectory.Trim(), "java_config.yaml");
        try
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(configPath));
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(configPath, TypedYamlSerializer.Serialize(config), new UTF8Encoding(false));
            return true;
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine("保存实例 Java 配置失败: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// 删除实例目录下的 java_config.yaml（回退到全局设置）。
    /// </summary>
    public static bool DeleteInstanceJavaConfig(string instanceDirectory)
    {
        if (string.IsNullOrWhiteSpace(instanceDirectory))
        {
            return false;
        }

        string configPath = Path.Combine(instanceDirectory.Trim(), "java_config.yaml");
        try
        {
            if (File.Exists(configPath))
            {
                File.Delete(configPath);
            }

            return true;
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine("删除实例 Java 配置失败: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// 配置项键名。
    /// </summary>
    public static class Keys
    {
        public const string JavaPath = "javaPath";
        public const string MinecraftPath = "minecraftPath";
        public const string DefaultVersionIsolation = "defaultVersionIsolation";
        public const string VerifyFilesBeforeLaunch = "verifyFilesBeforeLaunch";
        public const string GameWindowWidth = "globalLaunchWindowWidth";
        public const string GameWindowHeight = "globalLaunchWindowHeight";
        public const string GameJavaExecutable = "globalLaunchJavaExecutable";
        public const string GameJvmArguments = "globalLaunchJvmArguments";
        public const string GameArguments = "globalLaunchGameArguments";
        public const string GameProcessPriority = "globalLaunchProcessPriority";
        public const string GameWrapperCommand = "globalLaunchWrapperCommand";
        public const string GameEnvironmentVariables = "globalLaunchEnvVars";
        public const string GameFullscreen = "globalLaunchFullscreen";
        public const string GameSnapshotBeforeLaunch = "globalLaunchSnapshotBeforeLaunch";
        public const string GameMinimumMemoryMb = "globalLaunchMinimumMemoryMb";
        public const string GameMaximumMemoryMb = "globalLaunchMaximumMemoryMb";

        /// <summary>Java 路径的"自动选择"占位值。</summary>
        public const string AutomaticJavaValue = "$auto";
    }

    private static YamlDomain DomainOf(string key)
    {
        if (key.StartsWith("globalLaunch", StringComparison.Ordinal))
        {
            return YamlDomain.Game;
        }

        return YamlDomain.Config;
    }

    private static YamlFileStore Store(YamlDomain domain)
    {
        lock (__syncRoot)
        {
            if (__stores.TryGetValue(domain, out YamlFileStore? store))
            {
                return store;
            }

            store = new YamlFileStore(FilePathOf(domain));
            __stores[domain] = store;
            return store;
        }
    }

    private static string FilePathOf(YamlDomain domain)
    {
        return Path.Combine(ConfigDirectory, domain switch
        {
            YamlDomain.Game => GameFileName,
            _ => ConfigFileName,
        });
    }

    private static bool ReadBool(string key, bool fallback)
    {
        string value = GetValue(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return bool.TryParse(value.Trim(), out bool result) ? result : fallback;
    }

    private static int ReadInt(string key, int fallback, int minimum)
    {
        return int.TryParse(GetValue(key), out int value) && value >= minimum ? value : fallback;
    }

    private static string FormatBool(bool value)
    {
        return value ? "True" : "False";
    }

    private static string ReadGameJavaExecutable()
    {
        string configured = GetValue(YamlDomain.Game, Keys.GameJavaExecutable);
        if (configured == Keys.AutomaticJavaValue)
        {
            return string.Empty;
        }

        return configured.Trim().Length == 0 ? JavaExecutable : configured.Trim();
    }

    private static List<string> ReadArguments(string key)
    {
        string value = GetValue(YamlDomain.Game, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        try
        {
            List<string>? arguments = JsonSerializer.Deserialize<List<string>>(value, JsonOptions);
            return arguments is null ? [] : NormalizeArguments(arguments);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string SerializeStringList(List<string> list)
    {
        return JsonSerializer.Serialize(list, JsonOptions);
    }

    private static List<string> NormalizeArguments(IEnumerable<string>? arguments)
    {
        List<string> result = [];
        if (arguments is null)
        {
            return result;
        }

        foreach (string argument in arguments)
        {
            string trimmed = argument.Trim();
            if (trimmed.Length > 0)
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    private static List<object?> JavaEntriesOf(Dictionary<string, object?> document)
    {
        if (document.TryGetValue(Keys.JavaPath, out object? value) && value is List<object?> entries)
        {
            return entries;
        }

        List<object?> created = [];
        document[Keys.JavaPath] = created;
        return created;
    }

    private static List<JavaPathItem> ParseJavaPaths(Dictionary<string, object?> document)
    {
        List<JavaPathItem> result = [];
        if (!document.TryGetValue(Keys.JavaPath, out object? value) || value is not List<object?> entries)
        {
            return result;
        }

        foreach (object? raw in entries)
        {
            if (raw is not Dictionary<string, object?> entry)
            {
                continue;
            }

            string path = entry.TryGetValue("path", out object? pathValue) && pathValue is string pathText ? pathText : string.Empty;
            string version = entry.TryGetValue("version", out object? versionValue) && versionValue is string versionText ? versionText : string.Empty;
            if (path.Length == 0)
            {
                continue;
            }

            result.Add(new JavaPathItem { Path = path, Version = version });
        }

        return result;
    }

    internal static object? NormalizeValue(object? value)
    {
        if (value is IDictionary dictionary)
        {
            Dictionary<string, object?> result = new(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is null)
                {
                    continue;
                }

                result[entry.Key.ToString() ?? string.Empty] = NormalizeValue(entry.Value);
            }

            return result;
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            List<object?> list = [];
            foreach (object? item in enumerable)
            {
                list.Add(NormalizeValue(item));
            }

            return list;
        }

        return value;
    }
}

/// <summary>
/// 配置文件域：config.yaml（Minecraft 常规配置）、game.yaml（游戏设置）。
/// </summary>
public enum YamlDomain
{
    Config,
    Game,
}

/// <summary>
/// 一条已保存的 Java 路径。
/// </summary>
public class JavaPathItem
{
    public string Path { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string DisplayName => string.IsNullOrWhiteSpace(Version) ? Path : $"{Path} (Java {Version})";
}

/// <summary>
/// 启动器窗口尺寸。
/// </summary>


/// <summary>
/// 全局高级启动设置的内存快照（game.yaml）。
/// </summary>
public class GlobalLaunchSettings
{
    public int WindowWidth { get; set; } = 854;

    public int WindowHeight { get; set; } = 480;

    public string JavaExecutable { get; set; } = string.Empty;

    public List<string> AdditionalJvmArguments { get; set; } = [];

    public List<string> AdditionalGameArguments { get; set; } = [];

    /// <summary>游戏进程优先级：low / belownormal / normal / abovenormal / high。</summary>
    public string ProcessPriority { get; set; } = "normal";

    /// <summary>包装命令模板，需含 %command% 占位。</summary>
    public string WrapperCommand { get; set; } = string.Empty;

    /// <summary>注入游戏进程的额外环境变量（"KEY=VALUE" 每项一条）。</summary>
    public List<string> AdditionalEnvironmentVariables { get; set; } = [];

    public bool LaunchFullscreen { get; set; }

    public bool SnapshotBeforeLaunch { get; set; }

    public int MinimumMemoryMb { get; set; } = 2048;

    public int MaximumMemoryMb { get; set; } = 4096;
}

/// <summary>
/// 实例 Java 配置（存放在实例目录的 java_config.yaml）。
/// </summary>
public class InstanceJavaConfig
{
    public string JavaExecutable { get; set; } = string.Empty;

    public int MinMemoryMB { get; set; }

    public int MaxMemoryMB { get; set; }

    public List<string> AdditionalJvmArguments { get; set; } = [];

    public List<string> AdditionalGameArguments { get; set; } = [];
}

/// <summary>
/// 单个 YAML 配置文件的管理器：原子落盘、损坏时备份后重建、瞬时 IO 失败不覆盖原文件。
/// </summary>
internal sealed class YamlFileStore
{
    private static readonly ISerializer Serializer = new SerializerBuilder().Build();
    private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();

    private readonly object _gate = new();
    private readonly string _filePath;
    private Dictionary<string, object?> _document;

    public YamlFileStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("filePath 不能为空", nameof(filePath));
        }

        _filePath = filePath;
        _document = Load();
    }

    public string FilePath => _filePath;

    public object Gate => _gate;

    public Dictionary<string, object?> Document
    {
        get
        {
            lock (_gate)
            {
                return _document;
            }
        }
    }

    public string Get(string key)
    {
        lock (_gate)
        {
            return ReadString(_document.TryGetValue(key, out object? value) ? value : null);
        }
    }

    public bool Set(string key, string value)
    {
        return Update(document =>
        {
            document[key] = value;
            return true;
        });
    }

    public bool Remove(string key)
    {
        return Update(document => document.Remove(key));
    }

    public bool EnsureDefaults(IReadOnlyDictionary<string, object?> defaults)
    {
        return Update(document =>
        {
            bool changed = false;
            foreach (KeyValuePair<string, object?> pair in defaults)
            {
                if (document.ContainsKey(pair.Key))
                {
                    continue;
                }

                document[pair.Key] = YamlRead.NormalizeValue(pair.Value);
                changed = true;
            }

            return changed;
        });
    }

    public IReadOnlyDictionary<string, object?> Snapshot()
    {
        lock (_gate)
        {
            return DeepClone(_document);
        }
    }

    public void Reload()
    {
        lock (_gate)
        {
            _document = Load();
        }
    }

    internal bool Update(Func<Dictionary<string, object?>, bool> mutation)
    {
        lock (_gate)
        {
            Dictionary<string, object?> previous = DeepClone(_document);
            try
            {
                if (!mutation(_document))
                {
                    return false;
                }

                if (Save(_document))
                {
                    return true;
                }

                _document = previous;
                return false;
            }
            catch (Exception e)
            {
                _document = previous;
                System.Diagnostics.Debug.WriteLine("更新配置文件失败: " + e.Message);
                return false;
            }
        }
    }

    private Dictionary<string, object?> Load()
    {
        if (!File.Exists(_filePath))
        {
            Dictionary<string, object?> empty = new(StringComparer.Ordinal);
            if (!Save(empty))
            {
                throw new InvalidOperationException("无法创建配置文件：" + Path.GetFullPath(_filePath));
            }

            return empty;
        }

        string text;
        try
        {
            text = File.ReadAllText(_filePath, Encoding.UTF8);
        }
        catch (Exception e)
        {
            throw new IOException($"读取配置文件失败：{_filePath}（{e.Message}）", e);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        try
        {
            Dictionary<string, object?>? root = Deserializer
                .Deserialize<Dictionary<string, object?>?>(new StringReader(text));
            return YamlRead.NormalizeValue(root) as Dictionary<string, object?>
                   ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"配置文件损坏: {e.Message}，已备份并重建默认配置");
            string backupPath = _filePath + $".corrupted-{DateTime.Now:yyyyMMddHHmmss}.bak";
            try
            {
                File.Copy(_filePath, backupPath, overwrite: true);
            }
            catch (Exception copyError)
            {
                throw new IOException($"配置文件损坏且无法备份：{_filePath}（{copyError.Message}）", copyError);
            }

            System.Diagnostics.Debug.WriteLine("已备份损坏的配置文件到: " + backupPath);
            Dictionary<string, object?> rebuilt = new(StringComparer.Ordinal);
            if (!Save(rebuilt))
            {
                throw new InvalidOperationException("无法重建配置文件：" + Path.GetFullPath(_filePath));
            }

            return rebuilt;
        }
    }

    private bool Save(Dictionary<string, object?> document)
    {
        string temporaryPath = string.Empty;
        try
        {
            string data = Serializer.Serialize(document);
            string fullPath = Path.GetFullPath(_filePath);
            string directory = Path.GetDirectoryName(fullPath) ?? ".";
            Directory.CreateDirectory(directory);
            // 临时文件写同一目录，保证 replace 是同一文件系统内的原子操作
            temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporaryPath, data, new UTF8Encoding(false));
            File.Move(temporaryPath, fullPath, overwrite: true);
            temporaryPath = string.Empty;
            return true;
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine("保存配置文件失败: " + e.Message);
            return false;
        }
        finally
        {
            if (temporaryPath.Length > 0)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // 临时文件清理失败不应掩盖原始保存结果
                }
            }
        }
    }

    private static string ReadString(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is string text)
        {
            return text;
        }

        System.Diagnostics.Debug.WriteLine("读取配置项失败: 值不是字符串类型");
        return string.Empty;
    }

    private static Dictionary<string, object?> DeepClone(Dictionary<string, object?> source)
    {
        Dictionary<string, object?> clone = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> pair in source)
        {
            clone[pair.Key] = CloneValue(pair.Value);
        }

        return clone;
    }

    private static object? CloneValue(object? value)
    {
        return value switch
        {
            Dictionary<string, object?> map => DeepClone(map),
            List<object?> list => list.Select(CloneValue).ToList(),
            _ => value,
        };
    }
}
