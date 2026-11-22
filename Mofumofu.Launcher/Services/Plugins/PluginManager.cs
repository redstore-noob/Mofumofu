using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using Mofumofu.ExtensionAPI;

namespace Mofumofu.Launcher.Services.Plugins;

public enum PluginRuntimeState
{
    Loaded,

    Disabled,

    Refused,

    Error,

    Incompatible,

    Broken,

    PendingConsent,
}

public sealed class PluginRuntime
{
    public PluginManifest Manifest { get; init; } = null!;
    public PluginRuntimeState State { get; set; } = PluginRuntimeState.Broken;
    public string Error { get; set; } = string.Empty;
    public IMofuPlugin? Instance { get; set; }
    public PluginContextImpl? Context { get; set; }
    public System.Reflection.Assembly? Assembly { get; set; }
    public AssemblyLoadContext? Alc { get; set; }
    internal WeakReference? AlcReference { get; set; }
    internal List<IDisposable> Registrations { get; } = new();
}

public static class PluginManager
{
    /// <summary>宿主实现的插件 API 版本；清单声明高于它则判不兼容。</summary>
    public static readonly Version HostApiVersion = new(1, 0, 0);

    private static readonly object Gate = new();
    private static readonly List<PluginRuntime> Plugins = new();
    private static bool _initialized;

    public static event Action? PluginsChanged;

    public static IReadOnlyList<PluginRuntime> All
    {
        get { lock (Gate) return Plugins.ToList(); }
    }

    public static PluginRuntime? ById(string id)
    {
        lock (Gate) return Plugins.FirstOrDefault(p =>
            string.Equals(p.Manifest.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public static string PluginsRoot => Path.Combine(
        MofuCore.Config.YamlRead.DataRoot,
        MofuCore.Config.YamlRead.DataDirectoryName,
        "plugins");

    /// <summary>
    /// 校验插件 id 白名单并把基于它的路径规范化到 pluginsRoot 之下。
    /// 含路径分隔符、..、绝对路径或规范化后越出 pluginsRoot 的 id 一律拒绝（fail-closed）。
    /// suffix 用于拼 ".nekoex" 等文件名后缀；返回规范化后的绝对路径。
    /// </summary>
    internal static string ResolvePluginPath(string id, string? suffix = null)
    {
        if (PluginManifest.IsValidPluginId(id) == false)
        {
            throw new InvalidOperationException("非法的插件 id：" + id);
        }

        string rootFull = Path.GetFullPath(PluginsRoot);
        string rootPrefix = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(rootFull, id + (suffix ?? string.Empty)));
        if (full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) == false)
        {
            throw new InvalidOperationException("插件路径越出 pluginsRoot：" + id);
        }
        return full;
    }

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized) return;
            _initialized = true;
        }

        HookDownloadEvents();
        HookLaunchSessionEvents();
        Rescan();
    }

    private static string _sessionInstanceId = string.Empty;
    private static string _sessionVersionId = string.Empty;
    private static DateTime? _sessionStartedAt;
    private static bool _launchSessionHooked;

    /// <summary>
    /// 启动前跑一圈 ILaunchAction.LaunchingAsync（UI 线程、单钩子 5 秒超时、异常视为放行）。
    /// 钩子可直接往 options.AdditionalJvmArguments 加参数（引用同一个列表）。
    /// </summary>
    /// <returns>是否放行；false 时 Reason 为拦截原因（可直接展示给玩家）。</returns>
    public static async Task<(bool Proceed, string? Reason)> RunLaunchHooks(
        MofuCore.Launch.MinecraftLaunchOptions options, string instanceId)
    {
        _sessionInstanceId = instanceId;
        _sessionVersionId = options.VersionId;
        _sessionStartedAt = null;

        List<PluginRuntime> hookPlugins;
        lock (Gate)
        {
            hookPlugins = Plugins
                .Where(p => p.State == PluginRuntimeState.Loaded && p.Instance is Mofumofu.ExtensionAPI.Actions.ILaunchAction)
                .ToList();
        }

        if (hookPlugins.Count == 0) return (true, null);

        foreach (PluginRuntime runtime in hookPlugins)
        {
            // 启动钩子参与启动流程：派发前按用途做权限校验（fail-closed）。
            // 与下载通知钩子一致：Context 缺失或未声明 launch 权限的插件不派发。
            if (runtime.Context is not { } hookContext)
            {
                continue;
            }
            try
            {
                hookContext.Demand(PluginPermissions.Launch);
            }
            catch (PluginPermissionException)
            {
                continue;
            }
            catch (Exception error)
            {
                PluginLog.Write(runtime.Manifest.Id, "启动钩子权限检查异常：" + error.Message);
                continue;
            }

            var hook = (Mofumofu.ExtensionAPI.Actions.ILaunchAction)runtime.Instance!;
            var context = new LaunchContextImpl(instanceId, options.VersionId, options.MinecraftDirectory,
                options.AdditionalJvmArguments);

            bool allow;
            try
            {
                // 钩子在 UI 线程执行（可能碰界面）；宿主侧 5 秒超时兜底
                Task<bool> hookTask = Avalonia.Threading.Dispatcher.UIThread
                    .InvokeAsync(() => hook.LaunchingAsync(context));
                Task winner = await Task.WhenAny(hookTask, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                if (winner == hookTask)
                {
                    allow = await hookTask.ConfigureAwait(false);
                }
                else
                {
                    allow = true;
                    PluginLog.Write(runtime.Manifest.Id, "启动钩子超时（5 秒），按放行处理");
                    // 超时后 loser（hookTask）仍在跑：挂异常观察 continuation，避免产生未观察异常。
                    _ = hookTask.ContinueWith(
                        t => PluginLog.Write(runtime.Manifest.Id,
                            "超时后的启动钩子异常：" + (t.Exception?.GetBaseException().Message ?? "未知")),
                        System.Threading.CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                }
            }
            catch (Exception error)
            {
                allow = true;
                PluginLog.Write(runtime.Manifest.Id, "启动钩子异常（按放行处理）：" + error.Message);
            }

            if (context.IsCancelled)
            {
                PluginLog.Write(runtime.Manifest.Id, "启动被插件拦截：" + context.CancelReason);
                return (false, context.CancelReason);
            }

            if (allow == false)
            {
                PluginLog.Write(runtime.Manifest.Id, "启动被插件拦截（返回 false）");
                return (false, "插件 " + runtime.Manifest.Name + " 拒绝了本次启动");
            }
        }

        return (true, null);
    }

    private static void HookLaunchSessionEvents()
    {
        if (_launchSessionHooked) return;
        _launchSessionHooked = true;

        LaunchProgress.Shared.ProcessStarted += _ => _sessionStartedAt = DateTime.Now;
        LaunchProgress.Shared.Completed += (_, args) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_sessionStartedAt is not { } startedAt) return;

            DateTime started = startedAt;
            _sessionStartedAt = null;
            string instanceId = _sessionInstanceId;
            string versionId = _sessionVersionId;

            bool crashed = args.Success == false
                           && args.Cancelled == false
                           && args.ExitCode is int code and not 0;

            List<PluginRuntime> hookPlugins;
            lock (Gate)
            {
                hookPlugins = Plugins
                    .Where(p => p.State == PluginRuntimeState.Loaded && p.Instance is Mofumofu.ExtensionAPI.Actions.ILaunchAction)
                    .ToList();
            }

            foreach (PluginRuntime runtime in hookPlugins)
            {
                var hook = (Mofumofu.ExtensionAPI.Actions.ILaunchAction)runtime.Instance!;
                try
                {
                    if (crashed)
                    {
                        hook.GameCrashedHook(args.Message);
                    }
                    else
                    {
                        hook.GameStoppedHook(new Mofumofu.ExtensionAPI.Data.GameSessionData(
                            instanceId,
                            instanceId,
                            versionId,
                            started,
                            args.Duration,
                            args.ExitCode ?? -1,
                            crashed,
                            crashed ? args.Message : null));
                    }
                }
                catch (Exception error)
                {
                    PluginLog.Write(runtime.Manifest.Id, "会话通知钩子异常：" + error.Message);
                }
            }
        });
    }

    private sealed class LaunchContextImpl(
        string instanceId,
        string versionId,
        string gameDirectory,
        IList<string> jvmArguments) : Mofumofu.ExtensionAPI.Actions.ILaunchContext
    {
        public string InstanceId { get; } = instanceId;
        public string VersionId { get; } = versionId;
        public string GameDirectory { get; } = gameDirectory;
        public IReadOnlyList<string> JvmArguments { get; } = new GuardedJvmArguments(jvmArguments);

        public bool IsCancelled { get; private set; }
        public string CancelReason { get; private set; } = string.Empty;

        public void Cancel(string reason)
        {
            IsCancelled = true;
            CancelReason = reason;
        }
    }

    private sealed class GuardedJvmArguments(IList<string> inner) : IList<string>, IReadOnlyList<string>
    {
        public string this[int index]
        {
            get => inner[index];
            set { if (Mofumofu.ExtensionAPI.Actions.JvmArgumentPolicy.IsAllowed(value)) inner[index] = value; }
        }

        public int Count => inner.Count;
        public bool IsReadOnly => false;

        public void Add(string item)
        {
            if (Mofumofu.ExtensionAPI.Actions.JvmArgumentPolicy.IsAllowed(item)) inner.Add(item);
        }

        public void Insert(int index, string item)
        {
            if (Mofumofu.ExtensionAPI.Actions.JvmArgumentPolicy.IsAllowed(item)) inner.Insert(index, item);
        }

        // 删除 / 清空会移除宿主或其它插件注入的参数（authlib 注入、类路径等），而白名单策略
        // 只校验「注入的内容」、无法授权删除：这里 fail-closed —— Clear 一律拒绝，Remove/RemoveAt
        // 仅允许移除本身通过白名单的参数项，避免插件借 Clear/Remove 绕过白名单清掉宿主参数。
        public void Clear() { }
        public bool Contains(string item) => inner.Contains(item);
        public void CopyTo(string[] array, int arrayIndex) => inner.CopyTo(array, arrayIndex);
        public IEnumerator<string> GetEnumerator() => inner.GetEnumerator();
        public int IndexOf(string item) => inner.IndexOf(item);
        public bool Remove(string item) =>
            Mofumofu.ExtensionAPI.Actions.JvmArgumentPolicy.IsAllowed(item) && inner.Remove(item);
        public void RemoveAt(int index)
        {
            if (index >= 0 && index < inner.Count
                && Mofumofu.ExtensionAPI.Actions.JvmArgumentPolicy.IsAllowed(inner[index]))
            {
                inner.RemoveAt(index);
            }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => inner.GetEnumerator();
    }

    private static void HookDownloadEvents()
    {
        if (_downloadEventsHooked) return;
        _downloadEventsHooked = true;

        Services.DownloadTaskService.Changed += snapshot =>
        {
            if (snapshot.State is not (Services.DownloadTaskState.Done or Services.DownloadTaskState.Failed))
            {
                return;
            }

            int taskNumber;
            if (int.TryParse(snapshot.ShortId, out var parsed) == false) return;
            taskNumber = parsed;

            // 锁内只取待派发快照；第三方回调一律移到锁外执行，避免单个插件阻塞/重入拖住插件管理。
            var targets = new List<(Mofumofu.ExtensionAPI.Actions.IDownloadAction Action, string Id)>();
            lock (Gate)
            {
                foreach (var runtime in Plugins.Where(p => p.State == PluginRuntimeState.Loaded))
                {
                    if (runtime.Instance is not Mofumofu.ExtensionAPI.Actions.IDownloadAction action) continue;
                    if (runtime.Context is not { } context) continue;
                    try
                    {
                        context.Demand(PluginPermissions.NetworkReadonly);
                    }
                    catch (PluginPermissionException)
                    {
                        continue;
                    }
                    catch (Exception error)
                    {
                        PluginLog.Write(runtime.Manifest.Id, "权限检查异常：" + error.Message);
                        continue;
                    }

                    targets.Add((action, runtime.Manifest.Id));
                }
            }

            foreach (var (action, id) in targets)
            {
                try
                {
                    if (snapshot.State == Services.DownloadTaskState.Done)
                    {
                        action.DownloadCompletedHook(taskNumber, snapshot.Title);
                    }
                    else
                    {
                        action.DownloadFailedHook(taskNumber, snapshot.Title, snapshot.Detail);
                    }
                }
                catch (Exception error)
                {
                    PluginLog.Write(id, "下载通知钩子异常：" + error.Message);
                }
            }
        };
    }

    private static bool _downloadEventsHooked;

    public static void Rescan()
    {
        lock (Gate)
        {
            foreach (var runtime in Plugins.Where(p => p.State == PluginRuntimeState.Loaded).ToList())
            {
                UnloadRuntime(runtime);
            }

            Plugins.Clear();
        }

        ExtractLoosePackages();

        if (Directory.Exists(PluginsRoot))
        {
            foreach (string directory in Directory.GetDirectories(PluginsRoot))
            {
                PluginManifest? manifest = PluginManifest.TryLoad(directory);
                if (manifest is null) continue;

                var runtime = new PluginRuntime { Manifest = manifest };
                lock (Gate) Plugins.Add(runtime);

                if (IsEnabled(manifest.Id) == false)
                {
                    runtime.State = PluginRuntimeState.Disabled;
                    continue;
                }

                TryActivate(runtime);
            }
        }

        PluginsChanged?.Invoke();
    }

    public static PluginRuntime? LoadPlugin(string directory, string? sourcePackagePath = null)
    {
        PluginManifest? manifest = PluginManifest.TryLoad(directory);
        if (manifest is null) return null;
        manifest.SourcePackagePath = sourcePackagePath;

        lock (Gate)
        {
            PluginRuntime? existing = Plugins.FirstOrDefault(p =>
                string.Equals(p.Manifest.Id, manifest.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                UnloadRuntime(existing);
                Plugins.Remove(existing);
            }
        }

        var runtime = new PluginRuntime { Manifest = manifest };
        lock (Gate) Plugins.Add(runtime);
        TryActivate(runtime);
        PluginsChanged?.Invoke();
        return runtime;
    }

    internal static string? ResolveDllPath(PluginManifest manifest) =>
        File.Exists(manifest.DllPath)
            ? manifest.DllPath
            : Directory.GetFiles(manifest.Directory, "*.dll")
                .FirstOrDefault(path => Path.GetFileName(path).Equals("extension.dll", StringComparison.OrdinalIgnoreCase) == false);

    private static void TryActivate(PluginRuntime runtime)
    {
        try
        {
            string? dllPath = ResolveDllPath(runtime.Manifest);
            if (dllPath is null)
            {
                runtime.State = PluginRuntimeState.Broken;
                runtime.Error = "插件目录里没有 DLL";
                return;
            }

            if (IsIncompatible(runtime.Manifest))
            {
                runtime.State = PluginRuntimeState.Incompatible;
                runtime.Error = $"清单声明 apiVersion {runtime.Manifest.ApiVersion}，宿主为 {HostApiVersion}";
                PluginLog.Host($"{runtime.Manifest.Id} API 版本不兼容：{runtime.Error}");
                return;
            }

            // 同意门：先同意、后加载。未授权（首次安装/指纹变化/新增权限）一律不进 ALC
            PluginConsentState consent = PluginConsent.Evaluate(runtime.Manifest, out _);
            if (consent != PluginConsentState.Granted)
            {
                runtime.State = PluginRuntimeState.PendingConsent;
                runtime.Error = consent switch
                {
                    PluginConsentState.Changed => "插件文件已变化（指纹不一致），需要重新授权",
                    PluginConsentState.NewPermissions => "清单新增了权限声明，需要重新授权",
                    _ => "首次安装，等待玩家授权",
                };
                PluginLog.Host($"{runtime.Manifest.Id} 待授权（{runtime.Error}），未加载");
                return;
            }

            var alc = new PluginAssemblyLoadContext(runtime.Manifest.Id, runtime.Manifest.Directory);
            runtime.Alc = alc;

            Assembly assembly;
            try
            {
                assembly = alc.LoadFromAssemblyPath(Path.GetFullPath(dllPath));
            }
            catch (Exception error)
            {
                runtime.State = PluginRuntimeState.Error;
                runtime.Error = "extension.dll 加载失败：" + error.Message;
                PluginLog.Host($"{runtime.Manifest.Id} {runtime.Error}");
                return;
            }

            IMofuPlugin? instance = FindPluginEntry(assembly);
            if (instance is null)
            {
                runtime.State = PluginRuntimeState.Broken;
                runtime.Error = "extension.dll 中没有 IMofuPlugin 实现";
                PluginLog.Host($"{runtime.Manifest.Id} {runtime.Error}");
                return;
            }

            runtime.Instance = instance;
            runtime.Assembly = assembly;
            runtime.Context = new PluginContextImpl(runtime);

            bool accepted;
            try
            {
                accepted = instance.Init(runtime.Context);
            }
            catch (Exception error)
            {
                runtime.State = PluginRuntimeState.Error;
                runtime.Error = error.Message;
                PluginLog.Write(runtime.Manifest.Id, "Init 异常：" + error);
                UnloadRuntime(runtime);
                return;
            }

            runtime.State = accepted ? PluginRuntimeState.Loaded : PluginRuntimeState.Refused;
            if (!accepted)
            {
                PluginLog.Write(runtime.Manifest.Id, "Init 返回 false，插件拒绝加载");
                UnloadRuntime(runtime);
            }
            else
            {
                PluginLog.Write(runtime.Manifest.Id, $"已加载 v{runtime.Manifest.Version}");
            }
        }
        catch (Exception error)
        {
            runtime.State = PluginRuntimeState.Error;
            runtime.Error = error.Message;
            PluginLog.Host($"{runtime.Manifest.Id} 激活失败：{error}");
        }
    }

    private static bool IsIncompatible(PluginManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.ApiVersion)) return false;
        if (Version.TryParse(manifest.ApiVersion.TrimStart('v', 'V').Split('+', '-')[0], out var declared) == false)
        {
            return false;
        }

        return declared > HostApiVersion;
    }

    private static IMofuPlugin? FindPluginEntry(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException error)
        {
            types = error.Types.Where(t => t is not null).Select(t => t!).ToArray();
        }

        Type? entryType = types.FirstOrDefault(t =>
            t is { IsAbstract: false, IsInterface: false } &&
            typeof(IMofuPlugin).IsAssignableFrom(t));
        if (entryType is null) return null;

        try
        {
            return Activator.CreateInstance(entryType) as IMofuPlugin;
        }
        catch
        {
            return null;
        }
    }

    public static void Unload(string pluginId)
    {
        PluginRuntime? runtime = ById(pluginId);
        if (runtime is null) return;

        lock (Gate) UnloadRuntime(runtime);
        runtime.State = PluginRuntimeState.Disabled;
        PluginsChanged?.Invoke();
    }

    public static void SetEnabled(string pluginId, bool enabled)
    {
        // 插件状态的「查找 / 新增 / 修改 / 保存」整段走 LocalSettings 的 Gate，
        // 与 SaveCore 的序列化互斥，避免并发枚举抛「集合已修改」。
        LocalSettings.MutatePluginStates(states =>
        {
            var state = states.FirstOrDefault(entry =>
                string.Equals(entry.Id, pluginId, StringComparison.OrdinalIgnoreCase));
            if (state is null)
            {
                state = new PluginStateEntry { Id = pluginId };
                states.Add(state);
            }

            state.Enabled = enabled;
            LocalSettings.Save();
        });

        PluginRuntime? runtime = ById(pluginId);
        if (runtime is null) return;

        if (enabled && runtime.State is PluginRuntimeState.Disabled
                         or PluginRuntimeState.Refused
                         or PluginRuntimeState.Error
                         or PluginRuntimeState.Broken
                         or PluginRuntimeState.Incompatible
                         or PluginRuntimeState.PendingConsent)
        {
            lock (Gate)
            {
                UnloadRuntime(runtime);
                Plugins.Remove(runtime);
            }

            var fresh = new PluginRuntime { Manifest = runtime.Manifest };
            lock (Gate) Plugins.Add(fresh);
            TryActivate(fresh);
        }
        else if (!enabled && runtime.State == PluginRuntimeState.Loaded)
        {
            Unload(pluginId);
        }

        PluginsChanged?.Invoke();
    }

    public static bool IsEnabled(string pluginId)
    {
        return LocalSettings.WithPluginStates(states =>
            states.FirstOrDefault(entry =>
                string.Equals(entry.Id, pluginId, StringComparison.OrdinalIgnoreCase))?.Enabled ?? true);
    }

    private static void UnloadRuntime(PluginRuntime runtime)
    {
        foreach (IDisposable registration in runtime.Registrations.ToList())
        {
            try
            {
                registration.Dispose();
            }
            catch (Exception error)
            {
                PluginLog.Host($"回收 {runtime.Manifest.Id} 的注册句柄失败：{error.Message}");
            }
        }

        runtime.Registrations.Clear();
        runtime.Instance = null;
        runtime.Context = null;
        runtime.Assembly = null;

        if (runtime.Alc is { } alc)
        {
            runtime.AlcReference = new WeakReference(alc);
            try
            {
                alc.Unload();
            }
            catch (Exception error)
            {
                PluginLog.Host($"{runtime.Manifest.Id} ALC 卸载异常：{error.Message}");
            }

            runtime.Alc = null;

            for (int i = 0; i < 5; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                if (runtime.AlcReference.IsAlive == false) break;
            }

            if (runtime.AlcReference.IsAlive)
            {
                PluginLog.Host($"{runtime.Manifest.Id} 的旧程序集未能完全释放，将在重启启动器后回收");
                Shell.Toast(Localization.Localization.T(
                    "Pages.Plugins.Unload.Linger",
                    "插件 {0} 的旧版本将在重启启动器后完全释放", runtime.Manifest.Name));
            }
        }
    }

    public static void Track(PluginRuntime runtime, IDisposable registration)
    {
        lock (Gate) runtime.Registrations.Add(registration);
    }

    public static void DemandPermission(PluginRuntime runtime, string zone) =>
        PluginPermissions.Demand(runtime, zone);

    private static void ExtractLoosePackages()
    {
        try
        {
            if (Directory.Exists(PluginsRoot) == false) return;

            foreach (string package in Directory.GetFiles(PluginsRoot, "*.nekoex"))
            {
                string id = Path.GetFileNameWithoutExtension(package);
                if (PluginManifest.IsValidPluginId(id) == false)
                {
                    PluginLog.Host("跳过 id 非法的 .nekoex：" + Path.GetFileName(package));
                    continue;
                }
                string target = ResolvePluginPath(id);
                if (Directory.Exists(target) && Directory.EnumerateFiles(target).Any()) continue;

                NekoexInstaller.ExtractPackage(package, target);
                PluginLog.Host($"已解包 {Path.GetFileName(package)} → plugins/{id}/");
            }
        }
        catch (Exception error)
        {
            PluginLog.Host("解包 .nekoex 失败：" + error.Message);
        }
    }
}

/// <summary>
/// 插件专属 AssemblyLoadContext：插件目录里的依赖优先从插件目录加载；
/// Avalonia 与契约库回退到宿主已加载的同一份，避免类型分裂。
/// </summary>
internal sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private readonly string _pluginDirectory;

    public PluginAssemblyLoadContext(string name, string pluginDirectory)
        : base($"plugin:{name}", isCollectible: true)
    {
        _pluginDirectory = pluginDirectory;
        Resolving += FallbackToHost;
    }

    private static bool IsHostOwnedAssembly(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name.StartsWith("Mofumofu", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("MofuCore", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("System", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
               || name is "netstandard" or "mscorlib";
    }

    private static bool IsShareableAssembly(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return string.Equals(name, "Mofumofu.ExtensionAPI", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("System", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
               || name is "netstandard" or "mscorlib";
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 宿主/框架程序集不允许从插件目录加载同名副本（避免插件用伪造的
        // Mofumofu.ExtensionAPI.dll / MofuCore.dll 等劫持类型解析），统一交给 Resolving 兜底到宿主同一份。
        if (IsHostOwnedAssembly(assemblyName.Name)) return null;

        string local = Path.Combine(_pluginDirectory, assemblyName.Name + ".dll");
        if (File.Exists(local))
        {
            return LoadFromAssemblyPath(Path.GetFullPath(local));
        }

        // 共享程序集与宿主保持同一份，交给 Resolving 兜底
        return null;
    }

    private Assembly? FallbackToHost(AssemblyLoadContext context, AssemblyName name)
    {
        // 只把契约层/框架程序集交回插件以维持类型同一性；
        // MofuCore、Mofumofu.Launcher 等含内部状态的宿主程序集一律不交回（避免反射读取账号令牌等私有状态）。
        if (IsShareableAssembly(name.Name) == false) return null;

        Assembly? shared = AssemblyLoadContext.Default.Assemblies
            .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase));
        if (shared is not null) return shared;

        string hostPath = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
        if (File.Exists(hostPath))
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(hostPath));
        }

        return null;
    }
}
