using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MofuCore.Server;

/// <summary>
/// Minecraft 服务端管理：实例清单、Java 子进程生命周期、控制台环形缓冲、RCON、玩家与备份。
/// 服务器目录默认在 ./Mofumofu/mc-servers/&lt;id&gt;，元数据为目录内的 server.json。
/// </summary>
public class ServerManage
{
    public const string CoreVanilla = "vanilla";
    public const string CorePaper = "paper";
    public const string CoreNeoForge = "neoforge";
    public const string CoreFabric = "fabric";

    public const string StatusStopped = "stopped";
    public const string StatusStarting = "starting";
    public const string StatusRunning = "running";
    public const string StatusStopping = "stopping";

    private const int ConsoleCapacity = 800;
    private const int MaxAutoRestarts = 3;
    private const int PidPollMilliseconds = 200;

    private static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PlayerListPeriod = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AutoRestartDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AutoRestartReset = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ForceStopWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ExitWaitLimit = TimeSpan.FromSeconds(3);

    private static readonly Regex ListResponseRegex = new(
        @"There are (\d+) of a max of (\d+) players online", RegexOptions.Compiled);

    private static readonly Regex VersionNumberRegex = new(@"version ""(?<version>[^""]+)""", RegexOptions.Compiled);
    private static readonly Regex DigitRunRegex = new(@"\d+", RegexOptions.Compiled);

    private static readonly object RuntimesLock = new();
    private static readonly Dictionary<string, ServerRuntime> Runtimes = new();
    private static readonly object TickerLock = new();
    private static readonly ConcurrentDictionary<string, int> JavaVersionCache = new(StringComparer.OrdinalIgnoreCase);
    private static CancellationTokenSource? _lifetime;
    private static Task? _playerTicker;

    /// <summary>事件：服务器控制台新增一行输出（serverId, line）。前端可据此实时渲染控制台。</summary>
    public static event Action<string, string>? ConsoleLine;

    /// <summary>所有服务器的根目录，默认 ./Mofumofu/mc-servers。</summary>
    public static string ServerRootDirectory { get; set; } = Path.Combine("Mofumofu", "mc-servers");

    /// <summary>某个服务器的目录。</summary>
    public static string ServerDirectory(string id) => Path.Combine(ServerRootDirectory, id);

    internal static void ValidateId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.IndexOfAny(new[] { '/', '\\' }) >= 0 ||
            id.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("服务器 id 非法");
        }
    }

    public static bool IsValidId(string id)
    {
        try
        {
            ValidateId(id);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>读取服务器的 server.json，不存在或损坏时返回 null。</summary>
    public static ServerConfig? LoadServerConfig(string id)
    {
        return IsValidId(id) ? LoadServerConfigFromDirectory(ServerDirectory(id)) : null;
    }

    internal static ServerConfig? LoadServerConfigFromDirectory(string directory)
    {
        string path = Path.Combine(directory, "server.json");
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), ServerJson.Options);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static void SaveServerConfigToDirectory(string directory, ServerConfig config)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "server.json"),
            JsonSerializer.Serialize(config, ServerJson.Options));
    }

    /// <summary>扫描全部服务器元数据（按名称排序）。</summary>
    public static IReadOnlyList<ServerConfig> LoadServers()
    {
        List<ServerConfig> result = [];
        if (!Directory.Exists(ServerRootDirectory))
        {
            return result;
        }
        foreach (string directory in Directory.EnumerateDirectories(ServerRootDirectory))
        {
            ServerConfig? config = LoadServerConfigFromDirectory(directory);
            if (config is null)
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(config.Id))
            {
                config.Id = Path.GetFileName(directory);
            }
            result.Add(config);
        }
        result.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
        return result;
    }

    /// <summary>服务器实际世界存档目录（按 server.properties 的 level-name）。</summary>
    public static string WorldDirectory(string id)
    {
        ValidateId(id);
        string directory = ServerDirectory(id);
        string levelName = "world";
        try
        {
            foreach (ServerProperty property in ServerProperties.GetValuesOrEmpty(directory))
            {
                if (property.Key == "level-name" && !string.IsNullOrWhiteSpace(property.Value))
                {
                    levelName = property.Value.Trim();
                }
            }
        }
        catch
        {
            // 属性文件损坏时回退默认世界目录
        }
        return Path.Combine(directory, levelName);
    }

    /// <summary>全部服务器摘要（元数据 + 运行状态/在线数）。</summary>
    public static IReadOnlyList<ServerInfo> ListServerInfos()
    {
        IReadOnlyList<ServerConfig> configs = LoadServers();
        List<ServerInfo> result = new(configs.Count);
        foreach (ServerConfig config in configs)
        {
            result.Add(ToServerInfo(config));
        }
        return result;
    }

    /// <summary>单个服务器摘要，不存在时返回 null。</summary>
    public static ServerInfo? GetServerInfo(string id)
    {
        ServerConfig? config = LoadServerConfig(id);
        return config is null ? null : ToServerInfo(config);
    }

    private static ServerInfo ToServerInfo(ServerConfig config)
    {
        ServerInfo info = new()
        {
            Id = config.Id,
            Name = config.Name,
            Core = config.Core,
            CoreVersion = config.CoreVersion,
            McVersion = config.McVersion,
            Port = config.Port,
            MaxPlayers = config.MaxPlayers,
            Status = StatusStopped,
        };
        ServerRuntime? runtime = FindRuntime(config.Id);
        if (runtime is not null)
        {
            lock (runtime.Sync)
            {
                info.Status = runtime.Status;
                info.Players = runtime.Players;
            }
        }
        return info;
    }

    /// <summary>创建服务器实例：目录、eula.txt、初始 server.properties 与元数据。</summary>
    /// <param name="options">创建参数，AcceptEula 必须为 true。</param>
    /// <param name="installCore">可选的核心安装回调（目录, 配置, 取消令牌），由调用方下载并落盘服务端 jar。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<string> CreateServerAsync(
        CreateOptions options,
        Func<string, ServerConfig, CancellationToken, Task>? installCore = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.AcceptEula)
        {
            throw new InvalidOperationException("未同意 Minecraft EULA，无法创建服务器");
        }
        if (string.IsNullOrWhiteSpace(options.Name) || string.IsNullOrWhiteSpace(options.McVersion))
        {
            throw new InvalidOperationException("参数无效");
        }
        switch (options.Core)
        {
            case CoreVanilla:
            case CorePaper:
            case CoreFabric:
            case CoreNeoForge:
                break;
            default:
                throw new InvalidOperationException($"不支持的服务器核心：{options.Core}");
        }
        if (options.Port <= 0)
        {
            options.Port = 25565;
        }
        if (options.MaxPlayers <= 0)
        {
            options.MaxPlayers = 20;
        }

        string name = SanitizeServerName(options.Name);
        if (string.IsNullOrEmpty(name))
        {
            name = "server";
        }
        string id = UniqueServerDirectory(name);
        string directory = Path.Combine(ServerRootDirectory, id);
        Directory.CreateDirectory(directory);

        ServerConfig config = new()
        {
            Id = id,
            Name = options.Name,
            Core = options.Core,
            CoreVersion = options.CoreVersion?.Trim() ?? string.Empty,
            McVersion = options.McVersion.Trim(),
            Port = options.Port,
            MaxPlayers = options.MaxPlayers,
            JavaPath = options.JavaPath?.Trim() ?? string.Empty,
        };

        try
        {
            if (installCore is not null)
            {
                await installCore(directory, config, cancellationToken).ConfigureAwait(false);
            }
            WriteEula(directory);
            WriteInitialProperties(directory, config.Port, config.MaxPlayers, config.Name);
            try
            {
                ServerProperties.EnsureRcon(directory);
            }
            catch (Exception e)
            {
                LogWarn($"为服务器 {id} 写入 RCON 配置失败：{e.Message}（将退回 stdin 控制台路径）");
            }
            SaveServerConfigToDirectory(directory, config);
            return id;
        }
        catch
        {
            TryRemoveDirectory(directory);
            throw;
        }
    }

    /// <summary>删除服务器（必须已停止）。</summary>
    public static void DeleteServer(string id)
    {
        ValidateId(id);
        if (IsRunning(id))
        {
            throw new InvalidOperationException("服务器运行中，请先停止");
        }
        TryRemoveDirectory(ServerDirectory(id));
    }

    /// <summary>读取崩溃自动重启开关。</summary>
    public static bool AutoRestartEnabled(string id)
    {
        ServerConfig? config = LoadServerConfig(id);
        return config is not null && config.AutoRestart;
    }

    /// <summary>保存崩溃自动重启开关。</summary>
    public static void SetAutoRestart(string id, bool enabled)
    {
        ValidateId(id);
        string directory = ServerDirectory(id);
        ServerConfig config = LoadServerConfigFromDirectory(directory)
                              ?? throw new InvalidOperationException("服务器不存在");
        config.AutoRestart = enabled;
        SaveServerConfigToDirectory(directory, config);
    }

    internal static void ClearAutoRestartState(string id)
    {
        ServerRuntime? runtime = FindRuntime(id);
        if (runtime is null)
        {
            return;
        }
        lock (runtime.Sync)
        {
            runtime.RestartCount = 0;
        }
    }

    /// <summary>读取 JVM 启动参数（内存 + 额外参数）。</summary>
    public static LaunchOptions GetLaunchOptions(string id)
    {
        ServerConfig config = LoadServerConfig(id) ?? throw new InvalidOperationException("服务器不存在");
        return new LaunchOptions
        {
            MemoryMb = config.MemoryMb,
            MemoryMinMb = config.MemoryMinMb,
            ExtraJavaArgs = [.. config.ExtraJavaArgs],
        };
    }

    /// <summary>保存 JVM 启动参数（服务器运行中拒绝修改）。</summary>
    public static void SaveLaunchOptions(string id, LaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateId(id);
        if (IsRunning(id))
        {
            throw new InvalidOperationException("服务器运行中，请先停止再修改启动参数");
        }
        List<string> cleaned = CleanExtraArgs(options.ExtraJavaArgs);
        ValidateExtraArgs(cleaned);
        if (options.MemoryMb is < 0 or > 1048576)
        {
            throw new InvalidOperationException("最大内存取值非法（0 ~ 1048576 MB）");
        }
        if (options.MemoryMinMb is < 0 or > 1048576)
        {
            throw new InvalidOperationException("初始内存取值非法（0 ~ 1048576 MB）");
        }
        if (options.MemoryMinMb > 0 && options.MemoryMb > 0 && options.MemoryMinMb > options.MemoryMb)
        {
            throw new InvalidOperationException("初始内存不能大于最大内存");
        }

        string directory = ServerDirectory(id);
        ServerConfig config = LoadServerConfigFromDirectory(directory)
                              ?? throw new InvalidOperationException("服务器不存在");
        config.MemoryMb = options.MemoryMb;
        config.MemoryMinMb = options.MemoryMinMb;
        config.ExtraJavaArgs = cleaned;
        SaveServerConfigToDirectory(directory, config);
    }

    private static List<string> CleanExtraArgs(IEnumerable<string>? args)
    {
        List<string> result = [];
        if (args is null)
        {
            return result;
        }
        foreach (string arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg))
            {
                continue;
            }
            foreach (string field in arg.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                result.Add(field);
            }
        }
        return result;
    }

    private static void ValidateExtraArgs(IEnumerable<string> args)
    {
        foreach (string arg in args)
        {
            if (arg == "-jar" || arg.StartsWith('@'))
            {
                throw new InvalidOperationException($"额外参数不允许包含 \"{arg}\"（启动入口由启动器管理）");
            }
        }
    }

    /// <summary>运行指定 Minecraft 版本所需的最低 Java 主版本，无法识别时返回 null。</summary>
    public static int? RequiredJavaMajorVersion(string mcVersion)
    {
        (int major, int minor, int patch) = SplitVersionComponents(mcVersion);
        if (major < 0)
        {
            return null;
        }
        if (major == 1)
        {
            if (minor >= 21)
            {
                return 21;
            }
            if (minor == 20 && patch >= 5)
            {
                return 21;
            }
            if (minor == 20)
            {
                return 17;
            }
            if (minor >= 18)
            {
                return 17;
            }
            if (minor == 17)
            {
                return 16;
            }
            return 8;
        }
        return major >= 26 ? 25 : null;
    }

    private static (int Major, int Minor, int Patch) SplitVersionComponents(string version)
    {
        int major = -1, minor = -1, patch = -1;
        MatchCollection matches = DigitRunRegex.Matches(version ?? string.Empty);
        if (matches.Count > 0 && int.TryParse(matches[0].Value, out int value))
        {
            major = value;
        }
        if (matches.Count > 1 && int.TryParse(matches[1].Value, out value))
        {
            minor = value;
        }
        if (matches.Count > 2 && int.TryParse(matches[2].Value, out value))
        {
            patch = value;
        }
        return (major, minor, patch);
    }

    /// <summary>启动服务器进程：starting → 检测到 Done ( 后 running。</summary>
    public static async Task StartAsync(string id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        string directory = ServerDirectory(id);
        ServerConfig config = LoadServerConfigFromDirectory(directory)
                              ?? throw new InvalidOperationException("读取服务器配置失败：server.json 不存在");
        RequireEulaAccepted(directory);

        ServerRuntime runtime = EnsureRuntime(id);
        TaskCompletionSource<bool>? previousExit;
        lock (runtime.Sync)
        {
            previousExit = runtime.ExitDone;
        }
        if (previousExit is not null)
        {
            await Task.WhenAny(previousExit.Task, Task.Delay(ExitWaitLimit, cancellationToken))
                .ConfigureAwait(false);
        }

        CancellationTokenSource startCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        TaskCompletionSource<bool> exitSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (runtime.Sync)
        {
            if (runtime.Status is StatusRunning or StatusStarting)
            {
                startCts.Dispose();
                throw new InvalidOperationException("服务器已在运行中");
            }
            if (runtime.Status is StatusStopping)
            {
                startCts.Dispose();
                throw new InvalidOperationException("服务器正在停止，请稍后再启动");
            }
            runtime.Status = StatusStarting;
            runtime.StopRequested = false;
            runtime.CancelStart = startCts;
            runtime.ExitDone = exitSignal;
        }

        bool processStarted = false;
        try
        {
            try
            {
                RconConfiguration rcon = ServerProperties.EnsureRcon(directory);
                lock (runtime.Sync)
                {
                    runtime.Rcon = rcon;
                }
            }
            catch (Exception e)
            {
                LogWarn($"为服务器 {id} 写入 RCON 配置失败：{e.Message}（将退回 stdin 控制台路径）");
            }

            List<string> orphanNotes = OrphanCleaner.KillOrphanServerProcesses(directory);
            List<string> startupLogs = [];
            string java = await ResolveJavaAsync(config, startupLogs, startCts.Token).ConfigureAwait(false);
            List<string> arguments = BuildJavaArguments(config, directory);

            ProcessStartInfo startInfo = new()
            {
                FileName = java,
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) => OnProcessLine(id, runtime, args.Data);
            process.ErrorDataReceived += (_, args) => OnProcessLine(id, runtime, args.Data);
            if (!process.Start())
            {
                throw new InvalidOperationException("启动 Java 进程失败");
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.StandardInput.AutoFlush = true;

            lock (runtime.Sync)
            {
                long previousCursor = runtime.Cursor;
                List<string> initial = new(startupLogs.Count + orphanNotes.Count);
                initial.AddRange(startupLogs);
                initial.AddRange(orphanNotes);
                runtime.Process = process;
                runtime.Stdin = process.StandardInput;
                runtime.Pid = process.Id;
                runtime.Logs.Clear();
                runtime.Logs.AddRange(initial);
                runtime.Cursor = previousCursor + initial.Count;
                runtime.Players = 0;
                runtime.CpuPercent = 0;
                runtime.MemoryMb = 0;
                runtime.LastCpuTime = TimeSpan.Zero;
                runtime.LastCpuSampleAt = DateTime.UtcNow;
            }
            processStarted = true;
            OrphanCleaner.WritePidFile(directory, process.Id);
            _ = AwaitExitAsync(id, runtime, process, exitSignal);
            EnsurePlayerTicker();
        }
        catch
        {
            lock (runtime.Sync)
            {
                if (!processStarted)
                {
                    runtime.Status = StatusStopped;
                    runtime.ExitDone = null;
                }
            }
            exitSignal.TrySetResult(true);
            throw;
        }
        finally
        {
            lock (runtime.Sync)
            {
                runtime.CancelStart = null;
            }
            startCts.Cancel();
        }
    }

    /// <summary>停止服务器。force=false 软停止（stop 指令 + 宽限期后强杀）；force=true 立即结束进程树。</summary>
    public static async Task StopAsync(string id, bool force = false)
    {
        if (!IsValidId(id))
        {
            return;
        }
        ServerRuntime? runtime = FindRuntime(id);
        if (runtime is null)
        {
            return;
        }

        StreamWriter? stdin;
        CancellationTokenSource? cancelStart;
        Process? process;
        lock (runtime.Sync)
        {
            bool active = runtime.Status is StatusRunning or StatusStarting or StatusStopping;
            if (!active)
            {
                return;
            }
            runtime.StopRequested = true;
            runtime.Status = StatusStopping;
            stdin = runtime.Stdin;
            cancelStart = runtime.CancelStart;
            process = runtime.Process;
        }

        if (force)
        {
            if (process is not null)
            {
                OrphanCleaner.KillProcessTree(process);
            }
            else
            {
                cancelStart?.Cancel();
            }
            await WaitForExitAsync(runtime).ConfigureAwait(false);
            return;
        }

        if (stdin is not null)
        {
            try
            {
                await stdin.WriteAsync("stop\n").ConfigureAwait(false);
                await stdin.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 管道已断说明进程正在退出，继续等状态收敛
            }
        }
        else if (cancelStart is not null)
        {
            cancelStart.Cancel();
            return;
        }

        DateTime deadline = DateTime.UtcNow + StopGracePeriod;
        while (DateTime.UtcNow < deadline)
        {
            bool stopped;
            lock (runtime.Sync)
            {
                stopped = runtime.Status == StatusStopped;
                process = runtime.Process;
            }
            if (stopped)
            {
                return;
            }
            await Task.Delay(PidPollMilliseconds).ConfigureAwait(false);
        }

        if (process is not null)
        {
            OrphanCleaner.KillProcessTree(process);
        }
        await WaitForExitAsync(runtime).ConfigureAwait(false);
    }

    /// <summary>重启服务器：先软停止（宽限期内退出），再重新启动。</summary>
    public static async Task RestartAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!IsRunning(id))
        {
            throw new InvalidOperationException("服务器未在运行");
        }
        ServerRuntime? runtime = FindRuntime(id);
        if (runtime is not null)
        {
            lock (runtime.Sync)
            {
                runtime.StopRequested = true;
                runtime.RestartCount = 0;
            }
        }
        await StopAsync(id).ConfigureAwait(false);
        await StartAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>退出启动器前优雅停止全部托管服务器，返回给日志用的说明行。</summary>
    public static async Task<IReadOnlyList<string>> ShutdownAllAsync(TimeSpan timeout)
    {
        List<string> ids = [];
        lock (RuntimesLock)
        {
            foreach ((string id, ServerRuntime runtime) in Runtimes)
            {
                lock (runtime.Sync)
                {
                    if (runtime.Status is StatusRunning or StatusStarting)
                    {
                        ids.Add(id);
                    }
                }
            }
        }
        if (ids.Count == 0)
        {
            return [];
        }

        Task all = Task.WhenAll(ids.Select(id => StopAsync(id)));
        Task finished = await Task.WhenAny(all, Task.Delay(timeout)).ConfigureAwait(false);
        List<string> notes = [];
        if (finished == all)
        {
            foreach (string id in ids)
            {
                notes.Add($"已停止托管服务器 {id}");
            }
            return notes;
        }

        List<string> pending = [];
        foreach (string id in ids)
        {
            if (ServerProcessGone(id))
            {
                notes.Add($"已停止托管服务器 {id}");
                continue;
            }
            pending.Add(id);
        }
        foreach (string id in pending)
        {
            try
            {
                await StopAsync(id, true).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                LogWarn($"强杀托管服务器 {id} 失败：{e.Message}");
            }
        }

        DateTime deadline = DateTime.UtcNow + ForceStopWait;
        foreach (string id in pending)
        {
            bool gone = false;
            while (DateTime.UtcNow < deadline)
            {
                if (ServerProcessGone(id))
                {
                    gone = true;
                    break;
                }
                await Task.Delay(50).ConfigureAwait(false);
            }
            if (gone)
            {
                notes.Add($"托管服务器 {id} 未在 {timeout.TotalSeconds:0} 秒内退出，已强制结束");
                continue;
            }
            LogError($"托管服务器 {id} 强杀后仍在运行，可能残留进程占用服务器目录，请在任务管理器里结束它");
            notes.Add($"托管服务器 {id} 强杀后仍未退出，请手动检查进程");
        }
        return notes;
    }

    private static bool ServerProcessGone(string id)
    {
        ServerRuntime? runtime = FindRuntime(id);
        if (runtime is null)
        {
            return true;
        }
        string status;
        Process? process;
        lock (runtime.Sync)
        {
            status = runtime.Status;
            process = runtime.Process;
        }
        if (status == StatusStopped)
        {
            return true;
        }
        if (process is null)
        {
            return true;
        }
        try
        {
            return process.HasExited;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>向控制台写入一条指令。</summary>
    public static async Task SendCommandAsync(string id, string command)
    {
        ServerRuntime? runtime = FindRuntime(id);
        if (runtime is null)
        {
            throw new InvalidOperationException("服务器未运行");
        }
        StreamWriter? stdin;
        string status;
        lock (runtime.Sync)
        {
            stdin = runtime.Stdin;
            status = runtime.Status;
        }
        if (stdin is null || (status != StatusRunning && status != StatusStarting))
        {
            throw new InvalidOperationException("服务器未运行");
        }
        await stdin.WriteAsync(command.TrimEnd('\r', '\n') + "\n").ConfigureAwait(false);
        await stdin.FlushAsync().ConfigureAwait(false);
    }

    internal static async Task SendCommandAnyAsync(string id, string command)
    {
        try
        {
            await RunRconCommandAsync(id, command).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await SendCommandAsync(id, command).ConfigureAwait(false);
        }
    }

    /// <summary>对指定服务器执行一条 RCON 指令；未启用 RCON 时抛出异常。</summary>
    public static async Task<string> RunRconCommandAsync(string id, string command)
    {
        ServerRuntime? runtime = FindRuntime(id);
        if (runtime is null)
        {
            throw new InvalidOperationException("服务器未在运行");
        }
        RconConfiguration rcon;
        bool running;
        lock (runtime.Sync)
        {
            rcon = runtime.Rcon;
            running = runtime.Status == StatusRunning;
        }
        if (!running)
        {
            throw new InvalidOperationException("服务器未在运行");
        }
        if (!rcon.Enabled)
        {
            throw new InvalidOperationException("该服务器未启用 RCON");
        }
        return await RconClient.ExecuteAsync(rcon.Port, rcon.Password, command).ConfigureAwait(false);
    }

    /// <summary>服务器进程状态。</summary>
    public static string GetStatus(string id)
    {
        ServerRuntime? runtime = FindRuntime(id);
        if (runtime is null)
        {
            return StatusStopped;
        }
        lock (runtime.Sync)
        {
            return runtime.Status;
        }
    }

    /// <summary>服务器进程 pid，未运行时为 0。</summary>
    public static int GetPid(string id)
    {
        ServerRuntime? runtime = FindRuntime(id);
        if (runtime is null)
        {
            return 0;
        }
        lock (runtime.Sync)
        {
            return runtime.Pid;
        }
    }

    /// <summary>服务器进程是否存活（含启动准备与停止中）。</summary>
    public static bool IsRunning(string id)
    {
        ServerRuntime? runtime = FindRuntime(id);
        if (runtime is null)
        {
            return false;
        }
        lock (runtime.Sync)
        {
            return runtime.Status is StatusRunning or StatusStarting or StatusStopping;
        }
    }

    /// <summary>前端轮询：返回 cursor 之后的新日志与运行指标。</summary>
    public static ServerSnapshot Poll(string id, long cursor)
    {
        ServerSnapshot snapshot = new() { Status = StatusStopped, LogLines = [] };
        ServerRuntime? runtime = FindRuntime(id);
        if (runtime is null)
        {
            return snapshot;
        }

        SampleMetrics(runtime);

        lock (runtime.Sync)
        {
            snapshot.Status = runtime.Status;
            snapshot.Players = runtime.Players;
            snapshot.CpuPercent = runtime.CpuPercent;
            snapshot.MemoryMb = runtime.MemoryMb;
            if (runtime.Cursor > cursor)
            {
                int from = (int)(cursor - (runtime.Cursor - runtime.Logs.Count));
                if (from < 0)
                {
                    from = 0;
                }
                if (from < runtime.Logs.Count)
                {
                    snapshot.LogLines.AddRange(runtime.Logs.GetRange(from, runtime.Logs.Count - from));
                }
            }
            snapshot.NextCursor = runtime.Cursor;
        }
        return snapshot;
    }

    internal static void AppendConsole(string id, string line)
    {
        ServerRuntime? runtime = FindRuntime(id);
        if (runtime is null)
        {
            return;
        }
        lock (runtime.Sync)
        {
            runtime.Cursor++;
            AppendLineLocked(runtime, line);
        }
    }

    internal static bool TryParsePlayerList(string response, out int players)
    {
        players = 0;
        Match match = ListResponseRegex.Match(response);
        return match.Success && int.TryParse(match.Groups[1].Value, out players);
    }

    internal static List<string> ParsePlayerListNames(string response)
    {
        List<string> names = [];
        Match match = ListResponseRegex.Match(response);
        if (!match.Success)
        {
            return names;
        }
        int index = response.IndexOf(':');
        if (index < 0)
        {
            return names;
        }
        foreach (string name in response[(index + 1)..].Split(','))
        {
            string trimmed = name.Trim();
            if (trimmed.Length > 0)
            {
                names.Add(trimmed);
            }
        }
        return names;
    }

    internal static void RequireEulaAccepted(string directory)
    {
        string path = Path.Combine(directory, "eula.txt");
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                "服务器目录里没有 eula.txt：请到「文件」页新建该文件并写入 eula=true" +
                "（Minecraft EULA 需你本人同意，启动器不会代为确认）");
        }
        foreach (string line in File.ReadAllLines(path))
        {
            if (line.Trim().Equals("eula=true", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
        throw new InvalidOperationException(
            "eula.txt 里不是 eula=true：请到「文件」页改成 eula=true 后再启动" +
            "（Minecraft EULA 需你本人同意，启动器不会代为确认）");
    }

    private static void WriteEula(string directory)
    {
        string content = $"# Accepted via Mofumofu at {DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz}\neula=true\n";
        File.WriteAllText(Path.Combine(directory, "eula.txt"), content);
    }

    private static void WriteInitialProperties(string directory, int port, int maxPlayers, string motd)
    {
        string content = $"server-port={port}\nmax-players={maxPlayers}\nmotd={SanitizePropertyValue(motd)}\n";
        File.WriteAllText(Path.Combine(directory, "server.properties"), content);
    }

    private static string SanitizePropertyValue(string value)
    {
        StringBuilder builder = new(value?.Length ?? 0);
        foreach (char character in value ?? string.Empty)
        {
            if (character is not ('\r' or '\n' or '\0'))
            {
                builder.Append(character);
            }
        }
        return builder.ToString();
    }

    internal static string SanitizeServerName(string name)
    {
        StringBuilder builder = new();
        foreach (char character in (name ?? string.Empty).Trim())
        {
            builder.Append(character switch
            {
                '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' => '_',
                _ => character,
            });
        }
        return builder.ToString().Trim(' ', '.');
    }

    private static string UniqueServerDirectory(string name)
    {
        if (!Directory.Exists(Path.Combine(ServerRootDirectory, name)))
        {
            return name;
        }
        for (int index = 1; ; index++)
        {
            string candidate = $"{name}-{index}";
            if (!Directory.Exists(Path.Combine(ServerRootDirectory, candidate)))
            {
                return candidate;
            }
        }
    }

    private static void TryRemoveDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch (Exception e)
        {
            LogWarn($"删除服务器目录失败：{e.Message}");
        }
    }

    private static List<string> BuildJavaArguments(ServerConfig config, string directory)
    {
        List<string> arguments = [];
        if (config.MemoryMb > 0)
        {
            arguments.Add($"-Xmx{config.MemoryMb}M");
        }
        if (config.MemoryMinMb > 0)
        {
            arguments.Add($"-Xms{config.MemoryMinMb}M");
        }
        arguments.AddRange(config.ExtraJavaArgs);

        if (config.Core == CoreNeoForge)
        {
            if (string.IsNullOrWhiteSpace(config.CoreVersion))
            {
                throw new InvalidOperationException("缺少 NeoForge 版本信息，请重新创建服务器");
            }
            string relative = Path.Combine("libraries", "net", "neoforged", "neoforge",
                config.CoreVersion, "win_args.txt");
            if (!File.Exists(Path.Combine(directory, relative)))
            {
                relative = Path.Combine("libraries", "net", "neoforged", "neoforge",
                    config.CoreVersion, "unix_args.txt");
            }
            if (!File.Exists(Path.Combine(directory, relative)))
            {
                throw new InvalidOperationException("缺少 NeoForge 启动参数文件，请重新创建服务器");
            }
            arguments.Add("@" + relative.Replace('\\', '/'));
            return arguments;
        }

        string jar = config.Core switch
        {
            CoreVanilla => "server.jar",
            CorePaper => ResolvePaperJarName(directory),
            CoreFabric => "fabric-server.jar",
            _ => throw new InvalidOperationException($"不支持的服务器核心：{config.Core}"),
        };
        arguments.Add("-jar");
        arguments.Add(jar);
        arguments.Add("nogui");
        return arguments;
    }

    /// <summary>Paper 下载的 jar 名带版本号（paper-1.21.4-232.jar），启动时自动找实际文件。</summary>
    private static string ResolvePaperJarName(string directory)
    {
        string legacy = Path.Combine(directory, "paper.jar");
        if (File.Exists(legacy)) return "paper.jar";

        string? candidate = Directory.EnumerateFiles(directory, "paper*.jar")
            .OrderByDescending(path => new FileInfo(path).Length)
            .FirstOrDefault();
        return candidate is null ? "paper.jar" : Path.GetFileName(candidate);
    }

    private static async Task<string> ResolveJavaAsync(
        ServerConfig config, List<string> startupLogs, CancellationToken cancellationToken)
    {
        int? required = RequiredJavaMajorVersion(config.McVersion);
        string executable = OperatingSystem.IsWindows() ? "java.exe" : "java";
        List<string> candidates = [];

        string configured = config.JavaPath?.Trim() ?? string.Empty;
        if (configured.Length > 0)
        {
            if (File.Exists(configured))
            {
                candidates.Add(configured);
            }
            else if (Directory.Exists(configured))
            {
                string nested = Path.Combine(configured, "bin", executable);
                if (File.Exists(nested))
                {
                    candidates.Add(nested);
                }
            }
            else
            {
                startupLogs.Add($"[{DateTime.Now:HH:mm:ss}] 配置的 Java 路径不存在：{configured}");
            }
        }
        candidates.AddRange(FindJavaCandidates());

        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int major = await ReadJavaMajorAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (major <= 0)
            {
                continue;
            }
            if (required is null || major >= required)
            {
                startupLogs.Add($"[{DateTime.Now:HH:mm:ss}] 使用 Java {major}：{candidate}");
                return candidate;
            }
            startupLogs.Add(
                $"[{DateTime.Now:HH:mm:ss}] 跳过 Java {major}（{candidate}）：Minecraft {config.McVersion} 需要 Java {required}");
        }

        throw new InvalidOperationException(required is null
            ? "没有找到可用的 Java 运行时，请在服务器启动参数里指定 Java 路径"
            : $"没有找到满足 Java {required} 的运行时（Minecraft {config.McVersion}），请在服务器启动参数里指定 Java 路径");
    }

    private static List<string> FindJavaCandidates()
    {
        string executable = OperatingSystem.IsWindows() ? "java.exe" : "java";
        List<string> candidates = [];

        void AddIfExists(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    if (File.Exists(path))
                    {
                        candidates.Add(path);
                    }
                }
                catch (Exception)
                {
                    // 非法路径直接跳过
                }
            }
        }

        string? javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            AddIfExists(Path.Combine(javaHome, "bin", executable));
        }
        string? pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathVariable))
        {
            foreach (string part in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                AddIfExists(Path.Combine(part.Trim().Trim('"'), executable));
            }
        }

        foreach (string root in CommonJavaRoots())
        {
            string[] directories;
            try
            {
                directories = Directory.GetDirectories(root);
            }
            catch (Exception)
            {
                continue;
            }
            foreach (string directory in directories)
            {
                AddIfExists(Path.Combine(directory, "bin", executable));
            }
        }
        return candidates;
    }

    private static List<string> CommonJavaRoots()
    {
        List<string> roots = [];
        void Add(string? root)
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                roots.Add(root);
            }
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (string vendor in new[]
                 {
                     "Java", "Eclipse Adoptium", "Microsoft", "Zulu", "BellSoft", "Amazon Corretto",
                     "Semeru", "OpenJDK",
                 })
        {
            Add(Path.Combine(programFiles, vendor));
        }
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        Add(Path.Combine(programFilesX86, "Java"));
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Add(Path.Combine(localAppData, "Programs", "Eclipse Adoptium"));
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add(Path.Combine(userProfile, ".jdks"));
        Add(Path.Combine("Mofumofu", "java"));
        return roots;
    }

    private static async Task<int> ReadJavaMajorAsync(string javaPath, CancellationToken cancellationToken)
    {
        if (JavaVersionCache.TryGetValue(javaPath, out int cached))
        {
            return cached;
        }
        int major = 0;
        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = javaPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("-version");
            using Process process = new() { StartInfo = startInfo };
            if (process.Start())
            {
                Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
                Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
                Task wait = process.WaitForExitAsync(cancellationToken);
                Task completed = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken))
                    .ConfigureAwait(false);
                if (completed == wait)
                {
                    string output = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
                    major = ParseJavaMajor(output);
                }
                else
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception)
                    {
                        // 进程已经退出
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            major = 0;
        }
        JavaVersionCache[javaPath] = major;
        return major;
    }

    private static int ParseJavaMajor(string output)
    {
        Match match = VersionNumberRegex.Match(output ?? string.Empty);
        if (!match.Success)
        {
            return 0;
        }
        string[] parts = match.Groups["version"].Value.Split('.');
        if (parts.Length == 0 || !int.TryParse(parts[0], out int first))
        {
            return 0;
        }
        if (first == 1 && parts.Length > 1 && int.TryParse(parts[1], out int legacy))
        {
            return legacy;
        }
        return first;
    }

    private static void OnProcessLine(string id, ServerRuntime runtime, string? line)
    {
        if (line is null)
        {
            return;
        }
        lock (runtime.Sync)
        {
            runtime.Cursor++;
            AppendLineLocked(runtime, line);
            if (runtime.Status == StatusStarting && line.Contains("Done (", StringComparison.Ordinal))
            {
                runtime.Status = StatusRunning;
            }
            if (TryParsePlayerList(line, out int players))
            {
                runtime.Players = players;
            }
        }
        try
        {
            ConsoleLine?.Invoke(id, line);
        }
        catch (Exception)
        {
            // 订阅方异常不影响进程输出读取
        }
    }

    private static void AppendLineLocked(ServerRuntime runtime, string line)
    {
        runtime.Logs.Add(line);
        int overflow = runtime.Logs.Count - ConsoleCapacity;
        if (overflow > 0)
        {
            runtime.Logs.RemoveRange(0, overflow);
        }
    }

    private static async Task AwaitExitAsync(
        string id, ServerRuntime runtime, Process process, TaskCompletionSource<bool> exitSignal)
    {
        int exitCode = -1;
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            exitCode = process.ExitCode;
        }
        catch (Exception)
        {
            // 进程句柄失效按异常退出处理
        }

        bool userRequested;
        lock (runtime.Sync)
        {
            runtime.Status = StatusStopped;
            runtime.Players = 0;
            runtime.Stdin = null;
            runtime.Process = null;
            runtime.Pid = 0;
            runtime.Cursor++;
            string detail = exitCode == 0
                ? $"服务器进程已退出"
                : $"服务器进程已退出：exit code {exitCode}";
            AppendLineLocked(runtime, $"[{DateTime.Now:HH:mm:ss}] {detail}");
            userRequested = runtime.StopRequested;
            runtime.StopRequested = false;
        }
        try
        {
            process.Dispose();
        }
        catch (Exception)
        {
            // 忽略
        }
        OrphanCleaner.RemovePidFile(ServerDirectory(id));
        exitSignal.TrySetResult(true);

        if (!userRequested)
        {
            await MaybeAutoRestartAsync(id, exitCode).ConfigureAwait(false);
        }
    }

    private static async Task MaybeAutoRestartAsync(string id, int exitCode)
    {
        ServerConfig? config = LoadServerConfigFromDirectory(ServerDirectory(id));
        ServerRuntime? runtime = FindRuntime(id);
        if (runtime is null || config is null || !config.AutoRestart)
        {
            return;
        }

        bool overLimit;
        lock (runtime.Sync)
        {
            DateTime now = DateTime.UtcNow;
            if (runtime.LastRestart < now - AutoRestartReset)
            {
                runtime.RestartCount = 0;
            }
            if (runtime.RestartCount >= MaxAutoRestarts)
            {
                runtime.Cursor++;
                AppendLineLocked(runtime,
                    $"[{DateTime.Now:HH:mm:ss}] 已达自动重启上限（{MaxAutoRestarts} 次 / {AutoRestartReset.TotalMinutes:0} 分钟），不再重启：请检查崩溃原因");
                overLimit = true;
            }
            else
            {
                runtime.RestartCount++;
                runtime.LastRestart = now;
                int attempt = runtime.RestartCount;
                runtime.Cursor++;
                AppendLineLocked(runtime,
                    $"[{DateTime.Now:HH:mm:ss}] 检测到异常退出（exit code {exitCode}），{AutoRestartDelay.TotalSeconds:0} 秒后自动重启（第 {attempt}/{MaxAutoRestarts} 次）");
                overLimit = false;
            }
        }
        if (overLimit)
        {
            return;
        }

        DateTime deadline = DateTime.UtcNow + AutoRestartDelay;
        while (DateTime.UtcNow < deadline)
        {
            TimeSpan remaining = deadline - DateTime.UtcNow;
            TimeSpan delay = remaining < TimeSpan.FromMilliseconds(250)
                ? remaining
                : TimeSpan.FromMilliseconds(250);
            await Task.Delay(delay).ConfigureAwait(false);
            lock (runtime.Sync)
            {
                if (runtime.Status != StatusStopped)
                {
                    runtime.Cursor++;
                    AppendLineLocked(runtime,
                        $"[{DateTime.Now:HH:mm:ss}] 检测到手动操作，取消本次自动重启。");
                    return;
                }
            }
        }

        try
        {
            await StartAsync(id).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            LogWarn($"自动重启 {id} 失败：{e.Message}");
            AppendConsole(id, $"[{DateTime.Now:HH:mm:ss}] 自动重启失败：{e.Message}（可手动点启动重试）");
        }
    }

    private static async Task WaitForExitAsync(ServerRuntime runtime)
    {
        TaskCompletionSource<bool>? exitSignal;
        lock (runtime.Sync)
        {
            exitSignal = runtime.ExitDone;
        }
        if (exitSignal is null)
        {
            return;
        }
        await Task.WhenAny(exitSignal.Task, Task.Delay(ExitWaitLimit)).ConfigureAwait(false);
    }

    private static void SampleMetrics(ServerRuntime runtime)
    {
        Process? process;
        lock (runtime.Sync)
        {
            process = runtime.Process;
        }
        if (process is null)
        {
            return;
        }
        try
        {
            if (process.HasExited)
            {
                return;
            }
            TimeSpan cpuTime = process.TotalProcessorTime;
            long workingSet = process.WorkingSet64;
            DateTime now = DateTime.UtcNow;
            lock (runtime.Sync)
            {
                double elapsedMs = (now - runtime.LastCpuSampleAt).TotalMilliseconds;
                double cpuMs = (cpuTime - runtime.LastCpuTime).TotalMilliseconds;
                runtime.CpuPercent = elapsedMs > 0
                    ? Math.Clamp(cpuMs / elapsedMs / Environment.ProcessorCount * 100.0, 0, 100)
                    : 0;
                runtime.LastCpuTime = cpuTime;
                runtime.LastCpuSampleAt = now;
                runtime.MemoryMb = workingSet / 1024.0 / 1024.0;
            }
        }
        catch (Exception)
        {
            // 进程退出的竞态，忽略本次采样
        }
    }

    private static void EnsurePlayerTicker()
    {
        lock (TickerLock)
        {
            if (_playerTicker is not null)
            {
                return;
            }
            _lifetime ??= new CancellationTokenSource();
            _playerTicker = Task.Run(async () =>
            {
                using PeriodicTimer timer = new(PlayerListPeriod);
                try
                {
                    while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
                    {
                        List<ServerRuntime> states;
                        lock (RuntimesLock)
                        {
                            states = [.. Runtimes.Values];
                        }
                        foreach (ServerRuntime runtime in states)
                        {
                            await RefreshPlayersAsync(runtime).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // 启动器退出
                }
            });
        }
    }

    private static async Task RefreshPlayersAsync(ServerRuntime runtime)
    {
        bool running;
        StreamWriter? stdin;
        RconConfiguration rcon;
        lock (runtime.Sync)
        {
            running = runtime.Status == StatusRunning;
            stdin = runtime.Stdin;
            rcon = runtime.Rcon;
        }
        if (!running)
        {
            return;
        }

        if (rcon.Enabled)
        {
            try
            {
                string response = await RconClient.ExecuteAsync(rcon.Port, rcon.Password, "list")
                    .ConfigureAwait(false);
                if (TryParsePlayerList(response, out int players))
                {
                    lock (runtime.Sync)
                    {
                        runtime.Players = players;
                    }
                    return;
                }
            }
            catch (Exception)
            {
                // RCON 不可用（未监听/密码被改）时静默退回 stdin
            }
        }

        if (stdin is not null)
        {
            try
            {
                await stdin.WriteAsync("list\n").ConfigureAwait(false);
                await stdin.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 进程正在退出
            }
        }
    }

    private static ServerRuntime EnsureRuntime(string id)
    {
        lock (RuntimesLock)
        {
            if (!Runtimes.TryGetValue(id, out ServerRuntime? runtime))
            {
                runtime = new ServerRuntime();
                Runtimes[id] = runtime;
            }
            return runtime;
        }
    }

    internal static ServerRuntime? FindRuntime(string id)
    {
        lock (RuntimesLock)
        {
            return Runtimes.TryGetValue(id, out ServerRuntime? runtime) ? runtime : null;
        }
    }

    internal static void LogWarn(string message) => Debug.WriteLine("[Server][WARN] " + message);

    internal static void LogError(string message) => Debug.WriteLine("[Server][ERROR] " + message);

    internal sealed class ServerRuntime
    {
        public readonly object Sync = new();
        public Process? Process;
        public StreamWriter? Stdin;
        public string Status = StatusStopped;
        public readonly List<string> Logs = new();
        public long Cursor;
        public int Players;
        public double CpuPercent;
        public double MemoryMb;
        public TimeSpan LastCpuTime;
        public DateTime LastCpuSampleAt = DateTime.UtcNow;
        public CancellationTokenSource? CancelStart;
        public RconConfiguration Rcon = new();
        public bool StopRequested;
        public TaskCompletionSource<bool>? ExitDone;
        public int RestartCount;
        public DateTime LastRestart;
        public int Pid;
    }
}

/// <summary>server.json 落盘的服务器元数据。</summary>
public class ServerConfig
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("core")] public string Core { get; set; } = string.Empty;
    [JsonPropertyName("coreVersion")] public string CoreVersion { get; set; } = string.Empty;
    [JsonPropertyName("mcVersion")] public string McVersion { get; set; } = string.Empty;
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("maxPlayers")] public int MaxPlayers { get; set; }
    [JsonPropertyName("javaPath")] public string JavaPath { get; set; } = string.Empty;
    [JsonPropertyName("memoryMB")] public int MemoryMb { get; set; }
    [JsonPropertyName("memoryMinMB")] public int MemoryMinMb { get; set; }
    [JsonPropertyName("extraJavaArgs")] public List<string> ExtraJavaArgs { get; set; } = [];
    [JsonPropertyName("autoRestart")] public bool AutoRestart { get; set; }
}

/// <summary>列表用摘要（合并运行时状态）。</summary>
public class ServerInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Core { get; set; } = string.Empty;
    public string CoreVersion { get; set; } = string.Empty;
    public string McVersion { get; set; } = string.Empty;
    public int Port { get; set; }
    public int MaxPlayers { get; set; }
    public string Status { get; set; } = ServerManage.StatusStopped;
    public int Players { get; set; }
}

/// <summary>创建服务器参数。AcceptEula 必须为 true 才会创建。</summary>
public class CreateOptions
{
    public string Name { get; set; } = string.Empty;
    public string Core { get; set; } = ServerManage.CoreVanilla;
    public string McVersion { get; set; } = string.Empty;
    public string CoreVersion { get; set; } = string.Empty;
    public int Port { get; set; }
    public int MaxPlayers { get; set; }
    public string JavaPath { get; set; } = string.Empty;
    public bool AcceptEula { get; set; }
}

/// <summary>JVM 启动参数编辑结构。</summary>
public class LaunchOptions
{
    public int MemoryMb { get; set; }
    public int MemoryMinMb { get; set; }
    public List<string> ExtraJavaArgs { get; set; } = [];
}

/// <summary>server.properties 单条配置。</summary>
public class ServerProperty
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

/// <summary>前端轮询返回值：状态 + 占用 + 新增日志。</summary>
public class ServerSnapshot
{
    public string Status { get; set; } = ServerManage.StatusStopped;
    public int Players { get; set; }
    public double CpuPercent { get; set; }
    public double MemoryMb { get; set; }
    public List<string> LogLines { get; set; } = [];
    public long NextCursor { get; set; }
}

/// <summary>从 server.properties 读出的 RCON 配置。</summary>
public class RconConfiguration
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 25575;
    public string Password { get; set; } = string.Empty;
}

internal static class ServerJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>server.properties 的读写：保持行序与注释，仅替换/追加键值。</summary>
public static class ServerProperties
{
    /// <summary>读取全部键值（按文件顺序）。</summary>
    public static IReadOnlyList<ServerProperty> Get(string id)
    {
        ServerManage.ValidateId(id);
        return GetValues(ServerManage.ServerDirectory(id));
    }

    /// <summary>更新键值（保留注释与未知行；新键追加到文件末尾）。</summary>
    public static void Set(string id, IEnumerable<ServerProperty> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ServerManage.ValidateId(id);
        string directory = ServerManage.ServerDirectory(id);
        if (ServerManage.IsRunning(id))
        {
            throw new InvalidOperationException("服务器运行中，请先停止再修改配置");
        }
        List<PropertyLine> lines = LoadLines(directory);
        ApplyUpdates(directory, lines, [.. properties]);
    }

    internal static List<ServerProperty> GetValuesOrEmpty(string directory)
    {
        List<ServerProperty> result = [];
        List<PropertyLine> lines;
        try
        {
            lines = LoadLines(directory);
        }
        catch (Exception)
        {
            return result;
        }
        foreach (PropertyLine line in lines)
        {
            if (!string.IsNullOrEmpty(line.Key))
            {
                result.Add(new ServerProperty { Key = line.Key, Value = line.Value });
            }
        }
        return result;
    }

    internal static List<ServerProperty> GetValues(string directory)
    {
        return GetValuesOrEmpty(directory);
    }

    internal static RconConfiguration ReadRcon(string directory)
    {
        Dictionary<string, string> values;
        try
        {
            values = CollectValues(LoadLines(directory));
        }
        catch (Exception)
        {
            return new RconConfiguration();
        }
        if (!values.TryGetValue("enable-rcon", out string? enabled) ||
            !string.Equals(enabled.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return new RconConfiguration();
        }
        int port = 25575;
        if (values.TryGetValue("rcon.port", out string? portText) &&
            int.TryParse(portText.Trim(), out int parsed) && parsed > 0 && parsed <= 65535)
        {
            port = parsed;
        }
        if (!values.TryGetValue("rcon.password", out string? password) ||
            string.IsNullOrWhiteSpace(password))
        {
            return new RconConfiguration();
        }
        return new RconConfiguration { Enabled = true, Port = port, Password = password.Trim() };
    }

    internal static RconConfiguration EnsureRcon(string directory)
    {
        List<PropertyLine> lines = LoadLines(directory);
        Dictionary<string, string> values = CollectValues(lines);
        string password = values.TryGetValue("rcon.password", out string? existing)
            ? existing.Trim()
            : string.Empty;
        if (password.Length == 0)
        {
            password = GenerateRconPassword();
        }
        int port = 25575;
        if (values.TryGetValue("rcon.port", out string? portText) &&
            int.TryParse(portText.Trim(), out int parsed) && parsed > 0 && parsed <= 65535)
        {
            port = parsed;
        }

        List<ServerProperty> updates =
        [
            new() { Key = "enable-rcon", Value = "true" },
            new() { Key = "rcon.port", Value = port.ToString() },
            new() { Key = "rcon.password", Value = password },
        ];
        bool changed = updates.Any(update =>
            !string.Equals(values.TryGetValue(update.Key, out string? current) ? current.Trim() : string.Empty,
                update.Value, StringComparison.Ordinal));
        if (changed)
        {
            ApplyUpdates(directory, lines, updates);
        }
        return new RconConfiguration { Enabled = true, Port = port, Password = password };
    }

    internal static void WriteUpdates(string directory, IReadOnlyList<ServerProperty> updates)
    {
        ApplyUpdates(directory, LoadLines(directory), updates);
    }

    private static Dictionary<string, string> CollectValues(IEnumerable<PropertyLine> lines)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (PropertyLine line in lines)
        {
            if (!string.IsNullOrEmpty(line.Key))
            {
                values[line.Key] = line.Value;
            }
        }
        return values;
    }

    private static List<PropertyLine> LoadLines(string directory)
    {
        string path = Path.Combine(directory, "server.properties");
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("server.properties 不存在（先启动一次服务器后重试）");
        }
        List<PropertyLine> lines = [];
        foreach (string raw in File.ReadAllLines(path))
        {
            string text = raw.Trim();
            if (text.Length == 0 || text.StartsWith('#'))
            {
                lines.Add(new PropertyLine { Comment = raw });
                continue;
            }
            int index = text.IndexOf('=');
            if (index < 0)
            {
                lines.Add(new PropertyLine { Comment = raw });
                continue;
            }
            lines.Add(new PropertyLine
            {
                Key = text[..index].Trim(),
                Value = text[(index + 1)..],
            });
        }
        return lines;
    }

    private static void ApplyUpdates(string directory, List<PropertyLine> lines,
        IReadOnlyList<ServerProperty> updates)
    {
        Dictionary<string, string> wanted = new(StringComparer.Ordinal);
        foreach (ServerProperty property in updates)
        {
            if (!string.IsNullOrEmpty(property.Key))
            {
                wanted[property.Key] = property.Value;
            }
        }
        HashSet<string> seen = new(StringComparer.Ordinal);
        StringBuilder builder = new();
        foreach (PropertyLine line in lines)
        {
            if (string.IsNullOrEmpty(line.Key))
            {
                builder.Append(line.Comment).Append('\n');
                continue;
            }
            seen.Add(line.Key);
            string value = wanted.TryGetValue(line.Key, out string? updated) ? updated : line.Value;
            builder.Append(line.Key).Append('=').Append(value).Append('\n');
        }
        foreach (ServerProperty property in updates)
        {
            if (!string.IsNullOrEmpty(property.Key) && seen.Add(property.Key))
            {
                builder.Append(property.Key).Append('=').Append(property.Value).Append('\n');
            }
        }
        File.WriteAllText(Path.Combine(directory, "server.properties"), builder.ToString());
    }

    private static string GenerateRconPassword()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        byte[] buffer = RandomNumberGenerator.GetBytes(24);
        char[] chars = new char[buffer.Length];
        for (int index = 0; index < buffer.Length; index++)
        {
            chars[index] = alphabet[buffer[index] % alphabet.Length];
        }
        return new string(chars);
    }

    private sealed class PropertyLine
    {
        public string Comment = string.Empty;
        public string Key = string.Empty;
        public string Value = string.Empty;
    }
}

/// <summary>
/// RCON 客户端（Source RCON 协议）。报文为长度前缀 + 请求 id + 类型 + 正文，
/// 小端序：int32 长度、int32 请求 id（鉴权失败回 -1）、int32 类型（3=鉴权 2=指令 0=响应）。
/// </summary>
public sealed class RconClient : IDisposable
{
    private const int TypeResponse = 0;
    private const int TypeCommand = 2;
    private const int TypeAuth = 3;
    private const int AuthFailedRequestId = -1;
    private const int PacketLimit = 4096;

    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(8);

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private int _requestId;

    private RconClient(TcpClient client, NetworkStream stream)
    {
        _client = client;
        _stream = stream;
    }

    /// <summary>建立连接并完成鉴权。</summary>
    public static async Task<RconClient> ConnectAsync(
        string host, int port, string password, CancellationToken cancellationToken = default)
    {
        TcpClient client = new();
        try
        {
            using CancellationTokenSource dialCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            dialCts.CancelAfter(DialTimeout);
            await client.ConnectAsync(host, port, dialCts.Token).ConfigureAwait(false);
            NetworkStream stream = client.GetStream();
            RconClient rcon = new(client, stream);
            await rcon.AuthenticateAsync(password, cancellationToken).ConfigureAwait(false);
            return rcon;
        }
        catch (Exception)
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>一次性执行一条 RCON 指令（自己建连、自己关闭）。</summary>
    public static async Task<string> ExecuteAsync(
        int port, string password, string command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("未配置 RCON 密码");
        }
        using RconClient client = await ConnectAsync("127.0.0.1", port, password, cancellationToken)
            .ConfigureAwait(false);
        return await client.CommandAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private async Task AuthenticateAsync(string password, CancellationToken cancellationToken)
    {
        await WritePacketAsync(TypeAuth, password, cancellationToken).ConfigureAwait(false);
        (int requestId, _, _) = await ReadPacketAsync(cancellationToken).ConfigureAwait(false);
        if (requestId == AuthFailedRequestId)
        {
            throw new InvalidOperationException("RCON 密码不正确");
        }
    }

    /// <summary>执行一条指令并返回响应正文。</summary>
    public async Task<string> CommandAsync(string command, CancellationToken cancellationToken = default)
    {
        await WritePacketAsync(TypeCommand, command, cancellationToken).ConfigureAwait(false);
        StringBuilder builder = new();
        while (true)
        {
            (_, int type, string body) = await ReadPacketAsync(cancellationToken).ConfigureAwait(false);
            builder.Append(body);
            if (type != TypeResponse || body.Length < PacketLimit - 16)
            {
                break;
            }
        }
        return builder.ToString();
    }

    private async Task WritePacketAsync(int type, string body, CancellationToken cancellationToken)
    {
        _requestId++;
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
        byte[] payload = new byte[12 + bodyBytes.Length + 2];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), bodyBytes.Length + 10);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4, 4), _requestId);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8, 4), type);
        bodyBytes.CopyTo(payload, 12);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        await _stream.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
        await _stream.FlushAsync(timeout.Token).ConfigureAwait(false);
    }

    private async Task<(int RequestId, int Type, string Body)> ReadPacketAsync(
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);

        byte[] header = new byte[4];
        await _stream.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 10 || length > PacketLimit)
        {
            throw new InvalidOperationException($"RCON 报文长度异常：{length}");
        }
        byte[] payload = new byte[length];
        await _stream.ReadExactlyAsync(payload, timeout.Token).ConfigureAwait(false);
        int requestId = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4));
        int type = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4, 4));
        string body = Encoding.UTF8.GetString(payload, 8, payload.Length - 8).TrimEnd('\0');
        return (requestId, type, body);
    }

    public void Dispose()
    {
        try
        {
            _stream.Dispose();
        }
        catch (Exception)
        {
            // 忽略
        }
        _client.Dispose();
    }
}

/// <summary>whitelist.json 条目。</summary>
public class WhitelistEntry
{
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
}

/// <summary>ops.json 条目。</summary>
public class OpEntry
{
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("level")] public int Level { get; set; }
    [JsonPropertyName("bypassesPlayerLimit")] public bool BypassesPlayerLimit { get; set; }
}

/// <summary>banned-players.json 条目。</summary>
public class BannedPlayerEntry
{
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("created")] public string Created { get; set; } = string.Empty;
    [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;
    [JsonPropertyName("expires")] public string Expires { get; set; } = string.Empty;
    [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
}

/// <summary>一个服务器的玩家与权限全貌。</summary>
public class ServerPlayerSummary
{
    public List<string> Online { get; set; } = [];
    public bool WhitelistEnabled { get; set; }
    public List<WhitelistEntry> Whitelist { get; set; } = [];
    public List<OpEntry> Ops { get; set; } = [];
    public List<BannedPlayerEntry> Banned { get; set; } = [];
    public bool Running { get; set; }
}

/// <summary>玩家与权限管理：白名单 / OP / 封禁名单的结构化读写 + 在线玩家与广播。</summary>
public static class ServerPlayers
{
    private const string WhitelistFileName = "whitelist.json";
    private const string OpsFileName = "ops.json";
    private const string BannedFileName = "banned-players.json";

    private static readonly Regex PlayerNamePattern = new(@"^[A-Za-z0-9_]{1,16}$", RegexOptions.Compiled);

    /// <summary>汇总玩家与权限信息。</summary>
    public static ServerPlayerSummary Get(string id)
    {
        if (!ServerManage.IsValidId(id))
        {
            return new ServerPlayerSummary();
        }
        string directory = ServerManage.ServerDirectory(id);
        ServerPlayerSummary result = new()
        {
            Online = OnlinePlayerNames(id),
            Whitelist = ReadPlayersFile<WhitelistEntry>(Path.Combine(directory, WhitelistFileName)),
            Ops = ReadPlayersFile<OpEntry>(Path.Combine(directory, OpsFileName)),
            Banned = ReadPlayersFile<BannedPlayerEntry>(Path.Combine(directory, BannedFileName)),
            Running = ServerManage.IsRunning(id),
        };
        foreach (ServerProperty property in ServerProperties.GetValuesOrEmpty(directory))
        {
            if (property.Key == "white-list")
            {
                result.WhitelistEnabled = string.Equals(property.Value.Trim(), "true",
                    StringComparison.OrdinalIgnoreCase);
            }
        }
        result.Online.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>覆盖写入白名单（服务器必须已停止）。</summary>
    public static void SaveWhitelist(string id, IEnumerable<WhitelistEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        List<WhitelistEntry> normalized = [];
        foreach (WhitelistEntry entry in entries)
        {
            string name;
            try
            {
                name = NormalizePlayerName(entry.Name);
            }
            catch (InvalidOperationException)
            {
                continue;
            }
            normalized.Add(new WhitelistEntry
            {
                Uuid = ResolveEntryUuid(entry.Uuid, name),
                Name = name,
            });
        }
        WritePlayersEntries(id, WhitelistFileName, normalized);
    }

    /// <summary>覆盖写入 OP 列表（服务器必须已停止）。</summary>
    public static void SaveOps(string id, IEnumerable<OpEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        List<OpEntry> normalized = [];
        foreach (OpEntry entry in entries)
        {
            string name = NormalizePlayerName(entry.Name);
            int level = entry.Level < 1 ? 4 : entry.Level > 4 ? 4 : entry.Level;
            normalized.Add(new OpEntry
            {
                Uuid = ResolveEntryUuid(entry.Uuid, name),
                Name = name,
                Level = level,
                BypassesPlayerLimit = entry.BypassesPlayerLimit,
            });
        }
        WritePlayersEntries(id, OpsFileName, normalized);
    }

    /// <summary>覆盖写入封禁名单（服务器必须已停止）。</summary>
    public static void SaveBanned(string id, IEnumerable<BannedPlayerEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        List<BannedPlayerEntry> normalized = [];
        foreach (BannedPlayerEntry entry in entries)
        {
            string name = NormalizePlayerName(entry.Name);
            normalized.Add(new BannedPlayerEntry
            {
                Name = name,
                Uuid = ResolveEntryUuid(entry.Uuid, name),
                Created = string.IsNullOrWhiteSpace(entry.Created) ? "1970-01-01 00:00:00 +0000" : entry.Created,
                Source = string.IsNullOrWhiteSpace(entry.Source) ? "Mofumofu" : entry.Source,
                Expires = string.IsNullOrWhiteSpace(entry.Expires) ? "forever" : entry.Expires,
                Reason = string.IsNullOrWhiteSpace(entry.Reason) ? "Banned by an operator." : entry.Reason,
            });
        }
        WritePlayersEntries(id, BannedFileName, normalized);
    }

    /// <summary>开关白名单（写 server.properties；服务器运行中会被拒绝）。</summary>
    public static void SetWhitelistEnabled(string id, bool enabled)
    {
        ServerProperties.Set(id,
            [new ServerProperty { Key = "white-list", Value = enabled ? "true" : "false" }]);
    }

    /// <summary>在运行中的服务器上执行一条玩家/权限指令（RCON 优先，退回控制台 stdin）。</summary>
    public static async Task RunCommandAsync(string id, string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException("指令为空");
        }
        if (!ServerManage.IsRunning(id))
        {
            throw new InvalidOperationException("服务器未在运行；请先启动，或直接编辑下方的名单");
        }
        await ServerManage.SendCommandAnyAsync(id, command).ConfigureAwait(false);
    }

    private static List<string> OnlinePlayerNames(string id)
    {
        ServerManage.ServerRuntime? runtime = ServerManage.FindRuntime(id);
        if (runtime is null)
        {
            return [];
        }
        RconConfiguration rcon;
        bool running;
        lock (runtime.Sync)
        {
            running = runtime.Status == ServerManage.StatusRunning;
            rcon = runtime.Rcon;
        }
        if (!running || !rcon.Enabled)
        {
            return [];
        }
        try
        {
            string response = RconClient.ExecuteAsync(rcon.Port, rcon.Password, "list")
                .GetAwaiter().GetResult();
            return ServerManage.ParsePlayerListNames(response);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static List<T> ReadPlayersFile<T>(string path)
    {
        List<T> result = [];
        if (!File.Exists(path))
        {
            return result;
        }
        try
        {
            List<T>? entries = JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), ServerJson.Options);
            return entries ?? result;
        }
        catch (Exception)
        {
            return result;
        }
    }

    private static void WritePlayersEntries<T>(string id, string fileName, List<T> entries)
    {
        ServerManage.ValidateId(id);
        if (ServerManage.IsRunning(id))
        {
            throw new InvalidOperationException(
                "服务器运行中：请用指令修改（启动器会自动改走 RCON / 控制台），停止后再改文件");
        }
        string payload = JsonSerializer.Serialize(entries, ServerJson.Options) + "\n";
        File.WriteAllText(Path.Combine(ServerManage.ServerDirectory(id), fileName), payload);
    }

    private static string NormalizePlayerName(string name)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (!PlayerNamePattern.IsMatch(trimmed))
        {
            throw new InvalidOperationException($"玩家名不合法：\"{name}\"（1–16 位字母、数字或下划线）");
        }
        return trimmed;
    }

    private static string ResolveEntryUuid(string uuid, string name)
    {
        string trimmed = (uuid ?? string.Empty).Trim().ToLowerInvariant();
        if (IsUuidLike(trimmed))
        {
            string compact = trimmed.Replace("-", string.Empty);
            return $"{compact[..8]}-{compact[8..12]}-{compact[12..16]}-{compact[16..20]}-{compact[20..32]}";
        }
        return OfflinePlayerUuid(name);
    }

    private static bool IsUuidLike(string value)
    {
        string compact = value.Replace("-", string.Empty);
        if (compact.Length != 32)
        {
            return false;
        }
        return compact.All(character => Uri.IsHexDigit(character));
    }

    /// <summary>离线模式 UUID：MD5("OfflinePlayer:"+name)，按 RFC 4122 设置版本位与变体位。</summary>
    public static string OfflinePlayerUuid(string name)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        string hex = Convert.ToHexString(hash).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
    }
}

/// <summary>一份备份的元信息。</summary>
public class ServerBackupInfo
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public long CreatedAt { get; set; }
    public bool Hot { get; set; }
}

/// <summary>备份策略。</summary>
public class BackupSettings
{
    public bool Enabled { get; set; }
    public int IntervalHours { get; set; } = 6;
    public int KeepCount { get; set; } = 5;
    public int KeepDays { get; set; }

    internal void Normalize()
    {
        if (IntervalHours < ServerBackup.MinimumIntervalHours)
        {
            IntervalHours = ServerBackup.MinimumIntervalHours;
        }
        if (KeepCount < 0)
        {
            KeepCount = 0;
        }
        if (KeepDays < 0)
        {
            KeepDays = 0;
        }
    }
}

/// <summary>
/// 服务器备份：热备份（运行中）+ 冷备份（已停止）+ 保留策略 + 定时任务。
/// 热备份靠服务端的 save-off / save-all flush 把世界刷盘并暂停写入，压缩期间不产生半写状态。
/// </summary>
public static class ServerBackup
{
    internal const int MinimumIntervalHours = 1;

    private const string BackupsDirectoryName = "backups";
    private const string HotPrefix = "hot-";
    private const string ColdPrefix = "cold-";
    private const long MaxTotalBytes = 8L << 30;
    private const int DefaultKeepCount = 5;
    private const int DefaultIntervalHours = 6;

    private static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly object SettingsLock = new();
    private static readonly object SchedulerLock = new();
    private static BackupSettings? _cachedSettings;
    private static int _schedulerStarted;

    private static string SettingsPath => Path.Combine(ServerManage.ServerRootDirectory, "backup-settings.json");

    /// <summary>当前备份策略。</summary>
    public static BackupSettings CurrentSettings()
    {
        lock (SettingsLock)
        {
            if (_cachedSettings is not null)
            {
                return _cachedSettings;
            }
            BackupSettings settings = new()
            {
                IntervalHours = DefaultIntervalHours,
                KeepCount = DefaultKeepCount,
            };
            try
            {
                if (File.Exists(SettingsPath))
                {
                    settings = JsonSerializer.Deserialize<BackupSettings>(
                                   File.ReadAllText(SettingsPath), ServerJson.Options) ?? settings;
                }
            }
            catch (Exception)
            {
                // 配置损坏时用默认值
            }
            settings.Normalize();
            _cachedSettings = settings;
            return settings;
        }
    }

    /// <summary>保存备份策略。</summary>
    public static void SaveSettings(BackupSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Normalize();
        lock (SettingsLock)
        {
            _cachedSettings = settings;
            try
            {
                Directory.CreateDirectory(ServerManage.ServerRootDirectory);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, ServerJson.Options));
            }
            catch (Exception e)
            {
                ServerManage.LogWarn($"保存备份策略失败：{e.Message}");
            }
        }
    }

    /// <summary>为服务器创建一份备份，返回备份信息。</summary>
    public static async Task<ServerBackupInfo> CreateAsync(string id, CancellationToken cancellationToken = default)
    {
        ServerManage.ValidateId(id);
        string directory = ServerManage.ServerDirectory(id);
        if (!Directory.Exists(directory))
        {
            throw new InvalidOperationException("服务器目录不存在");
        }

        bool running = ServerManage.IsRunning(id);
        bool worldSavingDisabled = false;
        try
        {
            if (running)
            {
                try
                {
                    await ServerManage.SendCommandAnyAsync(id, "save-off").ConfigureAwait(false);
                    worldSavingDisabled = true;
                }
                catch (Exception e)
                {
                    ServerManage.LogWarn(
                        $"服务器 {id} 无法暂停世界写入（{e.Message}），本次热备份可能包含未完全落盘的区块。");
                }
                await ServerManage.SendCommandAnyAsync(id, "save-all flush").ConfigureAwait(false);
                await Task.Delay(FlushDelay, cancellationToken).ConfigureAwait(false);
            }

            string backupsDirectory = Path.Combine(directory, BackupsDirectoryName);
            Directory.CreateDirectory(backupsDirectory);

            string prefix = running ? HotPrefix : ColdPrefix;
            string name = Path.GetFileName(UniqueBackupPath(backupsDirectory,
                $"{prefix}{DateTime.Now:yyyyMMdd-HHmmss}.zip"));
            string target = Path.Combine(backupsDirectory, name);
            string temporary = target + ".tmp";

            long written;
            try
            {
                written = await WriteArchiveAsync(directory, temporary, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, target);
            }
            catch
            {
                TryDeleteFile(temporary);
                throw;
            }

            long size = new FileInfo(target).Length;
            try
            {
                Prune(id);
            }
            catch (Exception e)
            {
                ServerManage.LogWarn($"清理旧备份失败：{e.Message}");
            }
            return new ServerBackupInfo
            {
                Name = name,
                Path = target,
                SizeBytes = size > 0 ? size : written,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Hot = running,
            };
        }
        finally
        {
            if (worldSavingDisabled)
            {
                try
                {
                    await ServerManage.SendCommandAnyAsync(id, "save-on").ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    ServerManage.LogWarn($"服务器 {id} 恢复世界写入失败：{e.Message}");
                }
            }
        }
    }

    /// <summary>列出全部备份（新→旧）。</summary>
    public static IReadOnlyList<ServerBackupInfo> List(string id)
    {
        List<ServerBackupInfo> result = [];
        if (!ServerManage.IsValidId(id))
        {
            return result;
        }
        string directory = Path.Combine(ServerManage.ServerDirectory(id), BackupsDirectoryName);
        string[] files;
        try
        {
            files = Directory.GetFiles(directory);
        }
        catch (Exception)
        {
            return result;
        }
        foreach (string file in files)
        {
            if (!file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            FileInfo info = new(file);
            result.Add(new ServerBackupInfo
            {
                Name = info.Name,
                Path = file,
                SizeBytes = info.Length,
                CreatedAt = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(),
                Hot = info.Name.StartsWith(HotPrefix, StringComparison.OrdinalIgnoreCase),
            });
        }
        result.Sort((left, right) => string.CompareOrdinal(right.Name, left.Name));
        return result;
    }

    /// <summary>删除一份备份。</summary>
    public static void Delete(string id, string name)
    {
        ServerManage.ValidateId(id);
        string safe = SafeBackupName(name);
        string target = Path.Combine(ServerManage.ServerDirectory(id), BackupsDirectoryName, safe);
        if (!File.Exists(target))
        {
            throw new InvalidOperationException($"备份不存在：{safe}");
        }
        File.Delete(target);
    }

    /// <summary>按保留策略清理旧备份，返回删除数量。</summary>
    public static int Prune(string id)
    {
        IReadOnlyList<ServerBackupInfo> backups = List(id);
        if (backups.Count == 0)
        {
            return 0;
        }
        BackupSettings settings = CurrentSettings();
        int keepCount = settings.KeepCount;
        int keepDays = settings.KeepDays;
        DateTime deadline = DateTime.UtcNow.AddDays(-keepDays);

        int removed = 0;
        for (int index = 0; index < backups.Count; index++)
        {
            ServerBackupInfo backup = backups[index];
            bool overCount = keepCount > 0 && index >= keepCount;
            bool overAge = keepDays > 0 &&
                           DateTimeOffset.FromUnixTimeSeconds(backup.CreatedAt).UtcDateTime < deadline;
            if ((!overCount && !overAge) || index == 0)
            {
                continue;
            }
            try
            {
                File.Delete(backup.Path);
                removed++;
            }
            catch (Exception)
            {
                return removed;
            }
        }
        return removed;
    }

    /// <summary>用指定备份覆盖服务器目录（要求服务器已停止；覆盖前会自动留一份安全备份）。</summary>
    public static async Task RestoreAsync(string id, string name, CancellationToken cancellationToken = default)
    {
        ServerManage.ValidateId(id);
        if (ServerManage.IsRunning(id))
        {
            throw new InvalidOperationException("请先停止服务器再恢复备份");
        }
        string safe = SafeBackupName(name);
        string directory = ServerManage.ServerDirectory(id);
        string archive = Path.Combine(directory, BackupsDirectoryName, safe);
        if (!File.Exists(archive))
        {
            throw new InvalidOperationException($"备份不存在：{safe}");
        }

        try
        {
            await CreateAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"恢复前的安全备份失败（已中止）：{e.Message}", e);
        }

        try
        {
            using ZipArchive zip = ZipFile.OpenRead(archive);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string target = SafeArchiveTarget(directory, entry.FullName);
                if (entry.FullName.EndsWith('/'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, true);
            }
        }
        catch (InvalidDataException e)
        {
            throw new InvalidOperationException($"打开备份失败：{e.Message}", e);
        }
    }

    /// <summary>启动自动备份巡检（应用启动时调用一次）。</summary>
    public static void StartScheduler(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _schedulerStarted, 1) != 0)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            using PeriodicTimer timer = new(TimeSpan.FromMinutes(10));
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    await RunScheduledBackupsAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 启动器退出
            }
        }, cancellationToken);
    }

    /// <summary>巡检一轮：对已到间隔的服务器做热/冷备份。</summary>
    public static async Task RunScheduledBackupsAsync(CancellationToken cancellationToken = default)
    {
        BackupSettings settings = CurrentSettings();
        if (!settings.Enabled)
        {
            return;
        }
        TimeSpan interval = TimeSpan.FromHours(settings.IntervalHours);
        foreach (ServerInfo server in ServerManage.ListServerInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ServerBackupInfo> backups = List(server.Id);
            if (backups.Count > 0 &&
                DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(backups[0].CreatedAt) < interval)
            {
                continue;
            }
            try
            {
                await CreateAsync(server.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                ServerManage.LogWarn($"自动备份 {server.Name} 失败：{e.Message}");
                ServerManage.AppendConsole(server.Id,
                    $"[{DateTime.Now:HH:mm:ss}] 自动备份失败：{e.Message}");
            }
        }
    }

    internal static string SafeBackupName(string name)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException("备份名为空");
        }
        if (trimmed != Path.GetFileName(trimmed) || trimmed.IndexOfAny(new[] { '/', '\\' }) >= 0)
        {
            throw new InvalidOperationException("备份名不合法");
        }
        if (!trimmed.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("备份名不合法");
        }
        return trimmed;
    }

    internal static string SafeArchiveTarget(string root, string name)
    {
        string cleaned = ServerFiles.CleanZipPath(name);
        if (cleaned.Length == 0)
        {
            throw new InvalidOperationException($"备份内包含越界路径：{name}");
        }
        string rootFull = Path.GetFullPath(root);
        string target = Path.GetFullPath(Path.Combine(rootFull, cleaned.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"备份内包含越界路径：{name}");
        }
        return target;
    }

    private static async Task<long> WriteArchiveAsync(string directory, string target,
        CancellationToken cancellationToken)
    {
        long written = 0;
        using FileStream file = File.Create(target);
        using ZipArchive archive = new(file, ZipArchiveMode.Create);
        foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(directory, path);
            string top = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (top == BackupsDirectoryName || top == "logs")
            {
                continue;
            }
            if (path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".mofu-download", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            ZipArchiveEntry entry = archive.CreateEntry(relative.Replace('\\', '/'), CompressionLevel.Optimal);
            await using Stream entryStream = entry.Open();
            await using FileStream source = File.OpenRead(path);
            await source.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
            written += source.Length;
            if (written > MaxTotalBytes)
            {
                throw new InvalidOperationException($"备份体积超过上限（{MaxTotalBytes >> 30} GB），已中止");
            }
        }
        return written;
    }

    private static string UniqueBackupPath(string directory, string name)
    {
        string baseName = name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;
        string candidate = name;
        for (int index = 1; index <= 1000; index++)
        {
            string path = Path.Combine(directory, candidate);
            if (!File.Exists(path))
            {
                return path;
            }
            candidate = $"{baseName}-{index}.zip";
        }
        return Path.Combine(directory, candidate);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 忽略
        }
    }
}

/// <summary>服务器目录文件列表项。</summary>
public class ServerFileEntry
{
    public string Name { get; set; } = string.Empty;
    public bool IsDir { get; set; }
    public long Size { get; set; }
    public DateTime ModTime { get; set; }
}

/// <summary>服务器目录的文件管理：列表、文本读写（内置编辑器）、存档/模组/插件导入。</summary>
public static class ServerFiles
{
    private const long TextFileLimit = 2L << 20;

    /// <summary>列出目录（文件夹在前，名称排序）。</summary>
    public static IReadOnlyList<ServerFileEntry> List(string id, string relativePath)
    {
        string target = ResolvePath(id, relativePath);
        List<ServerFileEntry> result = [];
        foreach (FileSystemInfo entry in new DirectoryInfo(target).EnumerateFileSystemInfos())
        {
            result.Add(new ServerFileEntry
            {
                Name = entry.Name,
                IsDir = entry is DirectoryInfo,
                Size = entry is FileInfo file ? file.Length : 0,
                ModTime = entry.LastWriteTimeUtc,
            });
        }
        result.Sort((left, right) =>
        {
            if (left.IsDir != right.IsDir)
            {
                return left.IsDir ? -1 : 1;
            }
            return string.CompareOrdinal(left.Name, right.Name);
        });
        return result;
    }

    /// <summary>读取文本文件（≤2MB，供内置编辑器）。</summary>
    public static string ReadText(string id, string relativePath)
    {
        string target = ResolvePath(id, relativePath);
        FileInfo info = new(target);
        if (!info.Exists)
        {
            throw new InvalidOperationException("文件不存在");
        }
        if (info.Length > TextFileLimit)
        {
            throw new InvalidOperationException("文件过大（>2MB），请在服务器目录中用外部编辑器打开");
        }
        return File.ReadAllText(target);
    }

    /// <summary>写回文本文件。</summary>
    public static void WriteText(string id, string relativePath, string content)
    {
        string target = ResolvePath(id, relativePath);
        File.WriteAllText(target, content ?? string.Empty);
    }

    /// <summary>新建文本文件（内容为空，已存在时报错）。</summary>
    public static void CreateTextFile(string id, string relativePath)
    {
        string target = ResolvePath(id, relativePath);
        string root = Path.GetFullPath(ServerManage.ServerDirectory(id));
        if (string.Equals(Path.GetFullPath(target), root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("文件名不能为空");
        }
        if (File.Exists(target) || Directory.Exists(target))
        {
            throw new InvalidOperationException("同名文件已存在");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? ServerManage.ServerDirectory(id));
        File.WriteAllText(target, string.Empty);
    }

    /// <summary>新建文件夹（已存在时报错）。</summary>
    public static void CreateDirectory(string id, string relativePath)
    {
        string target = ResolvePath(id, relativePath);
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new InvalidOperationException("同名文件或文件夹已存在");
        }
        Directory.CreateDirectory(target);
    }

    /// <summary>重命名文件/文件夹，返回新的相对路径。</summary>
    public static string Rename(string id, string relativePath, string newName)
    {
        string source = ResolvePath(id, relativePath);
        ValidateEntryName(newName);

        string parent = Path.GetDirectoryName(source) ?? ServerManage.ServerDirectory(id);
        string destination = Path.Combine(parent, newName.Trim());
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new InvalidOperationException("目标名称已存在");
        }

        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination);
        }

        string parentRelative = Path.GetDirectoryName(relativePath) ?? string.Empty;
        return string.IsNullOrEmpty(parentRelative) ? newName.Trim() : Path.Combine(parentRelative, newName.Trim());
    }

    /// <summary>删除文件或文件夹（文件夹递归删除，服务器根目录不可删）。</summary>
    public static void Delete(string id, string relativePath)
    {
        string target = ResolvePath(id, relativePath);
        string root = Path.GetFullPath(ServerManage.ServerDirectory(id));
        if (string.Equals(Path.GetFullPath(target), root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("不能删除服务器根目录");
        }

        if (Directory.Exists(target))
        {
            Directory.Delete(target, true);
        }
        else if (File.Exists(target))
        {
            File.Delete(target);
        }
    }

    private static void ValidateEntryName(string name)
    {
        string trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed is "." or ".." ||
            trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            trimmed.Contains('/') || trimmed.Contains('\\'))
        {
            throw new InvalidOperationException("名称不合法");
        }
    }

    /// <summary>导入存档压缩包（zip，顶层为 level.dat 或单层目录）。</summary>
    public static void ImportWorld(string id, string zipPath)
    {
        if (ServerManage.IsRunning(id))
        {
            throw new InvalidOperationException("服务器运行中，请先停止再导入存档");
        }
        ServerManage.ValidateId(id);
        string worldDirectory = Path.Combine(ServerManage.ServerDirectory(id), WorldDirectoryName(id));
        Directory.CreateDirectory(worldDirectory);
        ExtractZipRoot(zipPath, worldDirectory);
    }

    /// <summary>复制 mod jar 到 mods/（Fabric/NeoForge）。</summary>
    public static void ImportMod(string id, string jarPath)
    {
        ServerConfig config = ServerManage.LoadServerConfig(id)
                              ?? throw new InvalidOperationException("服务器不存在");
        if (!CoreSupportsMods(config.Core))
        {
            throw new InvalidOperationException($"核心 {config.Core} 不支持模组（仅 Fabric/NeoForge）");
        }
        CopyIntoServerDirectory(id, jarPath, "mods");
    }

    /// <summary>复制插件 jar 到 plugins/（Paper）。</summary>
    public static void ImportPlugin(string id, string jarPath)
    {
        ServerConfig config = ServerManage.LoadServerConfig(id)
                              ?? throw new InvalidOperationException("服务器不存在");
        if (config.Core != ServerManage.CorePaper)
        {
            throw new InvalidOperationException($"核心 {config.Core} 不支持插件（仅 Paper）");
        }
        CopyIntoServerDirectory(id, jarPath, "plugins");
    }

    internal static string ResolvePath(string id, string relativePath)
    {
        ServerManage.ValidateId(id);
        string root = Path.GetFullPath(ServerManage.ServerDirectory(id));
        string combined = string.IsNullOrWhiteSpace(relativePath)
            ? root
            : Path.GetFullPath(Path.Combine(root, relativePath));
        if (!string.Equals(combined, root, StringComparison.OrdinalIgnoreCase) &&
            !combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("路径越出服务器目录");
        }
        return combined;
    }

    private static string WorldDirectoryName(string id)
    {
        foreach (ServerProperty property in ServerProperties.GetValuesOrEmpty(ServerManage.ServerDirectory(id)))
        {
            if (property.Key == "level-name" && !string.IsNullOrWhiteSpace(property.Value))
            {
                return property.Value.Trim();
            }
        }
        return "world";
    }

    private static bool CoreSupportsMods(string core)
    {
        return core is ServerManage.CoreFabric or ServerManage.CoreNeoForge;
    }

    private static void CopyIntoServerDirectory(string id, string sourcePath, string subdirectory)
    {
        if (ServerManage.IsRunning(id))
        {
            throw new InvalidOperationException("服务器运行中，请先停止再导入");
        }
        ServerManage.ValidateId(id);
        string destinationDirectory = Path.Combine(ServerManage.ServerDirectory(id), subdirectory);
        Directory.CreateDirectory(destinationDirectory);
        File.Copy(sourcePath, Path.Combine(destinationDirectory, Path.GetFileName(sourcePath)), true);
    }

    private static void ExtractZipRoot(string zipPath, string target)
    {
        using ZipArchive archive = OpenArchive(zipPath);
        List<(string Name, ZipArchiveEntry Entry)> entries = [];
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string cleaned = CleanZipPath(entry.FullName);
            if (cleaned.Length == 0)
            {
                continue;
            }
            entries.Add((cleaned, entry));
        }

        string root = string.Empty;
        if (entries.Count > 0)
        {
            string first = entries[0].Name.Split('/')[0];
            bool only = true;
            bool nested = false;
            foreach ((string name, _) in entries)
            {
                if (name.StartsWith(first + "/", StringComparison.Ordinal))
                {
                    nested = true;
                }
                if (name != first && !name.StartsWith(first + "/", StringComparison.Ordinal))
                {
                    only = false;
                    break;
                }
            }
            if (only && first.Length > 0 && nested)
            {
                root = first + "/";
            }
        }

        foreach ((string name, ZipArchiveEntry entry) in entries)
        {
            string relative = name.StartsWith(root, StringComparison.Ordinal) ? name[root.Length..] : name;
            if (relative.Length == 0)
            {
                continue;
            }
            string destination = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            CopyZipEntry(entry, destination);
        }
    }

    internal static ZipArchive OpenArchive(string path)
    {
        try
        {
            return ZipFile.OpenRead(path);
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"不是有效的压缩包：{e.Message}", e);
        }
    }

    internal static string CleanZipPath(string name)
    {
        string normalized = (name ?? string.Empty).Trim().Replace('\\', '/');
        if (normalized.Length == 0)
        {
            return string.Empty;
        }
        if (normalized.StartsWith('/') || (normalized.Length > 1 && normalized[1] == ':'))
        {
            throw new InvalidOperationException("压缩包内路径非法");
        }
        List<string> segments = [];
        foreach (string segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    throw new InvalidOperationException("压缩包内路径越出目标目录");
                }
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        return string.Join('/', segments);
    }

    internal static void CopyZipEntry(ZipArchiveEntry entry, string destination)
    {
        using Stream source = entry.Open();
        using FileStream target = File.Create(destination);
        source.CopyTo(target);
    }
}

/// <summary>服务端内容目录里的一个条目。</summary>
public class ServerContentEntry
{
    public string Name { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public bool Enabled { get; set; }
    public string Path { get; set; } = string.Empty;
}

/// <summary>服务端内容管理：mods / plugins 的列表、启停、删除与安装。</summary>
public static class ServerContent
{
    public const string KindMods = "mods";
    public const string KindPlugins = "plugins";

    private const string DisabledSuffix = ".disabled";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>服务器核心对应的内容目录类型。</summary>
    public static string KindForCore(string core)
    {
        return core switch
        {
            ServerManage.CoreFabric or ServerManage.CoreNeoForge => KindMods,
            ServerManage.CorePaper => KindPlugins,
            ServerManage.CoreVanilla => throw new InvalidOperationException("原版服务端不支持模组或插件"),
            _ => throw new InvalidOperationException($"未知的服务器核心：{core}"),
        };
    }

    /// <summary>列出内容目录里的 jar（含 .disabled）。</summary>
    public static IReadOnlyList<ServerContentEntry> List(string id, string kind)
    {
        List<ServerContentEntry> result = [];
        string directory;
        try
        {
            directory = ContentDirectory(id, kind);
        }
        catch (Exception)
        {
            return result;
        }
        if (!Directory.Exists(directory))
        {
            return result;
        }
        foreach (string file in Directory.GetFiles(directory))
        {
            string fileName = Path.GetFileName(file);
            string lower = fileName.ToLowerInvariant();
            if (!lower.EndsWith(".jar", StringComparison.Ordinal) &&
                !lower.EndsWith(".jar" + DisabledSuffix, StringComparison.Ordinal))
            {
                continue;
            }
            FileInfo info = new(file);
            result.Add(new ServerContentEntry
            {
                Name = DisplayContentName(fileName),
                FileName = fileName,
                SizeBytes = info.Length,
                Enabled = !lower.EndsWith(DisabledSuffix, StringComparison.Ordinal),
                Path = file,
            });
        }
        result.Sort((left, right) =>
            string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>启用/停用：靠 .disabled 后缀改名（服务端只加载 .jar）。</summary>
    public static void SetEnabled(string id, string kind, string fileName, bool enabled)
    {
        string directory = ContentDirectory(id, kind);
        string safe = SafeContentFileName(fileName);
        string source = Path.Combine(directory, safe);
        bool disabled = safe.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);

        if (!File.Exists(source))
        {
            string alternate = disabled ? safe[..^DisabledSuffix.Length] : safe + DisabledSuffix;
            if (!File.Exists(Path.Combine(directory, alternate)))
            {
                throw new InvalidOperationException($"文件不存在：{safe}");
            }
            if (enabled == !disabled)
            {
                return;
            }
            File.Move(Path.Combine(directory, alternate), source);
            return;
        }

        if (enabled && disabled)
        {
            string target = safe[..^DisabledSuffix.Length];
            if (File.Exists(Path.Combine(directory, target)))
            {
                throw new InvalidOperationException($"同名文件已存在：{target}");
            }
            File.Move(source, Path.Combine(directory, target));
            return;
        }
        if (!enabled && !disabled)
        {
            File.Move(source, source + DisabledSuffix);
        }
    }

    /// <summary>删除一个内容文件。</summary>
    public static void Delete(string id, string kind, string fileName)
    {
        string directory = ContentDirectory(id, kind);
        string safe = SafeContentFileName(fileName);
        string target = Path.Combine(directory, safe);
        if (!File.Exists(target))
        {
            throw new InvalidOperationException($"文件不存在：{safe}");
        }
        File.Delete(target);
    }

    /// <summary>下载一个 jar 到内容目录，返回实际写入的文件名（同名不覆盖）。</summary>
    public static async Task<string> InstallFromUrlAsync(string id, string kind, string downloadUrl,
        string fileName, CancellationToken cancellationToken = default)
    {
        string directory = ContentDirectory(id, kind);
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            throw new InvalidOperationException("下载地址为空");
        }
        string safe = SafeContentFileName(fileName);
        if (!safe.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"只支持安装 .jar 文件（收到 {safe}）");
        }
        Directory.CreateDirectory(directory);
        string target = UniqueContentPath(directory, safe);
        string temporary = target + ".part";
        try
        {
            using HttpResponseMessage response = await Http
                .GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using FileStream destination = File.Create(temporary);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, target);
        }
        catch
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (Exception)
            {
                // 忽略
            }
            throw;
        }
        return Path.GetFileName(target);
    }

    internal static string ContentDirectory(string id, string kind)
    {
        ServerManage.ValidateId(id);
        if (kind != KindMods && kind != KindPlugins)
        {
            throw new InvalidOperationException($"未知的内容类型：{kind}");
        }
        return Path.Combine(ServerManage.ServerDirectory(id), kind);
    }

    private static string DisplayContentName(string fileName)
    {
        string name = fileName;
        if (name.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^DisabledSuffix.Length];
        }
        if (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }
        return name;
    }

    private static string SafeContentFileName(string fileName)
    {
        string trimmed = (fileName ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException("文件名为空");
        }
        if (trimmed != Path.GetFileName(trimmed) || trimmed.IndexOfAny(new[] { '/', '\\' }) >= 0)
        {
            throw new InvalidOperationException("文件名不合法");
        }
        return trimmed;
    }

    private static string UniqueContentPath(string directory, string fileName)
    {
        string extension = Path.GetExtension(fileName);
        string baseName = fileName[..^extension.Length];
        string candidate = fileName;
        for (int index = 1; index <= 1000; index++)
        {
            string path = Path.Combine(directory, candidate);
            if (!File.Exists(path))
            {
                return path;
            }
            candidate = $"{baseName}-{index}{extension}";
        }
        return Path.Combine(directory, candidate);
    }
}

/// <summary>整包导出/导入的进度快照（Done/Total 为字节数）。</summary>
public class ServerTransferProgress
{
    [JsonPropertyName("serverId")] public string ServerId { get; set; } = string.Empty;
    [JsonPropertyName("phase")] public string Phase { get; set; } = string.Empty;
    [JsonPropertyName("done")] public long Done { get; set; }
    [JsonPropertyName("total")] public long Total { get; set; }
}

/// <summary>服务器整包传输：.mofuser 文件（zip 容器）的导出与导入。</summary>
public static class ServerPackage
{
    /// <summary>把服务器目录整包压缩为 .mofuser 文件（运行中拒绝）。</summary>
    public static async Task ExportAsync(string id, string destinationZip,
        IProgress<ServerTransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ServerManage.ValidateId(id);
        if (ServerManage.IsRunning(id))
        {
            throw new InvalidOperationException("服务器运行中，请先停止");
        }
        string root = ServerManage.ServerDirectory(id);
        if (ServerManage.LoadServerConfig(id) is null)
        {
            throw new InvalidOperationException("服务器不存在");
        }

        List<(string Path, string Relative, long Size)> tasks = [];
        long totalBytes = 0;
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            FileInfo info = new(path);
            string relative = Path.GetRelativePath(root, path);
            tasks.Add((path, relative, info.Length));
            totalBytes += info.Length;
        }

        TransferReporter reporter = new(id, "export", totalBytes, progress);
        try
        {
            using FileStream output = File.Create(destinationZip);
            using ZipArchive archive = new(output, ZipArchiveMode.Create);
            long written = 0;
            foreach ((string path, string relative, long size) in tasks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ZipArchiveEntry entry = archive.CreateEntry(relative.Replace('\\', '/'),
                    CompressionLevel.Optimal);
                await using Stream entryStream = entry.Open();
                await using FileStream source = File.OpenRead(path);
                await source.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
                written += size;
                reporter.Report(written);
            }
            reporter.Report(written, true);
        }
        catch
        {
            try
            {
                if (File.Exists(destinationZip))
                {
                    File.Delete(destinationZip);
                }
            }
            catch (Exception)
            {
                // 忽略
            }
            throw;
        }
    }

    /// <summary>从 .mofuser 包恢复服务器，返回新服务器 id。</summary>
    public static async Task<string> ImportAsync(string zipPath,
        IProgress<ServerTransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Dictionary<string, ZipArchiveEntry> entries = new(StringComparer.Ordinal);
        ServerConfig? config = null;
        long totalBytes = 0;
        using (ZipArchive archive = ServerFiles.OpenArchive(zipPath))
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string cleaned = ServerFiles.CleanZipPath(entry.FullName);
                if (cleaned.Length == 0 || entries.ContainsKey(cleaned))
                {
                    continue;
                }
                entries[cleaned] = entry;
                if (!entry.FullName.EndsWith('/'))
                {
                    totalBytes += entry.Length;
                }
                if (cleaned == "server.json")
                {
                    await using Stream stream = entry.Open();
                    config = await JsonSerializer
                        .DeserializeAsync<ServerConfig>(stream, ServerJson.Options, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (config is null)
            {
                throw new InvalidOperationException("不是有效的 .mofuser 服务器包（缺少 server.json）");
            }

            string name = ServerManage.SanitizeServerName(config.Name);
            if (string.IsNullOrEmpty(name))
            {
                name = "server";
            }
            string id = UniqueDirectory(name);
            string directory = Path.Combine(ServerManage.ServerRootDirectory, id);
            Directory.CreateDirectory(directory);

            try
            {
                TransferReporter reporter = new(id, "import", totalBytes, progress);
                long written = 0;
                foreach ((string relative, ZipArchiveEntry entry) in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.FullName.EndsWith('/'))
                    {
                        continue;
                    }
                    string destination = Path.Combine(directory,
                        relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    ServerFiles.CopyZipEntry(entry, destination);
                    written += entry.Length;
                    reporter.Report(written);
                }
                reporter.Report(written, true);

                config.Id = id;
                ServerManage.SaveServerConfigToDirectory(directory, config);
                return id;
            }
            catch
            {
                try
                {
                    Directory.Delete(directory, true);
                }
                catch (Exception)
                {
                    // 忽略
                }
                throw;
            }
        }
    }

    private static string UniqueDirectory(string name)
    {
        if (!Directory.Exists(Path.Combine(ServerManage.ServerRootDirectory, name)))
        {
            return name;
        }
        for (int index = 1; ; index++)
        {
            string candidate = $"{name}-{index}";
            if (!Directory.Exists(Path.Combine(ServerManage.ServerRootDirectory, candidate)))
            {
                return candidate;
            }
        }
    }

    private sealed class TransferReporter(
        string serverId, string phase, long total, IProgress<ServerTransferProgress>? progress)
    {
        private int _lastPercent = -1;

        public void Report(long done, bool final = false)
        {
            if (progress is null || total <= 0)
            {
                return;
            }
            int percent = (int)(done * 100 / total);
            if (percent == _lastPercent && !final)
            {
                return;
            }
            _lastPercent = percent;
            progress.Report(new ServerTransferProgress
            {
                ServerId = serverId,
                Phase = phase,
                Done = done,
                Total = total,
            });
        }
    }
}

/// <summary>残留进程清理：启动器异常退出后清理仍在占用服务器目录的 Java 进程（.mofu-pid 记录）。</summary>
public static class OrphanCleaner
{
    private const string PidFileName = ".mofu-pid";

    /// <summary>结束占用服务器目录的残留进程，返回给控制台展示的说明行。</summary>
    public static List<string> KillOrphanServerProcesses(string directory)
    {
        List<string> notes = [];
        string absolute;
        try
        {
            absolute = Path.GetFullPath(directory);
        }
        catch (Exception)
        {
            return notes;
        }

        int pid = ReadPidFile(absolute);
        if (pid > 0)
        {
            if (ProcessAlive(pid))
            {
                if (KillProcessTree(pid))
                {
                    notes.Add($"已结束残留的服务器进程 (PID {pid})，它仍在占用服务器目录");
                }
                else
                {
                    notes.Add($"检测到占用服务器目录的残留进程 (PID {pid}) 结束失败");
                }
            }
            RemovePidFile(absolute);
        }

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && ServerFilesLocked(absolute))
        {
            Thread.Sleep(200);
        }
        return notes;
    }

    /// <summary>写入进程 pid 记录。</summary>
    public static void WritePidFile(string directory, int pid)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, PidFileName), pid.ToString());
        }
        catch (Exception e)
        {
            ServerManage.LogWarn($"写入 pid 文件失败：{e.Message}");
        }
    }

    /// <summary>删除进程 pid 记录。</summary>
    public static void RemovePidFile(string directory)
    {
        try
        {
            string path = Path.Combine(directory, PidFileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 忽略
        }
    }

    /// <summary>判断 pid 对应的进程是否还活着。</summary>
    public static bool ProcessAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>结束进程及其全部子进程（Java 可能经由转发器启动，必须杀整棵树）。</summary>
    public static bool KillProcessTree(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            return true;
        }
        catch (Exception)
        {
            // taskkill 兜底
        }
        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = "taskkill",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("/PID");
            startInfo.ArgumentList.Add(pid.ToString());
            startInfo.ArgumentList.Add("/T");
            startInfo.ArgumentList.Add("/F");
            using Process? taskkill = Process.Start(startInfo);
            taskkill?.WaitForExit(5000);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>结束一个进程对象及其子进程。</summary>
    public static void KillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            try
            {
                process.Kill();
            }
            catch (Exception)
            {
                // 进程已经退出
            }
        }
    }

    private static int ReadPidFile(string directory)
    {
        try
        {
            string path = Path.Combine(directory, PidFileName);
            if (!File.Exists(path))
            {
                return 0;
            }
            return int.TryParse(File.ReadAllText(path).Trim(), out int pid) ? pid : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static bool ServerFilesLocked(string directory)
    {
        string path = Path.Combine(directory, "logs", "latest.log");
        try
        {
            using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.None);
            return false;
        }
        catch (Exception)
        {
            return File.Exists(path) || Directory.Exists(Path.GetDirectoryName(path));
        }
    }
}
