using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MofuCore.Config;
using MofuCore.Account;
using MofuCore.Java;
using MofuCore.Launch;
using Mofumofu.Launcher.Localization;
using Mofumofu.Launcher.Services;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views;

public sealed class OobeJavaItem
{
    public string DisplayName { get; init; } = string.Empty;
    public string JavaPath { get; init; } = string.Empty;
    public int? MajorVersion { get; init; }
    public override string ToString() => DisplayName;
}

/// <summary>首次启动引导：语言 / 主题 / 游戏目录 / Java / 离线账号。</summary>
public partial class OobeWindow : Window
{
    private readonly List<StackPanel> _steps;
    private readonly Border[] _stepItems;
    private readonly Border[] _stepBadges;
    private readonly List<OobeJavaItem> _javaItems = [];
    private bool _initializing;
    private int _step;
    private int _stepDirection = 1;
    private string? _microsoftAccountName;
    private string? _authlibAccountName;
    private CancellationTokenSource? _loginCts;

    public event Action? Completed;

    public OobeWindow()
    {
        _initializing = true;
        InitializeComponent();

        _steps = [WelcomePanel, DirectoryPanel, JavaPanel, AccountPanel, FinishPanel];
        _stepItems = [StepNav0, StepNav1, StepNav2, StepNav3, StepNav4];
        _stepBadges = [StepBadge0, StepBadge1, StepBadge2, StepBadge3, StepBadge4];

        // 与设置页共用同一份语言清单，并用卡片完整展示，避免下拉列表看起来缺项。
        LanguageCards.ItemsSource = L.Languages;
        ThemeCombo.SelectedIndex = LocalSettings.Current.Theme switch
        {
            "system" => 0,
            "light" => 2,
            _ => 1,
        };
        GameDirectoryBox.Text = YamlRead.GameDirectory;
        _initializing = false;

        UpdateLanguageSelection();
        Dispatcher.UIThread.Post(UpdateLanguageSelection, DispatcherPriority.Loaded);
        UpdateStep();
        _ = DetectJavaAsync();

        Opened += (_, _) =>
        {
            if (Content is Control content)
            {
                UiAnimations.Reset(content);
                UiAnimations.FadeSlideIn(content, 0.99, 10, 220);
            }
        };
    }

    private void OnLanguageCardClick(object? sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not Button { Tag: string code }) return;

        // 保留 system 选项本身；CurrentLanguage 是解析后的语言，不能用来判断选中卡片。
        LocalSettings.Current.Language = code;
        L.SetLanguage(code);
        UpdateLanguageSelection();
        UpdateStep();
    }

    private void UpdateLanguageSelection()
    {
        string selectedCode = LocalSettings.Current.Language;
        foreach (Button card in LanguageCards.GetVisualDescendants().OfType<Button>())
        {
            card.Classes.Set("active", string.Equals(card.Tag as string, selectedCode,
                StringComparison.OrdinalIgnoreCase));
        }
    }

    private void OnThemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        string theme = ThemeCombo.SelectedIndex switch
        {
            0 => "system",
            2 => "light",
            _ => "dark",
        };
        LocalSettings.Current.Theme = theme;
        LocalSettings.Save();
        ThemeManager.Apply(theme);
    }

    private void OnAccountTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        string type = AccountTypeCombo.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : "offline";
        OfflineAccountPanel.IsVisible = type == "offline";
        MicrosoftAccountPanel.IsVisible = type == "microsoft";
        AuthlibAccountPanel.IsVisible = type == "authlib";
    }

    private async void OnMicrosoftLoginClick(object? sender, RoutedEventArgs e)
    {
        if (_loginCts is not null)
        {
            _loginCts.Cancel();
            return;
        }

        if (_microsoftAccountName is not null)
        {
            MicrosoftStatusText.Text = L.T("Oobe.Account.LoggedIn", "已登录：{0}", _microsoftAccountName);
            return;
        }

        _loginCts?.Dispose();
        var cts = new CancellationTokenSource();
        _loginCts = cts;

        MicrosoftLoginButton.Content = L.T("Oobe.Account.CancelLogin", "取消登录");
        MicrosoftStatusText.Text = L.T("Oobe.Account.WaitingBrowser", "请在浏览器完成 Microsoft 登录…");
        string? openError = null;
        try
        {
            MicrosoftAccount account = await MinecraftTokenHandle.StartBrowserLoginAsync(
                url =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (OpenLinkBrowser.TryOpen(url, out string error)) return;

                        openError = error;
                        cts.Cancel();
                    });
                    return Task.CompletedTask;
                },
                cancellationToken: cts.Token);

            _microsoftAccountName = account.Username;
            LocalSettings.Current.SelectedAccountName = account.Username;
            LocalSettings.Save();
            MicrosoftStatusText.Text = L.T("Oobe.Account.LoggedIn", "已登录：{0}", account.Username);
        }
        catch (OperationCanceledException)
        {
            MicrosoftStatusText.Text = openError is { Length: > 0 }
                ? L.T("Oobe.Account.LoginFailed", "登录失败：{0}", openError)
                : L.T("Oobe.Account.LoginCancelled", "已取消 Microsoft 登录");
        }
        catch (Exception error)
        {
            MicrosoftStatusText.Text = L.T("Oobe.Account.LoginFailed", "登录失败：{0}", error.Message);
        }
        finally
        {
            cts.Dispose();
            _loginCts = null;
            MicrosoftLoginButton.Content = L.T("Oobe.Account.MicrosoftLogin", "登录 Microsoft");
            MicrosoftLoginButton.IsEnabled = true;
        }
    }

    private async void OnAuthlibLoginClick(object? sender, RoutedEventArgs e)
    {
        string server = (AuthlibServerBox.Text ?? string.Empty).Trim();
        string username = (AuthlibUsernameBox.Text ?? string.Empty).Trim();
        string password = AuthlibPasswordBox.Text ?? string.Empty;
        if (server.Length == 0 || username.Length == 0 || password.Length == 0)
        {
            AuthlibStatusText.Text = L.T("Oobe.Account.AuthlibRequired", "请填写服务器地址、用户名和密码");
            return;
        }

        AuthlibLoginButton.IsEnabled = false;
        AuthlibStatusText.Text = L.T("Oobe.Account.AuthlibLoggingIn", "正在登录皮肤站…");
        try
        {
            var handle = new MinecraftTokenHandle();
            AuthlibAccountManage manager = handle.Authlib;
            // clientToken 与后续保活链路保持一致，避免 validate/refresh 因 clientToken 不一致被拒
            string clientToken = handle.GetOrCreateClientToken();
            AuthlibServerInfo info = await manager.ResolveServerAsync(server);
            AuthlibLoginResult login = await manager.AuthenticateAsync(info.ApiRoot, username, password,
                clientToken);
            AuthlibProfileInfo? profile = login.Profiles.FirstOrDefault();
            if (profile is null)
            {
                AuthlibStatusText.Text = L.T("Oobe.Account.AuthlibNoProfile", "该账号没有可用角色");
                return;
            }

            // 登录即时保活：确认刚签发的令牌可用；若皮肤站回换新令牌，写回存储的就是新令牌
            string accessToken = login.AccessToken;
            try
            {
                accessToken = await manager.ValidateOrRefreshAsync(new AuthlibCredential
                {
                    ApiRoot = info.ApiRoot,
                    AccessToken = login.AccessToken,
                    ProfileUuid = profile.Id,
                }, clientToken);
            }
            catch (Exception)
            {
                // 保活失败不阻断登录：保留登录时签发的令牌
                accessToken = login.AccessToken;
            }

            handle.SaveAuthlibCredential(new AuthlibCredential
            {
                Username = username,
                ProfileName = profile.Name,
                ProfileUuid = profile.Id,
                AccessToken = accessToken,
                ApiRoot = info.ApiRoot,
                ServerName = info.ServerName,
            });

            _authlibAccountName = profile.Name;
            LocalSettings.Current.SelectedAccountName = profile.Name;
            LocalSettings.Save();
            AuthlibStatusText.Text = L.T("Oobe.Account.LoggedIn", "已登录：{0}", profile.Name);
        }
        catch (Exception error)
        {
            AuthlibStatusText.Text = L.T("Oobe.Account.LoginFailed", "登录失败：{0}", error.Message);
        }
        finally
        {
            AuthlibLoginButton.IsEnabled = true;
        }
    }

    private async void OnBrowseDirectoryClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            TopLevel? top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider is not { } storage) return;

            IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = L.T("Oobe.DirectoryLabel", "选择游戏目录"),
                    AllowMultiple = false,
                });
            if (folders.Count == 0) return;

            string? path = folders[0].TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                GameDirectoryBox.Text = path;
            }
        }
        catch (Exception error)
        {
            JavaStatusText.Text = error.Message;
        }
    }

    private async void OnDetectJavaClick(object? sender, RoutedEventArgs e) => await DetectJavaAsync();

    private async Task DetectJavaAsync()
    {
        JavaStatusText.Text = L.T("Oobe.JavaDetecting", "正在检测本机 Java…");
        try
        {
            IReadOnlyList<JavaRuntime> runtimes = await JavaPathSearch.FindAllJavaAsync();
            _javaItems.Clear();
            foreach (JavaRuntime runtime in runtimes)
            {
                _javaItems.Add(new OobeJavaItem
                {
                    DisplayName = $"Java {runtime.MajorVersion?.ToString() ?? runtime.VersionString} · {runtime.JavaPath}",
                    JavaPath = runtime.JavaPath,
                    MajorVersion = runtime.MajorVersion,
                });
            }

            JavaCombo.ItemsSource = _javaItems;
            JavaCombo.SelectedIndex = _javaItems.Count > 0 ? 0 : -1;
            JavaStatusText.Text = _javaItems.Count == 0
                ? L.T("Oobe.JavaNone", "未检测到 Java，可稍后在「下载 → Java」安装。")
                : L.T("Oobe.JavaFound", "检测到 {0} 个 Java 运行时。", _javaItems.Count);
        }
        catch (Exception error)
        {
            JavaStatusText.Text = L.T("Oobe.JavaFailed", "检测 Java 失败：{0}", error.Message);
        }
    }

    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        if (_step <= 0) return;
        _step--;
        _stepDirection = -1;
        UpdateStep();
    }

    private void OnNextClick(object? sender, RoutedEventArgs e)
    {
        if (_step >= _steps.Count - 1)
        {
            Finish();
            return;
        }

        _step++;
        _stepDirection = 1;
        UpdateStep();

        if (_steps[_step] == JavaPanel && _javaItems.Count == 0)
        {
            _ = DetectJavaAsync();
        }
    }

    private void UpdateStep()
    {
        for (var index = 0; index < _steps.Count; index++)
        {
            _steps[index].IsVisible = index == _step;
            _stepItems[index].Classes.Set("active", index == _step);
            _stepItems[index].Classes.Set("complete", index < _step);
            _stepBadges[index].Classes.Set("active", index == _step);
            _stepBadges[index].Classes.Set("complete", index < _step);
        }

        StepLabel0.Text = L.T("Oobe.Language", "界面语言") + " / " + L.T("Oobe.Theme", "外观主题");
        StepProgress.Value = (_step + 1) * 100d / _steps.Count;
        StepText.Text = L.T("Oobe.Step", "第 {0} / {1} 步", _step + 1, _steps.Count);
        BackButton.IsVisible = _step > 0;
        NextButton.Content = _step == _steps.Count - 1
            ? L.T("Oobe.Finish", "完成")
            : L.T("Oobe.Next", "下一步");

        if (_steps[_step] == FinishPanel)
        {
            UpdateSummary();
        }

        UiAnimations.Reset(_steps[_step]);
        UiAnimations.FadeSlideIn(_steps[_step], 0.995, _stepDirection * 6, 170);
    }

    private void UpdateSummary()
    {
        SummaryLanguage.Text = L.Languages.FirstOrDefault(option =>
            string.Equals(option.Code, LocalSettings.Current.Language, StringComparison.OrdinalIgnoreCase))
            ?.Name ?? L.CurrentLanguage;
        SummaryDirectory.Text = string.IsNullOrWhiteSpace(GameDirectoryBox.Text)
            ? YamlRead.GameDirectory
            : GameDirectoryBox.Text!.Trim();
        SummaryJava.Text = JavaCombo.SelectedItem is OobeJavaItem java
            ? java.DisplayName
            : L.T("Oobe.JavaNone", "未检测到 Java，可稍后在「下载 → Java」安装。");
        SummaryAccount.Text = _microsoftAccountName ?? _authlibAccountName ??
            (string.IsNullOrWhiteSpace(OfflineNameBox.Text)
                ? L.T("Oobe.AccountSkip", "稍后添加账号")
                : OfflineNameBox.Text!.Trim());
    }

    private void Finish()
    {
        try
        {
            string directory = (GameDirectoryBox.Text ?? string.Empty).Trim();
            if (directory.Length == 0) directory = YamlRead.GameDirectory;
            Directory.CreateDirectory(directory);
            YamlRead.SaveGameDirectory(directory);

            if (JavaCombo.SelectedItem is OobeJavaItem java)
            {
                YamlRead.SaveJava(java.JavaPath,
                    $"Java {java.MajorVersion?.ToString() ?? "runtime"}");
            }

            string username = (OfflineNameBox.Text ?? string.Empty).Trim();
            string? accountName = _microsoftAccountName ?? _authlibAccountName;
            if (accountName is null && GameLaunch.IsValidOfflineUsername(username))
            {
                new MinecraftTokenHandle().SaveOfflineAccount(username);
                accountName = username;
            }

            if (accountName is not null)
            {
                LocalSettings.Current.SelectedAccountName = accountName;
            }

            LocalSettings.Current.OobeCompleted = true;
            LocalSettings.Save();
            Completed?.Invoke();
            Close();
        }
        catch (Exception error)
        {
            JavaStatusText.Text = error.Message;
        }
    }

    private void OnSkipClick(object? sender, RoutedEventArgs e)
    {
        LocalSettings.Current.OobeCompleted = true;
        LocalSettings.Save();
        Completed?.Invoke();
        Close();
    }
}
