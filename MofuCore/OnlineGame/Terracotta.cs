using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using MofuCore;

namespace MofuCore.OnlineGame;

/// <summary>
/// 适配陶瓦联机功能，可以直接与他人进行联机。
/// 注：陶瓦联机实际上与本项目没有任何关系，Terracotta是第三方软件，本项目尊重所有关于Terracotta的版权。
/// 仅做客户端适配：按官方约定的 `terracotta --hmcl &lt;文件&gt;` 拉起本机服务，
/// 读端口文件后用 rocket 的 /state 系列接口驱动状态机。
/// </summary>
public class Terracotta
{
    /// <summary>项目主页。</summary>
    public const string Homepage = "https://github.com/burningtnt/Terracotta";

    /// <summary>发行包下载页。</summary>
    public const string ReleasesUrl = "https://github.com/burningtnt/Terracotta/releases";

    /// <summary>国内镜像发行页。</summary>
    public const string MirrorReleasesUrl = "https://gitee.com/burningtnt/Terracotta/releases";

    /// <summary>空闲，可以建房或加入。</summary>
    public const string StateIdle = "idle";

    /// <summary>准备中（拉起进程 / 等待对端）。</summary>
    public const string StateStarting = "starting";

    /// <summary>我是房主，房间已就绪。</summary>
    public const string StateHosting = "hosting";

    /// <summary>我是房客，已接入房主的网络。</summary>
    public const string StateJoined = "joined";

    /// <summary>出错，需要用户处理。</summary>
    public const string StateError = "error";

    private const string Provider = "terracotta";
    private const string LatestJsonUrl = "https://api.github.com/repos/burningtnt/Terracotta/releases/latest";
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PortPollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan StateInterval = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly HttpClient LocalHttp = new() { Timeout = RequestTimeout };
    private static readonly HttpClient RemoteHttp = new() { Timeout = TimeSpan.FromMinutes(2) };

    private readonly object _gate = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    private string? _binaryPath;
    private int _port;
    private string _version = string.Empty;
    private bool _running;
    private bool _managed;
    private Process? _process;
    private TerracottaStatus _status;
    private long _since;
    private CancellationTokenSource? _watcher;

    /// <param name="binaryPath">手动指定的陶瓦联机可执行文件路径；为空时自动探测。</param>
    /// <param name="storageDirectory">启动器数据目录（自动安装与自动探测的根）；为空时使用默认目录。</param>
    public Terracotta(string? binaryPath = null, string? storageDirectory = null)
    {
        _binaryPath = string.IsNullOrWhiteSpace(binaryPath) ? null : binaryPath!.Trim();
        StorageDirectory = string.IsNullOrWhiteSpace(storageDirectory) ? DefaultStorageDirectory : storageDirectory!;
        _status = new TerracottaStatus
        {
            Provider = Provider,
            State = StateIdle,
            Phase = "空闲",
            Tip = "点「创建房间」开一局，或粘贴朋友的房间码加入。",
        };
    }

    /// <summary>默认的启动器数据目录。</summary>
    public static string DefaultStorageDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mofumofu");

    public string StorageDirectory { get; }

    /// <summary>会话状态变更回调（前端据此刷新联机页）。</summary>
    public Action<TerracottaStatus>? StatusChanged { get; set; }

    /// <summary>过程日志回调。</summary>
    public Action<string>? Log { get; set; }

    /// <summary>手动指定的可执行文件路径（空 = 自动探测）。</summary>
    public string? BinaryPath
    {
        get => _binaryPath;
        set => _binaryPath = string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
    }

    /// <summary>当前会话快照。</summary>
    public TerracottaStatus CurrentStatus
    {
        get
        {
            lock (_gate)
            {
                return _status.Clone();
            }
        }
    }

    // ------------------------------------------------------------------
    // 信息 / 运行时
    // ------------------------------------------------------------------

    /// <summary>静态介绍 + 当前可用性（可执行文件是否存在）。</summary>
    public TerracottaProviderInfo GetInfo()
    {
        TerracottaProviderInfo info = new()
        {
            Id = Provider,
            Name = "陶瓦联机",
            Summary = "基于 EasyTier 的虚拟局域网：贴一个房间码就能连，不需要公网 IP，也不用改路由器。",
            Homepage = Homepage,
            HostNote = "先进入存档 → Esc → 对局域网开放，再回到这里点「创建房间」；陶瓦会自动发现你的世界并生成房间码。",
            JoinNote = "把房主发来的房间码粘进来即可加入；连上后在游戏里直连 127.0.0.1 就能进服。",
            NeedsMod = false,
            GuestNeedsMod = false,
        };

        if (ResolveBinary(out _) is null)
        {
            info.Ready = false;
            info.Hint = "本机还没有陶瓦联机：请先下载并解压，再在右侧设置里选中它的可执行文件。";
        }
        else
        {
            info.Ready = true;
            info.Hint = "已找到可执行文件，路径见右侧设置。";
        }

        return info;
    }

    /// <summary>本机守护进程状态。</summary>
    public TerracottaRuntime GetRuntime()
    {
        lock (_gate)
        {
            return new TerracottaRuntime
            {
                Provider = Provider,
                Running = _running,
                Managed = _managed,
                Version = _version,
                Binary = ResolveBinary(out _) ?? string.Empty,
                Port = _port,
            };
        }
    }

    // ------------------------------------------------------------------
    // 建房 / 加入 / 退出
    // ------------------------------------------------------------------

    /// <summary>建房：让陶瓦扫描本机开放的局域网世界并生成房间码。</summary>
    public async Task HostAsync(string? player = null, CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Begin("正在准备陶瓦联机…", "第一次使用会先拉起本机服务，可能要几秒钟。");
            await EnsureServerAsync(cancellationToken).ConfigureAwait(false);
            Begin("正在寻找局域网世界…", "还没找到世界？先进入存档 → Esc → 对局域网开放，陶瓦会自动发现它。");

            Dictionary<string, string> query = [];
            if (!string.IsNullOrWhiteSpace(player))
            {
                query["player"] = player!.Trim();
            }

            (_, _, Exception? error) = await RequestAsync("/state/scanning", query, cancellationToken)
                .ConfigureAwait(false);
            if (error is not null)
            {
                Exception wrapped = new InvalidOperationException("创建房间失败：" + error.Message, error);
                Fail(wrapped);
                throw wrapped;
            }

            StartWatcher();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>加入房间：房间码形态不做假设，逐个候选尝试。</summary>
    public async Task JoinAsync(string room, string? player = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> variants = RoomCode.Variants(room);
        if (variants.Count == 0)
        {
            InvalidOperationException empty = new("请先填写房主给你的房间码");
            Fail(empty);
            throw empty;
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Begin("正在准备陶瓦联机…", "第一次使用会先拉起本机服务，可能要几秒钟。");
            await EnsureServerAsync(cancellationToken).ConfigureAwait(false);
            Begin("正在连接房间…", "正在与房主建立虚拟局域网，第一次连接通常需要十几秒。");

            Dictionary<string, string> query = [];
            if (!string.IsNullOrWhiteSpace(player))
            {
                query["player"] = player!.Trim();
            }

            Exception? lastError = null;
            foreach (string variant in variants)
            {
                query["room"] = variant;
                (_, int statusCode, Exception? error) = await RequestAsync("/state/guesting", query, cancellationToken)
                    .ConfigureAwait(false);
                if (error is null)
                {
                    StartWatcher();
                    return;
                }

                if (statusCode == (int)HttpStatusCode.BadRequest)
                {
                    lastError = new InvalidOperationException($"房间码 {variant} 没有被接受");
                    continue;
                }

                lastError = error;
                break;
            }

            Exception wrapped = new InvalidOperationException(
                "加入房间失败：" + (lastError?.Message ?? "房间码无效"), lastError);
            Fail(wrapped);
            throw wrapped;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>退出会话：把状态机拉回空闲，但保留守护进程（马上再开一局更快）。</summary>
    public async Task LeaveAsync(CancellationToken cancellationToken = default)
    {
        StopWatcher();

        int port;
        bool hadSession;
        lock (_gate)
        {
            hadSession = _since != 0;
            _since = 0;
            port = _port;
        }

        if (hadSession && port != 0)
        {
            _ = await RequestAsync("/state/ide", null, cancellationToken).ConfigureAwait(false);
        }

        Publish(new TerracottaStatus
        {
            Provider = Provider,
            State = StateIdle,
            Phase = "空闲",
            Tip = "点「创建房间」开一局，或粘贴朋友的房间码加入。",
        });
    }

    /// <summary>结束本机守护进程（用户在界面上显式要求；会一并关掉用户自己开的窗口）。</summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        StopWatcher();

        int port;
        lock (_gate)
        {
            port = _port;
            _since = 0;
        }

        Exception? error = null;
        if (port != 0)
        {
            (_, _, Exception? requestError) = await RequestAsync(
                "/panic", new Dictionary<string, string> { ["peaceful"] = "true" }, cancellationToken)
                .ConfigureAwait(false);
            if (requestError is not null)
            {
                error = new InvalidOperationException("关闭陶瓦联机失败：" + requestError.Message, requestError);
            }
        }

        lock (_gate)
        {
            _port = 0;
            _version = string.Empty;
            _running = false;
            _managed = false;
            _process = null;
        }

        Publish(new TerracottaStatus
        {
            Provider = Provider,
            State = StateIdle,
            Phase = "空闲",
            Tip = "点「创建房间」开一局，或粘贴朋友的房间码加入。",
        });

        if (error is not null)
        {
            throw error;
        }
    }

    /// <summary>应用退出：只停本进程的轮询，守护进程按设计常驻（下次进来直接复用）。</summary>
    public void Close()
    {
        StopWatcher();
    }

    // ------------------------------------------------------------------
    // 进程与端口
    // ------------------------------------------------------------------

    /// <summary>定位陶瓦联机可执行文件：用户指定 → 启动器数据目录 → PATH。</summary>
    private string? ResolveBinary(out string? error)
    {
        error = null;
        string? custom = BinaryPath;
        if (!string.IsNullOrWhiteSpace(custom))
        {
            if (File.Exists(custom))
            {
                return custom;
            }

            error = "设置里指定的陶瓦联机文件不存在：" + custom;
            return null;
        }

        string[] names = BinaryNames();
        string installDirectory = Path.Combine(StorageDirectory, "terracotta");
        foreach (string name in names)
        {
            string candidate = Path.Combine(installDirectory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        if (LocateBinary(installDirectory, out string? located))
        {
            return located;
        }

        foreach (string name in names)
        {
            string? path = FindOnPath(name);
            if (path is not null)
            {
                return path;
            }
        }

        error = $"没有找到陶瓦联机（Terracotta）可执行文件。请到 {ReleasesUrl} 下载后解压，" +
                $"再在右侧「陶瓦联机」设置里选中它（也可参考国内镜像 {MirrorReleasesUrl}）。";
        return null;
    }

    private static string[] BinaryNames()
    {
        return OperatingSystem.IsWindows()
            ? ["terracotta.exe", "terracotta-windows-amd64.exe"]
            : ["terracotta", "terracotta-daemon"];
    }

    private static string? FindOnPath(string fileName)
    {
        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (string directory in pathValue.Split(Path.PathSeparator))
        {
            string trimmed = directory.Trim().Trim('"');
            if (trimmed.Length == 0)
            {
                continue;
            }

            string candidate = Path.Combine(trimmed, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task EnsureServerAsync(CancellationToken cancellationToken)
    {
        bool alive;
        lock (_gate)
        {
            alive = _port != 0;
        }

        if (alive && await RefreshMetaAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        SetPort(0);
        string binary = ResolveBinary(out string? error) ??
                        throw new InvalidOperationException(error ?? "没有找到陶瓦联机可执行文件。");

        string probe = Path.Combine(Path.GetTempPath(), $"mofucore-terracotta-{DateTime.UtcNow.Ticks}.json");
        TryDelete(probe);
        TryDelete(probe + ".tmp");

        ProcessStartInfo startInfo = new(binary)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(binary) ?? Environment.CurrentDirectory,
        };
        startInfo.ArgumentList.Add("--hmcl");
        startInfo.ArgumentList.Add(probe);
        Process? process = Process.Start(startInfo) ??
                           throw new InvalidOperationException("启动陶瓦联机失败：进程未能创建。");
        // Windows 下 --hmcl 会立刻派生 --hmcl2 后自身退出；其它平台它就是服务本体
        _ = process.WaitForExitAsync(cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);

        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(StartTimeout);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int port = ReadPortFile(probe);
            if (port != 0)
            {
                TryDelete(probe);
                lock (_gate)
                {
                    _port = port;
                    _managed = true;
                    _process = process;
                }

                if (!await RefreshMetaAsync(cancellationToken).ConfigureAwait(false))
                {
                    SetPort(0);
                    throw new InvalidOperationException("陶瓦联机已启动，但本地接口不可用。");
                }

                return;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                TryDelete(probe);
                throw new TimeoutException("等待陶瓦联机启动超时（45 秒）。建议先手动运行一次 terracotta，" +
                                           "确认它能正常打开窗口，再回到联机页重试。");
            }

            await Task.Delay(PortPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static int ReadPortFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return 0;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("port", out JsonElement portElement) ||
                !portElement.TryGetInt32(out int port) || port <= 0 || port > 65535)
            {
                return 0;
            }

            return port;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 清理失败忽略
        }
    }

    private async Task<bool> RefreshMetaAsync(CancellationToken cancellationToken)
    {
        (string? body, _, Exception? error) = await RequestAsync("/meta", null, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null || body is null)
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            string version = document.RootElement.TryGetProperty("version", out JsonElement value)
                ? value.GetString() ?? string.Empty
                : string.Empty;
            lock (_gate)
            {
                _version = version;
                _running = true;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>调用本地接口：返回响应体、HTTP 状态码与错误（2xx 之外都算错误，状态码仍返回）。</summary>
    private async Task<(string? Body, int StatusCode, Exception? Error)> RequestAsync(
        string path,
        Dictionary<string, string>? query,
        CancellationToken cancellationToken)
    {
        int port;
        lock (_gate)
        {
            port = _port;
        }

        if (port == 0)
        {
            return (null, 0, new InvalidOperationException("陶瓦联机尚未启动"));
        }

        string target = $"http://127.0.0.1:{port}{path}";
        if (query is { Count: > 0 })
        {
            target += "?" + string.Join("&", query.Select(pair =>
                Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        }

        try
        {
            using HttpResponseMessage response = await LocalHttp.GetAsync(target, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (body, (int)response.StatusCode,
                    new HttpRequestException($"陶瓦联机接口 {path} 返回 {(int)response.StatusCode}"));
            }

            return (body, (int)response.StatusCode, null);
        }
        catch (Exception e) when ((e is HttpRequestException or TaskCanceledException) &&
                                  !cancellationToken.IsCancellationRequested)
        {
            return (null, 0, new InvalidOperationException("无法连接陶瓦联机本地服务：" + e.Message, e));
        }
    }

    private void SetPort(int port)
    {
        lock (_gate)
        {
            _port = port;
            if (port == 0)
            {
                _running = false;
            }
        }
    }

    // ------------------------------------------------------------------
    // 状态机
    // ------------------------------------------------------------------

    private void StartWatcher()
    {
        StopWatcher();
        CancellationTokenSource cancellation = new();
        lock (_gate)
        {
            _watcher = cancellation;
        }

        _ = PollStateAsync(cancellationToken: default);
        _ = Task.Run(async () =>
        {
            try
            {
                using PeriodicTimer timer = new(StateInterval);
                while (await timer.WaitForNextTickAsync(cancellation.Token).ConfigureAwait(false))
                {
                    await PollStateAsync(cancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常停止
            }
        });
    }

    private void StopWatcher()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _watcher;
            _watcher = null;
        }

        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    private async Task PollStateAsync(CancellationToken cancellationToken)
    {
        (string? body, _, Exception? error) = await RequestAsync("/state", null, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            Fail(new InvalidOperationException(
                "与陶瓦联机的本地服务失去联系（可能被手动关闭了）：" + error.Message, error));
            return;
        }

        if (body is null)
        {
            return;
        }

        TerracottaStatePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TerracottaStatePayload>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return;
        }

        if (payload is not null)
        {
            ApplyState(payload);
        }
    }

    private void ApplyState(TerracottaStatePayload payload)
    {
        List<TerracottaPlayer> players = [];
        foreach (TerracottaProfile profile in payload.Profiles ?? [])
        {
            players.Add(new TerracottaPlayer { Name = profile.Name, Kind = profile.Kind, Vendor = profile.Vendor });
        }

        long since;
        lock (_gate)
        {
            since = _since;
        }

        TerracottaStatus status = new() { Provider = Provider, Players = players, Since = since };
        switch (payload.State)
        {
            case "host-scanning":
                status.State = StateStarting;
                status.Phase = "正在寻找局域网世界…";
                status.Tip = "还没找到世界？先进入存档 → Esc → 对局域网开放，陶瓦会自动发现它。";
                break;
            case "host-starting":
                status.State = StateStarting;
                status.Phase = "正在创建房间…";
                status.Tip = "正在申请联机房间，请稍候。";
                break;
            case "host-ok":
                string room = RoomCode.FormatForDisplay(payload.Room);
                status.State = StateHosting;
                status.Phase = "房间已就绪";
                status.Room = room;
                status.Address = room;
                status.Tip = "把房间码发给朋友，Ta 在联机页粘贴就能加入。";
                break;
            case "guest-connecting":
                status.State = StateStarting;
                status.Phase = "正在连接房间…";
                status.Tip = "正在连接房主，请稍候。";
                break;
            case "guest-starting":
                status.State = StateStarting;
                status.Phase = "正在建立虚拟局域网…";
                status.Tip = "正在与房主建立虚拟局域网，首次连接通常需要十几秒。";
                break;
            case "guest-ok":
                (string host, int port) = SplitHostPort(payload.Url);
                status.State = StateJoined;
                status.Phase = "已进入房间";
                status.LocalAddress = payload.Url;
                status.JoinHost = payload.Url.Length == 0 ? string.Empty : host;
                status.JoinPort = payload.Url.Length == 0 ? 0 : port;
                status.Tip = "已经接入房主的局域网，点下面的「一键进服」就能直接进游戏。";
                break;
            case "exception":
                status.State = StateError;
                status.Phase = "出错";
                status.Error = ExceptionText(payload.Type);
                status.Tip = "点「退出房间」后可以重新建房或加入。";
                break;
            default: // waiting
                status.State = StateIdle;
                status.Phase = "空闲";
                status.Tip = "点「创建房间」开一局，或粘贴朋友的房间码加入。";
                status.Since = 0;
                break;
        }

        Publish(status);

        if (status.State is StateIdle or StateError)
        {
            // 会话已经不在陶瓦那边了，没必要继续轮询
            lock (_gate)
            {
                _since = 0;
            }

            StopWatcher();
        }
    }

    private static (string Host, int Port) SplitHostPort(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return (string.Empty, 0);
        }

        string value = address.Trim();
        int index = value.LastIndexOf(':');
        if (index <= 0 || index == value.Length - 1 ||
            !int.TryParse(value[(index + 1)..], out int port) || port <= 0 || port > 65535)
        {
            return (value, 0);
        }

        return (value[..index], port);
    }

    private static string ExceptionText(int? kind)
    {
        return kind switch
        {
            0 => "连接不上房主：房间码可能不对，或房主已经退出。",
            1 => "房主的网络拒绝了连接，可能是防火墙或运营商拦截。",
            2 => "房客侧的虚拟局域网组件异常退出，请重试。",
            3 => "房主侧的虚拟局域网组件异常退出，请重试。",
            4 => "房主的 Minecraft 没有响应：请确认游戏里已经「对局域网开放」。",
            5 => "联机中心返回了无法识别的数据，建议升级陶瓦联机版本。",
            null => "陶瓦联机发生了未知错误。",
            _ => $"陶瓦联机报错（代码 {kind}）。",
        };
    }

    // ------------------------------------------------------------------
    // 状态发布
    // ------------------------------------------------------------------

    private void Begin(string phase, string tip)
    {
        long since = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_gate)
        {
            _since = since;
        }

        Publish(new TerracottaStatus
        {
            Provider = Provider,
            State = StateStarting,
            Phase = phase,
            Tip = tip,
            Since = since,
        });
    }

    private void Fail(Exception error)
    {
        StopWatcher();
        lock (_gate)
        {
            _since = 0;
        }

        Publish(new TerracottaStatus
        {
            Provider = Provider,
            State = StateError,
            Phase = "出错",
            Error = error.Message,
            Tip = "按提示处理后可以重新建房或加入。",
        });
    }

    private void Publish(TerracottaStatus status)
    {
        lock (_gate)
        {
            _status = status.Clone();
        }

        try
        {
            StatusChanged?.Invoke(status);
        }
        catch
        {
            // 单个订阅者异常不扩散
        }
    }

    // ------------------------------------------------------------------
    // 自动安装
    // ------------------------------------------------------------------

    /// <summary>自动下载并安装陶瓦联机：查 GitHub Releases → 按平台挑资产 → 下载 → 解压。</summary>
    public async Task<TerracottaInstallResult> InstallAsync(CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(InstallTimeout);
        CancellationToken token = timeout.Token;

        TerracottaRelease release = await FetchLatestReleaseAsync(token).ConfigureAwait(false);
        TerracottaInstallResult result = new() { Version = release.TagName };

        TerracottaReleaseAsset? asset = PickAsset(release.Assets, out string? pickError);
        if (asset is null)
        {
            result.ManualHint = $"{pickError}。请到 {ReleasesUrl} 手动下载（国内可试 {MirrorReleasesUrl}），" +
                                "再用右侧「选择可执行文件」指定。";
            return result;
        }

        result.AssetName = asset.Name;
        string lowerAsset = asset.Name.ToLowerInvariant();
        if (!lowerAsset.EndsWith(".zip", StringComparison.Ordinal) &&
            !lowerAsset.EndsWith(".tar.gz", StringComparison.Ordinal) &&
            !lowerAsset.EndsWith(".tgz", StringComparison.Ordinal))
        {
            result.ManualHint = $"该平台的发行包是 {asset.Name}，启动器暂不自动解压。" +
                                $"请手动解压后到 {ReleasesUrl} 获取，再用右侧「选择可执行文件」指定" +
                                $"（国内可试 {MirrorReleasesUrl}）。";
            return result;
        }

        string root = Path.Combine(StorageDirectory, "terracotta");
        // 按版本号分子目录：升级后旧版本文件留在原地，定位只在新目录里做
        string directory = Path.Combine(root, release.TagName.TrimStart('v', 'V'));
        Directory.CreateDirectory(directory);
        string archivePath = Path.Combine(root, asset.Name);

        await DownloadFileAsync(asset.Url, archivePath, token).ConfigureAwait(false);
        try
        {
            ExtractArchive(archivePath, directory);
        }
        finally
        {
            TryDelete(archivePath);
        }

        if (!LocateBinary(directory, out string? binary) || binary is null)
        {
            throw new InvalidOperationException(
                "压缩包里没有找到可执行文件（期望 terracotta / terracotta.exe 或 terracotta-<版本>-<平台> 形式的文件）");
        }

        if (!OperatingSystem.IsWindows())
        {
            // 发行包打包/解压链路丢执行位时，会"装好了却起不来"
            try
            {
                File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                             UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                             UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch (IOException)
            {
                // 权限设置失败不影响后续
            }
        }

        BinaryPath = binary;
        result.Path = binary;
        Log?.Invoke($"陶瓦联机已自动安装：{binary}（{release.TagName}）");
        return result;
    }

    private static async Task<TerracottaRelease> FetchLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, LatestJsonUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", "MofuCore/" + MofuCoreInfo.Version);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

        using HttpResponseMessage response = await RemoteHttp.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            throw new InvalidOperationException("GitHub API 限流了，请稍后再试，或到发行页手动下载");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub Releases 返回 {(int)response.StatusCode}");
        }

        TerracottaRelease? release = await response.Content
            .ReadFromJsonAsync<TerracottaRelease>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return release ?? throw new InvalidOperationException("解析 release 失败。");
    }

    private static TerracottaReleaseAsset? PickAsset(
        IReadOnlyList<TerracottaReleaseAsset>? assets,
        out string? error)
    {
        error = null;
        if (assets is null || assets.Count == 0)
        {
            error = "发行包里没有任何资产";
            return null;
        }

        string platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
        string architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "amd64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X86 => "386",
            _ => "unknown",
        };

        TerracottaReleaseAsset? best = null;
        int bestScore = -1;
        foreach (TerracottaReleaseAsset asset in assets)
        {
            int score = AssetPlatformScore(asset.Name, platform, architecture);
            if (score > bestScore)
            {
                best = asset;
                bestScore = score;
            }
        }

        if (best is null || bestScore < 0)
        {
            error = $"发行包里没有找到适配 {platform}/{architecture} 的资产";
            return null;
        }

        return best;
    }

    /// <summary>
    /// 给资产名打分：平台 + 架构都对上才给正分；校验文件、源码包一律不选。
    /// </summary>
    public static int AssetPlatformScore(string name, string platform, string architecture)
    {
        string lower = name.ToLowerInvariant();
        foreach (string excluded in new[] { ".sha256", ".sha512", ".sig", ".asc", "checksums", "source" })
        {
            if (lower.Contains(excluded, StringComparison.Ordinal))
            {
                return -1;
            }
        }

        bool Matches(IEnumerable<string> candidates) =>
            candidates.Any(candidate => lower.Contains(candidate, StringComparison.Ordinal));

        int score = 0;
        switch (platform)
        {
            case "windows":
                if (!Matches(["windows", "win32", "win64", "-win"]))
                {
                    return -1;
                }

                score += 4;
                break;
            case "darwin":
                if (!Matches(["macos", "darwin", "osx", "apple"]))
                {
                    return -1;
                }

                score += 4;
                break;
            case "linux":
                if (!Matches(["linux", "gnu"]))
                {
                    return -1;
                }

                score += 4;
                break;
            default:
                return -1;
        }

        switch (architecture)
        {
            case "amd64":
                if (Matches(["x86_64", "amd64", "x64", "64bit"]))
                {
                    score += 2;
                }
                else if (Matches(["aarch64", "arm64"]))
                {
                    return -1;
                }

                break;
            case "arm64":
                if (Matches(["aarch64", "arm64"]))
                {
                    score += 2;
                }
                else if (Matches(["x86_64", "amd64", "x64"]))
                {
                    return -1;
                }

                break;
            case "386":
                if (Matches(["i686", "i386", "x86", "32bit"]))
                {
                    score += 2;
                }

                break;
        }

        // 可解压格式优先
        if (lower.EndsWith(".zip", StringComparison.Ordinal))
        {
            score += 3;
        }
        else if (lower.EndsWith(".tar.gz", StringComparison.Ordinal) || lower.EndsWith(".tgz", StringComparison.Ordinal))
        {
            score += 2;
        }

        return score;
    }

    private static async Task DownloadFileAsync(string url, string targetPath, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await RemoteHttp
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"下载 {Path.GetFileName(targetPath)} 失败（HTTP {(int)response.StatusCode}）");
        }

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using FileStream target = new(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private static void ExtractArchive(string archivePath, string targetDirectory)
    {
        string lower = archivePath.ToLowerInvariant();
        if (lower.EndsWith(".zip", StringComparison.Ordinal))
        {
            ExtractZip(archivePath, targetDirectory);
        }
        else if (lower.EndsWith(".tar.gz", StringComparison.Ordinal) || lower.EndsWith(".tgz", StringComparison.Ordinal))
        {
            ExtractTarGz(archivePath, targetDirectory);
        }
        else
        {
            throw new InvalidOperationException("不支持的压缩格式：" + Path.GetFileName(archivePath));
        }
    }

    private static void ExtractZip(string archivePath, string targetDirectory)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string target = SafeArchiveTarget(targetDirectory, entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }

    private static void ExtractTarGz(string archivePath, string targetDirectory)
    {
        using FileStream file = File.OpenRead(archivePath);
        using GZipStream gzip = new(file, CompressionMode.Decompress);
        using TarReader reader = new(gzip);
        while (reader.GetNextEntry() is { } entry)
        {
            string target = SafeArchiveTarget(targetDirectory, entry.Name);
            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }

    private static string SafeArchiveTarget(string targetDirectory, string name)
    {
        string cleaned = Path.GetFullPath(Path.Combine(targetDirectory, name.Replace('\\', '/')));
        string root = Path.GetFullPath(targetDirectory) + Path.DirectorySeparatorChar;
        if (!cleaned.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("压缩包内含越界路径：" + name);
        }

        return cleaned;
    }

    /// <summary>在安装目录里找可执行文件（深搜；固定名优先，其次按带版本号文件名挑当前平台最匹配的）。</summary>
    private static bool LocateBinary(string directory, out string? binary)
    {
        binary = null;
        if (!Directory.Exists(directory))
        {
            return false;
        }

        string[] legacy = BinaryNames();
        string? found = null;
        string? versioned = null;
        int versionedScore = -1;
        foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(path);
            if (legacy.Any(candidate => string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                found = path;
                break;
            }

            if (!IsVersionedBinary(name))
            {
                continue;
            }

            int score = VersionedScore(name);
            if (score > versionedScore ||
                (score == versionedScore &&
                 string.CompareOrdinal(name.ToLowerInvariant(), (versioned is null ? string.Empty : Path.GetFileName(versioned)).ToLowerInvariant()) > 0))
            {
                versioned = path;
                versionedScore = score;
            }
        }

        binary = found ?? versioned;
        return binary is not null;
    }

    /// <summary>新版发行包里的带版本号可执行文件（terracotta-&lt;版本&gt;-&lt;平台&gt;[-&lt;架构&gt;][.exe]）。</summary>
    private static bool IsVersionedBinary(string name)
    {
        string lower = name.ToLowerInvariant();
        if (!lower.StartsWith("terracotta-", StringComparison.Ordinal) ||
            lower.EndsWith(".pkg", StringComparison.Ordinal) ||
            lower.Contains("vcruntime", StringComparison.Ordinal))
        {
            return false;
        }

        return OperatingSystem.IsWindows()
            ? lower.EndsWith(".exe", StringComparison.Ordinal)
            : !lower.EndsWith(".exe", StringComparison.Ordinal);
    }

    private static int VersionedScore(string name)
    {
        string lower = name.ToLowerInvariant();
        string platform = OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux";
        string architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x86_64",
            System.Runtime.InteropServices.Architecture.X86 => "i686",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => string.Empty,
        };

        int score = 0;
        if (lower.Contains(platform, StringComparison.Ordinal))
        {
            score += 2;
        }

        if (architecture.Length > 0 && lower.Contains(architecture, StringComparison.Ordinal))
        {
            score++;
        }

        return score;
    }

    // ------------------------------------------------------------------
    // 内部 DTO
    // ------------------------------------------------------------------

    private sealed class TerracottaStatePayload
    {
        [JsonPropertyName("state")] public string State { get; set; } = string.Empty;
        [JsonPropertyName("room")] public string Room { get; set; } = string.Empty;
        [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
        [JsonPropertyName("difficulty")] public string Difficulty { get; set; } = string.Empty;
        [JsonPropertyName("type")] public int? Type { get; set; }
        [JsonPropertyName("profiles")] public List<TerracottaProfile>? Profiles { get; set; }
    }

    private sealed class TerracottaProfile
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("vendor")] public string Vendor { get; set; } = string.Empty;
        [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
    }

    private sealed class TerracottaRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("assets")] public List<TerracottaReleaseAsset>? Assets { get; set; }
    }

    private sealed class TerracottaReleaseAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("browser_download_url")] public string Url { get; set; } = string.Empty;
        [JsonPropertyName("size")] public long Size { get; set; }
    }
}

/// <summary>陶瓦联机的房间成员身份（与 Scaffolding 协议的 kind 字段一致）。</summary>
public static class TerracottaPlayerKind
{
    public const string Host = "HOST";
    public const string Local = "LOCAL";
    public const string Guest = "GUEST";
}

/// <summary>房间成员（陶瓦联机 /state 的 profiles 字段）。</summary>
public sealed class TerracottaPlayer
{
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Vendor { get; set; } = string.Empty;
}

/// <summary>联机会话快照：前端唯一的状态来源。</summary>
public sealed class TerracottaStatus
{
    public string Provider { get; set; } = string.Empty;

    /// <summary>idle / starting / hosting / joined / error。</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>当前阶段的说明文案。</summary>
    public string Phase { get; set; } = string.Empty;

    /// <summary>房间码（陶瓦）。</summary>
    public string Room { get; set; } = string.Empty;

    /// <summary>别人要连接的目标（陶瓦是房间码）。</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>本机在游戏里直连的地址（陶瓦房客为 127.0.0.1:&lt;port&gt;）。</summary>
    public string LocalAddress { get; set; } = string.Empty;

    /// <summary>一键进服参数（为空表示当前状态还不能进服）。</summary>
    public string JoinHost { get; set; } = string.Empty;

    public int JoinPort { get; set; }

    /// <summary>房间成员。</summary>
    public IReadOnlyList<TerracottaPlayer> Players { get; set; } = [];

    /// <summary>当前状态下给用户的一句操作提示。</summary>
    public string Tip { get; set; } = string.Empty;

    /// <summary>出错信息（State == error 时有值）。</summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>本次会话开始时间（Unix 秒；0 表示当前没有会话）。</summary>
    public long Since { get; set; }

    public TerracottaStatus Clone()
    {
        return new TerracottaStatus
        {
            Provider = Provider,
            State = State,
            Phase = Phase,
            Room = Room,
            Address = Address,
            LocalAddress = LocalAddress,
            JoinHost = JoinHost,
            JoinPort = JoinPort,
            Players = Players,
            Tip = Tip,
            Error = Error,
            Since = Since,
        };
    }
}

/// <summary>供应商的静态介绍（联机页说明卡片用）。</summary>
public sealed class TerracottaProviderInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Homepage { get; set; } = string.Empty;

    /// <summary>当前是否可用（本机存在可执行文件）。</summary>
    public bool Ready { get; set; }

    /// <summary>不可用时的处理建议。</summary>
    public string Hint { get; set; } = string.Empty;

    public bool NeedsMod { get; set; }
    public bool GuestNeedsMod { get; set; }
    public string HostNote { get; set; } = string.Empty;
    public string JoinNote { get; set; } = string.Empty;
}

/// <summary>供应商在本机的运行时状态（设置卡片与"关闭后台服务"按钮用）。</summary>
public sealed class TerracottaRuntime
{
    public string Provider { get; set; } = string.Empty;

    /// <summary>本机服务已在运行（HTTP 服务可达）。</summary>
    public bool Running { get; set; }

    /// <summary>由本启动器会话拉起（关闭它不会影响用户自己开的窗口）。</summary>
    public bool Managed { get; set; }

    public string Version { get; set; } = string.Empty;
    public string Binary { get; set; } = string.Empty;
    public int Port { get; set; }
}

/// <summary>自动安装的结果（前端展示用）。</summary>
public sealed class TerracottaInstallResult
{
    /// <summary>release 的 tag。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>选中的资产文件名。</summary>
    public string AssetName { get; set; } = string.Empty;

    /// <summary>安装后的可执行文件路径（已写回设置）。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>非空表示"需要用户手动完成"：内容是原因与发行页地址。</summary>
    public string ManualHint { get; set; } = string.Empty;
}

/// <summary>
/// 陶瓦联机房间码（Scaffolding 协议）：`U/XXXX-XXXX-XXXX-XXXX`，字符集为 0-9 加除 I、O 外的大写字母。
/// 用户粘贴时可能带前缀也可能不带，这里只做收敛与候选生成，合法性最终由陶瓦进程判定。
/// </summary>
public static class RoomCode
{
    private const string Prefix = "U/";
    private const string Alphabet = "0123456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const int BodyLength = 16;
    private const int Group = 4;

    /// <summary>只保留字母数字：丢掉连字符、斜杠、空白与全角符号，并统一转大写。</summary>
    private static string Sanitize(string raw)
    {
        System.Text.StringBuilder builder = new();
        foreach (char character in (raw ?? string.Empty).Trim().ToUpperInvariant())
        {
            if (character is >= '0' and <= '9' or >= 'A' and <= 'Z')
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    /// <summary>返回去掉前缀后的 16 位房间码主体；位数不符时返回空串。</summary>
    public static string Body(string raw)
    {
        string body = Sanitize(raw);
        if (body.Length == BodyLength + 1 && body.StartsWith('U'))
        {
            body = body[1..];
        }

        return body.Length == BodyLength ? body : string.Empty;
    }

    /// <summary>把房间码按 4 位一段重新分组。</summary>
    public static string Format(string body)
    {
        if (body.Length != BodyLength)
        {
            return body;
        }

        List<string> groups = [];
        for (int index = 0; index < BodyLength; index += Group)
        {
            groups.Add(body.Substring(index, Group));
        }

        return string.Join("-", groups);
    }

    /// <summary>粗校验房间码：位数正确且字符都在协议字符集内。</summary>
    public static bool LooksValid(string raw)
    {
        string body = Body(raw);
        return body.Length > 0 && body.All(character => Alphabet.Contains(character, StringComparison.Ordinal));
    }

    /// <summary>返回按可信度排序的候选房间码，进房时逐个尝试。</summary>
    public static IReadOnlyList<string> Variants(string raw)
    {
        string body = Body(raw);
        if (body.Length == 0)
        {
            string trimmed = (raw ?? string.Empty).Trim();
            return trimmed.Length == 0 ? [] : [trimmed];
        }

        string formatted = Format(body);
        return [Prefix + formatted, formatted];
    }

    /// <summary>把用户输入或进程回传的房间码整理成展示形态；无法解析时原样返回。</summary>
    public static string FormatForDisplay(string raw)
    {
        string body = Body(raw);
        return body.Length == 0 ? (raw ?? string.Empty).Trim() : Prefix + Format(body);
    }
}
