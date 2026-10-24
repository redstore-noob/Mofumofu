using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Mofumofu.Launcher.Models;
using Mofumofu.Launcher.Services;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views;

/// <summary>
/// 联机页：陶瓦联机卡 + 右侧联机设置面板。
/// 房间码、后台服务、会话状态全部来自 MofuCore.OnlineGame.Terracotta 真实会话。
/// </summary>
public partial class OnlineView : UserControl
{
    private readonly MofuCore.OnlineGame.Terracotta _terracotta = new();

    private bool _initialized;
    private bool _roomReady;
    private bool _serviceRunning;
    private int _providerIndex;
    private string _servicePath = string.Empty;

    public OnlineView()
    {
        InitializeComponent();
        LoadProviderData();
        NicknameBox.Text = LocalSettings.Current.SelectedAccountName ?? string.Empty;
        OnlinePlayersText.Text = L.T("Pages.Online.PlayersOnline", "在线玩家 {0}", 0);
        ApplyServiceState();
        UpdateProviderDisplay();
        _initialized = true;

        AttachedToVisualTree += (_, _) => PlayEnterAnimation();
    }

    /// <summary>进场：供应商卡与设置面板依次淡入上滑（QA 关动画时保持直接可见）。</summary>
    private void PlayEnterAnimation()
    {
        UiAnimations.Reset(BodyGrid);
        UiAnimations.FadeSlideIn(BodyGrid, 0.99, 10, 200);
    }

    /// <summary>面板出现动画：内容先重置到可见态，再按开关播放过渡。</summary>
    private static void AnimateAppear(Control? target, double fromScale = 0.99, double offsetY = 8)
    {
        if (target is null) return;

        UiAnimations.Reset(target);
        UiAnimations.FadeSlideIn(target, fromScale, offsetY, 170);
    }

    private static readonly OnlineProvider TerracottaProvider = new(
        "terracotta", "陶瓦联机",
        "基于 Terracotta 的虚拟局域网联机，房主需要安装 Terracotta。",
        "https://github.com/burningtnt/Terracotta", false, true,
        "房主需要安装 Terracotta 并安装联机模组。", "房主会给你一个 U/ 开头的房间码。");

    private static OnlineProvider? ProviderAt(int index) => index == 0 ? TerracottaProvider : null;

    private object? FindRes(string key) =>
        (this.TryFindResource(key, out var local) ? local : null)
        ?? (Application.Current is { } app && app.TryFindResource(key, out var global) ? global : null);

    // ==================== 初始化 ====================

    private void LoadProviderData()
    {
        var terracotta = ProviderAt(0);
        if (terracotta is not null)
        {
            MofuCore.OnlineGame.TerracottaProviderInfo info = _terracotta.GetInfo();
            MofuCore.OnlineGame.TerracottaRuntime runtime = _terracotta.GetRuntime();
            TerracottaName.Text = ProviderText(terracotta, "Name", terracotta.Name);
            TerracottaSummary.Text = ProviderText(terracotta, "Summary", info.Summary);
            TerracottaNote.Text = ProviderText(terracotta, "HostNote", info.HostNote);
            TerracottaReadyBadgeText.Text = info.Ready
                ? L.T("Pages.Online.Badge.Available", "可用")
                : L.T("Pages.Online.Badge.NotReady", "未就绪");
            TerracottaReadyBadgeText.Foreground = (IBrush?)FindRes(
                info.Ready ? "NekoSuccessBrush" : "NekoWarningBrush");
            TerracottaModBadge.IsVisible = terracotta.NeedsMod;
            TerracottaReadyBadgeText.Text = info.Ready
                ? L.T("Pages.Online.Badge.Available", "可用")
                : L.T("Pages.Online.Badge.NotReady", "未就绪");
            if (!string.IsNullOrWhiteSpace(runtime.Binary)) _servicePath = runtime.Binary;
        }

        _terracotta.StatusChanged = status => Dispatcher.UIThread.Post(() => ApplyTerracottaStatus(status));
        _terracotta.Log = line => Debug.WriteLine("[terracotta] " + line);

    }

    /// <summary>供应商专名/介绍按 id 走语言包，MockData 里的中文只作兜底（数据文件不翻译）。</summary>
    private static string ProviderText(OnlineProvider provider, string field, string fallback)
    {
        var prefix = provider.Id switch
        {
            "terracotta" => "Pages.Online.Provider.Terracotta.",
            _ => string.Empty,
        };

        return prefix.Length == 0 ? fallback : L.T(prefix + field, fallback);
    }

    private static string ProviderName(OnlineProvider? provider, string fallback)
    {
        return provider is null ? fallback : ProviderText(provider, "Name", provider.Name);
    }

    private void UpdateProviderDisplay()
    {
        var provider = ProviderAt(_providerIndex);
        if (provider is not null)
        {
            ServiceTitle.Text = ProviderName(provider, provider.Name);
            ModBadge.IsVisible = provider.NeedsMod;
            ProviderBadgeText.Text = L.T("Pages.Online.Provider.Current", "当前供应商：{0}",
                ProviderName(provider, provider.Name));
        }

        if (!_serviceRunning)
        {
            _servicePath = string.Empty;
        }

        ProviderCombo.SelectedIndex = _providerIndex;
    }

    // ==================== 房间状态 ====================

    private void SetRoomState(bool ready, string code, string address, bool joined)
    {
        _roomReady = ready;
        var provider = ProviderAt(_providerIndex);
        var providerName = ProviderName(provider, L.T("Pages.Online.Provider.Fallback", "联机"));

        TerracottaIdle.IsVisible = !ready;
        TerracottaReady.IsVisible = ready;

        if (ready)
        {
            var time = DateTime.Now.ToString("HH:mm:ss");
            var detail = joined
                ? L.T("Pages.Online.Detail.Joined", "已加入房间 · 开始于 {0}", time)
                : L.T("Pages.Online.Detail.Hosted", "房间已就绪 · 开始于 {0}", time);

            TerracottaCode.Text = code;
            TerracottaAddress.Text = address;
            TerracottaDetail.Text = detail;

            StatusChip.Classes.Set("accent", true);
            StatusDot.Background = (IBrush?)FindRes("NekoAccentBrush");
            StatusText.Text = L.T("Pages.Online.Status.Ready", "房间已就绪");
            StatusText.Foreground = (IBrush?)FindRes("NekoAccentBrush");

            ProviderBadge.Classes.Set("accent", true);
            ProviderBadgeText.Text = L.T("Pages.Online.Provider.Current", "当前供应商：{0}", providerName);
            ProviderBadgeText.Foreground = (IBrush?)FindRes("NekoAccentBrush");
            ModBadge.IsVisible = provider?.NeedsMod ?? false;

            SessionHint.Text = joined
                ? L.T("Pages.Online.Session.Joined", "房客 · {0} · {1}", providerName, code)
                : L.T("Pages.Online.Session.Host", "房主 · {0} · 把房间码发给房客", providerName);

            AnimateAppear(TerracottaReady);
            AnimateAppear(StatusChip, 1, 0);
        }
        else
        {
            StatusChip.Classes.Set("accent", false);
            StatusDot.Background = (IBrush?)FindRes("NekoDimBrush");
            StatusText.Text = L.T("Pages.Online.Status.Idle", "空闲");
            StatusText.Foreground = (IBrush?)FindRes("NekoMutedBrush");

            ProviderBadge.Classes.Set("accent", false);
            ProviderBadgeText.Text = L.T("Pages.Online.Provider.Current", "当前供应商：{0}", providerName);
            ProviderBadgeText.Foreground = (IBrush?)FindRes("NekoMutedBrush");
            ModBadge.IsVisible = provider?.NeedsMod ?? false;

            SessionHint.Text = L.T("Pages.Online.Session.None", "尚未创建或加入房间");
        }

        // 提示条与房间面板出现时给一点过渡（不再依赖动画才显示内容）
        if (ready)
        {
            AnimateAppear(ProviderBadge, 1, 0);
        }
    }

    // ==================== 后台服务 ====================

    private void ApplyServiceState()
    {
        MofuCore.OnlineGame.TerracottaRuntime runtime = _terracotta.GetRuntime();
        _serviceRunning = runtime.Running;
        if (!string.IsNullOrWhiteSpace(runtime.Binary)) _servicePath = runtime.Binary;

        ServiceStatusText.Text = _serviceRunning
            ? L.T("Pages.Online.Service.Running", "后台服务：已运行")
            : L.T("Pages.Online.Service.NotRunning", "后台服务：未运行");
        ServiceStatusText.Foreground = (IBrush?)FindRes(
            _serviceRunning ? "NekoSuccessBrush" : "NekoMutedBrush");
        ServicePathText.Text = string.IsNullOrWhiteSpace(_servicePath)
            ? L.T("Pages.Online.Service.NoBinary", "未检测到，可点「自动下载并安装」")
            : _servicePath;
        AutoInstallButton.IsVisible = !_serviceRunning;
    }

    // ==================== 事件 ====================

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        LoadProviderData();
        UpdateProviderDisplay();
        Shell.Toast(L.T("Pages.Online.Toast.Refreshed", "联机状态已刷新"));
    }

    private void OnToggleSettingsClick(object? sender, RoutedEventArgs e)
    {
        SettingsPanel.IsVisible = !SettingsPanel.IsVisible;

        if (SettingsPanel.IsVisible)
        {
            AnimateAppear(SettingsPanel, 0.98, 10);
        }

        Shell.Toast(SettingsPanel.IsVisible
            ? L.T("Pages.Online.Toast.SettingsShown", "已展开联机设置")
            : L.T("Pages.Online.Toast.SettingsHidden", "已收起联机设置"));
    }

    private void OnProviderHomepageClick(object? sender, RoutedEventArgs e)
    {
        // 供应商项目主页：用 OpenLinkBrowser 直接在浏览器打开
        if (sender is not Button { Tag: string url }) return;

        var result = OpenLinkBrowser.OpenLink(url);
        Shell.Toast(result == "success"
            ? L.T("Pages.Online.Toast.HomeOpened", "已在浏览器打开项目主页")
            : L.T("Pages.Online.Toast.HomeOpenFailed", "打开项目主页失败：{0}", result));
    }

    private async void OnCreateTerracottaClick(object? sender, RoutedEventArgs e)
    {
        _providerIndex = 0;
        UpdateProviderDisplay();
        _terracotta.BinaryPath = (ServicePathText.Text ?? string.Empty).Trim();
        try
        {
            await _terracotta.HostAsync((NicknameBox.Text ?? string.Empty).Trim());
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Pages.Online.Toast.HostFailed", "创建房间失败：{0}", ex.Message));
        }
    }

    private void OnJoinFromCardClick(object? sender, RoutedEventArgs e) =>
        OnJoinRoomClick(sender, e);

    private async void OnExitRoomClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await _terracotta.LeaveAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[terracotta] leave failed: " + ex.Message);
        }

        SetRoomState(false, string.Empty, string.Empty, joined: false);
        JoinCodeBox.Text = string.Empty;
        Shell.Toast(L.T("Pages.Online.Toast.LeftRoom", "已退出房间"));
    }

    private async void OnJoinRoomClick(object? sender, RoutedEventArgs e)
    {
        var input = (JoinCodeBox.Text ?? string.Empty).Trim();
        if (input.Length == 0)
        {
            Shell.Toast(L.T("Pages.Online.Toast.NeedInput", "请输入房间码"));
            return;
        }

        if (!input.StartsWith("U/", StringComparison.OrdinalIgnoreCase))
        {
            Shell.Toast(L.T("Pages.Online.Toast.BadFormat",
                "房间码格式不正确，应以 U/ 开头，例如 U/7F2A-9C31-D40E"));
            return;
        }

        _terracotta.BinaryPath = (ServicePathText.Text ?? string.Empty).Trim();
        try
        {
            await _terracotta.JoinAsync(input, (NicknameBox.Text ?? string.Empty).Trim());
            Shell.Toast(L.T("Pages.Online.Toast.Joined", "正在加入房间：{0}", input));
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Pages.Online.Toast.JoinFailed", "加入房间失败：{0}", ex.Message));
        }
    }

    private void OnSaveSettingsClick(object? sender, RoutedEventArgs e)
    {
        _terracotta.BinaryPath = (ServicePathText.Text ?? string.Empty).Trim();
        LoadProviderData();
        UpdateProviderDisplay();
        Shell.Toast(L.T("Pages.Online.Toast.SettingsSaved", "联机设置已保存"));
    }

    /// <summary>MofuCore 的陶瓦会话状态 → 联机页房间卡。</summary>
    private void ApplyTerracottaStatus(MofuCore.OnlineGame.TerracottaStatus status)
    {
        TerracottaDetail.Text = string.IsNullOrWhiteSpace(status.Phase) ? status.Tip : status.Phase;
        OnlinePlayersText.Text = L.T("Pages.Online.PlayersOnline", "在线玩家 {0}", status.Players.Count);

        if (status.State is MofuCore.OnlineGame.Terracotta.StateHosting or MofuCore.OnlineGame.Terracotta.StateJoined)
        {
            string code = MofuCore.OnlineGame.RoomCode.FormatForDisplay(status.Room);
            string address = string.IsNullOrWhiteSpace(status.Address) ? status.LocalAddress : status.Address;
            SetRoomState(true, code, address, status.State == MofuCore.OnlineGame.Terracotta.StateJoined);
            JoinCodeBox.Text = code;
            return;
        }

        if (status.State == MofuCore.OnlineGame.Terracotta.StateError)
        {
            SetRoomState(false, string.Empty, string.Empty, joined: false);
            Shell.Toast(L.T("Pages.Online.Toast.HostFailed", "创建房间失败：{0}",
                string.IsNullOrWhiteSpace(status.Error) ? status.Tip : status.Error));
        }
    }

    private void OnProviderChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;

        _providerIndex = ProviderCombo.SelectedIndex < 0 ? 0 : ProviderCombo.SelectedIndex;

        // 换供应商会失效当前房间，直接退出，避免两套状态打架
        if (_roomReady)
        {
            SetRoomState(false, string.Empty, string.Empty, joined: false);
            JoinCodeBox.Text = string.Empty;
        }

        UpdateProviderDisplay();

        var provider = ProviderAt(_providerIndex);
        Shell.Toast(L.T("Pages.Online.Toast.ProviderSwitched", "已切换供应商：{0}",
            ProviderName(provider, L.T("Pages.Online.Provider.Unknown", "未知"))));
    }

    private async void OnAutoInstallClick(object? sender, RoutedEventArgs e)
    {
        AutoInstallButton.IsEnabled = false;
        var task = DownloadTaskService.Start(
            L.T("Pages.Online.Service.TaskTitle", "陶瓦联机组件"),
            L.T("Pages.Online.Service.Downloading", "正在下载…"));
        try
        {
            MofuCore.OnlineGame.TerracottaInstallResult result = await _terracotta.InstallAsync();
            if (result.ManualHint.Length > 0)
            {
                task.Fail(result.ManualHint);
                Shell.Toast(result.ManualHint);
                return;
            }

            _servicePath = result.Path;
            task.Complete(L.T("Pages.Online.Toast.Downloaded", "已下载陶瓦联机：{0}", result.Path));
            ApplyServiceState();
            Shell.Toast(L.T("Pages.Online.Toast.Downloaded", "已下载陶瓦联机：{0}", result.Path));
        }
        catch (Exception ex)
        {
            task.Fail(ex.Message);
            Shell.Toast(L.T("Pages.Online.Toast.InstallFailed", "自动安装失败：{0}", ex.Message));
        }
        finally
        {
            AutoInstallButton.IsEnabled = true;
        }
    }

    private async void OnPickExecutableClick(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = L.T("Pages.Online.PickExecutable", "选择 terracotta 可执行文件"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new Avalonia.Platform.Storage.FilePickerFileType("Terracotta")
                {
                    Patterns = new[] { "terracotta*.exe", "*.exe" },
                },
            },
        });

        if (files.Count == 0) return;
        string? path = files[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;

        _terracotta.BinaryPath = path;
        _servicePath = path;
        ApplyServiceState();
        LoadProviderData();
        Shell.Toast(L.T("Pages.Online.Toast.ExecutablePicked", "已选择可执行文件：{0}", path));
    }

    private void OnRecheckServiceClick(object? sender, RoutedEventArgs e)
    {
        ApplyServiceState();
        Shell.Toast(_serviceRunning
            ? L.T("Pages.Online.Toast.ServiceOk", "后台服务运行正常")
            : L.T("Pages.Online.Toast.ServiceMissing", "未检测到后台服务，请先「自动下载并安装」"));
    }

    private async void OnStopServiceClick(object? sender, RoutedEventArgs e)
    {
        if (!_serviceRunning)
        {
            Shell.Toast(L.T("Pages.Online.Toast.ServiceNotRunning", "后台服务本来就未运行"));
            return;
        }

        try
        {
            await _terracotta.ShutdownAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[terracotta] shutdown failed: " + ex.Message);
        }

        ApplyServiceState();
        Shell.Toast(L.T("Pages.Online.Toast.ServiceStopped", "后台服务已关闭"));
    }

}
