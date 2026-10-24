using System;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Services;

/// <summary>游戏运行状态与当前选择（版本 / 账号），供开始栏、桌面启动卡与启动进度页共享。</summary>
public static class GameState
{
    public static bool IsRunning { get; private set; }

    /// <summary>当前选中的版本 id（开始栏下拉里选的，默认取本地设置/Mock）。</summary>
    public static string SelectedVersion { get; private set; } = DefaultVersion();

    /// <summary>当前选中的账号名。</summary>
    public static string SelectedAccount { get; private set; } = DefaultAccount();

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
        if (IsRunning) return;
        IsRunning = true;
        Changed?.Invoke();
    }

    public static void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        Changed?.Invoke();
    }

    /// <summary>开始栏切换版本。</summary>
    public static void SetVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || version == SelectedVersion) return;
        SelectedVersion = version;
        Changed?.Invoke();
    }

    /// <summary>开始栏切换账号。</summary>
    public static void SetAccount(string account)
    {
        if (string.IsNullOrWhiteSpace(account) || account == SelectedAccount) return;
        SelectedAccount = account;
        Changed?.Invoke();
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
