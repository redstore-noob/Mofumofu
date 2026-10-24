using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MofuCore.Account;
using MofuCore.Java;
using MofuCore.Log;
using LibraryResolver = MofuCore.Download.Minecraft.LibraryResolver;
using ResolvedLibraries = MofuCore.Download.Minecraft.ResolvedLibraries;

namespace MofuCore.Launch;

/// <summary>
/// 游戏启动的类：解析版本 JSON 继承链、评估 rules、解析依赖库与 natives、定位 Java、
/// 装配启动参数、拉起 Java 进程并逐行转发 stdout/stderr，退出后给出退出码与崩溃诊断。
/// </summary>
public class GameLaunch
{
    /// <summary>全局共享实例。</summary>
    public static GameLaunch Shared { get; } = new();

    private static readonly Regex OfflineUsernamePattern = new(@"^[A-Za-z0-9_]{1,16}$", RegexOptions.Compiled);

    private readonly object _gate = new();
    private readonly VersionLoader _profileLoader = new();
    private readonly LibraryResolver _libraryResolver = new();
    private readonly MinecraftArgumentBuilder _argumentBuilder = new();
    private readonly JavaRuntimeLocator _javaLocator = new();

    private MinecraftLaunchResult? _current;

    /// <summary>当前运行中的游戏进程；没有运行时为 null。</summary>
    public MinecraftLaunchResult? Current
    {
        get
        {
            lock (_gate)
            {
                return _current is { IsRunning: true } ? _current : null;
            }
        }
    }

    public bool IsGameRunning => Current is not null;

    /// <summary>启动 Minecraft。</summary>
    public async Task<MinecraftLaunchResult> LaunchAsync(
        MinecraftLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (IsGameRunning)
        {
            throw new MinecraftLaunchException("游戏已经在运行。");
        }

        string minecraftDirectory = NormalizeDirectory(options.MinecraftDirectory,
            "Minecraft 目录不存在：");
        string gameDirectory = string.IsNullOrWhiteSpace(options.GameDirectory)
            ? ResolveDefaultGameDirectory(minecraftDirectory, options.VersionId)
            : NormalizeDirectory(options.GameDirectory, "实例游戏目录不存在：");
        await Task.Yield();

        if (options.Account is MicrosoftLaunchAccount microsoft && microsoft.IsExpired)
        {
            throw new MinecraftLaunchException("正版账号的访问令牌已过期，请先通过认证器刷新或重新登录。");
        }

        if (options.MinimumMemoryMb <= 0 || options.MaximumMemoryMb < options.MinimumMemoryMb)
        {
            throw new MinecraftLaunchException("内存设置无效：最大内存必须大于等于最小内存。");
        }

        options.LogCallback?.Invoke("正在解析版本、依赖库与 Java 运行时。");
        LaunchProfile profile = await _profileLoader
            .LoadAsync(minecraftDirectory, options.VersionId, cancellationToken).ConfigureAwait(false);

        Dictionary<string, bool> features = MinecraftRuleEvaluator.CreateDefaultFeatures(
            options.WindowWidth > 0 && options.WindowHeight > 0);
        ResolvedLibraries resolved = await _libraryResolver
            .ResolveAsync(profile, minecraftDirectory, features, cancellationToken).ConfigureAwait(false);
        string nativeDirectory = await _libraryResolver
            .ExtractNativesAsync(profile.Id, resolved.Natives, minecraftDirectory, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            int? requiredJavaMajorVersion = profile.RequiredJavaMajorVersion;
            string javaExecutable = await ResolveJavaExecutableAsync(
                options, minecraftDirectory, requiredJavaMajorVersion, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<string> additionalJvmArguments = await PrepareAccountJvmArgumentsAsync(
                options, minecraftDirectory, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<string> arguments = _argumentBuilder.Build(
                profile, options, options.Account!, nativeDirectory, resolved.Classpath, profile.MainClass,
                additionalJvmArguments);
            ValidateFinalArguments(arguments);

            MinecraftLaunchResult result = StartProcess(options, profile, gameDirectory, javaExecutable,
                nativeDirectory, arguments, requiredJavaMajorVersion);
            lock (_gate)
            {
                _current = result;
            }

            return result;
        }
        catch
        {
            LibraryResolver.TryDeleteDirectory(nativeDirectory);
            throw;
        }
    }

    /// <summary>停止当前运行中的游戏（整棵进程树）。</summary>
    /// <summary>
    /// Java 运行时策略（对齐参考启动器）：
    /// 1) 优先使用已有主版本精确匹配；
    /// 2) 没有精确匹配时自动向 Mojang 下载所需 Java 到游戏目录 runtime/；
    /// 3) 下载失败再回退到满足最低版本要求的现有 Java。
    /// </summary>
    private async Task<string> ResolveJavaExecutableAsync(
        MinecraftLaunchOptions options,
        string minecraftDirectory,
        int? requiredMajorVersion,
        CancellationToken cancellationToken)
    {
        if (requiredMajorVersion is int required)
        {
            string? exact = await JavaPathSearch.FindExactMatchJavaAsync(
                options.JavaExecutable, required, options.JavaRuntimeDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (exact is not null) return exact;

            options.LogCallback?.Invoke($"未检测到 Java {required}，正在自动下载所需运行时…");
            try
            {
                var progress = new Progress<JavaRuntimeInstallProgress>(value =>
                    options.LogCallback?.Invoke("下载 Java：" + value.Phase));
                InstalledJavaRuntime? installed = await JavaDownload.InstallForMajorVersionAsync(
                    required, minecraftDirectory, progress, cancellationToken).ConfigureAwait(false);
                if (installed?.JavaExecutablePath is { Length: > 0 } installedPath)
                {
                    options.LogCallback?.Invoke($"Java {required} 已安装：{installedPath}");
                    return installedPath;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                options.LogCallback?.Invoke(
                    $"自动下载 Java {required} 失败（可能是网络无法连接下载源）：{error.Message}。" +
                    "将继续尝试使用本机已有的 Java。");
            }

            options.LogCallback?.Invoke($"回退到现有 Java（最低要求 Java {required}）…");
        }

        return await _javaLocator.FindJavaExecutableAsync(
            options.JavaExecutable, requiredMajorVersion, options.JavaRuntimeDirectory,
            options.LogCallback, cancellationToken).ConfigureAwait(false);
    }

    public bool TryStopGame()
    {
        MinecraftLaunchResult? result = Current;
        if (result is null)
        {
            return false;
        }

        result.Kill();
        return true;
    }

    // ------------------------------------------------------------------
    // 账号 / 语言 / 进程
    // ------------------------------------------------------------------

    private static async Task<IReadOnlyList<string>> PrepareAccountJvmArgumentsAsync(
        MinecraftLaunchOptions options,
        string minecraftDirectory,
        CancellationToken cancellationToken)
    {
        List<string> arguments = [.. options.AdditionalJvmArguments];
        if (options.Account is AuthlibLaunchAccount authlib)
        {
            // 皮肤站账号：把游戏会话服务重定向到皮肤站，需要注入器 jar 在本地就绪
            options.LogCallback?.Invoke("正在准备 authlib-injector 注入器。");
            string injectorPath = await AuthlibInjector.EnsureAsync(
                minecraftDirectory, options.LogCallback, cancellationToken).ConfigureAwait(false);
            arguments.Add($"-javaagent:{injectorPath}={authlib.ApiRoot}");
            options.LogCallback?.Invoke($"已启用外置登录：{authlib.ApiRoot}");
        }

        return arguments;
    }

    /// <summary>
    /// 新实例语言跟随系统：启动前检查实例游戏目录的 options.txt，若尚未写入 lang 键
    /// （新实例 / 从未运行过的实例），按电脑系统语言写入；已有 lang 的实例不做覆盖。
    /// 返回是否真的写入。失败只记日志，不阻断启动。
    /// </summary>
    public static bool SyncInstanceLanguage(string gameDirectory, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return false;
        }

        string code = MinecraftLanguage.SystemLanguageCode();
        if (code.Length == 0)
        {
            log?.Invoke("无法识别系统语言，游戏语言保持默认。");
            return false;
        }

        string optionsPath = Path.Combine(gameDirectory, "options.txt");
        string content;
        try
        {
            content = File.Exists(optionsPath) ? File.ReadAllText(optionsPath, Encoding.UTF8) : string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"读取 options.txt 失败，游戏语言保持默认：{e.Message}");
            return false;
        }

        if (MinecraftLanguage.HasOptionsKey(content, "lang"))
        {
            // 实例已经跑过一次（MC 首次运行就会写入 lang）或用户设置过语言
            return false;
        }

        try
        {
            File.WriteAllText(optionsPath, MinecraftLanguage.UpsertOptionsKey(content, "lang", code),
                new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"写入游戏语言失败：{e.Message}");
            return false;
        }

        log?.Invoke($"新实例：已将游戏语言设置为 {code}（跟随系统语言）。");
        return true;
    }

    private MinecraftLaunchResult StartProcess(
        MinecraftLaunchOptions options,
        LaunchProfile profile,
        string gameDirectory,
        string javaExecutable,
        string nativeDirectory,
        IReadOnlyList<string> arguments,
        int? requiredJavaMajorVersion)
    {
        (string program, IReadOnlyList<string> programArguments) = BuildProcessCommand(
            javaExecutable, arguments, options.WrapperCommand, options.LogCallback);

        ProcessStartInfo startInfo = new(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = gameDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in programArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach ((string name, string value) in options.EnvironmentVariables)
        {
            startInfo.Environment[name] = value;
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new MinecraftLaunchException("Java 进程未能启动。");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new MinecraftLaunchException("Java 进程未能启动：" + e.Message, e);
        }

        ProcessPriority.Apply(process, options.ProcessPriority, options.LogCallback);

        MinecraftLaunchResult result = new(process, profile.Id, options.Account!.Username, requiredJavaMajorVersion,
            nativeDirectory);
        result.AttachOutput(options.GameOutputCallback);
        result.StartExitMonitor();
        options.LogCallback?.Invoke($"Java 进程已启动，进程 ID：{process.Id}。");
        return result;
    }

    private static (string Program, IReadOnlyList<string> Arguments) BuildProcessCommand(
        string javaExecutable,
        IReadOnlyList<string> arguments,
        string wrapperCommand,
        Action<string>? log)
    {
        string[] wrapper = (wrapperCommand ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (wrapper.Length == 0)
        {
            return (javaExecutable, arguments);
        }

        if (!wrapper.Contains("%command%", StringComparer.Ordinal))
        {
            log?.Invoke("包装命令缺少 %command% 占位，已忽略该设置。");
            return (javaExecutable, arguments);
        }

        List<string> expanded = [];
        foreach (string part in wrapper)
        {
            if (part == "%command%")
            {
                expanded.Add(javaExecutable);
                expanded.AddRange(arguments);
                continue;
            }

            expanded.Add(part);
        }

        return (expanded[0], expanded.Skip(1).ToList());
    }

    private static void ValidateFinalArguments(IReadOnlyList<string> arguments)
    {
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument.Length == 0)
            {
                string previous = index > 0 ? arguments[index - 1] : string.Empty;
                throw new MinecraftLaunchException($"最终 Java 命令出现空参数（下标 {index}，前一项 {previous}）。");
            }

            if (argument.Contains('\0'))
            {
                throw new MinecraftLaunchException($"最终 Java 命令在下标 {index} 处包含非法字符 \\0。");
            }
        }
    }

    private static string NormalizeDirectory(string path, string errorPrefix)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            throw new MinecraftLaunchException(errorPrefix + path);
        }

        return Path.GetFullPath(path);
    }

    /// <summary>
    /// 未显式指定 GameDirectory 时的默认值：版本隔离开启时使用 versions/{versionId}
    /// （不存在则连同 mods/saves 等子目录一起创建），关闭时回到共享根目录。
    /// </summary>
    private static string ResolveDefaultGameDirectory(string minecraftDirectory, string versionId)
    {
        if (!Config.YamlRead.DefaultVersionIsolation)
        {
            return minecraftDirectory;
        }

        try
        {
            string isolated = Path.Combine(minecraftDirectory, "versions", versionId.Trim());
            Directory.CreateDirectory(isolated);
            foreach (string sub in (string[])["mods", "saves", "resourcepacks", "config"])
            {
                Directory.CreateDirectory(Path.Combine(isolated, sub));
            }

            return isolated;
        }
        catch (Exception)
        {
            return minecraftDirectory;
        }
    }

    // ------------------------------------------------------------------
    // 直接进服 / 快速进存档参数
    // ------------------------------------------------------------------

    /// <summary>直接进服：优先 Minecraft 1.20+ 的 Quick Play 参数，同时附上旧版兼容参数。</summary>
    public static IReadOnlyList<string> BuildServerArguments(string serverHost, int serverPort = 25565)
    {
        if (string.IsNullOrWhiteSpace(serverHost))
        {
            return [];
        }

        string address = serverPort == 25565 ? serverHost : $"{serverHost}:{serverPort}";
        return
        [
            "--quickPlayMultiplayer", address,
            "--server", serverHost,
            "--port", serverPort.ToString(CultureInfo.InvariantCulture),
        ];
    }

    /// <summary>快速进入存档（需 Minecraft 1.20+，旧版本会忽略）。</summary>
    public static IReadOnlyList<string> BuildWorldArguments(string worldName)
    {
        return string.IsNullOrWhiteSpace(worldName) ? [] : ["--quickPlaySingleplayer", worldName];
    }

    /// <summary>离线用户名是否合法（1–16 位，只含英文字母、数字和下划线）。</summary>
    public static bool IsValidOfflineUsername(string username)
    {
        return OfflineUsernamePattern.IsMatch((username ?? string.Empty).Trim());
    }

    /// <summary>把 UUID 归一化为 32 位无连字符格式。</summary>
    public static string CompactUuid(string uuid)
    {
        return (uuid ?? string.Empty).Trim().Replace("-", string.Empty, StringComparison.Ordinal);
    }
}

/// <summary>可供 Minecraft 启动使用的统一账号抽象。</summary>
public interface IMinecraftAccount
{
    /// <summary>offline / microsoft / authlib。</summary>
    string Kind { get; }

    string Username { get; }
    string Uuid { get; }
    string AccessToken { get; }

    /// <summary>msa / legacy。</summary>
    string UserType { get; }

    string XboxUserId { get; }

    string ClientId { get; }
}

/// <summary>不包含任何访问令牌的离线 Minecraft 账号。</summary>
public sealed class OfflineAccount : IMinecraftAccount
{
    private OfflineAccount(string username, string uuid)
    {
        Username = username;
        Uuid = uuid;
    }

    public string Kind => "offline";
    public string Username { get; }
    public string Uuid { get; }
    public string AccessToken => "0";
    public string UserType => "legacy";
    public string XboxUserId => string.Empty;
    public string ClientId => string.Empty;

    /// <summary>构造离线账号；用户名非法时抛错。</summary>
    public static OfflineAccount Create(string username)
    {
        string normalized = (username ?? string.Empty).Trim();
        if (!GameLaunch.IsValidOfflineUsername(normalized))
        {
            throw new MinecraftLaunchException(
                $"离线用户名必须为 1–16 位，只能包含英文字母、数字和下划线。（输入：{username}）");
        }

        return new OfflineAccount(normalized, OfflineUuid(normalized));
    }

    /// <summary>Java UUID.nameUUIDFromBytes("OfflinePlayer:" + name) 语义：MD5 + RFC 4122 位。</summary>
    private static string OfflineUuid(string username)
    {
        byte[] hash = System.Security.Cryptography.MD5.HashData(
            Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return Convert.ToHexStringLower(hash);
    }
}

/// <summary>正版（Microsoft）账号的启动视图。</summary>
public sealed class MicrosoftLaunchAccount : IMinecraftAccount
{
    public string Username { get; set; } = string.Empty;
    public string Uuid { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string XboxUserId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }

    public string Kind => "microsoft";
    public string UserType => "msa";
    public bool IsExpired => ExpiresAt != default && DateTimeOffset.UtcNow > ExpiresAt.AddMinutes(-5);

    /// <summary>由认证器产出的账号转换而来。</summary>
    public static MicrosoftLaunchAccount From(MicrosoftAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return new MicrosoftLaunchAccount
        {
            Username = account.Username,
            Uuid = GameLaunch.CompactUuid(account.Uuid),
            AccessToken = account.AccessToken,
            XboxUserId = account.XboxUserId,
            ClientId = account.ClientId,
            ExpiresAt = account.ExpiresAt,
        };
    }
}

/// <summary>皮肤站（authlib-injector）账号的启动视图。</summary>
public sealed class AuthlibLaunchAccount : IMinecraftAccount
{
    public string ProfileName { get; set; } = string.Empty;
    public string ProfileUuid { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>皮肤站 Yggdrasil API 根地址（javaagent 注入目标）。</summary>
    public string ApiRoot { get; set; } = string.Empty;

    public string Kind => "authlib";
    public string Username => ProfileName;
    public string Uuid => ProfileUuid;
    public string UserType => "msa";
    public string XboxUserId => string.Empty;
    public string ClientId => string.Empty;
}

/// <summary>Minecraft 启动失败时抛出的错误。</summary>
public sealed class MinecraftLaunchException : Exception
{
    public MinecraftLaunchException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>离线/正版启动所需的外部配置。版本文件、依赖库和资源应已存在于游戏目录。</summary>
public sealed class MinecraftLaunchOptions
{
    /// <summary>Minecraft 根目录（.minecraft）。</summary>
    public string MinecraftDirectory { get; set; } = string.Empty;

    /// <summary>可选的实例游戏目录，用于隔离 mods/config/saves；为空时使用 MinecraftDirectory。</summary>
    public string GameDirectory { get; set; } = string.Empty;

    /// <summary>要启动的版本 ID。</summary>
    public string VersionId { get; set; } = string.Empty;

    /// <summary>启动使用的账号（离线 / 正版 / 皮肤站）。</summary>
    public IMinecraftAccount? Account { get; set; }

    /// <summary>可选的 Java 可执行文件；为空时依次检查 MOFUCORE_JAVA、JAVA_HOME 和 PATH。</summary>
    public string JavaExecutable { get; set; } = string.Empty;

    /// <summary>可选的 Minecraft runtime 根目录；会递归查找并选择版本要求的 Java。</summary>
    public string JavaRuntimeDirectory { get; set; } = string.Empty;

    public int MinimumMemoryMb { get; set; } = 512;
    public int MaximumMemoryMb { get; set; } = 4096;
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public string LauncherName { get; set; } = "Mofumofu";
    public string LauncherVersion { get; set; } = "Mofumofu/0.1.0";
    public IList<string> AdditionalJvmArguments { get; set; } = new List<string>();
    public IList<string> AdditionalGameArguments { get; set; } = new List<string>();

    /// <summary>游戏进程优先级（low/belownormal/abovenormal/high，空串与 normal 表示不调整）。</summary>
    public string ProcessPriority { get; set; } = string.Empty;

    /// <summary>包装命令模板（含 %command% 占位）；空串表示直接启动 Java。</summary>
    public string WrapperCommand { get; set; } = string.Empty;

    /// <summary>注入游戏进程的额外环境变量。</summary>
    public IDictionary<string, string> EnvironmentVariables { get; set; } = new Dictionary<string, string>();

    /// <summary>以全屏启动（由调用方转成 --fullscreen 游戏参数，此字段仅记录）。</summary>
    public bool LaunchFullscreen { get; set; }

    /// <summary>启动过程中的文本日志回调（可为空）。</summary>
    public Action<string>? LogCallback { get; set; }

    /// <summary>游戏进程 stdout/stderr 行回调；第二个参数为是否 stderr（可为空）。</summary>
    public Action<string, bool>? GameOutputCallback { get; set; }

    /// <summary>返回一个除账号外其余配置完全相同的副本。</summary>
    public MinecraftLaunchOptions WithAccount(IMinecraftAccount account)
    {
        MinecraftLaunchOptions copy = (MinecraftLaunchOptions)MemberwiseClone();
        copy.Account = account;
        return copy;
    }
}

/// <summary>Minecraft 实例启动后的结果。</summary>
public sealed class MinecraftLaunchResult
{
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal MinecraftLaunchResult(
        Process process,
        string versionId,
        string username,
        int? requiredJavaMajorVersion,
        string nativeDirectory)
    {
        Process = process;
        VersionId = versionId;
        Username = username;
        RequiredJavaMajorVersion = requiredJavaMajorVersion;
        NativeDirectory = nativeDirectory;
    }

    public Process Process { get; }

    public string VersionId { get; }

    public string Username { get; }

    public int? RequiredJavaMajorVersion { get; }

    /// <summary>本次解压出来的 natives 目录（退出后自动清理）。</summary>
    public string NativeDirectory { get; }

    /// <summary>是否由用户手动停止（Kill 的退出码非 0，不能当异常报）。</summary>
    public bool StoppedManually { get; set; }

    public int ExitCode { get; private set; } = int.MinValue;

    public bool HasExited => _exit.Task.IsCompleted;

    public bool IsRunning => !HasExited && !Process.HasExited;

    public int Pid => Process.Id;

    /// <summary>等待进程退出，返回退出码。</summary>
    public Task<int> WaitForExitAsync() => _exit.Task;

    /// <summary>结束游戏进程树。</summary>
    public void Kill()
    {
        StoppedManually = true;
        ProcessPriority.ForceKillTree(Process);
    }

    internal void AttachOutput(Action<string, bool>? callback)
    {
        if (callback is null)
        {
            // 没有回调也要持续排空管道，避免子进程写满缓冲区阻塞
            Process.OutputDataReceived += (_, _) => { };
            Process.ErrorDataReceived += (_, _) => { };
        }
        else
        {
            Process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    callback(e.Data, false);
                }
            };
            Process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    callback(e.Data, true);
                }
            };
        }

        Process.BeginOutputReadLine();
        Process.BeginErrorReadLine();
    }

    internal void StartExitMonitor()
    {
        _ = Task.Run(async () =>
        {
            int exitCode;
            try
            {
                await Process.WaitForExitAsync().ConfigureAwait(false);
                exitCode = Process.ExitCode;
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                exitCode = -1;
            }
            finally
            {
                LibraryResolver.TryDeleteDirectory(NativeDirectory);
            }

            ExitCode = exitCode;
            _exit.TrySetResult(exitCode);
        });
    }
}

/// <summary>启动参数装配器。</summary>
public sealed class MinecraftArgumentBuilder
{
    private static readonly Regex PlaceholderPattern = new(@"\$\{([^}]+)\}", RegexOptions.Compiled);

    // 允许解析为空串的占位符：解析为空时连同前面的标志一起省略
    private static readonly HashSet<string> NullablePlaceholders = new(StringComparer.Ordinal)
    {
        "clientid", "auth_xuid", "user_type",
    };

    /// <summary>生成最终 Java 命令行参数。</summary>
    public IReadOnlyList<string> Build(
        LaunchProfile profile,
        MinecraftLaunchOptions options,
        IMinecraftAccount account,
        string nativeDirectory,
        IReadOnlyList<string> classpath,
        string mainClass,
        IReadOnlyList<string>? additionalJvmArguments = null)
    {
        string minecraftDirectory = Path.GetFullPath(options.MinecraftDirectory);
        string gameDirectory = string.IsNullOrWhiteSpace(options.GameDirectory)
            ? minecraftDirectory
            : Path.GetFullPath(options.GameDirectory);
        string assetsDirectory = Path.Combine(minecraftDirectory, "assets");
        string librariesDirectory = Path.Combine(minecraftDirectory, "libraries");
        string gameAssetsDirectory = GetLegacyGameAssetsDirectory(assetsDirectory, profile.AssetsId);
        string logConfigPath = string.IsNullOrWhiteSpace(profile.LoggingFileId)
            ? string.Empty
            : Path.Combine(assetsDirectory, "log_configs", profile.LoggingFileId);
        string classpathValue = string.Join(Path.PathSeparator, classpath);

        string versionName = profile.ClientJarVersionId;
        if (string.IsNullOrWhiteSpace(versionName))
        {
            versionName = profile.SourceId;
        }

        if (string.IsNullOrWhiteSpace(versionName))
        {
            versionName = profile.Id;
        }

        (string authPlayerName, string authUuid, string authAccessToken, string authSession, string clientId,
            string authXuid, string userType) = ResolveAccountArguments(account);

        Dictionary<string, string> placeholders = new(StringComparer.Ordinal)
        {
            ["auth_player_name"] = authPlayerName,
            ["version_name"] = versionName,
            ["game_directory"] = gameDirectory,
            ["assets_root"] = assetsDirectory,
            ["assets_index_name"] = profile.AssetsId,
            ["auth_uuid"] = authUuid,
            ["auth_access_token"] = authAccessToken,
            ["auth_session"] = authSession,
            ["clientid"] = clientId,
            ["auth_xuid"] = authXuid,
            ["user_type"] = userType,
            ["version_type"] = profile.VersionType,
            ["user_properties"] = "{}",
            ["profile_properties"] = "{}",
            ["game_assets"] = gameAssetsDirectory,
            ["natives_directory"] = nativeDirectory,
            ["launcher_name"] = options.LauncherName,
            ["launcher_version"] = options.LauncherVersion,
            ["classpath"] = classpathValue,
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["library_directory"] = librariesDirectory,
            ["resolution_width"] = options.WindowWidth.ToString(CultureInfo.InvariantCulture),
            ["resolution_height"] = options.WindowHeight.ToString(CultureInfo.InvariantCulture),
            ["path"] = logConfigPath,
            ["primary_jar_name"] = versionName + ".jar",
        };

        Dictionary<string, bool> features = MinecraftRuleEvaluator.CreateDefaultFeatures(
            options.WindowWidth > 0 && options.WindowHeight > 0);
        IEnumerable<string> userJvmArguments =
            additionalJvmArguments is not null ? additionalJvmArguments : options.AdditionalJvmArguments;
        bool userSpecifiesXms = ContainsMemoryArgument(userJvmArguments, "-Xms");
        bool userSpecifiesXmx = ContainsMemoryArgument(userJvmArguments, "-Xmx");

        List<string> result = [];

        // 日志配置：版本 JSON 的 logging.client.argument 由启动器补进 JVM 参数
        if (!string.IsNullOrWhiteSpace(profile.LoggingArgument) &&
            (logConfigPath.Length == 0 || File.Exists(logConfigPath)))
        {
            string resolved = ReplacePlaceholders(profile.LoggingArgument, placeholders);
            if (resolved.Length > 0)
            {
                result.Add(resolved);
            }
        }

        if (!userSpecifiesXms)
        {
            result.Add($"-Xms{options.MinimumMemoryMb}M");
        }

        if (!userSpecifiesXmx)
        {
            result.Add($"-Xmx{options.MaximumMemoryMb}M");
        }

        result.AddRange(BuildTuningArguments(options.MaximumMemoryMb));
        result.AddRange(userJvmArguments);

        int launcherJvmArgsStart = result.Count;
        if (profile.JvmArguments.Count > 0)
        {
            AppendModernArguments(result, profile.JvmArguments, features, placeholders);
        }
        else
        {
            result.Add("-Djava.library.path=" + nativeDirectory);
            result.Add("-cp");
            result.Add(classpathValue);
        }

        // NeoForge / Forge 的 FML 要求 system property libraryDirectory 指向 libraries
        if (!result.Any(argument => argument.StartsWith("-DlibraryDirectory=", StringComparison.Ordinal)))
        {
            result.Add("-DlibraryDirectory=" + librariesDirectory);
        }

        // classpath 探测只看启动器与版本档案生成的参数段
        if (!ContainsClasspathArgument(result.Skip(launcherJvmArgsStart).ToList()))
        {
            result.Add("-cp");
            result.Add(classpathValue);
        }

        result.Add(mainClass);

        if (profile.GameArguments.Count > 0)
        {
            AppendModernArguments(result, profile.GameArguments, features, placeholders);
        }
        else if (!string.IsNullOrWhiteSpace(profile.LegacyGameArguments))
        {
            foreach (string argument in TokenizeLegacyArguments(profile.LegacyGameArguments))
            {
                AppendResolvedArgument(result, argument, ReplacePlaceholders(argument, placeholders));
            }
        }
        else
        {
            throw new MinecraftLaunchException("版本配置没有可用的游戏启动参数。");
        }

        foreach (string argument in options.AdditionalGameArguments)
        {
            AppendResolvedArgument(result, argument, ReplacePlaceholders(argument, placeholders));
        }

        return result;
    }

    private static (string PlayerName, string Uuid, string AccessToken, string Session, string ClientId, string Xuid,
        string UserType) ResolveAccountArguments(IMinecraftAccount account)
    {
        string uuid = GameLaunch.CompactUuid(account.Uuid);
        return account.Kind switch
        {
            "offline" => (account.Username, uuid, "0", "token:0:" + uuid, string.Empty, string.Empty, "legacy"),
            "microsoft" => (account.Username, uuid, account.AccessToken,
                $"token:{account.AccessToken}:{uuid}", account.ClientId, account.XboxUserId, "msa"),
            "authlib" => (account.Username, uuid, account.AccessToken,
                $"token:{account.AccessToken}:{uuid}", string.Empty, string.Empty, account.UserType),
            _ => throw new MinecraftLaunchException($"不支持的账号类型：{account.Kind}"),
        };
    }

    /// <summary>通用 JVM 性能优化参数（针对 Minecraft 工作负载特性优化）。</summary>
    private static IReadOnlyList<string> BuildTuningArguments(int maximumMemoryMb)
    {
        List<string> arguments =
        [
            "-XX:+IgnoreUnrecognizedVMOptions",
            "-XX:+UnlockExperimentalVMOptions",
            "-XX:+UseG1GC",
            "-XX:G1NewSizePercent=20",
            "-XX:G1MaxNewSizePercent=60",
            "-XX:G1ReservePercent=20",
            "-XX:MaxGCPauseMillis=50",
            "-XX:G1HeapWastePercent=5",
            "-XX:G1MixedGCCountTarget=4",
            "-XX:G1MixedGCLiveThresholdPercent=90",
            "-XX:+ParallelRefProcEnabled",
            "-XX:+PerfDisableSharedMem",
            "-XX:+DisableExplicitGC",
            "-XX:+AlwaysActAsServerClassMachine",
            "-XX:-OmitStackTraceInFastThrow",
            "-XX:MaxInlineLevel=15",
        ];

        if (maximumMemoryMb >= 4096)
        {
            arguments.Add("-XX:+UseStringDeduplication");
            arguments.Add("-XX:StringDeduplicationAgeThreshold=1");
        }

        if (maximumMemoryMb >= 8192)
        {
            arguments.Add("-XX:+AlwaysPreTouch");
            arguments.Add("-XX:G1HeapRegionSize=32M");
            arguments.Add("-XX:InitiatingHeapOccupancyPercent=15");
        }
        else
        {
            arguments.Add("-XX:InitiatingHeapOccupancyPercent=40");
        }

        if (maximumMemoryMb >= 16384)
        {
            arguments.Add("-XX:G1NewSizePercent=30");
            arguments.Add("-XX:G1MaxNewSizePercent=50");
            arguments.Add("-XX:SurvivorRatio=32");
            arguments.Add("-XX:MaxTenuringThreshold=1");
        }

        return arguments;
    }

    private static bool ContainsMemoryArgument(IEnumerable<string> arguments, string prefix)
    {
        return arguments.Any(argument =>
            argument.Length >= prefix.Length &&
            string.Equals(argument[..prefix.Length], prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsClasspathArgument(IReadOnlyList<string> arguments)
    {
        for (int index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index] is "-cp" or "-classpath")
            {
                return true;
            }
        }

        return false;
    }

    private static string GetLegacyGameAssetsDirectory(string assetsDirectory, string assetsId)
    {
        string virtualDirectory = Path.Combine(assetsDirectory, "virtual", assetsId);
        return Directory.Exists(virtualDirectory) ? virtualDirectory : assetsDirectory;
    }

    private static void AppendModernArguments(
        List<string> target,
        IReadOnlyList<JsonElement> elements,
        IReadOnlyDictionary<string, bool> features,
        IReadOnlyDictionary<string, string> placeholders)
    {
        foreach (JsonElement element in elements)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                string text = element.GetString() ?? string.Empty;
                AppendResolvedArgument(target, text, ReplacePlaceholders(text, placeholders));
                continue;
            }

            if (element.ValueKind != JsonValueKind.Object || !MinecraftRuleEvaluator.IsAllowed(element, features))
            {
                continue;
            }

            if (!element.TryGetProperty("value", out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                string text = value.GetString() ?? string.Empty;
                AppendResolvedArgument(target, text, ReplacePlaceholders(text, placeholders));
                continue;
            }

            if (value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    string text = item.GetString() ?? string.Empty;
                    AppendResolvedArgument(target, text, ReplacePlaceholders(text, placeholders));
                }
            }
        }
    }

    /// <summary>
    /// 追加一个已解析参数；可空占位符解析为空时，连同刚追加的标志一起省略，
    /// 避免留下悬空空参数或失去取值的标志（离线账号的 ${clientid}）。
    /// </summary>
    private static void AppendResolvedArgument(List<string> target, string source, string resolved)
    {
        if (resolved.Length > 0 || !IsNullablePlaceholder(source))
        {
            target.Add(resolved);
            return;
        }

        if (target.Count > 0 && IsFlagArgument(target[^1]))
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    private static bool IsNullablePlaceholder(string source)
    {
        MatchCollection matches = PlaceholderPattern.Matches(source);
        return matches.Count == 1 &&
               source.Trim() == matches[0].Value &&
               NullablePlaceholders.Contains(matches[0].Groups[1].Value);
    }

    private static bool IsFlagArgument(string argument)
    {
        return argument.Length > 1 && argument[0] == '-';
    }

    private static string ReplacePlaceholders(string value, IReadOnlyDictionary<string, string> placeholders)
    {
        string? failure = null;
        string result = PlaceholderPattern.Replace(value, match =>
        {
            string name = match.Groups[1].Value;
            if (placeholders.TryGetValue(name, out string? replacement))
            {
                return replacement;
            }

            failure ??= name;
            return match.Value;
        });

        return failure is null
            ? result
            : throw new MinecraftLaunchException($"版本参数包含暂不支持的占位符：{failure}");
    }

    /// <summary>切分旧版 minecraftArguments（支持双引号与 \" 转义）。</summary>
    private static IReadOnlyList<string> TokenizeLegacyArguments(string commandLine)
    {
        List<string> result = [];
        StringBuilder current = new();
        bool quoted = false;
        for (int index = 0; index < commandLine.Length; index++)
        {
            char character = commandLine[index];
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (character == '\\' && index + 1 < commandLine.Length && commandLine[index + 1] == '"')
            {
                current.Append('"');
                index++;
                continue;
            }

            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (quoted)
        {
            throw new MinecraftLaunchException("旧版启动参数包含未闭合的引号。");
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }
}

/// <summary>Java 运行时定位器：找可执行文件并校验主版本。</summary>
public sealed class JavaRuntimeLocator
{
    private static readonly Regex JavaVersionPattern =
        new("version\\s+\"(\\d+)(?:\\.(\\d+))?", RegexOptions.Compiled);

    /// <summary>
    /// 查找符合要求的 Java：显式配置 → MOFUCORE_JAVA → runtime 目录 → JAVA_HOME → PATH，
    /// 优先主版本精确匹配，其次满足最低要求的最低版本。
    /// </summary>
    public async Task<string> FindJavaExecutableAsync(
        string configuredPath,
        int? requiredMajorVersion,
        string runtimeDirectory,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        List<(string Path, bool Preferred)> candidates = BuildCandidates(configuredPath, runtimeDirectory);
        List<(string Path, int MajorVersion, int Index, bool Preferred)> probed = [];
        HashSet<string> visitedPaths = new(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string path, bool preferred) = candidates[index];
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (expanded.Length == 0 || !visitedPaths.Add(expanded) || !File.Exists(expanded))
            {
                continue;
            }

            string fullPath = Path.GetFullPath(expanded);
            if (requiredMajorVersion is null)
            {
                return fullPath;
            }

            int? majorVersion = await TryGetJavaMajorVersionAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (majorVersion is not null)
            {
                probed.Add((fullPath, majorVersion.Value, index, preferred));
            }
        }

        if (requiredMajorVersion is null)
        {
            throw new MinecraftLaunchException(
                "未找到可用的 Java 运行时。请打开启动器「设置 → Java 运行时」，点「自动检索」扫描本机，或「添加」选择 java.exe。" +
                "（高级：也可配置 JAVA_HOME / MOFUCORE_JAVA 环境变量）");
        }

        int required = requiredMajorVersion.Value;
        if (probed.Count == 0)
        {
            throw new MinecraftLaunchException(
                $"启动该版本需要 Java {required}，但本机没有检测到任何 Java。" +
                "请打开启动器「设置 → Java 运行时」，点「自动检索」或「添加」选择 java.exe；" +
                "保持联网时启动器也会在启动前自动下载所需运行时。（高级：也可配置 JAVA_HOME / MOFUCORE_JAVA）");
        }

        // 主版本精确匹配：避免用 Java 25 启动要求 Java 17 的旧加载器
        foreach ((string path, int majorVersion, int _, bool preferred) in probed
                     .OrderBy(item => item.MajorVersion)
                     .ThenByDescending(item => item.Preferred)
                     .ThenBy(item => item.Index))
        {
            if (majorVersion == required)
            {
                return path;
            }
        }

        // 显式配置且 ≥ 最低要求
        foreach ((string path, int majorVersion, int _, bool preferred) in probed)
        {
            if (preferred && majorVersion >= required)
            {
                return path;
            }
        }

        // 自动探测中最低且 ≥ 最低要求
        foreach ((string path, int majorVersion, int _, bool _) in probed.OrderBy(item => item.MajorVersion))
        {
            if (majorVersion >= required)
            {
                return path;
            }
        }

        string detected = string.Join("、", probed.Select(item => item.MajorVersion).Distinct().OrderBy(value => value));
        string message = $"启动该版本需要 Java {required}，但本机只检测到：Java {detected}。" +
                         $"请打开启动器「设置 → Java 运行时」添加一个 Java {required}；" +
                         "若本机确实没有，检查网络后重新启动，启动器会尝试自动下载所需运行时。" +
                         "（高级：也可配置 JAVA_HOME / MOFUCORE_JAVA）";
        log?.Invoke(message);
        throw new MinecraftLaunchException(message);
    }

    /// <summary>探测指定 java 可执行文件的主版本号；无法执行或解析失败时返回 null。</summary>
    public static Task<int?> TryGetJavaMajorVersionAsync(string javaExecutable,
        CancellationToken cancellationToken = default)
    {
        return ProbeVersionAsync(javaExecutable, cancellationToken);
    }

    private static async Task<int?> ProbeVersionAsync(string javaExecutable, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            ProcessStartInfo startInfo = new(javaExecutable, "-version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using Process process = Process.Start(startInfo) ?? throw new System.ComponentModel.Win32Exception();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string output = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
            Match match = JavaVersionPattern.Match(output);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out int major))
            {
                return null;
            }

            // 旧式版本号（如 1.8.0_202）的主版本是次版本号 8
            if (major == 1 && int.TryParse(match.Groups[2].Value, out int minor))
            {
                return minor;
            }

            return major;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static List<(string Path, bool Preferred)> BuildCandidates(string configuredPath, string runtimeDirectory)
    {
        List<(string Path, bool Preferred)> candidates = [];
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidates.Add((configuredPath, true));
        }

        string? environmentJava = Environment.GetEnvironmentVariable("MOFUCORE_JAVA");
        if (!string.IsNullOrWhiteSpace(environmentJava))
        {
            candidates.Add((environmentJava!, true));
        }

        string[] names = OperatingSystem.IsWindows() ? ["java.exe", "javaw.exe"] : ["java"];
        string configuredRuntime = string.IsNullOrWhiteSpace(runtimeDirectory)
            ? Environment.GetEnvironmentVariable("MOFUCORE_JAVA_RUNTIME") ?? string.Empty
            : runtimeDirectory;
        if (!string.IsNullOrWhiteSpace(configuredRuntime) && Directory.Exists(configuredRuntime))
        {
            foreach (string name in names)
            {
                try
                {
                    foreach (string path in Directory.EnumerateFiles(configuredRuntime, name,
                                 SearchOption.AllDirectories))
                    {
                        candidates.Add((path, false));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // 扫描失败忽略
                }
            }
        }

        string? javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            foreach (string name in names)
            {
                candidates.Add((Path.Combine(javaHome!, "bin", name), false));
            }
        }

        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (string directory in pathValue!.Split(Path.PathSeparator))
            {
                string trimmed = directory.Trim().Trim('"');
                if (trimmed.Length == 0)
                {
                    continue;
                }

                foreach (string name in names)
                {
                    candidates.Add((Path.Combine(trimmed, name), false));
                }
            }
        }

        return candidates;
    }
}

/// <summary>Windows 进程优先级 / 进程树强杀。</summary>
public static class ProcessPriority
{
    private const int ProcessSetInformation = 0x0200;

    private const int IdlePriorityClass = 0x00000040;
    private const int BelowNormalPriorityClass = 0x00004000;
    private const int AboveNormalPriorityClass = 0x00008000;
    private const int HighPriorityClass = 0x00000080;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetPriorityClass(IntPtr processHandle, int priorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>按配置调整进程优先级；priority 为空或 "normal" 时不调整，失败只记日志。</summary>
    public static void Apply(Process process, string? priority, Action<string>? log)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(priority))
        {
            return;
        }

        int? priorityClass = priority.Trim().ToLowerInvariant() switch
        {
            "low" => IdlePriorityClass,
            "belownormal" => BelowNormalPriorityClass,
            "abovenormal" => AboveNormalPriorityClass,
            "high" => HighPriorityClass,
            _ => null,
        };
        if (priorityClass is null)
        {
            return;
        }

        IntPtr handle = OpenProcess(ProcessSetInformation, false, process.Id);
        if (handle == IntPtr.Zero)
        {
            log?.Invoke("设置进程优先级失败（无法打开进程句柄）。");
            return;
        }

        try
        {
            if (!SetPriorityClass(handle, priorityClass.Value))
            {
                log?.Invoke($"设置进程优先级失败（Win32 错误 {Marshal.GetLastWin32Error()}）。");
                return;
            }

            log?.Invoke($"已设置游戏进程优先级：{priority}。");
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    /// <summary>整棵进程树强杀：只杀主进程会留下孤儿 Java 进程。</summary>
    public static void ForceKillTree(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                ProcessStartInfo startInfo = new("taskkill.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                };
                startInfo.ArgumentList.Add("/PID");
                startInfo.ArgumentList.Add(process.Id.ToString(CultureInfo.InvariantCulture));
                startInfo.ArgumentList.Add("/T");
                startInfo.ArgumentList.Add("/F");
                using Process? killer = Process.Start(startInfo);
                killer?.WaitForExit(5000);
                return;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // 退回主进程 Kill
            }
        }

        try
        {
            process.Kill(true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 进程已退出
        }
    }
}

/// <summary>系统语言（BCP-47）→ Minecraft 语言码（options.txt 的 lang 值）映射。</summary>
public static class MinecraftLanguage
{
    private static readonly HashSet<string> KnownCodes = new(StringComparer.Ordinal)
    {
        "ar_sa", "az_az", "bg_bg", "ca_es", "cs_cz", "da_dk", "de_at", "de_ch", "de_de", "el_gr",
        "en_au", "en_ca", "en_gb", "en_nz", "en_us", "es_ar", "es_cl", "es_ec", "es_es", "es_mx",
        "es_uy", "es_ve", "fi_fi", "fr_ca", "fr_fr", "he_il", "hi_in", "hr_hr", "hu_hu", "id_id",
        "is_is", "it_it", "ja_jp", "ko_kr", "nl_nl", "nn_no", "no_no", "pl_pl", "pt_br", "pt_pt",
        "ro_ro", "ru_ru", "sk_sk", "sl_si", "sr_sp", "sv_se", "th_th", "tr_tr", "uk_ua", "vi_vn",
        "zh_cn", "zh_hk", "zh_tw",
    };

    private static readonly Dictionary<string, string> DefaultRegions = new(StringComparer.Ordinal)
    {
        ["ar"] = "sa", ["az"] = "az", ["bg"] = "bg", ["ca"] = "es", ["cs"] = "cz", ["da"] = "dk",
        ["de"] = "de", ["el"] = "gr", ["en"] = "us", ["es"] = "es", ["fi"] = "fi", ["fr"] = "fr",
        ["he"] = "il", ["hi"] = "in", ["hr"] = "hr", ["hu"] = "hu", ["id"] = "id", ["is"] = "is",
        ["it"] = "it", ["ja"] = "jp", ["ko"] = "kr", ["nl"] = "nl", ["no"] = "no", ["nn"] = "no",
        ["pl"] = "pl", ["pt"] = "br", ["ro"] = "ro", ["ru"] = "ru", ["sk"] = "sk", ["sl"] = "si",
        ["sr"] = "sp", ["sv"] = "se", ["th"] = "th", ["tr"] = "tr", ["uk"] = "ua", ["vi"] = "vn",
        ["zh"] = "cn",
    };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetUserDefaultLocaleName(char[] localeName, int localeNameLength);

    /// <summary>读取系统区域名（BCP-47，如 zh-CN / en-US）；失败返回空串。</summary>
    public static string ReadSystemLocale()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                char[] buffer = new char[85]; // LOCALE_NAME_MAX_LENGTH
                int length = GetUserDefaultLocaleName(buffer, buffer.Length);
                if (length > 0)
                {
                    return new string(buffer, 0, length - 1).Trim();
                }
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                // 退回 CultureInfo
            }
        }

        string name = CultureInfo.CurrentUICulture.Name;
        return name.Length > 0 ? name : string.Empty;
    }

    /// <summary>系统语言对应的 Minecraft 语言码；无法识别返回空串。</summary>
    public static string SystemLanguageCode()
    {
        return CodeForLocale(ReadSystemLocale());
    }

    /// <summary>把 BCP-47 区域名映射成 Minecraft 语言码。</summary>
    public static string CodeForLocale(string locale)
    {
        string normalized = (locale ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-');
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        (string lang, string script, string region) = ParseBcp47(normalized);
        if (lang.Length == 0)
        {
            return string.Empty;
        }

        // 中文按文字体系区分：简体 zh_cn，繁体 zh_tw（含 zh-hant，无区域时），港澳使用 zh_hk
        if (lang == "zh")
        {
            if (region is "hk" or "mo")
            {
                return "zh_hk";
            }

            return script == "hant" || region == "tw" ? "zh_tw" : "zh_cn";
        }

        if (region.Length == 0)
        {
            if (!DefaultRegions.TryGetValue(lang, out string? defaultRegion))
            {
                return string.Empty;
            }

            region = defaultRegion;
        }

        string code = lang + "_" + region;
        if (!KnownCodes.Contains(code))
        {
            // 系统 region 对应的语言文件 MC 没有时回落默认区域
            code = lang + "_" + DefaultRegions.GetValueOrDefault(lang, string.Empty);
            if (!KnownCodes.Contains(code))
            {
                return string.Empty;
            }
        }

        return code;
    }

    private static (string Lang, string Script, string Region) ParseBcp47(string locale)
    {
        string lang = string.Empty;
        string script = string.Empty;
        string region = string.Empty;
        foreach (string part in locale.Split('-'))
        {
            if (part.Length == 0)
            {
                continue;
            }

            if (part.Length == 4 && script.Length == 0)
            {
                // 文字体系标签固定 4 字母（hans / hant / cyrl…）
                script = part;
            }
            else if (part.Length == 2 && region.Length == 0 && lang.Length > 0)
            {
                region = part;
            }
            else if (lang.Length == 0)
            {
                lang = part;
            }
        }

        return (lang, script, region);
    }

    /// <summary>判断 options.txt 内容里是否已有某个键。</summary>
    public static bool HasOptionsKey(string content, string key)
    {
        string prefix = key + ":";
        return content.Split('\n').Any(line => line.Trim().StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>写入（或替换）options.txt 的一个键，保留其余行原样。</summary>
    public static string UpsertOptionsKey(string content, string key, string value)
    {
        string entry = key + ":" + value;
        string[] lines = content.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            if (lines[index].Trim().StartsWith(key + ":", StringComparison.Ordinal))
            {
                lines[index] = entry;
                return string.Join("\n", lines);
            }
        }

        if (content.Length > 0 && !content.EndsWith('\n'))
        {
            content += "\n";
        }

        return content + entry + "\n";
    }
}

/// <summary>
/// 崩溃诊断：把"游戏非零退出"从一句提示变成可执行的结论。
/// 旧的内建正则规则已并入 <see cref="Log.LogAnalysis"/>（模组冲突提取、hs_err_pid 解析、
/// Minecraft Wiki「Crash」条目列出的全部崩溃成因类别），这里只做结果形状转换。
/// </summary>
public sealed class CrashDiagnosis
{
    /// <summary>最新崩溃报告路径；没找到为空串。</summary>
    public string ReportPath { get; set; } = string.Empty;

    /// <summary>崩溃报告里的 Description 字段。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>崩溃报告里的首个异常行（含 Caused by 链首条）。</summary>
    public string Exception { get; set; } = string.Empty;

    /// <summary>命中的原因（中文原文，可能是多条）。</summary>
    public IReadOnlyList<string> Suspected { get; set; } = [];

    /// <summary>与 Suspected 一一对应的处置建议（中文原文）。</summary>
    public IReadOnlyList<string> Suggestions { get; set; } = [];

    /// <summary>一句话结论，直接作为弹窗正文。</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>从崩溃报告/日志里摘出的具体报错行（原始文本）。</summary>
    public IReadOnlyList<string> Details { get; set; } = [];

    /// <summary>诊断最近一次崩溃。</summary>
    public static CrashDiagnosis Diagnose(CrashDiagnosisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        LogAnalysisResult analysis = LogAnalysis.Analyze(new LogAnalysisInput
        {
            LogText = input.LogText,
            CrashReportsDirectory = input.CrashReportsDirectory,
            MinecraftVersion = input.MinecraftVersion,
            JavaVersion = input.JavaVersion,
            MemoryMb = input.MemoryMb,
        });

        List<string> details = analysis.Findings
            .SelectMany(finding => finding.Evidence)
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToList();

        CrashDiagnosis diagnosis = new()
        {
            ReportPath = analysis.CrashReportPath,
            Description = analysis.CrashDescription,
            Exception = analysis.CrashException,
            Suspected = analysis.Findings.Select(finding => finding.Title).ToList(),
            Suggestions = analysis.Findings
                .Select(finding => string.Join("；", finding.Decisions))
                .ToList(),
            Details = details,
        };
        diagnosis.Summary = analysis.Summary;
        return diagnosis;
    }
}

/// <summary>崩溃诊断输入（由绑定层采集）。</summary>
public sealed class CrashDiagnosisInput
{
    /// <summary>实例游戏目录下的 crash-reports。</summary>
    public string CrashReportsDirectory { get; set; } = string.Empty;

    /// <summary>启动日志全文（含游戏输出）。</summary>
    public string LogText { get; set; } = string.Empty;

    /// <summary>当前实例的版本 id（仅用于报告展示）。</summary>
    public string MinecraftVersion { get; set; } = string.Empty;

    /// <summary>当前使用的 Java 版本描述。</summary>
    public string JavaVersion { get; set; } = string.Empty;

    /// <summary>当前实例的最大内存（MiB，0 表示未知）。</summary>
    public int MemoryMb { get; set; }
}

/// <summary>扁平化结果：是否发生了合并、被合并掉的依赖版本 id 列表。</summary>
public sealed class VersionFlattenResult
{
    public bool Flattened { get; set; }
    public IReadOnlyList<string> ConsumedParentIds { get; set; } = [];
    public string ClientJarProviderId { get; set; } = string.Empty;
}

/// <summary>
/// 版本继承扁平化：把 inheritsFrom 继承链（如 Fabric 实例 → 原版）合并为单个自包含的版本 JSON，
/// 并把依赖版本的客户端 JAR 复制为实例自己的 {实例id}.jar。合并语义与
/// <see cref="VersionLoader"/> 完全一致。
/// </summary>
public sealed class MinecraftVersionJsonFlattener
{
    private static readonly string[] ScalarProperties =
    [
        "time", "releaseTime", "type", "mainClass",
        "assets", "minecraftArguments", "clientVersion", "minecraftVersion", "gameVersion",
    ];

    private static readonly string[] ObjectProperties = ["assetIndex", "javaVersion", "logging"];

    public async Task<VersionFlattenResult> FlattenAsync(
        string minecraftDirectory,
        string versionId,
        CancellationToken cancellationToken = default)
    {
        string checkedVersionId = VersionLoader.ValidateVersionId(versionId);
        List<(string Id, JsonObject Root)> chain = await LoadChainAsync(
            minecraftDirectory, checkedVersionId, cancellationToken).ConfigureAwait(false);
        if (chain.Count <= 1)
        {
            return new VersionFlattenResult();
        }

        string jarProviderId = ResolveClientJarProviderId(chain);
        string versionDirectory = Path.Combine(minecraftDirectory, "versions", checkedVersionId);
        string targetJar = Path.Combine(versionDirectory, checkedVersionId + ".jar");
        if (!File.Exists(targetJar))
        {
            if (jarProviderId.Length == 0)
            {
                throw new MinecraftLaunchException($"无法扁平化 {checkedVersionId}：继承链中没有任何版本声明客户端 JAR。");
            }

            string sourceJar = Path.Combine(minecraftDirectory, "versions", jarProviderId, jarProviderId + ".jar");
            if (!File.Exists(sourceJar))
            {
                throw new MinecraftLaunchException(
                    $"无法扁平化 {checkedVersionId}：依赖版本 {jarProviderId} 的客户端 JAR 缺失（{sourceJar}）。" +
                    "请先启动或修复该实例后重试。");
            }

            Directory.CreateDirectory(versionDirectory);
            File.Copy(sourceJar, targetJar);
        }

        JsonObject merged = MergeChain(checkedVersionId, chain);
        if (!merged.ContainsKey("clientVersion"))
        {
            // 依赖结构消失后，详情页/导出功能无法再靠 inheritsFrom 推断基础 MC 版本
            string declaredId = TryGetNodeString(chain[0].Root, "id") ?? chain[0].Id;
            merged["clientVersion"] = declaredId;
        }

        string jsonPath = Path.Combine(versionDirectory, checkedVersionId + ".json");
        string temporaryPath = jsonPath + ".mofu-flatten-tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, merged.ToJsonString(), new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, jsonPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return new VersionFlattenResult
        {
            Flattened = true,
            ClientJarProviderId = jarProviderId,
            ConsumedParentIds = chain.Take(chain.Count - 1).Select(entry => entry.Id).ToList(),
        };
    }

    /// <summary>检查指定版本是否仍被其它版本的 inheritsFrom 引用。</summary>
    public bool IsVersionReferenced(string minecraftDirectory, string versionId)
    {
        string versionsDirectory = Path.Combine(minecraftDirectory, "versions");
        if (!Directory.Exists(versionsDirectory))
        {
            return false;
        }

        try
        {
            foreach (string directory in Directory.EnumerateDirectories(versionsDirectory))
            {
                string id = Path.GetFileName(directory);
                if (id.Length == 0 || string.Equals(id, versionId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string jsonPath = Path.Combine(directory, id + ".json");
                if (!File.Exists(jsonPath))
                {
                    continue;
                }

                JsonObject? root;
                try
                {
                    root = JsonNode.Parse(File.ReadAllText(jsonPath, Encoding.UTF8)) as JsonObject;
                }
                catch (JsonException)
                {
                    return true;
                }
                catch (IOException)
                {
                    // 读取失败的版本无法证明引用关系，保守视为可能引用
                    return true;
                }

                if (root is not null &&
                    string.Equals(TryGetNodeString(root, "inheritsFrom"), versionId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }

        return false;
    }

    // ------------------------------------------------------------------
    // 继承链加载与合并
    // ------------------------------------------------------------------

    private static async Task<List<(string Id, JsonObject Root)>> LoadChainAsync(
        string minecraftDirectory,
        string versionId,
        CancellationToken cancellationToken)
    {
        List<(string Id, JsonObject Root)> chain = [];
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        string currentId = versionId;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(currentId))
            {
                throw new MinecraftLaunchException("版本继承出现循环：" + currentId);
            }

            if (chain.Count >= VersionLoader.MaximumInheritanceDepth)
            {
                throw new MinecraftLaunchException("版本继承层级过深。");
            }

            string jsonPath = VersionLoader.VersionJsonPath(minecraftDirectory, currentId);
            if (!File.Exists(jsonPath))
            {
                throw new MinecraftLaunchException("找不到版本配置：" + jsonPath);
            }

            JsonObject root;
            try
            {
                root = JsonNode.Parse(await File.ReadAllTextAsync(jsonPath, cancellationToken).ConfigureAwait(false))
                           as JsonObject
                       ?? throw new MinecraftLaunchException("版本配置不是有效 JSON：" + jsonPath);
            }
            catch (JsonException e)
            {
                throw new MinecraftLaunchException("版本配置不是有效 JSON：" + jsonPath, e);
            }

            chain.Add((currentId, root));
            string? parentId = TryGetNodeString(root, "inheritsFrom");
            if (string.IsNullOrWhiteSpace(parentId))
            {
                break;
            }

            currentId = VersionLoader.ValidateVersionId(parentId);
        }

        // 自最顶层依赖（原版）向实例方向合并
        chain.Reverse();
        return chain;
    }

    private static JsonObject MergeChain(string requestedVersionId, List<(string Id, JsonObject Root)> chain)
    {
        JsonObject merged = new();
        foreach (string propertyName in ScalarProperties)
        {
            if (LastChainValue(chain, propertyName) is JsonNode value)
            {
                merged[propertyName] = value.DeepClone();
            }
        }

        // 声明 id：链末（实例自身）声明的 id 优先，请求的目录名兜底
        string declaredId = requestedVersionId;
        foreach ((string _, JsonObject root) in chain)
        {
            string? value = TryGetNodeString(root, "id");
            if (!string.IsNullOrWhiteSpace(value))
            {
                declaredId = value;
            }
        }

        merged["id"] = declaredId;

        // logging 必须保留：log4j 配置声明只在原版 JSON 里
        foreach (string propertyName in ObjectProperties)
        {
            if (LastChainValue(chain, propertyName) is JsonNode value)
            {
                merged[propertyName] = value.DeepClone();
            }
        }

        // 客户端下载声明：沿链取最后一个含 downloads.client 的节点
        foreach ((string _, JsonObject root) in chain)
        {
            if (DownloadsClientNode(root) is JsonObject clientNode)
            {
                JsonObject downloads = new();
                downloads["client"] = clientNode.DeepClone();
                merged["downloads"] = downloads;
            }
        }

        // arguments：jvm / game 数组按父→子顺序拼接
        List<JsonNode> jvmArguments = [];
        List<JsonNode> gameArguments = [];
        bool hasArguments = false;
        foreach ((string _, JsonObject root) in chain)
        {
            if (root["arguments"] is not JsonObject arguments)
            {
                continue;
            }

            hasArguments = true;
            AppendArray(jvmArguments, arguments["jvm"]);
            AppendArray(gameArguments, arguments["game"]);
        }

        if (hasArguments)
        {
            JsonObject arguments = new();
            if (jvmArguments.Count > 0)
            {
                arguments["jvm"] = new JsonArray(jvmArguments.Select(node => (JsonNode?)node).ToArray());
            }

            if (gameArguments.Count > 0)
            {
                arguments["game"] = new JsonArray(gameArguments.Select(node => (JsonNode?)node).ToArray());
            }

            merged["arguments"] = arguments;
        }

        // libraries：按坐标键去重，子级覆盖，保持首次出现顺序
        Dictionary<string, int> libraryIndexes = new(StringComparer.Ordinal);
        List<JsonObject> libraries = [];
        foreach ((string _, JsonObject root) in chain)
        {
            if (root["libraries"] is not JsonArray chainLibraries)
            {
                continue;
            }

            foreach (JsonNode? library in chainLibraries)
            {
                if (library is not JsonObject libraryObject)
                {
                    continue;
                }

                string key = LibraryCoordinateKey(libraryObject);
                if (libraryIndexes.TryGetValue(key, out int existingIndex))
                {
                    libraries[existingIndex] = (JsonObject)libraryObject.DeepClone();
                }
                else
                {
                    libraryIndexes[key] = libraries.Count;
                    libraries.Add((JsonObject)libraryObject.DeepClone());
                }
            }
        }

        merged["libraries"] = new JsonArray(
            libraries.Select(library => (JsonNode?)library.DeepClone()).ToArray());
        return merged;
    }

    private static void AppendArray(List<JsonNode> target, JsonNode? source)
    {
        if (source is not JsonArray array)
        {
            return;
        }

        foreach (JsonNode? element in array)
        {
            if (element is not null)
            {
                target.Add(element.DeepClone());
            }
        }
    }

    private static JsonNode? LastChainValue(List<(string Id, JsonObject Root)> chain, string propertyName)
    {
        JsonNode? last = null;
        foreach ((string _, JsonObject root) in chain)
        {
            if (root[propertyName] is JsonNode value)
            {
                last = value.DeepClone();
            }
        }

        return last;
    }

    private static JsonObject? DownloadsClientNode(JsonObject root)
    {
        return root["downloads"] is JsonObject downloads && downloads["client"] is JsonObject client
            ? client
            : null;
    }

    /// <summary>
    /// 客户端 JAR 来源版本：显式 jar 字段优先（取最后声明），
    /// 否则为链中最后一个声明 downloads.client 的版本 id。
    /// </summary>
    private static string ResolveClientJarProviderId(List<(string Id, JsonObject Root)> chain)
    {
        string providerId = string.Empty;
        foreach ((string id, JsonObject root) in chain)
        {
            if (DownloadsClientNode(root) is not null)
            {
                providerId = id;
            }

            string? jarVersion = TryGetNodeString(root, "jar");
            if (!string.IsNullOrWhiteSpace(jarVersion))
            {
                providerId = jarVersion;
            }
        }

        return providerId;
    }

    private static string LibraryCoordinateKey(JsonObject library)
    {
        string? name = TryGetNodeString(library, "name");
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

    private static string? TryGetNodeString(JsonObject parent, string name)
    {
        return parent[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
    }
}
