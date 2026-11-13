using System;
using Avalonia.Threading;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Services;

/// <summary>游戏运行状态与当前选择（版本 / 账号），供开始栏、桌面启动卡与启动进度页共享。</summary>
public static class GameState
{
    private static readonly object _sync = new();
    private static bool _isRunning;
    private static bool _versionExplicitlySet;
    private static bool _accountExplicitlySet;
    private static string _selectedVersion = DefaultVersion();
    private static string _selectedAccount = DefaultAccount();

    public static bool IsRunning { get { lock (_sync) return _isRunning; } }

    /// <summary>当前选中的版本 id（开始栏下拉里选的，默认取本地设置/Mock）。</summary>
    public static string SelectedVersion
    {
        get
        {
            lock (_sync)
            {
                if (_versionExplicitlySet) return _selectedVersion;
                var saved = LocalSettings.Current.SelectedInstanceId;
                return string.IsNullOrWhiteSpace(saved) ? _selectedVersion : saved;
            }
        }
    }

    /// <summary>当前选中的账号名。</summary>
    public static string SelectedAccount
    {
        get
        {
            lock (_sync)
            {
                if (_accountExplicitlySet) return _selectedAccount;
                var saved = LocalSettings.Current.SelectedAccountName;
                return string.IsNullOrWhiteSpace(saved) ? _selectedAccount : saved;
            }
        }
    }

    public static string VersionName => SelectedVersion;

    public static string AccountName => SelectedAccount;

    /// <summary>是否真的选了一个版本（默认占位文案不算）。</summary>
    public static bool HasSelectedVersion
    {
        get
        {
            var version = SelectedVersion;
            return !string.IsNullOrWhiteSpace(version)
                   && !string.Equals(version, L.T("Shell.StartBar.NoVersion", "未选择版本"),
                       StringComparison.Ordinal);
        }
    }

    public static event Action? Changed;

    /// <summary>标记为运行中（启动进度页接管启动流程时用，不重复弹提示）。</summary>
    public static void Start()
    {
        lock (_sync)
        {
            if (_isRunning) return;
            _isRunning = true;
        }
        RaiseChanged();
    }

    public static void Stop()
    {
        lock (_sync)
        {
            if (!_isRunning) return;
            _isRunning = false;
        }
        RaiseChanged();
    }

    /// <summary>开始栏切换版本。</summary>
    public static void SetVersion(string version)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(version) || version == SelectedVersion) return;
            _selectedVersion = version;
            _versionExplicitlySet = true;
        }
        RaiseChanged();
    }

    /// <summary>开始栏切换账号。</summary>
    public static void SetAccount(string account)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(account) || account == SelectedAccount) return;
            _selectedAccount = account;
            _accountExplicitlySet = true;
        }
        RaiseChanged();
    }

    /// <summary>状态变更统一派发到 UI 线程，避免后台回调直接触发 UI。</summary>
    private static void RaiseChanged()
    {
        var handler = Changed;
        if (handler is null) return;
        if (Dispatcher.UIThread.CheckAccess()) handler();
        else Dispatcher.UIThread.Post(handler);
    }

    private static string DefaultVersion()
    {
        var saved = LocalSettings.Current.SelectedInstanceId;
        return string.IsNullOrWhiteSpace(saved)
            ? L.T("Shell.StartBar.NoVersion", "未选择版本")
            : saved;
    }

    private static string DefaultAccount()
    {
        var saved = LocalSettings.Current.SelectedAccountName;
        return string.IsNullOrWhiteSpace(saved)
            ? L.T("Shell.StartBar.NoAccount", "未选择账号")
            : saved;
    }
}
