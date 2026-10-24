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
public partial class OobeWindow : SukiUI.Controls.SukiWindow
{
    private readonly List<StackPanel> _steps;
    private readonly List<OobeJavaItem> _javaItems = [];
    private bool _initializing;
    private int _step;
    private int _stepDirection = 1;
    private string? _microsoftAccountName;
    private string? _authlibAccountName;
    private CancellationTokenSource? _loginCts;
    private LoginBrowserWindow? _loginWindow;

    public event Action? Completed;

    public OobeWindow()
    {
        _initializing = true;
        InitializeComponent();

        _steps = [WelcomePanel, DirectoryPanel, JavaPanel, AccountPanel, FinishPanel];

        LanguageCombo.ItemsSource = L.Languages.Select(option => option.Name).ToList();
        LanguageCombo.SelectedIndex = Math.Max(0, L.Languages.ToList()
            .FindIndex(option => option.Code == L.CurrentLanguage));
        ThemeCombo.SelectedIndex = LocalSettings.Current.Theme switch
        {
            "system" => 0,
            "light" => 2,
            _ => 1,
        };
        GameDirectoryBox.Text = YamlRead.GameDirectory;
        _initializing = false;

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

    private void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        string code = LanguageCombo.SelectedIndex >= 0 && LanguageCombo.SelectedIndex < L.Languages.Count
            ? L.Languages[LanguageCombo.SelectedIndex].Code
            : L.DefaultLanguage;
        L.SetLanguage(code);
        UpdateStep();
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
        if (_microsoftAccountName is not null)
        {
            MicrosoftStatusText.Text = L.T("Oobe.Account.LoggedIn", "已登录：{0}", _microsoftAccountName);
            return;
        }

        _loginCts?.Dispose();
        _loginCts = new CancellationTokenSource();
        _loginWindow = new LoginBrowserWindow();
        _loginWindow.Closed += (_, _) =>
        {
            if (!_loginWindow?.CloseByHost == true) _loginCts?.Cancel();
        };

        MicrosoftLoginButton.IsEnabled = false;
        MicrosoftStatusText.Text = L.T("Oobe.Account.WaitingBrowser", "请在浏览器完成 Microsoft 登录…");
        try
        {
            MicrosoftAccount account = await MinecraftTokenHandle.StartBrowserLoginAsync(
                url =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_loginWindow is not null && !_loginWindow.IsVisible) _loginWindow.Show(this);
                        _ = _loginWindow?.NavigateAsync(url);
                    });
                    return Task.CompletedTask;
                },
                cancellationToken: _loginCts.Token);

            _microsoftAccountName = account.Username;
            LocalSettings.Current.SelectedAccountName = account.Username;
            LocalSettings.Save();
            MicrosoftStatusText.Text = L.T("Oobe.Account.LoggedIn", "已登录：{0}", account.Username);
        }
        catch (OperationCanceledException)
        {
            MicrosoftStatusText.Text = L.T("Oobe.Account.LoginCancelled", "已取消 Microsoft 登录");
        }
        catch (Exception error)
        {
            MicrosoftStatusText.Text = L.T("Oobe.Account.LoginFailed", "登录失败：{0}", error.Message);
        }
        finally
        {
            _loginCts?.Dispose();
            _loginCts = null;
            if (_loginWindow is not null)
            {
                _loginWindow.CloseByHost = true;
                _loginWindow.Close();
                _loginWindow = null;
            }
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
            var manager = new AuthlibAccountManage();
            AuthlibServerInfo info = await manager.ResolveServerAsync(server);
            AuthlibLoginResult login = await manager.AuthenticateAsync(info.ApiRoot, username, password,
                Guid.NewGuid().ToString("D"));
            AuthlibProfileInfo? profile = login.Profiles.FirstOrDefault();
            if (profile is null)
            {
                AuthlibStatusText.Text = L.T("Oobe.Account.AuthlibNoProfile", "该账号没有可用角色");
                return;
            }

            var handle = new MinecraftTokenHandle();
            handle.SaveAuthlibCredential(new AuthlibCredential
            {
                Username = username,
                ProfileName = profile.Name,
                ProfileUuid = profile.Id,
                AccessToken = login.AccessToken,
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
        if (_step == 0 && LanguageCombo.SelectedIndex < 0)
        {
            LanguageCombo.SelectedIndex = 0;
        }

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
        }

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
        SummaryLanguage.Text = LanguageCombo.SelectedItem?.ToString() ?? L.CurrentLanguage;
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
