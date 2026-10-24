using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Mofumofu.ExtensionAPI;
using Mofumofu.ExtensionAPI.Actions;
using Mofumofu.ExtensionAPI.Data;
using Mofumofu.ExtensionAPI.Downloads;
using Mofumofu.ExtensionAPI.Game;
using Mofumofu.ExtensionAPI.Java;
using Mofumofu.ExtensionAPI.Tools;
using Mofumofu.ExtensionAPI.UI;
using Mofumofu.Launcher.Controls;
using Mofumofu.Launcher.Views.Widgets;
using MofuCore.Download.Minecraft;
using MofuCore.Download.Modrinth;
using MofuCore.Java;
using MofuCore.Launch;

namespace Mofumofu.Launcher.Services.Plugins;

/// <summary>
/// IPluginContext 的宿主实现：把契约的每个能力域转接到启动器内部服务，
/// 全部带权限检查与 UI 线程调度；返回的撤销句柄统一登记到 PluginManager，卸载时代为回收。
/// </summary>
public sealed class PluginContextImpl : IPluginContext
{
    private readonly PluginRuntime _runtime;

    public PluginContextImpl(PluginRuntime runtime)
    {
        _runtime = runtime;
        Popup = new PopupImpl(this);
        Pages = new PagesImpl(this);
        PageRegister = new PageRegisterImpl(this);
        Widget = new WidgetImpl(this);
        Wallpaper = new WallpaperImpl(this);
        Style = new StyleImpl(this);
        Game = new GameImpl(this);
        Downloads = new DownloadsImpl(this);
        DownloadAction = new DownloadActionImpl(this);
        ResourceDownload = new ResourceDownloadImpl(this);
        Java = new JavaImpl(this);
        InstanceAction = new InstanceActionImpl(this);
        Taskbar = new TaskbarImpl(this);
        Theme = new ThemeImpl(this);
        SystemTray = new SystemTrayImpl(this);
        Config = new ConfigImpl(this);
    }

    internal PluginRuntime Runtime => _runtime;

    internal void Demand(string zone) => PluginManager.DemandPermission(_runtime, zone);

    internal IDisposable Track(IDisposable registration)
    {
        PluginManager.Track(_runtime, registration);
        return registration;
    }

    // ---- IPluginContext ----

    public IPopup Popup { get; }
    public IPageContentHost Pages { get; }
    public IPageRegister PageRegister { get; }
    public IWidgetAdd Widget { get; }
    public IWallpaperEdit Wallpaper { get; }
    public IStyleLoad Style { get; }
    public IGameLaunch Game { get; }
    public IInstanceDownload Downloads { get; }
    public IDownloadAction DownloadAction { get; }
    public IResourceDownload ResourceDownload { get; }
    public IJavaDownload Java { get; }
    public IInstanceAction InstanceAction { get; }
    public ITaskbarTray Taskbar { get; }
    public ITheme Theme { get; }
    public ISystemTray SystemTray { get; }
    public IConfig Config { get; }

    public void Log(string message) => PluginLog.Write(_runtime.Manifest.Id, message);

    public Control? CreateIcon(string kind, double size = 16)
    {
        if (string.IsNullOrEmpty(kind) || IconParkGlyphs.ByKey.ContainsKey(kind) == false) return null;
        return new IconParkIcon { Kind = kind, Width = size, Height = size };
    }

    public bool HasIcon(string kind) =>
        !string.IsNullOrEmpty(kind) && IconParkGlyphs.ByKey.ContainsKey(kind);

    public IReadOnlyList<string> IconKinds => [.. IconParkGlyphs.ByKey.Keys.OrderBy(k => k, StringComparer.Ordinal)];

    /// <summary>解析实例的某个资源子目录（mods / shaderpacks / resourcepacks），并做路径穿越防护。</summary>
    internal static string ResolveInstanceSubDirectory(PluginContextImpl owner, string instanceId, ResourceKind kind)
    {
        string versionsRoot = Path.Combine(
            MofuCore.Config.YamlRead.GameDirectory ?? string.Empty, "versions");
        string instanceDirectory = Path.GetFullPath(Path.Combine(versionsRoot, instanceId));
        if (instanceDirectory.StartsWith(Path.GetFullPath(versionsRoot), StringComparison.OrdinalIgnoreCase) == false
            || Directory.Exists(instanceDirectory) == false)
        {
            throw new InvalidOperationException("实例不存在：" + instanceId);
        }

        string sub = kind switch
        {
            ResourceKind.Mod => "mods",
            ResourceKind.Shader => "shaderpacks",
            _ => "resourcepacks",
        };
        return Path.Combine(instanceDirectory, sub);
    }

    internal static string ResolveModsDirectory(PluginContextImpl owner, string instanceId) =>
        ResolveInstanceSubDirectory(owner, instanceId, ResourceKind.Mod);

    /// <summary>按启用态文件名解析实际磁盘路径；strict=false 时两个状态都试。</summary>
    private static (string Current, string Target) ResolveModPaths(
        PluginContextImpl owner, string instanceId, string fileName, bool enable)
    {
        string mods = ResolveModsDirectory(owner, instanceId);
        string safeName = Path.GetFileName(fileName); // 防穿越
        string enabledPath = Path.Combine(mods, safeName);
        string disabledPath = enabledPath + ".disabled";
        return enable ? (disabledPath, enabledPath) : (enabledPath, disabledPath);
    }

    private static string ResolveExistingModPath(PluginContextImpl owner, string instanceId, string fileName)
    {
        string mods = ResolveModsDirectory(owner, instanceId);
        string safeName = Path.GetFileName(fileName);
        string enabledPath = Path.Combine(mods, safeName);
        if (File.Exists(enabledPath)) return enabledPath;
        string disabledPath = enabledPath + ".disabled";
        return File.Exists(disabledPath) ? disabledPath : string.Empty;
    }

    internal static void RunOnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    // ==================================================================
    // Popup
    // ==================================================================

    private sealed class PopupImpl(PluginContextImpl owner) : IPopup
    {
        public void Dialog(Control content, string title)
        {
            owner.Demand(PluginPermissions.Notify);
            RunOnUi(() => Shell.Dialog(content, title));
        }

        public void Toast(string message)
        {
            owner.Demand(PluginPermissions.Notify);
            RunOnUi(() => Shell.Toast($"[{owner.Runtime.Manifest.Name}] {message}"));
        }
    }

    // ==================================================================
    // Pages（页面内容注入，宿主已有 PageComposer）
    // ==================================================================

    private sealed class PagesImpl(PluginContextImpl owner) : IPageContentHost
    {
        public IDisposable Add(string pageId, PageSlot slot, Func<Control> factory, int order = 0)
        {
            IDisposable registration = PageComposer.Current.Add(pageId, slot, WrapFactory(owner, factory), order);
            return owner.Track(registration);
        }
    }

    /// <summary>插件回调包 try/catch：托盘点击等入口炸了只记日志。</summary>
    internal static Action WrapClick(PluginContextImpl owner, Action click) =>
        () =>
        {
            try
            {
                click();
            }
            catch (Exception error)
            {
                PluginLog.Write(owner.Runtime.Manifest.Id, "点击回调异常：" + error);
            }
        };

    /// <summary>插件工厂全部包一层 try/catch：单个插件炸了不牵连页面渲染。</summary>
    internal static Func<Control> WrapFactory(PluginContextImpl owner, Func<Control> factory) =>
        () =>
        {
            try
            {
                return factory();
            }
            catch (Exception error)
            {
                PluginLog.Write(owner.Runtime.Manifest.Id, "内容工厂异常：" + error);
                return new TextBlock
                {
                    Text = Localization.Localization.T(
                        "Pages.Plugins.ContentFailed", "插件 {0} 的内容构建失败", owner.Runtime.Manifest.Name),
                    Foreground = ThemeTokens.Brush("NekoDangerBrush"),
                    FontSize = 12,
                };
            }
        };

    // ==================================================================
    // PageRegister（新导航页）
    // ==================================================================

    private sealed class PageRegisterImpl(PluginContextImpl owner) : IPageRegister
    {
        public IDisposable RegisterPage(string pageName, Func<Control> createPage, Control icon, bool isTool)
        {
            // isTool（注册到工具下拉栏）暂未接入宿主路由，行为与普通页面一致
            string pageId = $"ext:{owner.Runtime.Manifest.Id}:{Guid.NewGuid():N}";
            IDisposable registration = MainWindow.RegisterPluginPage(
                pageId, pageName, icon, WrapFactory(owner, createPage));
            return owner.Track(registration);
        }
    }

    // ==================================================================
    // Widget（桌面小组件）
    // ==================================================================

    private sealed class WidgetImpl(PluginContextImpl owner) : IWidgetAdd
    {
        public IDisposable WidgetAdd(string id, Control widget)
        {
            owner.Demand(PluginPermissions.Widget);
            string uniqueId = $"{owner.Runtime.Manifest.Id}.{id}";
            IDisposable registration = WidgetRegistry.RegisterDynamic(
                uniqueId,
                Localization.Localization.T(
                    $"Widgets.{uniqueId}.Title", owner.Runtime.Manifest.Name),
                owner.Runtime.Manifest.Description,
                WrapFactory(owner, () => widget),
                4, 3);
            return owner.Track(registration);
        }

        public void WidgetTitleEdit(string id, string newTitle)
        {
            owner.Demand(PluginPermissions.Widget);
            WidgetRegistry.EditDynamicTitle($"{owner.Runtime.Manifest.Id}.{id}", newTitle);
        }

        public void WidgetEdit(string id, Control newWidget)
        {
            owner.Demand(PluginPermissions.Widget);
            WidgetRegistry.EditDynamicContent($"{owner.Runtime.Manifest.Id}.{id}", WrapFactory(owner, () => newWidget));
        }
    }

    // ==================================================================
    // Wallpaper（背景接管）
    // ==================================================================

    private sealed class WallpaperImpl(PluginContextImpl owner) : IWallpaperEdit
    {
        public IDisposable WallpaperTakeOver(Control wallpaperContent)
        {
            IDisposable registration = MainWindow.TakeOverWallpaper(wallpaperContent);
            return owner.Track(registration);
        }
    }

    // ==================================================================
    // Style（运行时 axaml 样式）
    // ==================================================================

    private sealed class StyleImpl(PluginContextImpl owner) : IStyleLoad
    {
        public IDisposable StyleEdit(Stream xamlStream) => Load(owner, ReadAll(xamlStream));

        public IDisposable StyleEdit(string xamlPath)
        {
            string full = Path.Combine(owner.Runtime.Manifest.Directory, xamlPath);
            if (File.Exists(full) == false)
            {
                throw new FileNotFoundException("插件目录下找不到 axaml 文件：" + xamlPath);
            }

            return Load(owner, File.ReadAllBytes(full));
        }

        public IDisposable StyleEdit(IStyle styles)
        {
            // 主题库（Material.Avalonia 等）的标准入口：直接合入现成实例
            IDisposable registration = new StyleRegistration(styles);
            RunOnUi(() => App.Current!.Styles.Add(styles));
            return owner.Track(registration);
        }

        private static byte[] ReadAll(Stream stream)
        {
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }

        private static IDisposable Load(PluginContextImpl owner, byte[] xamlBytes)
        {
            LoadThemeDependenciesIntoHost(owner);
            try
            {
                using var stream = new MemoryStream(xamlBytes);
                if (Avalonia.Markup.Xaml.AvaloniaRuntimeXamlLoader.Load(stream) is IStyle style)
                {
                    IDisposable registration = new StyleRegistration(style);
                    RunOnUi(() => App.Current!.Styles.Add(style));
                    return owner.Track(registration);
                }
            }
            catch (Exception error)
            {
                PluginLog.Write(owner.Runtime.Manifest.Id, "样式加载失败：" + error);
                throw new InvalidOperationException("axaml 样式解析失败：" + error.Message, error);
            }

            throw new InvalidOperationException("axaml 根节点必须是 Styles（<Styles xmlns='https://github.com/avaloniaui'>）");
        }

        private static readonly HashSet<string> HostedThemeAssemblies = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 把插件随包的主题库 DLL（Material.Avalonia 等）装进宿主默认 ALC：
        /// DefaultAssetLoader 只认默认上下文里的程序集，装进去后 avares:// 才能解析。
        /// 代价：这些 DLL 被钉住（无法热卸载）——主题库本来就长驻，可接受。
        /// </summary>
        private static void LoadThemeDependenciesIntoHost(PluginContextImpl owner)
        {
            foreach (string dll in Directory.GetFiles(owner.Runtime.Manifest.Directory, "*.dll"))
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(dll);
                if (string.Equals(name, "extension", StringComparison.OrdinalIgnoreCase)) continue;
                lock (HostedThemeAssemblies)
                {
                    if (HostedThemeAssemblies.Contains(name)) continue;
                    HostedThemeAssemblies.Add(name);
                }

                try
                {
                    System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(System.IO.Path.GetFullPath(dll));
                    PluginLog.Write(owner.Runtime.Manifest.Id, "主题依赖已装入宿主：" + name);
                }
                catch (Exception error)
                {
                    PluginLog.Write(owner.Runtime.Manifest.Id, "主题依赖装入失败 " + name + "：" + error.Message);
                }
            }
        }

        private sealed class StyleRegistration(IStyle style) : IDisposable
        {
            public void Dispose()
            {
                RunOnUi(() => App.Current?.Styles.Remove(style));
            }
        }
    }

    // ==================================================================
    // Theme（主题读取）
    // ==================================================================

    private sealed class UnsubscribeToken(Action unsubscribe) : IDisposable
    {
        public void Dispose() => unsubscribe();
    }

    private sealed class ThemeImpl(PluginContextImpl owner) : ITheme
    {
        private event Action? _themeChanged;

        public string AccentColorHex =>
            ThemeManager.AccentOptions.FirstOrDefault(option =>
                    string.Equals(option.Key, ThemeManager.CurrentAccent, StringComparison.OrdinalIgnoreCase))
                .Hex
            ?? "#2F88FF";

        public string ThemeMode => ThemeManager.CurrentTheme;

        public bool IsLight => ThemeManager.IsLight;

        public event Action ThemeChanged
        {
            add
            {
                _themeChanged += value;
                ThemeManager.ThemeChanged += OnHostThemeChanged;
                owner.Track(new UnsubscribeToken(() =>
                {
                    _themeChanged -= value;
                    ThemeManager.ThemeChanged -= OnHostThemeChanged;
                }));
            }
            remove => _themeChanged -= value;
        }

        private void OnHostThemeChanged() => RunOnUi(() => _themeChanged?.Invoke());
    }

    // ==================================================================
    // Game（实例查询 / 启动）
    // ==================================================================

    private sealed class GameImpl(PluginContextImpl owner) : IGameLaunch
    {
        // 事件源头：LaunchProgress 的进程启动/完成信号（全插件共享一份静态桥）
        private static readonly object SourceGate = new();
        private static bool _sourceHooked;
        private static event Action? SourceGameLaunched;
        private static event Action<int>? SourceGameExited;

        private event Action? _gameLaunched;
        private event Action<int>? _gameExited;
        private bool _bridged;

        public event Action GameLaunched
        {
            add
            {
                owner.Demand(PluginPermissions.Readonly);
                EnsureBridged(owner);
                _gameLaunched += value;
                owner.Track(new EventToken(() => _gameLaunched -= value));
            }
            remove => _gameLaunched -= value;
        }

        public event Action<int> GameExited
        {
            add
            {
                owner.Demand(PluginPermissions.Readonly);
                EnsureBridged(owner);
                _gameExited += value;
                owner.Track(new EventToken(() => _gameExited -= value));
            }
            remove => _gameExited -= value;
        }

        /// <summary>首个订阅者出现时把本实例桥接到静态源；桥本身登记进卸载清单，停用时整条链断开。</summary>
        private void EnsureBridged(PluginContextImpl owner)
        {
            if (_bridged) return;
            _bridged = true;

            lock (SourceGate)
            {
                if (_sourceHooked == false)
                {
                    _sourceHooked = true;
                    LaunchProgress.Shared.ProcessStarted += pid =>
                        RunOnUi(() => SourceGameLaunched?.Invoke());
                    LaunchProgress.Shared.Completed += (_, e) =>
                        RunOnUi(() => SourceGameExited?.Invoke(e.ExitCode ?? -1));
                }
            }

            SourceGameLaunched += OnSourceLaunched;
            SourceGameExited += OnSourceExited;
            owner.Track(new EventToken(() =>
            {
                SourceGameLaunched -= OnSourceLaunched;
                SourceGameExited -= OnSourceExited;
            }));
        }

        private void OnSourceLaunched() => _gameLaunched?.Invoke();

        private void OnSourceExited(int code) => _gameExited?.Invoke(code);

        private sealed class EventToken(Action unsubscribe) : IDisposable
        {
            public void Dispose() => unsubscribe();
        }

        public async Task<IReadOnlyList<InstanceInfo>> GetInstancesAsync(CancellationToken cancellationToken = default)
        {
            owner.Demand(PluginPermissions.Readonly);
            return await Task.Run(() => (IReadOnlyList<InstanceInfo>)ScanInstances(), cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<InstanceInfo?> GetSelectedInstanceAsync(CancellationToken cancellationToken = default)
        {
            owner.Demand(PluginPermissions.Readonly);
            IReadOnlyList<InstanceInfo> instances = await GetInstancesAsync(cancellationToken).ConfigureAwait(false);
            string selected = LocalSettings.Current.SelectedInstanceId;
            return instances.FirstOrDefault(instance =>
                string.Equals(instance.Id, selected, StringComparison.OrdinalIgnoreCase));
        }

        public Task LaunchSelectedAsync(CancellationToken cancellationToken = default)
        {
            // 只启动玩家选中的实例——玩家意图即授权，不设权限区
            return Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (GameLaunch.Shared.IsGameRunning)
                {
                    throw new InvalidOperationException("游戏已经在运行。");
                }

                if (LaunchRequestResolver.TryResolve(
                        out MinecraftLaunchOptions options, out string displayName, out string versionLabel,
                        out string reason) == false)
                {
                    throw new InvalidOperationException(reason);
                }

                if (LaunchProgress.Shared.StartReal(options, displayName, versionLabel) == false)
                {
                    throw new InvalidOperationException(Localization.Localization.T(
                        "Pages.Plugins.LaunchBusy", "启动器正忙，请稍后再试"));
                }
            }, DispatcherPriority.Normal).GetTask();
        }

        public bool IsGameRunning => GameLaunch.Shared.IsGameRunning;

        private static List<InstanceInfo> ScanInstances()
        {
            List<InstanceInfo> result = [];
            try
            {
                string? gameRoot = MofuCore.Config.YamlRead.GameDirectory;
                if (string.IsNullOrWhiteSpace(gameRoot)) return result;

                string versions = Path.Combine(gameRoot, "versions");
                if (Directory.Exists(versions) == false) return result;

                foreach (string directory in Directory.GetDirectories(versions))
                {
                    string id = Path.GetFileName(directory);
                    // 没有版本 JSON 的目录（备份/半成品）不算实例
                    if (string.IsNullOrWhiteSpace(id) ||
                        !File.Exists(Path.Combine(directory, id + ".json")))
                    {
                        continue;
                    }

                    result.Add(new InstanceInfo(
                        id, id, id, string.Empty, null, TimeSpan.Zero, 0, 0, 0));
                }
            }
            catch
            {
                // 目录不可读按空列表处理
            }

            return result;
        }
    }

    // ==================================================================
    // Downloads（版本清单 / 原版实例安装）
    // ==================================================================

    private sealed class DownloadsImpl(PluginContextImpl owner) : IInstanceDownload
    {
        public async Task<IReadOnlyList<DownloadableVersionInfo>> GetVersionsAsync(CancellationToken cancellationToken = default)
        {
            owner.Demand(PluginPermissions.NetworkReadonly);
            IReadOnlyList<MinecraftVersion> versions = await MinecraftDownload.GetVersionsAsync(cancellationToken)
                .ConfigureAwait(false);
            return versions
                .Select(v => new DownloadableVersionInfo(v.Id, v.Type, v.ReleaseTime.UtcDateTime))
                .ToList();
        }

        public async Task<IReadOnlyList<LoaderVersionInfo>> GetLoaderVersionsAsync(
            ModLoaderType loader, string gameVersion, CancellationToken cancellationToken = default)
        {
            owner.Demand(PluginPermissions.NetworkReadonly);
            IReadOnlyList<ModLoaderVersionInfo> versions = await ModLoaderInstaller.GetVersionsAsync(
                ToHostKind(loader), gameVersion, cancellationToken).ConfigureAwait(false);
            return versions
                .Select(v => new LoaderVersionInfo(loader, v.MinecraftVersion, v.LoaderVersion, v.IsStable))
                .ToList();
        }

        public async Task<InstanceInfo> DownloadAsync(
            InstanceDownloadRequest request,
            IProgress<InstanceDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            owner.Demand(PluginPermissions.Network);

            string gameDirectory = MofuCore.Config.YamlRead.EnsureGameDirectory()
                ?? throw new InvalidOperationException("无法确定游戏目录。");
            string displayName = request.DisplayName ?? request.VersionId;

            // 带加载器：查该游戏版本的加载器列表 → 按版本号挑选（缺省取最新稳定版）→ 安装（原版依赖自动补装）
            if (request.Loader is { } loader)
            {
                IReadOnlyList<ModLoaderVersionInfo> loaderVersions = await ModLoaderInstaller.GetVersionsAsync(
                    ToHostKind(loader), request.VersionId, cancellationToken).ConfigureAwait(false);
                if (loaderVersions.Count == 0)
                {
                    throw new InvalidOperationException(
                        loader + " 没有适配 " + request.VersionId + " 的版本。");
                }

                ModLoaderVersionInfo? chosen = request.LoaderVersion is null
                    ? loaderVersions.FirstOrDefault(v => v.IsStable) ?? loaderVersions[0]
                    : loaderVersions.FirstOrDefault(v => string.Equals(
                        v.LoaderVersion, request.LoaderVersion, StringComparison.OrdinalIgnoreCase));
                if (chosen is null)
                {
                    throw new InvalidOperationException(
                        loader + " " + request.LoaderVersion + " 不在适配 " + request.VersionId + " 的版本列表里。");
                }

                var loaderProgress = new Progress<ModLoaderInstallProgress>(value => progress?.Report(
                    new InstanceDownloadProgress(
                        value.StageName + (value.Detail.Length > 0 ? " · " + value.Detail : string.Empty),
                        value.CompletedBytes,
                        value.TotalBytes,
                        (long)value.BytesPerSecond,
                        0, 0)));

                string installedId = await ModLoaderInstaller.InstallAsync(
                    chosen, gameDirectory, displayName, loaderProgress, cancellationToken).ConfigureAwait(false);

                return new InstanceInfo(
                    installedId,
                    displayName,
                    request.VersionId,
                    chosen.Kind.ToString(),
                    null, TimeSpan.Zero, 0, 0, 0);
            }


            string? metadataUrl = await MinecraftDownload.FindVersionMetadataUrlAsync(request.VersionId, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(metadataUrl))
            {
                throw new InvalidOperationException($"版本清单中没有找到 {request.VersionId} 的元数据。");
            }

            var download = new MinecraftDownload(gameDirectory);
            var installProgress = new Progress<MinecraftInstallProgress>(value => progress?.Report(
                new InstanceDownloadProgress(
                    value.StageName,
                    value.CompletedBytes,
                    value.TotalBytes,
                    (long)value.BytesPerSecond,
                    0, 0)));
            await download.InstallAsync(request.VersionId, metadataUrl, installProgress, cancellationToken)
                .ConfigureAwait(false);

            return new InstanceInfo(
                request.VersionId,
                displayName,
                request.VersionId,
                string.Empty, null, TimeSpan.Zero, 0, 0, 0);
        }

        private static ModLoaderKind ToHostKind(ModLoaderType loader) => loader switch
        {
            ModLoaderType.NeoForge => ModLoaderKind.NeoForge,
            ModLoaderType.Fabric => ModLoaderKind.Fabric,
            ModLoaderType.Quilt => ModLoaderKind.Quilt,
            _ => ModLoaderKind.Forge,
        };
    }

    // ==================================================================
    // DownloadAction（按 4 位编号查询 / 操作下载任务 + 完成/失败通知）
    // ==================================================================

    private sealed class DownloadActionImpl(PluginContextImpl owner) : IDownloadAction
    {
        public Task<DownloadTaskData?> GetTaskAsync(int taskNumber, CancellationToken cancellationToken = default)
        {
            owner.Demand(PluginPermissions.NetworkReadonly);
            DownloadTaskSnapshot? snapshot = DownloadTaskService.Snapshots
                .FirstOrDefault(item => item.ShortId == taskNumber.ToString("0000"));
            return Task.FromResult<DownloadTaskData?>(snapshot is null ? null : ToData(snapshot));
        }

        public bool Cancel(int taskNumber)
        {
            owner.Demand(PluginPermissions.Network);
            string? id = FindId(taskNumber);
            return id is not null && DownloadTaskService.TryCancel(id);
        }

        public bool SetPaused(int taskNumber, bool paused)
        {
            owner.Demand(PluginPermissions.Network);
            string? id = FindId(taskNumber);
            return id is not null && DownloadTaskService.TrySetPaused(id, paused);
        }

        public void DownloadCompletedHook(int taskNumber, string title)
        {
            // 由 PluginManager 统一派发（见 HookDownloadEvents）
        }

        public void DownloadFailedHook(int taskNumber, string title, string reason)
        {
            // 由 PluginManager 统一派发
        }

        private static string? FindId(int taskNumber) =>
            DownloadTaskService.Snapshots
                .FirstOrDefault(item => item.ShortId == taskNumber.ToString("0000"))?.Id;

        internal static DownloadTaskData ToData(DownloadTaskSnapshot snapshot) => new(
            int.Parse(snapshot.ShortId),
            snapshot.Title,
            snapshot.State switch
            {
                Services.DownloadTaskState.Downloading => Mofumofu.ExtensionAPI.Data.DownloadTaskState.Downloading,
                Services.DownloadTaskState.Paused => Mofumofu.ExtensionAPI.Data.DownloadTaskState.Paused,
                Services.DownloadTaskState.Done => Mofumofu.ExtensionAPI.Data.DownloadTaskState.Done,
                Services.DownloadTaskState.Cancelled => Mofumofu.ExtensionAPI.Data.DownloadTaskState.Cancelled,
                _ => Mofumofu.ExtensionAPI.Data.DownloadTaskState.Failed,
            },
            snapshot.Percent,
            snapshot.SpeedText,
            snapshot.SizeText,
            snapshot.CanPause);
    }

    // ==================================================================
    // ResourceDownload（Modrinth 资源搜索 / 下载到实例）
    // ==================================================================

    private sealed class ResourceDownloadImpl(PluginContextImpl owner) : IResourceDownload
    {
        public async Task<IReadOnlyList<ResourceProject>> SearchAsync(
            string query, ResourceKind kind, string? gameVersion = null, string? loader = null,
            int limit = 20, int offset = 0, CancellationToken cancellationToken = default)
        {
            owner.Demand(PluginPermissions.NetworkReadonly);
            IReadOnlyList<ModrinthProject> hits = await ModrinthResourceSearch.SearchAsync(
                query, ToModrinthType(kind), gameVersion,
                string.IsNullOrWhiteSpace(loader) ? null : [loader!],
                Math.Clamp(limit, 1, 100), offset, "relevance", cancellationToken).ConfigureAwait(false);
            return hits.Select(ToProject).ToList();
        }

        public async Task<IReadOnlyList<ResourceFileInfo>> GetFilesAsync(
            string projectId, string? gameVersion = null, string? loader = null,
            CancellationToken cancellationToken = default)
        {
            owner.Demand(PluginPermissions.NetworkReadonly);
            IReadOnlyList<ModrinthVersion> versions = await ModrinthResourceDownload.GetVersionsForComboAsync(
                projectId, gameVersion ?? string.Empty, loader ?? string.Empty, cancellationToken).ConfigureAwait(false);
            return versions
                .Select(ToFileInfo)
                .Where(info => info is not null)
                .Select(info => info!)
                .ToList();
        }

        public async Task<ResourceDownloadResult> DownloadAsync(
            string versionId, ResourceKind kind, string instanceId,
            string? gameVersion = null, string? loader = null, bool includeDependencies = true,
            IProgress<(int Completed, int Total, string Detail)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            owner.Demand(PluginPermissions.Network);
            string targetDirectory = ResolveInstanceSubDirectory(owner, instanceId, kind);
            Directory.CreateDirectory(targetDirectory);

            var progressAdapter = progress is null
                ? null
                : new Progress<ModrinthDownloadProgress>(value =>
                    progress.Report((value.Completed, value.Total, value.Detail)));

            ModrinthDownloadResult result = await ModrinthResourceDownload.DownloadWithDependenciesAsync(
                versionId, targetDirectory, includeDependencies,
                gameVersion, loader, progressAdapter, cancellationToken).ConfigureAwait(false);

            return new ResourceDownloadResult(
                targetDirectory,
                result.Files.Select(file => file.FilePath).ToList());
        }

        internal static string ToModrinthType(ResourceKind kind) => kind switch
        {
            ResourceKind.Mod => "mod",
            ResourceKind.Shader => "shader",
            _ => "resourcepack",
        };

        private static ResourceProject ToProject(ModrinthProject project) => new(
            project.ProjectId,
            project.Title,
            project.Description,
            string.Empty, // Modrinth 搜索接口不返回作者字段
            project.Downloads,
            string.IsNullOrWhiteSpace(project.IconUrl) ? null : project.IconUrl);

        private static ResourceFileInfo? ToFileInfo(ModrinthVersion version)
        {
            ModrinthVersionFile? file = version.PrimaryFile();
            return file is null
                ? null
                : new ResourceFileInfo(version.Id, version.VersionNumber, file.Filename, file.Size);
        }
    }

    // ==================================================================
    // Java（Mojang 运行时安装 + 本机 Java 检测）
    // ==================================================================

    private sealed class JavaImpl(PluginContextImpl owner) : IJavaDownload
    {
        public IReadOnlyList<int> SupportedMajorVersions =>
            [.. MofuCore.Java.VendorJavaInstaller.SupportedVersions];

        public async Task<IReadOnlyList<JavaRuntimeChoice>> GetAvailableAsync(CancellationToken cancellationToken = default)
        {
            owner.Demand(PluginPermissions.NetworkReadonly);
            IReadOnlyList<MojangJavaRuntime> runtimes = await JavaDownload.GetAvailableAsync(null, cancellationToken)
                .ConfigureAwait(false);
            return runtimes.Select(runtime => new JavaRuntimeChoice(
                runtime.Component, runtime.VersionName, runtime.MajorVersion, runtime.Released)).ToList();
        }

        public async Task<InstalledJavaInfo?> InstallAsync(
            int majorVersion,
            IProgress<(string Phase, long CompletedBytes, long TotalBytes)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            owner.Demand(PluginPermissions.Network);
            string gameDirectory = MofuCore.Config.YamlRead.EnsureGameDirectory()
                ?? throw new InvalidOperationException("无法确定游戏目录。");

            var progressAdapter = progress is null
                ? null
                : new Progress<JavaRuntimeInstallProgress>(value =>
                    progress.Report((value.Phase, value.CompletedBytes, value.TotalBytes)));

            InstalledJavaRuntime? installed = await JavaDownload.InstallForMajorVersionAsync(
                majorVersion, gameDirectory, progressAdapter, cancellationToken).ConfigureAwait(false);
            if (installed is null || string.IsNullOrWhiteSpace(installed.JavaExecutablePath))
            {
                return null;
            }

            return new InstalledJavaInfo(
                installed.JavaExecutablePath,
                installed.DirectoryPath,
                installed.Component,
                installed.MajorVersion);
        }

        public async Task<IReadOnlyList<InstalledJavaInfo>> GetInstalledAsync(CancellationToken cancellationToken = default)
        {
            owner.Demand(PluginPermissions.Readonly);
            string? gameDirectory = MofuCore.Config.YamlRead.GameDirectory;
            IReadOnlyList<JavaRuntime> runtimes = await MofuCore.Java.JavaPathSearch.FindAllJavaAsync(
                gameDirectory is null ? null : JavaDownload.GetRuntimeDirectory(gameDirectory),
                cancellationToken).ConfigureAwait(false);
            return runtimes
                .Where(runtime => runtime.MajorVersion is not null)
                .Select(runtime => new InstalledJavaInfo(
                    runtime.JavaPath, runtime.HomePath, string.Empty, runtime.MajorVersion))
                .ToList();
        }
    }

    // ==================================================================
    // InstanceAction（实例 mods 启停 / 删除）
    // ==================================================================

    private sealed class InstanceActionImpl(PluginContextImpl owner) : IInstanceAction
    {
        public Task<IReadOnlyList<ModFileInfo>> GetModsAsync(string instanceId, CancellationToken cancellationToken = default)
        {
            owner.Demand(PluginPermissions.Readonly);
            string modsDirectory = ResolveModsDirectory(owner, instanceId);
            return Task.Run<IReadOnlyList<ModFileInfo>>(() =>
            {
                if (Directory.Exists(modsDirectory) == false) return [];

                var result = new List<ModFileInfo>();
                foreach (string path in Directory.EnumerateFiles(modsDirectory, "*.jar*"))
                {
                    string name = Path.GetFileName(path);
                    bool disabled = name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                    result.Add(new ModFileInfo(
                        disabled ? name[..^".disabled".Length] : name,
                        disabled == false,
                        File.Exists(path) ? new FileInfo(path).Length : 0));
                }

                result.Sort((left, right) => string.Compare(left.FileName, right.FileName, StringComparison.OrdinalIgnoreCase));
                return result;
            }, cancellationToken);
        }

        public bool SetModEnabled(string instanceId, string fileName, bool enabled)
        {
            owner.Demand(PluginPermissions.InstanceWrite);
            (string current, string target) = ResolveModPaths(owner, instanceId, fileName, enabled);
            try
            {
                if (File.Exists(current) == false) return false;
                if (File.Exists(target)) return false;
                File.Move(current, target);
                return true;
            }
            catch (Exception error)
            {
                PluginLog.Write(owner.Runtime.Manifest.Id, $"SetModEnabled({fileName}) 失败：{error.Message}");
                return false;
            }
        }

        public bool DeleteMod(string instanceId, string fileName)
        {
            owner.Demand(PluginPermissions.InstanceWrite);
            string path = ResolveExistingModPath(owner, instanceId, fileName);
            try
            {
                if (path.Length == 0 || File.Exists(path) == false) return false;
                File.Delete(path);
                return true;
            }
            catch (Exception error)
            {
                PluginLog.Write(owner.Runtime.Manifest.Id, $"DeleteMod({fileName}) 失败：{error.Message}");
                return false;
            }
        }
    }

    // ==================================================================
    // Taskbar（任务栏右侧图标，点击弹 Popup 或跳页面）
    // ==================================================================

    private sealed class TaskbarImpl(PluginContextImpl owner) : ITaskbarTray
    {
        public IDisposable AddWidget(string id, Control icon, Action click)
        {
            owner.Demand(PluginPermissions.Widget);
            string uniqueId = owner.Runtime.Manifest.Id + "." + id;
            IDisposable registration = MainWindow.AppendPluginTrayWidget(
                uniqueId, icon, WrapClick(owner, click));
            return owner.Track(registration);
        }

        public void NavigateTo(string pageId) => MainWindow.NavigateFromPlugin(pageId);
    }

    // ==================================================================
    // SystemTray（托盘常驻：菜单项 + 气泡通知）
    // ==================================================================

    private sealed class SystemTrayImpl(PluginContextImpl owner) : ISystemTray
    {
        public IDisposable AddMenuItem(string label, Action click)
        {
            owner.Demand(PluginPermissions.Widget);
            string uniqueId = owner.Runtime.Manifest.Id + "." + label;
            IDisposable registration = MainWindow.AppendPluginTrayMenuItem(
                uniqueId, label, WrapClick(owner, click));
            return owner.Track(registration);
        }

        public void ShowNotification(string title, string message)
        {
            owner.Demand(PluginPermissions.Notify);
            RunOnUi(() => MainWindow.ShowPluginTrayNotification(title, message));
        }
    }

    // ==================================================================
    // Config（插件独立配置文件）
    // ==================================================================

    private sealed class ConfigImpl(PluginContextImpl owner) : IConfig
    {
        private string ConfigPath => Path.Combine(
            MofuCore.Config.YamlRead.DataRoot,
            MofuCore.Config.YamlRead.DataDirectoryName,
            "plugin",
            owner.Runtime.Manifest.Id,
            "config.yaml");

        public Boolean ConfigAdd(string key, string value)
        {
            owner.Demand(PluginPermissions.Storage);
            try
            {
                Dictionary<string, string> values = PluginManifest.ParseYamlLines(ConfigPath);
                values[key] = value;
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                File.WriteAllLines(ConfigPath,
                    values.Select(pair => $"{pair.Key}: {pair.Value}"));
                return true;
            }
            catch (Exception error)
            {
                PluginLog.Write(owner.Runtime.Manifest.Id, $"ConfigAdd({key}) 失败：{error.Message}");
                return false;
            }
        }

        public Boolean ConfigRemove(string key)
        {
            owner.Demand(PluginPermissions.Storage);
            try
            {
                Dictionary<string, string> values = PluginManifest.ParseYamlLines(ConfigPath);
                if (values.Remove(key) == false) return false;

                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                File.WriteAllLines(ConfigPath,
                    values.Select(pair => $"{pair.Key}: {pair.Value}"));
                return true;
            }
            catch (Exception error)
            {
                PluginLog.Write(owner.Runtime.Manifest.Id, $"ConfigRemove({key}) 失败：{error.Message}");
                return false;
            }
        }
    }
}
