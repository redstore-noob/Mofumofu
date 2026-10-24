using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mofumofu.Launcher.Mock;
using Mofumofu.Launcher.Services;
using Mofumofu.Launcher.Web;
using MofuCore.Account;
using SukiUI.Controls;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views;

/// <summary>
/// 账户管理页：账号列表（选中/默认/删除）+ 3D 皮肤预览（局部 WebView2）+ 玩家外观。
/// 皮肤贴图按旧版做法获取：Mojang 档案 → 本地缓存 → 客户端内置 Steve/Alex → 页面内置兜底。
/// </summary>
public partial class AccountView : UserControl
{
    /// <summary>皮肤解析（网络）最多等这么久，超时走本地/内置兜底。</summary>
    private static readonly TimeSpan SkinResolveTimeout = TimeSpan.FromSeconds(4);

    private readonly ObservableCollection<AccountRow> _accounts = new();
    private readonly WebRegionHost _skinHost;
    private readonly DispatcherTimer _hostSyncTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _skinEditorTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private WebRegionHost? _skinEditorHost;
    private AccountRow? _skinEditorRow;
    private int _skinEditorTicks;

    private CancellationTokenSource? _skinCts;
    private CancellationTokenSource? _loginCts;
    private LoginBrowserWindow? _loginWindow;
    private string? _loginBrowserError;
    private SkinTexture? _skinTexture;
    private bool? _modelChoice;
    private bool _ready;
    private bool _syncingModel;
    private bool _syncingCape;
    private bool _themeSubscribed;
    private int _hostSyncTicks;

    public AccountView()
    {
        InitializeComponent();

        // 3D 皮肤预览走局部 WebView2（本地 skinview3d 页面）
        _skinHost = new WebRegionHost(SkinWebHost, "skin/index.html", "skin");
        _hostSyncTimer.Tick += OnHostSyncTick;
        _skinEditorTimer.Tick += OnSkinEditorTick;

        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;

        LoadAccounts();

        _ready = true;
        UpdateAccountInfo(SelectedRow ?? (_accounts.Count > 0 ? _accounts[0] : null));
        RefreshHeader();
        _ = ApplySkinPaletteAsync(SelectedRow);
    }

    private AccountRow? SelectedRow => AccountList.SelectedItem as AccountRow;

    private bool EffectiveSlim => _modelChoice ?? _skinTexture?.Slim ?? false;

    // ---------------- 生命周期 ----------------

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (!_themeSubscribed)
        {
            ThemeManager.ThemeChanged += OnThemeChanged;
            _themeSubscribed = true;
        }

        ApplyTheme();
        _hostSyncTicks = 0;
        _hostSyncTimer.Start();
        _ = _skinHost.AttachAsync();
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_themeSubscribed)
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
            _themeSubscribed = false;
        }

        _hostSyncTimer.Stop();
        _skinHost.Detach();
        _skinCts?.Cancel();
    }

    /// <summary>页面导航完成后一次性把主题 + 皮肤状态补推给 WebView（页面未就绪时脚本会排队）。</summary>
    private void OnHostSyncTick(object? sender, EventArgs e)
    {
        _hostSyncTicks++;

        if (_skinHost.IsReady)
        {
            _hostSyncTimer.Stop();
            SkinWebPlaceholder.IsVisible = false;
            ApplyTheme();
            PushCurrentState();

            return;
        }

        if (_hostSyncTicks >= 40)
        {
            _hostSyncTimer.Stop();
            SkinWebPlaceholder.Text = L.T("Account.Skin.Unavailable", "3D 皮肤预览不可用（缺少 WebView2 运行时）");
            SkinWebPlaceholder.IsVisible = true;
        }
    }

    // ---------------- 账号列表 ----------------

    /// <summary>账号列表来自真实存储：MofuCore 的 Microsoft / Authlib 凭据 + 本机离线账号。</summary>
    private void LoadAccounts()
    {
        _accounts.Clear();
        string selected = LocalSettings.Current.SelectedAccountName;
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        try
        {
            var handle = new MinecraftTokenHandle();
            foreach (MicrosoftAccount account in handle.LoadMicrosoftAccounts())
            {
                if (string.IsNullOrWhiteSpace(account.Username) || !names.Add(account.Username)) continue;
                _accounts.Add(new AccountRow(account.Username, true, false)
                {
                    Uuid = account.Uuid,
                    CustomSkinPath = SkinTextureService.ExistingCustomSkin(account.Username),
                    LocalCapePath = SkinTextureService.ExistingCape(account.Username),
                });
            }

            foreach (AuthlibCredential credential in handle.LoadAuthlibCredentials())
            {
                string name = string.IsNullOrWhiteSpace(credential.ProfileName)
                    ? credential.Username
                    : credential.ProfileName;
                if (string.IsNullOrWhiteSpace(name) || !names.Add(name)) continue;
                _accounts.Add(new AccountRow(name, false, false)
                {
                    CustomSkinPath = SkinTextureService.ExistingCustomSkin(name),
                    LocalCapePath = SkinTextureService.ExistingCape(name),
                });
            }

            foreach (string name in handle.LoadOfflineAccounts())
            {
                if (string.IsNullOrWhiteSpace(name) || !names.Add(name)) continue;
                _accounts.Add(new AccountRow(name, false, false)
                {
                    CustomSkinPath = SkinTextureService.ExistingCustomSkin(name),
                    LocalCapePath = SkinTextureService.ExistingCape(name),
                });
            }
        }
        catch (Exception error)
        {
            SkinLog("load stored accounts failed", error);
        }

        AccountRow? defaultRow = _accounts.FirstOrDefault(row =>
            string.Equals(row.Name, selected, StringComparison.OrdinalIgnoreCase));
        if (defaultRow is null && _accounts.Count > 0)
        {
            defaultRow = _accounts[0];
            LocalSettings.Save();
        }
        if (defaultRow is not null) defaultRow.IsDefault = true;

        AccountList.ItemsSource = _accounts;
        AccountList.SelectedIndex = defaultRow is null ? -1 : _accounts.IndexOf(defaultRow);

        foreach (var row in _accounts)
        {
            _ = LoadAccountHeadAsync(row);
        }
    }

    /// <summary>给账号列表行补真实皮肤头像（双层皮肤会叠帽子层）；失败保持首字母占位。</summary>
    private async Task LoadAccountHeadAsync(AccountRow row)
    {
        try
        {
            IImage? head = await SkinHeadService.LoadHeadAsync(row.Name);
            if (head is null)
            {
                var slim = SkinTextureService.FindChoice(row.OfflineSkinId).Slim;
                var texture = await SkinTextureService.ResolveAsync(row, slim, false, CancellationToken.None);
                var bytes = texture?.SkinDataUrl is { Length: > 0 } dataUrl ? TryDecodeDataUrl(dataUrl) : null;
                if (bytes is not null)
                {
                    head = SkinHeadService.ComposeHead(bytes);
                    if (head is null)
                    {
                        head = await Dispatcher.UIThread.InvokeAsync(
                            () => (IImage?)SkinHeadService.ComposeHead(bytes));
                    }
                }
            }

            if (head is not null) row.Head = head;
        }
        catch (Exception error)
        {
            SkinLog($"avatar head failed for {row.Name}", error);
        }
    }

    private void OnAccountSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || SelectedRow is not { } row) return;

        _modelChoice = null;
        UpdateAccountInfo(row);
        AnimateInfoPanel();
        _ = ApplySkinPaletteAsync(row);

        LocalSettings.Current.SelectedAccountName = row.Name;
        LocalSettings.Save();
        GameState.SetAccount(row.Name);
    }

    private void RefreshHeader()
    {
        var current = SelectedRow ?? (_accounts.Count > 0 ? _accounts[0] : null);

        Header.Subtitle = current is null
            ? L.T("Account.Subtitle.Empty", "还没有账号，点右上「添加账号」开始")
            : L.T("Account.Subtitle.Count", "{0} 个账号 · {1} 使用中", _accounts.Count, current.Name);
    }

    private void UpdateAccountInfo(AccountRow? row)
    {
        if (row is null) return;

        CurrentAccountName.Text = row.Name;
        CurrentAccountBadge.Text = row.IsDefault
            ? L.T("Account.Status.InUse", "使用中")
            : L.T("Account.Status.Selected", "已选中");
    }

    private void OnSetDefaultClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AccountRow row }) return;

        foreach (var account in _accounts) account.IsDefault = ReferenceEquals(account, row);

        LocalSettings.Current.SelectedAccountName = row.Name;
        LocalSettings.Save();
        GameState.SetAccount(row.Name);
        UpdateAccountInfo(row);
        Header.Subtitle = L.T("Account.Subtitle.Count", "{0} 个账号 · {1} 使用中", _accounts.Count, row.Name);
        Shell.Toast(L.T("Account.Toast.SetDefault", "已把「{0}」设为默认账号", row.Name));
    }

    private void OnDeleteAccountClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AccountRow row }) return;

        var index = _accounts.IndexOf(row);

        try
        {
            var handle = new MinecraftTokenHandle();
            foreach (MicrosoftAccount account in handle.LoadMicrosoftAccounts())
            {
                if (string.Equals(account.Username, row.Name, StringComparison.OrdinalIgnoreCase))
                {
                    handle.RemoveMicrosoftAccount(account.Uuid);
                }
            }

            handle.RemoveOfflineAccount(row.Name);
        }
        catch (Exception error)
        {
            SkinLog("remove stored account failed", error);
        }

        if (string.Equals(LocalSettings.Current.SelectedAccountName, row.Name, StringComparison.OrdinalIgnoreCase))
        {
            LocalSettings.Current.SelectedAccountName = string.Empty;
        }

        LocalSettings.Save();

        _accounts.Remove(row);
        if (_accounts.Count > 0)
        {
            AccountList.SelectedIndex = Math.Clamp(index, 0, _accounts.Count - 1);
            _accounts[Math.Clamp(index, 0, _accounts.Count - 1)].IsDefault = true;
            LocalSettings.Current.SelectedAccountName = SelectedRow?.Name ?? string.Empty;
        }
        else
        {
            AccountList.SelectedIndex = -1;
            CurrentAccountName.Text = "—";
            CurrentAccountBadge.Text = string.Empty;
        }

        LocalSettings.Save();
        Shell.Toast(L.T("Account.Toast.Deleted", "已删除账号「{0}」", row.Name));
        RefreshHeader();
    }

    // ---------------- 添加账号（正版内置浏览器登录 / 离线账号） ----------------

    private void OnAddAccountClick(object? sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox
        {
            PlaceholderText = L.T("Account.Add.NicknameHint", "例如：Mofu_Steve"),
            Width = 300,
        };

        var premiumButton = new Button
        {
            Content = L.T("Account.Add.PremiumLogin", "Microsoft 正版登录"),
            Classes = { "primary" },
            Height = 36,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };

        var offlineButton = new Button
        {
            Content = L.T("Account.Add.OfflineConfirm", "添加离线账号"),
            Classes = { "ghost" },
            Height = 32,
            Padding = new Thickness(14, 0, 14, 0),
        };

        var cancelButton = new Button
        {
            Content = L.T("Common.Cancel", "取消"),
            Classes = { "ghost" },
            Height = 32,
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(0, 0, 8, 0),
        };

        var form = new StackPanel
        {
            Spacing = 12,
            Width = 340,
            Children =
            {
                new TextBlock
                {
                    Text = L.T("Account.Add.Title", "添加账号"),
                    FontSize = 15,
                    FontWeight = FontWeight.SemiBold,
                },
                new TextBlock
                {
                    Text = L.T(
                        "Account.Add.Hint",
                        "正版登录会打开启动器内置浏览器，登录完成后回到这里显示处理过程；离线账号直接输入名称即可。"),
                    FontSize = 11,
                    Foreground = ThemeTokens.Brush("NekoDimBrush"),
                    TextWrapping = TextWrapping.Wrap,
                },
                premiumButton,
                new Border
                {
                    Height = 1,
                    Background = ThemeTokens.Brush("NekoBorderBrush"),
                },
                new TextBlock
                {
                    Text = L.T("Account.Add.OfflineSection", "或添加离线账号"),
                    FontSize = 11,
                    Foreground = ThemeTokens.Brush("NekoDimBrush"),
                },
                nameBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancelButton, offlineButton },
                },
            },
        };

        premiumButton.Click += (_, _) =>
        {
            CloseActiveDialog(premiumButton);
            StartPremiumLogin();
        };

        offlineButton.Click += (_, _) =>
        {
            var name = (nameBox.Text ?? string.Empty).Trim();

            if (name.Length == 0)
            {
                Shell.Toast(L.T("Account.Add.EmptyName", "请输入账号名"));

                return;
            }

            try
            {
                new MinecraftTokenHandle().SaveOfflineAccount(name);
            }
            catch (Exception error)
            {
                SkinLog("save offline account failed", error);
                Shell.Toast(L.T("Account.Toast.AddOfflineFailed", "保存离线账号失败：{0}", error.Message));
                return;
            }

            LocalSettings.Current.SelectedAccountName = name;
            LocalSettings.Save();
            GameState.SetAccount(name);

            var row = new AccountRow(name, false, _accounts.Count == 0);
            _accounts.Add(row);
            AccountList.SelectedIndex = _accounts.Count - 1;
            Shell.Toast(L.T("Account.Toast.AddedOffline", "已添加离线账号「{0}」", name));
            RefreshHeader();
            CloseActiveDialog(offlineButton);
        };

        cancelButton.Click += (_, _) => CloseActiveDialog(cancelButton);

        Shell.Dialog(form);
    }

    /// <summary>弹窗内容里的按钮：取它所在的 SukiDialogHost 关掉当前弹窗。</summary>
    private static void CloseActiveDialog(object? source)
    {
        if (source is Visual visual)
        {
            var host = visual.FindAncestorOfType<SukiDialogHost>(true);

            host?.Manager?.DismissDialog();
        }
    }

    // ---------------- 正版登录（内置浏览器 + 主窗口内分步处理过程） ----------------

    private async void StartPremiumLogin()
    {
        if (_loginCts is not null)
        {
            Shell.Toast(L.T("Account.Login.Busy", "已有登录流程正在进行"));

            return;
        }

        var cts = new CancellationTokenSource();
        _loginCts = cts;

        var panel = new LoginProgressPanel();
        panel.CancelRequested += () => cts.Cancel();
        panel.CloseRequested += () => CloseActiveDialog(panel);
        panel.RetryRequested += () =>
        {
            CloseActiveDialog(panel);
            StartPremiumLogin();
        };

        Shell.Dialog(panel, L.T("Account.Login.DialogTitle", "Microsoft 登录"));

        var progress = new Progress<LoginStep>(step => panel.Advance(step));

        try
        {
            var account = await MinecraftTokenHandle.StartBrowserLoginAsync(
                url =>
                {
                    Dispatcher.UIThread.Post(() => _ = ShowLoginBrowserAsync(url));

                    return Task.CompletedTask;
                },
                progress,
                cts.Token);

            var row = await AddLoggedInAccountAsync(account);
            var head = await SkinHeadService.LoadHeadAsync(row.Name);
            panel.ShowSuccess(row.Name, head);
            Shell.Toast(L.T("Account.Toast.LoginSucceeded", "已添加正版账号「{0}」", row.Name));
        }
        catch (OperationCanceledException)
        {
            if (_loginBrowserError is { Length: > 0 } browserError)
            {
                panel.ShowFailure(browserError);
                Shell.Toast(L.T("Account.Toast.LoginFailed", "登录失败：{0}", browserError));
            }
            else
            {
                panel.ShowCancelled();
                Shell.Toast(L.T("Account.Toast.LoginCancelled", "已取消 Microsoft 登录"));
            }
        }
        catch (MicrosoftAuthenticationException error)
        {
            SkinLog("premium login failed", error);
            panel.ShowFailure(error.Message);
            Shell.Toast(L.T("Account.Toast.LoginFailed", "登录失败：{0}", error.Message));
        }
        catch (Exception error)
        {
            SkinLog("premium login crashed", error);
            panel.ShowFailure(error.Message);
            Shell.Toast(L.T("Account.Toast.LoginFailed", "登录失败：{0}", error.Message));
        }
        finally
        {
            CloseLoginBrowserWindow();
            _loginBrowserError = null;
            _loginCts = null;
            cts.Dispose();
        }
    }

    /// <summary>打开（或复用）内置浏览器窗口并导航到授权页。</summary>
    private async Task ShowLoginBrowserAsync(string url)
    {
        var window = _loginWindow;
        if (window is null)
        {
            window = new LoginBrowserWindow();
            _loginWindow = window;
            window.Closed += (_, _) =>
            {
                if (!ReferenceEquals(_loginWindow, window)) return;

                _loginWindow = null;

                // 用户手动关掉内置浏览器 = 取消这次登录
                if (!window.CloseByHost)
                {
                    _loginCts?.Cancel();
                }
            };

            if (TopLevel.GetTopLevel(this) is Window owner)
            {
                window.Show(owner);
            }
            else
            {
                window.Show();
            }
        }

        try
        {
            await window.NavigateAsync(url);
        }
        catch (Exception error)
        {
            SkinLog("login browser failed", error);
            _loginBrowserError = error.Message;
            _loginCts?.Cancel();
        }
    }

    private void CloseLoginBrowserWindow()
    {
        var window = _loginWindow;
        _loginWindow = null;
        if (window is null) return;

        try
        {
            window.CloseByHost = true;
            window.Close();
        }
        catch (Exception error)
        {
            SkinLog("close login browser failed", error);
        }
    }

    /// <summary>把登录成功的账号加进列表并选中（同名离线账号会被升级成正版）。</summary>
    private async Task<AccountRow> AddLoggedInAccountAsync(MicrosoftAccount account)
    {
        var name = string.IsNullOrWhiteSpace(account.Username) ? "Minecraft" : account.Username.Trim();

        AccountRow? row = null;
        foreach (var candidate in _accounts)
        {
            if (!string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)) continue;

            if (candidate.IsMicrosoft)
            {
                row = candidate;
            }
            else
            {
                // 离线账号升级成正版：原位替换，保留默认标记
                var index = _accounts.IndexOf(candidate);
                _accounts.RemoveAt(index);
                row = new AccountRow(name, true, candidate.IsDefault) { Uuid = account.Uuid };
                _accounts.Insert(index, row);
            }

            break;
        }

        if (row is null)
        {
            var makeDefault = _accounts.All(candidate => !candidate.IsDefault);
            row = new AccountRow(name, true, makeDefault) { Uuid = account.Uuid };
            _accounts.Insert(0, row);
        }

        LocalSettings.Current.SelectedAccountName = row.Name;
        try
        {
            new MinecraftTokenHandle().RemoveOfflineAccount(name);
        }
        catch (Exception error)
        {
            SkinLog("upgrade offline account failed", error);
        }

        LocalSettings.Save();
        GameState.SetAccount(row.Name);

        AccountList.SelectedIndex = _accounts.IndexOf(row);
        UpdateAccountInfo(row);
        RefreshHeader();

        await ApplySkinPaletteAsync(row);
        SkinHeadService.Invalidate(name);

        return row;
    }

    // ---------------- 皮肤预览（获取 + 推送） ----------------

    private async Task ApplySkinPaletteAsync(AccountRow? row, bool forceRefresh = false)
    {
        if (row is null) return;

        // 切换账号时先只更新名字/模型/披风，保留上一张皮肤，避免 3D 区域出现空窗
        _skinTexture = null;
        UpdateCapeSwitch();
        PushSkinInfo();

        _skinCts?.Cancel();
        var cts = new CancellationTokenSource();
        _skinCts = cts;

        SkinTexture? texture = null;
        var timedOut = false;
        try
        {
            // 网络/令牌异常时最多等 4s：超时也走回退（本地缓存 → 客户端内置 → 页面内置 Steve/Alex）
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            timeout.CancelAfter(SkinResolveTimeout);

            texture = await SkinTextureService.ResolveAsync(row, EffectiveSlim, forceRefresh, timeout.Token);
        }
        catch (OperationCanceledException) when (!cts.IsCancellationRequested)
        {
            timedOut = true;
            SkinLog($"resolve timeout for {row.Name}");
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception error)
        {
            SkinLog($"resolve failed for {row.Name}", error);
        }

        if (cts.IsCancellationRequested || !ReferenceEquals(SelectedRow, row)) return;

        _skinTexture = texture;

        if (_modelChoice is null)
        {
            _syncingModel = true;
            SkinModelCombo.SelectedIndex = texture?.Slim == true ? 1 : 0;
            _syncingModel = false;
        }

        UpdateCapeSwitch();
        PushCurrentState();

        await UpdateFallbackPreviewAsync(row, texture);
        SkinLog($"resolved {row.Name}: {(texture is null ? "builtin" : texture.Source)} slim={texture?.Slim}");
    }

    /// <summary>WebView2 画出首帧前的兜底：显示账号皮肤大头。</summary>
    private async Task UpdateFallbackPreviewAsync(AccountRow row, SkinTexture? texture)
    {
        IImage? head = null;
        try
        {
            head = await SkinHeadService.LoadHeadAsync(row.Name);
        }
        catch (Exception error)
        {
            SkinLog("fallback head failed", error);
        }

        if (head is null && texture?.SkinDataUrl is { Length: > 0 } dataUrl)
        {
            var bytes = TryDecodeDataUrl(dataUrl);
            if (bytes is not null) head = SkinHeadService.ComposeHead(bytes) ?? CreateHeadBitmap(bytes);
        }

        if (head is not null) row.Head = head;

        if (!ReferenceEquals(SelectedRow, row)) return;

        SkinFallbackImage.Source = head;
        SkinFallbackImage.IsVisible = head is not null;
    }

    private static byte[]? TryDecodeDataUrl(string dataUrl)
    {
        try
        {
            var comma = dataUrl.IndexOf(',');
            if (comma < 0 || comma + 1 >= dataUrl.Length) return null;

            return Convert.FromBase64String(dataUrl[(comma + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>只推账号名/模型/披风开关（不动贴图）：切换账号期间保持上一张皮肤可见。</summary>
    private void PushSkinInfo()
    {
        var row = SelectedRow;
        if (row is null) return;

        _skinHost.ApplySkin(row.Name, row.Variant, EffectiveSlim, false);
    }

    /// <summary>把当前账号 / 模型 / 披风 / 贴图一次性推给 WebView 页面。</summary>
    private void PushCurrentState()
    {
        var row = SelectedRow;
        if (row is null) return;

        var slim = EffectiveSlim;
        var capeVisible = CapeSwitch.IsChecked == true && _skinTexture?.HasCape == true;

        _skinHost.ApplySkin(row.Name, row.Variant, slim, capeVisible);
        _skinHost.Post("texture", new
        {
            name = row.Name,
            skin = _skinTexture?.SkinDataUrl,
            cape = _skinTexture?.CapeDataUrl,
            slim,
            capeVisible,
            source = _skinTexture?.Source ?? "pending",
        });
    }

    private void UpdateCapeSwitch()
    {
        var hasCape = _skinTexture?.HasCape == true;
        var hasLocalCape = SelectedRow?.LocalCapePath is { Length: > 0 };

        _syncingCape = true;
        CapeSwitch.IsEnabled = hasCape;
        if (!hasCape) CapeSwitch.IsChecked = false;
        _syncingCape = false;

        ToolTip.SetTip(CapeSwitch, hasCape
            ? (hasLocalCape
                ? L.T("Account.Cape.TipLocal", "当前显示的是本机自定义披风（仅启动器预览）")
                : L.T("Account.Cape.Tip", "切换是否显示该账号的披风"))
            : L.T("Account.Cape.TipNone", "该账号没有披风贴图"));
        ResetCapeButton.IsEnabled = hasLocalCape;
    }

    /// <summary>修改披风：选本地 PNG 存进皮肤库，3D 预览立即换上（仅启动器预览，不影响游戏内官方披风）。</summary>
    private async void OnEditCapeClick(object? sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (row is null) return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            Shell.Toast(L.T("Account.Toast.NoFilePicker", "当前窗口不支持文件选择"));
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("Account.Cape.PickerTitle", "选择披风纹理（2:1 PNG，例如 64×32）"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(L.T("Account.Cape.FileType", "披风纹理"))
                {
                    Patterns = new[] { "*.png" },
                    MimeTypes = new[] { "image/png" },
                },
            },
        });

        string? path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrWhiteSpace(path)) return;

        if (!SkinTextureService.TryValidateCapeFile(path, out string error))
        {
            Shell.Toast(error);
            return;
        }

        try
        {
            SkinTextureService.SaveCape(path, row.Name);
            row.LocalCapePath = SkinTextureService.ExistingCape(row.Name);
            CapeSwitch.IsChecked = true;
            await ApplySkinPaletteAsync(row);
            Shell.Toast(L.T("Account.Toast.CapeUpdated", "已更新披风（本机预览；游戏内披风以官方档案为准）"));
        }
        catch (Exception exception)
        {
            Shell.Toast(L.T("Account.Toast.CapeFailed", "披风更新失败：{0}", exception.Message));
        }
    }

    /// <summary>还原披风：删除本机自定义披风，回到官方档案披风（没有官方披风的账号会回到无披风）。</summary>
    private async void OnResetCapeClick(object? sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (row is null || row.LocalCapePath is null)
        {
            Shell.Toast(L.T("Account.Toast.CapeNothing", "这个账号没有设置本机披风"));
            return;
        }

        SkinTextureService.DeleteCape(row.Name);
        row.LocalCapePath = null;
        await ApplySkinPaletteAsync(row);
        Shell.Toast(L.T("Account.Toast.CapeReset", "已还原官方披风"));
    }

    private void AnimateInfoPanel()
    {
        if (!UiAnimations.Enabled) return;

        // 3D 区域是 WebView2 子窗口，无法参与 Avalonia 动画，淡入放在信息卡与来源状态上
        UiAnimations.FadeSlideIn(SkinInfoPanel, 0.99, 6, 170);
    }

    private void OnCapeToggled(object? sender, RoutedEventArgs e)
    {
        if (_syncingCape) return;

        PushCurrentState();
        if (_ready)
        {
            Shell.Toast(CapeSwitch.IsChecked == true
                ? L.T("Account.Toast.CapeShown", "已显示披风")
                : L.T("Account.Toast.CapeHidden", "已隐藏披风"));
        }
    }

    private void OnSkinModelChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _syncingModel) return;

        _modelChoice = SkinModelCombo.SelectedIndex == 1;
        PushCurrentState();
        Shell.Toast(_modelChoice == true
            ? L.T("Account.Toast.ModelSlim", "已切换为纤细皮肤模型")
            : L.T("Account.Toast.ModelClassic", "已切换为经典皮肤模型"));
    }

    // ---------------- 主题 ----------------

    private void OnThemeChanged() => ApplyTheme();

    private void ApplyTheme()
    {
        var surface = ThemeTokens.Color("NekoFieldSoftBrush");
        _skinHost.SetBackground(System.Drawing.Color.FromArgb(surface.A, surface.R, surface.G, surface.B));
        PushTheme();
    }

    private void PushTheme() => PushThemeTo(_skinHost);

    private static void PushThemeTo(WebRegionHost host)
    {
        var light = ThemeManager.IsLight;
        var panel = ThemeTokens.Color(light ? "NekoCardSoftBrush" : "NekoDialogBrush");

        host.Post("theme", new
        {
            light,
            lang = L.CurrentLanguage,
            background = ToHex(ThemeTokens.Color("NekoFieldSoftBrush")),
            text = ToHex(ThemeTokens.Color("NekoTextBrush")),
            muted = ToHex(ThemeTokens.Color("NekoMutedBrush")),
            border = ToHex(ThemeTokens.Color("NekoBorderBrush")),
            panel = $"rgba({panel.R}, {panel.G}, {panel.B}, {panel.A / 255d:0.###})",
            accent = ToHex(ThemeTokens.Color("NekoAccentBrush")),
        });
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    // ---------------- 皮肤操作按钮 ----------------

    private async void OnRefreshSkinClick(object? sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (row is null) return;

        Shell.Toast(L.T("Account.Toast.Refreshing", "正在刷新皮肤…"));
        await ApplySkinPaletteAsync(row, forceRefresh: true);
    }

    private async void OnUploadSkinClick(object? sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (row is null) return;

        if (!row.IsMicrosoft)
        {
            Shell.Toast(L.T("Account.Toast.UploadOffline",
                "离线账号不能上传到 Mojang，请用「编辑皮肤」保存到本机"));
            return;
        }

        string? path = await PickSkinFileAsync();
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!SkinTextureService.TryValidateSkinFile(path, out string error))
        {
            Shell.Toast(error);
            return;
        }

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path);
            var handle = new MinecraftTokenHandle();
            MicrosoftAccount? account = handle.LoadMicrosoftAccounts()
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Username, row.Name, StringComparison.OrdinalIgnoreCase));
            if (account is null)
            {
                Shell.Toast(L.T("Account.Toast.UploadNoToken",
                    "找不到该账号的登录令牌，请重新登录 Microsoft 账号"));
                return;
            }

            await handle.UploadSkinAsync(account, bytes, EffectiveSlim);
            row.CustomSkinPath = null;
            SkinHeadService.Invalidate(row.Name);
            await ApplySkinPaletteAsync(row, forceRefresh: true);
            Shell.Toast(L.T("Account.Toast.UploadDone", "已上传到 Mojang，稍等片刻即可生效"));
        }
        catch (Exception exception)
        {
            SkinLog("upload skin failed", exception);
            Shell.Toast(L.T("Account.Toast.UploadFailed", "上传皮肤失败：{0}", exception.Message));
        }
    }

    /// <summary>3D 皮肤编辑器：涂色结果写回本机自定义皮肤（正版账号同时上传）。</summary>
    private async Task UploadSkinBytesAsync(AccountRow row, byte[] bytes, bool slim)
    {
        try
        {
            var handle = new MinecraftTokenHandle();
            MicrosoftAccount? account = handle.LoadMicrosoftAccounts()
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Username, row.Name, StringComparison.OrdinalIgnoreCase));
            if (account is null)
            {
                Shell.Toast(L.T("Account.Toast.UploadNoToken",
                    "找不到该账号的登录令牌，请重新登录 Microsoft 账号"));
                return;
            }

            await handle.UploadSkinAsync(account, bytes, slim);
            row.CustomSkinPath = null;
            SkinHeadService.Invalidate(row.Name);
            await ApplySkinPaletteAsync(row, forceRefresh: true);
            Shell.Toast(L.T("Account.Toast.UploadDone", "已上传到 Mojang，稍等片刻即可生效"));
        }
        catch (Exception exception)
        {
            SkinLog("upload skin failed", exception);
            Shell.Toast(L.T("Account.Toast.UploadFailed", "上传皮肤失败：{0}", exception.Message));
        }
    }

    private async void OnEditSkinClick(object? sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (row is null) return;

        _skinEditorRow = row;
        SkinEditorOverlay.IsVisible = true;

        if (_skinEditorHost is null)
        {
            _skinEditorHost = new WebRegionHost(SkinEditorHost, "skin/index.html?edit=1", "skin-editor-embedded");
            _skinEditorHost.MessageReceived += OnSkinEditorMessage;
        }

        _skinEditorTicks = 0;
        _skinEditorTimer.Start();
        await _skinEditorHost.AttachAsync();
    }

    private void OnSkinEditorTick(object? sender, EventArgs e)
    {
        _skinEditorTicks++;
        if (_skinEditorHost is { IsReady: true })
        {
            _skinEditorTimer.Stop();
            PushSkinEditorState();
            return;
        }

        if (_skinEditorTicks >= 40)
        {
            _skinEditorTimer.Stop();
            Shell.Toast(L.T("Account.Skin.Unavailable", "3D 皮肤预览不可用（缺少 WebView2 运行时）"));
        }
    }

    private void PushSkinEditorState()
    {
        if (_skinEditorHost is null || _skinEditorRow is null) return;

        Color surface = ThemeTokens.Color("NekoFieldSoftBrush");
        _skinEditorHost.SetBackground(System.Drawing.Color.FromArgb(surface.A, surface.R, surface.G, surface.B));
        PushThemeTo(_skinEditorHost);

        bool capeVisible = CapeSwitch.IsChecked == true && _skinTexture?.CapeDataUrl is not null;
        _skinEditorHost.Post("texture", new
        {
            name = _skinEditorRow.Name,
            skin = _skinTexture?.SkinDataUrl,
            cape = _skinTexture?.CapeDataUrl,
            slim = EffectiveSlim,
            capeVisible,
            source = "custom",
        });
        _skinEditorHost.Post("editor", new
        {
            open = true,
            slim = EffectiveSlim,
            capeVisible,
            lang = L.CurrentLanguage,
        });
    }

    private void OnCloseSkinEditorClick(object? sender, RoutedEventArgs e) => CloseSkinEditor();

    private void CloseSkinEditor()
    {
        _skinEditorTimer.Stop();
        SkinEditorOverlay.IsVisible = false;
        _skinEditorHost?.Detach();
        _skinEditorRow = null;
    }

    private async void OnSkinEditorMessage(string message)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(message);
            JsonElement root = document.RootElement;
            string type = root.TryGetProperty("type", out JsonElement typeElement)
                ? typeElement.GetString() ?? string.Empty
                : string.Empty;

            string dataUrl = root.TryGetProperty("dataUrl", out JsonElement value)
                ? value.GetString() ?? string.Empty
                : string.Empty;
            bool slim = root.TryGetProperty("slim", out JsonElement slimElement) &&
                        slimElement.ValueKind == JsonValueKind.True;

            switch (type)
            {
                case "skin/apply":
                {
                    byte[]? bytes = TryDecodeDataUrl(dataUrl);
                    if (bytes is null) return;
                    await ApplyEditedSkinAsync(bytes, slim);
                    CloseSkinEditor();
                    return;
                }
                case "skin/export":
                {
                    byte[]? bytes = TryDecodeDataUrl(dataUrl);
                    if (bytes is not null) await ExportSkinAsync(bytes);
                    return;
                }
                case "skin/upload":
                {
                    AccountRow? row = _skinEditorRow;
                    if (row is null) return;
                    if (!row.IsMicrosoft)
                    {
                        Shell.Toast(L.T("Account.Toast.UploadOffline",
                            "离线账号不能上传到 Mojang，请用「编辑皮肤」保存到本机"));
                        return;
                    }

                    byte[]? bytes = TryDecodeDataUrl(dataUrl);
                    if (bytes is not null) await UploadSkinBytesAsync(row, bytes, slim);
                    return;
                }
                case "skin/close":
                    CloseSkinEditor();
                    return;
            }
        }
        catch
        {
            // 非 JSON 消息忽略
        }
    }

    private async Task ApplyEditedSkinAsync(byte[] bytes, bool slim)
    {
        if (_skinEditorRow is not { } row) return;

        string temporary = Path.Combine(Path.GetTempPath(), $"mofu-skin-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(temporary, bytes);
        try
        {
            if (!SkinTextureService.TryValidateSkinFile(temporary, out string error))
            {
                Shell.Toast(error);
                return;
            }

            row.CustomSkinPath = SkinTextureService.SaveCustomSkin(temporary, row.Name);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // 忽略
            }
        }

        _modelChoice = slim;
        _syncingModel = true;
        SkinModelCombo.SelectedIndex = slim ? 1 : 0;
        _syncingModel = false;

        SkinHeadService.Invalidate(row.Name);
        await ApplySkinPaletteAsync(row);
        Shell.Toast(row.IsMicrosoft
            ? L.T("Account.Toast.SkinEditedPremium", "皮肤已保存到本机；点「上传」可同步到 Mojang")
            : L.T("Account.Toast.SkinEdited", "皮肤已保存"));
    }

    private async Task ExportSkinAsync(byte[] bytes)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.T("Account.Skin.Editor.Export", "导出皮肤"),
            SuggestedFileName = (_skinEditorRow?.Name ?? "skin") + ".png",
            DefaultExtension = "png",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PNG") { Patterns = new[] { "*.png" } },
            },
        });
        if (file is null) return;

        await using Stream stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        await stream.WriteAsync(bytes);
        Shell.Toast(L.T("Account.Skin.Editor.Exported", "已导出皮肤"));
    }



    private async Task<string?> PickSkinFileAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            Shell.Toast(L.T("Account.Toast.NoFilePicker", "当前窗口不支持文件选择"));
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("Account.Local.PickerTitle", "选择 Minecraft 皮肤（64×64 PNG）"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(L.T("Account.Local.FileType", "Minecraft 皮肤"))
                {
                    Patterns = new[] { "*.png" },
                    MimeTypes = new[] { "image/png" },
                },
            },
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async void OnLocalSkinClick(object? sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (row is null) return;

        try
        {
            string? path = await PickSkinFileAsync();
            if (string.IsNullOrWhiteSpace(path)) return;

            if (!SkinTextureService.TryValidateSkinFile(path, out var error))
            {
                Shell.Toast(error);

                return;
            }

            row.CustomSkinPath = SkinTextureService.SaveCustomSkin(path, row.Name);
            await ApplySkinPaletteAsync(row);
            Shell.Toast(row.IsMicrosoft
                ? L.T("Account.Toast.LocalSkinPreview", "已应用本地皮肤（仅本机预览）")
                : L.T("Account.Toast.LocalSkin", "已应用本地皮肤"));
        }
        catch (Exception error)
        {
            SkinLog("pick local skin failed", error);
            Shell.Toast(L.T("Account.Toast.LocalSkinFailed", "选择皮肤失败，请重试"));
        }
    }

    private void OnSkinLibraryClick(object? sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (row is null) return;

        if (row.IsMicrosoft)
        {
            Shell.Toast(L.T("Account.Toast.PremiumSkin", "正版账号的皮肤由 Mojang 档案决定，请在 Minecraft 内更换"));

            return;
        }

        var panel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            MaxWidth = 472,
            ItemWidth = 108,
            ItemHeight = 104,
        };

        var content = new StackPanel
        {
            Spacing = 12,
            Width = 480,
            Children =
            {
                new TextBlock
                {
                    Text = L.T("Account.Library.Title", "离线皮肤库"),
                    FontSize = 15,
                    FontWeight = FontWeight.SemiBold,
                },
                new TextBlock
                {
                    Text = L.T("Account.Library.Hint", "选择一件内置皮肤用于本地预览（从客户端资源中读取）。"),
                    FontSize = 11,
                    Foreground = ThemeTokens.Brush("NekoDimBrush"),
                    TextWrapping = TextWrapping.Wrap,
                },
                panel,
            },
        };

        foreach (var choice in SkinTextureService.Catalog)
        {
            var fallback = new TextBlock
            {
                Text = choice.Initial,
                FontSize = 18,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeTokens.Brush("NekoMutedBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var image = new Image
            {
                Stretch = Stretch.Uniform,
                IsVisible = false,
            };
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.None);

            var tile = new Button
            {
                Classes = { "tile" },
                Margin = new Thickness(0, 0, 6, 6),
                Width = 102,
                Height = 98,
                Content = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new Border
                        {
                            Width = 48,
                            Height = 48,
                            CornerRadius = new CornerRadius(8),
                            Background = ThemeTokens.Brush("NekoFieldSoftBrush"),
                            ClipToBounds = true,
                            Child = new Panel { Children = { fallback, image } },
                        },
                        new TextBlock
                        {
                            Text = choice.DisplayName,
                            FontSize = 11,
                            HorizontalAlignment = HorizontalAlignment.Center,
                        },
                    },
                },
            };

            tile.Click += async (_, _) =>
            {
                row.OfflineSkinId = choice.Id;
                row.CustomSkinPath = null;
                CloseActiveDialog(tile);
                await ApplySkinPaletteAsync(row);
                Shell.Toast(L.T("Account.Toast.LibraryChanged", "已切换到离线皮肤「{0}」", choice.DisplayName));
            };

            panel.Children.Add(tile);
            _ = LoadCatalogThumbnailAsync(choice, image, fallback);
        }

        Shell.Dialog(content, L.T("Account.Library.DialogTitle", "皮肤库"));
    }

    private static async Task LoadCatalogThumbnailAsync(
        OfflineSkinChoice choice, Image image, TextBlock fallback)
    {
        try
        {
            var bytes = await SkinTextureService.ResolveCatalogBytesAsync(choice, CancellationToken.None);
            if (bytes is null) return;

            var head = CreateHeadBitmap(bytes);
            if (head is null) return;

            image.Source = head;
            image.IsVisible = true;
            fallback.IsVisible = false;
        }
        catch (Exception error)
        {
            SkinLog($"thumbnail failed for {choice.Id}", error);
        }
    }

    internal static IImage? CreateHeadBitmap(byte[] pngBytes)
    {
        try
        {
            using var stream = new MemoryStream(pngBytes);
            var bitmap = new Bitmap(stream);
            var size = bitmap.PixelSize;
            if (size.Width < 16 || size.Height < 16) return bitmap;

            return new CroppedBitmap(bitmap, new PixelRect(8, 8, 8, 8));
        }
        catch
        {
            return null;
        }
    }

    private static void SkinLog(string message, Exception? error = null)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "mofu-account-skin.log"),
                $"{DateTime.Now:O} {message}{(error is null ? string.Empty : ": " + error)}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响功能
        }
    }
}

/// <summary>账号列表行（可观察：默认标记变化时更新徽标与按钮可见性）。</summary>
public sealed class AccountRow : INotifyPropertyChanged
{
    private static readonly IBrush MicrosoftAvatar = new SolidColorBrush(Color.Parse("#2F7F86"));
    private static readonly IBrush OfflineAvatar = new SolidColorBrush(Color.Parse("#3E5C7A"));
    private static readonly IBrush MicrosoftBadgeBg = new SolidColorBrush(Color.Parse("#1E3A3C"));
    private static readonly IBrush MicrosoftBadgeFg = new SolidColorBrush(Color.Parse("#3FC6C1"));
    private static readonly IBrush OfflineBadgeBg = new SolidColorBrush(Color.Parse("#1C2530"));
    private static readonly IBrush OfflineBadgeFg = new SolidColorBrush(Color.Parse("#93A0B4"));
    private static readonly IBrush AvatarInitial = new SolidColorBrush(Color.Parse("#06121A"));

    private bool _isDefault;

    public AccountRow(string name, bool isMicrosoft, bool isDefault)
    {
        Name = name;
        IsMicrosoft = isMicrosoft;
        _isDefault = isDefault;
    }

    public string Name { get; }

    public bool IsMicrosoft { get; }

    /// <summary>正版账号的 Minecraft UUID（离线账号为空）。</summary>
    public string Uuid { get; set; } = string.Empty;

    /// <summary>离线皮肤目录 Id：内置贴图取自客户端 jar（steve / alex / noor …）。</summary>
    public string OfflineSkinId { get; set; } = "steve";

    /// <summary>用户自选的本地皮肤 PNG 路径（仅本机预览）。</summary>
    public string? CustomSkinPath { get; set; }

    /// <summary>用户自选的本地披风 PNG 路径（仅启动器预览；空 = 用官方披风）。</summary>
    public string? LocalCapePath { get; set; }

    public string Variant => IsMicrosoft
        ? "microsoft"
        : string.Equals(Name, "Mofu", StringComparison.OrdinalIgnoreCase) ? "mofu" : "offline";

    public string TypeLabel => IsMicrosoft
        ? L.T("Account.Type.Premium", "正版账户")
        : L.T("Account.Type.Offline", "离线账户");

    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    private IImage? _head;

    /// <summary>合成后的皮肤头像（基础头部 + 帽子层）；为空时回退到首字母色块。</summary>
    public IImage? Head
    {
        get => _head;
        set
        {
            if (ReferenceEquals(_head, value)) return;

            _head = value;
            Raise();
            Raise(nameof(HasHead));
            Raise(nameof(HasFallback));
        }
    }

    public bool HasHead => _head is not null;

    public bool HasFallback => _head is null;

    public IBrush AvatarBrush => IsMicrosoft ? MicrosoftAvatar : OfflineAvatar;

    public IBrush InitialBrush => AvatarInitial;

    public IBrush TypeBadgeBg => IsMicrosoft ? MicrosoftBadgeBg : OfflineBadgeBg;

    public IBrush TypeBadgeFg => IsMicrosoft ? MicrosoftBadgeFg : OfflineBadgeFg;

    public bool IsDefault
    {
        get => _isDefault;
        set
        {
            if (_isDefault == value) return;
            _isDefault = value;
            Raise();
            Raise(nameof(IsNotDefault));
        }
    }

    public bool IsNotDefault => !_isDefault;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

/// <summary>解析后的完整皮肤贴图（data URI 可直接喂给 WebGL 渲染）。</summary>
internal sealed record SkinTexture(
    string Name,
    string? SkinDataUrl,
    string? CapeDataUrl,
    bool Slim,
    string Source)
{
    public bool HasCape => !string.IsNullOrEmpty(CapeDataUrl);
}

/// <summary>离线皮肤目录项（对应旧版 OfflineSkinCatalog.Choices）。</summary>
internal sealed record OfflineSkinChoice(string Id, string DisplayName, bool Slim)
{
    public string Initial => DisplayName.Length > 0 ? DisplayName[..1] : "?";
}

/// <summary>
/// 皮肤获取与磁盘缓存：用户名 → Mojang 档案 → 贴图 URL → data URI。
/// 失败静默回退到本地缓存 / 客户端 jar 内置 Steve、Alex，页面再兜底内置贴图。
/// </summary>
internal static class SkinTextureService
{
    private const string HttpUserAgent = "Mofumofu.Launcher/1.0 (skin-preview)";
    private const long MaxTextureBytes = 4 * 1024 * 1024;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private static readonly string RootDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mofumofu.Launcher", "skins");

    private static readonly string CustomDirectory = Path.Combine(RootDirectory, "custom");
    private static readonly string DefaultDirectory = Path.Combine(RootDirectory, "default");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private static readonly HttpClient Http = CreateHttpClient();

    /// <summary>离线皮肤目录（顺序即展示顺序，与旧版一致）。</summary>
    public static IReadOnlyList<OfflineSkinChoice> Catalog { get; } = new[]
    {
        new OfflineSkinChoice("steve", "Steve", false),
        new OfflineSkinChoice("alex", "Alex", true),
        new OfflineSkinChoice("noor", "Noor", true),
        new OfflineSkinChoice("sunny", "Sunny", false),
        new OfflineSkinChoice("ari", "Ari", true),
        new OfflineSkinChoice("zuri", "Zuri", true),
        new OfflineSkinChoice("makena", "Makena", false),
        new OfflineSkinChoice("kai", "Kai", true),
        new OfflineSkinChoice("efe", "Efe", true),
    };

    public static OfflineSkinChoice FindChoice(string? id) =>
        Catalog.FirstOrDefault(choice => string.Equals(choice.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? Catalog[0];

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", HttpUserAgent);

        return client;
    }

    // ---------------- 对外解析 ----------------

    public static async Task<SkinTexture?> ResolveAsync(
        AccountRow row, bool slimHint, bool forceRefresh, CancellationToken cancellationToken)
    {
        // 1) 用户自选的本地皮肤优先（仅本机预览）
        if (row.CustomSkinPath is { Length: > 0 } customPath && File.Exists(customPath))
        {
            var customData = TryReadDataUrl(customPath);
            if (customData is not null)
            {
                return WithLocalCape(new SkinTexture(row.Name, customData, null, slimHint, "custom"), row);
            }
        }

        // 2) 正版账号：本地缓存（24h）→ Mojang 档案 → 过期缓存兜底
        if (row.IsMicrosoft)
        {
            var cached = ReadProfileCache(row.Name);
            if (!forceRefresh && cached is not null && IsFresh(cached))
            {
                return WithLocalCape(
                    new SkinTexture(cached.Name, cached.Skin, cached.Cape, cached.Slim, "cache"), row);
            }

            var fetched = await FetchMojangProfileAsync(row.Name, cancellationToken);
            if (fetched is not null)
            {
                WriteProfileCache(row.Name, fetched);

                return fetched;
            }

            if (cached is not null && !string.IsNullOrEmpty(cached.Skin))
            {
                return WithLocalCape(
                    new SkinTexture(cached.Name, cached.Skin, cached.Cape, cached.Slim, "cache"), row);
            }
        }

        // 3) 离线目录：客户端 jar 里的官方 Steve / Alex 等贴图
        var choice = FindChoice(row.OfflineSkinId);
        var bytes = await ResolveDefaultBytesAsync(choice, cancellationToken);
        if (bytes is null) return null;

        return WithLocalCape(
            new SkinTexture(row.Name, ToDataUrl(bytes), null, choice.Slim, "client-jar"), row);
    }

    /// <summary>账号设置了本地自定义披风时，用它替换纹理结果里的披风（官方披风优先级低于本地披风）。</summary>
    private static SkinTexture WithLocalCape(SkinTexture texture, AccountRow row)
    {
        var cape = ReadCapeDataUrl(row.Name);

        return cape is null
            ? texture
            : texture with { CapeDataUrl = cape };
    }

    public static async Task<byte[]?> ResolveCatalogBytesAsync(
        OfflineSkinChoice choice, CancellationToken cancellationToken) =>
        await ResolveDefaultBytesAsync(choice, cancellationToken);

    // ---------------- 本地自定义皮肤 ----------------

    public static string CustomSkinPath(string accountName) =>
        Path.Combine(CustomDirectory, Sanitize(accountName) + ".png");

    public static string? ExistingCustomSkin(string accountName)
    {
        var path = CustomSkinPath(accountName);

        return File.Exists(path) ? path : null;
    }

    public static string SaveCustomSkin(string sourcePath, string accountName)
    {
        var target = CustomSkinPath(accountName);
        Directory.CreateDirectory(CustomDirectory);
        File.Copy(sourcePath, target, overwrite: true);

        return target;
    }

    // ---------------- 本地自定义披风（仅启动器预览） ----------------

    private static readonly string CapeDirectory = Path.Combine(RootDirectory, "capes");

    public static string CapePath(string accountName) =>
        Path.Combine(CapeDirectory, Sanitize(accountName) + ".png");

    public static string? ExistingCape(string accountName)
    {
        var path = CapePath(accountName);

        return File.Exists(path) ? path : null;
    }

    public static string SaveCape(string sourcePath, string accountName)
    {
        var target = CapePath(accountName);
        Directory.CreateDirectory(CapeDirectory);
        File.Copy(sourcePath, target, overwrite: true);

        return target;
    }

    public static void DeleteCape(string accountName)
    {
        try
        {
            File.Delete(CapePath(accountName));
        }
        catch (IOException)
        {
            // 删除失败等价于没有自定义披风
        }
    }

    /// <summary>读取账号的自定义披风并转 data URL；没有或读取失败返回 null。</summary>
    public static string? ReadCapeDataUrl(string accountName)
    {
        var path = CapePath(accountName);

        return File.Exists(path) ? TryReadDataUrl(path) : null;
    }

    /// <summary>校验披风文件：PNG、≤4 MiB、宽高比 2:1（64×32 / 92×64 / 128×64 等）。</summary>
    public static bool TryValidateCapeFile(string path, out string error)
    {
        error = string.Empty;

        try
        {
            if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
            {
                error = L.T("Account.Cape.InvalidPng", "披风文件必须是 PNG 图片");

                return false;
            }

            var info = new FileInfo(path);
            if (!info.Exists)
            {
                error = L.T("Account.Local.Missing", "皮肤文件不存在");

                return false;
            }

            if (info.Length <= 0 || info.Length > MaxTextureBytes)
            {
                error = L.T("Account.Local.TooLarge", "皮肤文件为空或超过 4 MiB");

                return false;
            }

            using var bitmap = new Bitmap(path);
            var size = bitmap.PixelSize;
            if (size.Width != size.Height * 2)
            {
                error = L.T("Account.Cape.BadSize", "披风纹理的宽高比需要是 2:1（例如 64×32 或 92×64）");

                return false;
            }

            return true;
        }
        catch
        {
            error = L.T("Account.Local.BadPng", "皮肤文件不是有效的 PNG 图片");

            return false;
        }
    }

    /// <summary>校验皮肤文件：PNG、≤4 MiB、64×64 或旧版 64×32。</summary>
    public static bool TryValidateSkinFile(string path, out string error)
    {
        error = string.Empty;

        try
        {
            if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
            {
                error = L.T("Account.Local.InvalidPng", "皮肤文件必须是 PNG 图片");

                return false;
            }

            var info = new FileInfo(path);
            if (!info.Exists)
            {
                error = L.T("Account.Local.Missing", "皮肤文件不存在");

                return false;
            }

            if (info.Length <= 0 || info.Length > MaxTextureBytes)
            {
                error = L.T("Account.Local.TooLarge", "皮肤文件为空或超过 4 MiB");

                return false;
            }

            using var bitmap = new Bitmap(path);
            var size = bitmap.PixelSize;
            if (size.Width != 64 || (size.Height != 64 && size.Height != 32))
            {
                error = L.T("Account.Local.BadSize", "Minecraft Java 皮肤必须是 64×64（或旧版 64×32）");

                return false;
            }

            return true;
        }
        catch
        {
            error = L.T("Account.Local.BadPng", "皮肤文件不是有效的 PNG 图片");

            return false;
        }
    }

    // ---------------- 离线内置贴图（客户端 jar） ----------------

    private static async Task<byte[]?> ResolveDefaultBytesAsync(
        OfflineSkinChoice choice, CancellationToken cancellationToken)
    {
        var cachePath = Path.Combine(DefaultDirectory, choice.Id + ".png");
        if (File.Exists(cachePath))
        {
            try
            {
                var cached = await File.ReadAllBytesAsync(cachePath, cancellationToken);
                if (cached.Length > 0) return cached;
            }
            catch (IOException)
            {
                // 缓存读取失败继续走 jar 提取
            }
        }

        var bytes = await Task.Run(() => ExtractFromClientJars(choice), cancellationToken);
        if (bytes is null) return null;

        try
        {
            Directory.CreateDirectory(DefaultDirectory);
            await File.WriteAllBytesAsync(cachePath, bytes, cancellationToken);
        }
        catch (IOException)
        {
            // 缓存写失败不影响本次使用
        }

        return bytes;
    }

    /// <summary>从已安装的客户端 jar 里提取内置贴图路径（新版 wide/slim 子目录 + 旧版扁平路径）。</summary>
    private static byte[]? ExtractFromClientJars(OfflineSkinChoice choice)
    {
        var preferred = choice.Slim ? "slim" : "wide";
        var other = choice.Slim ? "wide" : "slim";

        foreach (var jarPath in EnumerateClientJars())
        {
            try
            {
                using var archive = ZipFile.OpenRead(jarPath);

                foreach (var model in new[] { preferred, other })
                {
                    foreach (var entryPath in new[]
                             {
                                 $"assets/minecraft/textures/entity/player/{model}/{choice.Id}.png",
                                 $"assets/minecraft/textures/entity/{choice.Id}.png",
                             })
                    {
                        var entry = archive.GetEntry(entryPath);
                        if (entry is null) continue;

                        using var stream = entry.Open();
                        using var buffer = new MemoryStream();
                        stream.CopyTo(buffer);
                        if (buffer.Length > 0) return buffer.ToArray();
                    }
                }
            }
            catch (IOException)
            {
                // 坏 jar / 正在写入的 jar 跳过
            }
            catch (InvalidDataException)
            {
                // 不是 zip 归档，跳过
            }
        }

        return null;
    }

    /// <summary>候选客户端 jar（新到旧，限制数量避免扫盘过久）。</summary>
    private static IEnumerable<string> EnumerateClientJars()
    {
        var jars = new List<(string Path, DateTime Time)>();
        var minecraft = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

        void Collect(string directory, SearchOption option)
        {
            try
            {
                if (!Directory.Exists(directory)) return;

                foreach (var jar in Directory.EnumerateFiles(directory, "*.jar", option))
                {
                    try
                    {
                        jars.Add((jar, File.GetLastWriteTimeUtc(jar)));
                    }
                    catch (IOException)
                    {
                        // 单个文件读取失败忽略
                    }
                }
            }
            catch (IOException)
            {
                // 目录不可枚举忽略
            }
        }

        try
        {
            var versions = Path.Combine(minecraft, "versions");
            if (Directory.Exists(versions))
            {
                foreach (var versionDirectory in Directory.EnumerateDirectories(versions))
                {
                    Collect(versionDirectory, SearchOption.TopDirectoryOnly);
                    Collect(Path.Combine(versionDirectory, ".fabric", "remappedJars"), SearchOption.AllDirectories);
                }
            }

            Collect(Path.Combine(minecraft, ".fabric", "remappedJars"), SearchOption.AllDirectories);
        }
        catch (IOException)
        {
            // .minecraft 不可枚举时直接放弃
        }

        return jars
            .OrderByDescending(jar => jar.Time)
            .Select(jar => jar.Path)
            .Take(24)
            .ToList();
    }

    // ---------------- Mojang 档案 ----------------

    private static async Task<SkinTexture?> FetchMojangProfileAsync(
        string username, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            var name = username.Trim();
            var userUrl = $"https://api.mojang.com/users/profiles/minecraft/{Uri.EscapeDataString(name)}";
            using var userResponse = await Http.GetAsync(
                userUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!userResponse.IsSuccessStatusCode) return null;

            var userJson = await userResponse.Content.ReadAsStringAsync(timeout.Token);
            using var userDocument = JsonDocument.Parse(userJson);
            if (!userDocument.RootElement.TryGetProperty("id", out var idElement)) return null;
            var uuid = idElement.GetString();
            if (string.IsNullOrWhiteSpace(uuid)) return null;

            var displayName = userDocument.RootElement.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : name;

            using var profileResponse = await Http.GetAsync(
                $"https://sessionserver.mojang.com/session/minecraft/profile/{uuid}",
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!profileResponse.IsSuccessStatusCode) return null;

            var profileJson = await profileResponse.Content.ReadAsStringAsync(timeout.Token);
            var textures = ParseTextures(profileJson);
            if (textures is not { } set || set.SkinUrl.Length == 0) return null;

            var skinBytes = await DownloadBytesAsync(set.SkinUrl, timeout.Token);
            if (skinBytes is null) return null;

            byte[]? capeBytes = null;
            if (!string.IsNullOrWhiteSpace(set.CapeUrl))
            {
                capeBytes = await DownloadBytesAsync(set.CapeUrl, timeout.Token);
            }

            return new SkinTexture(
                string.IsNullOrWhiteSpace(displayName) ? name : displayName,
                ToDataUrl(skinBytes),
                capeBytes is null ? null : ToDataUrl(capeBytes),
                set.Slim,
                "mojang");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is OperationCanceledException or HttpRequestException
                                          or JsonException or IOException)
        {
            // 网络不可用 / 档案不存在：静默回退
            return null;
        }
    }

    private static (string SkinUrl, string? CapeUrl, bool Slim)? ParseTextures(string profileJson)
    {
        using var document = JsonDocument.Parse(profileJson);
        if (!document.RootElement.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var property in properties.EnumerateArray())
        {
            if (!property.TryGetProperty("name", out var nameElement)
                || !string.Equals(nameElement.GetString(), "textures", StringComparison.Ordinal))
            {
                continue;
            }

            if (!property.TryGetProperty("value", out var valueElement)) continue;

            var encoded = valueElement.GetString();
            if (string.IsNullOrWhiteSpace(encoded)) continue;

            string decoded;
            try
            {
                decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            }
            catch (FormatException)
            {
                continue;
            }

            using var payload = JsonDocument.Parse(decoded);
            if (!payload.RootElement.TryGetProperty("textures", out var textures)) return null;

            string? skinUrl = null;
            string? capeUrl = null;
            var slim = false;

            if (textures.TryGetProperty("SKIN", out var skin))
            {
                if (skin.TryGetProperty("url", out var skinUrlElement))
                {
                    skinUrl = NormalizeTextureUrl(skinUrlElement.GetString());
                }

                if (skin.TryGetProperty("metadata", out var metadata)
                    && metadata.TryGetProperty("model", out var modelElement)
                    && string.Equals(modelElement.GetString(), "slim", StringComparison.OrdinalIgnoreCase))
                {
                    slim = true;
                }
            }

            if (textures.TryGetProperty("CAPE", out var cape)
                && cape.TryGetProperty("url", out var capeUrlElement))
            {
                capeUrl = NormalizeTextureUrl(capeUrlElement.GetString());
            }

            return (skinUrl ?? string.Empty, capeUrl, slim);
        }

        return null;
    }

    /// <summary>贴图地址归一化：https 直接用；http 仅 textures.minecraft.net 升级为 https。</summary>
    private static string? NormalizeTextureUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme == Uri.UriSchemeHttps) return uri.ToString();

        if (uri.Scheme == Uri.UriSchemeHttp
            && string.Equals(uri.Host, "textures.minecraft.net", StringComparison.OrdinalIgnoreCase))
        {
            return "https://textures.minecraft.net" + uri.AbsolutePath;
        }

        return null;
    }

    private static async Task<byte[]?> DownloadBytesAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        if (response.Content.Headers.ContentLength is > MaxTextureBytes) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length == 0 || buffer.Length > MaxTextureBytes) return null;

        return buffer.ToArray();
    }

    // ---------------- 档案缓存 ----------------

    private sealed class ProfileCacheEntry
    {
        public string Name { get; set; } = string.Empty;
        public string? Skin { get; set; }
        public string? Cape { get; set; }
        public bool Slim { get; set; }
        public long FetchedAt { get; set; }
    }

    private static bool IsFresh(ProfileCacheEntry entry) =>
        entry.FetchedAt > 0
        && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - entry.FetchedAt < (long)CacheTtl.TotalSeconds;

    private static ProfileCacheEntry? ReadProfileCache(string username)
    {
        try
        {
            var path = SkinHeadService.ProfileCachePath(username);
            if (!File.Exists(path)) return null;

            var entry = JsonSerializer.Deserialize<ProfileCacheEntry>(File.ReadAllText(path), JsonOptions);

            return entry is { Skin.Length: > 0 } ? entry : null;
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            return null;
        }
    }

    private static void WriteProfileCache(string username, SkinTexture texture)
    {
        if (string.IsNullOrEmpty(texture.SkinDataUrl)) return;

        try
        {
            var path = SkinHeadService.ProfileCachePath(username);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var entry = new ProfileCacheEntry
            {
                Name = texture.Name,
                Skin = texture.SkinDataUrl,
                Cape = texture.CapeDataUrl,
                Slim = texture.Slim,
                FetchedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entry, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException)
        {
            // 缓存写失败不影响本次使用
        }
    }

    // ---------------- 小工具 ----------------

    private static string? TryReadDataUrl(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);

            return bytes.Length > 0 ? ToDataUrl(bytes) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static string ToDataUrl(byte[] bytes) =>
        "data:image/png;base64," + Convert.ToBase64String(bytes);

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        }

        var result = builder.ToString().Trim('_');

        return result.Length == 0 ? "account" : result;
    }
}
