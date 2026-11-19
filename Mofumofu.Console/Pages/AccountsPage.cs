using System.Collections.ObjectModel;
using MofuCore.Account;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mofumofu.Console.Pages;

public sealed class AccountsPage : MofuPage
{
    private readonly ListView _list;
    private readonly TextView _detail;
    private List<AccountEntry> _entries = [];

    public AccountsPage(IMofuShell shell) : base(shell, "账号")
    {
        _list = new ListView
        {
            X = 1,
            Y = 1,
            Width = Dim.Percent(50),
            Height = Dim.Fill(3),
            Title = " 账号 ",
            BorderStyle = LineStyle.Single,
        };
        _list.SetSource(new ObservableCollection<string>());
        _list.ValueChanged += (_, _) => UpdateDetail();
        _list.Accepted += (_, _) => SetCurrent();

        _detail = new TextView
        {
            X = Pos.Percent(50) + 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = Dim.Fill(3),
            ReadOnly = true,
            WordWrap = true,
        };

        Button offline = new() { Text = "添加离线", X = 1, Y = Pos.AnchorEnd(2) };
        Button microsoft = new() { Text = "微软登录", X = 15, Y = Pos.AnchorEnd(2) };
        Button authlib = new() { Text = "皮肤站登录", X = 29, Y = Pos.AnchorEnd(2) };
        Button current = new() { Text = "设为当前", X = 45, Y = Pos.AnchorEnd(2) };
        Button remove = new() { Text = "删除", X = 59, Y = Pos.AnchorEnd(2) };
        Button refresh = new() { Text = "刷新", X = 70, Y = Pos.AnchorEnd(2) };

        offline.Accepted += (_, _) => AddOffline();
        microsoft.Accepted += (_, _) => _ = LoginMicrosoftAsync();
        authlib.Accepted += (_, _) => _ = LoginAuthlibAsync();
        current.Accepted += (_, _) => SetCurrent();
        remove.Accepted += (_, _) => RemoveSelected();
        refresh.Accepted += (_, _) => Refresh();

        Add(_list, _detail, offline, microsoft, authlib, current, remove, refresh);
    }

    public override void Refresh()
    {
        Shell.Accounts.Reload();
        _entries = Shell.Accounts.Entries.ToList();
        ObservableCollection<string> display = new();
        foreach (AccountEntry entry in _entries)
        {
            display.Add((entry.Key == Shell.Settings.SelectedAccount ? "★ " : "  ") + entry.Display);
        }

        _list.SetSource(display);
        if (_entries.Count > 0)
        {
            int index = _entries.FindIndex(entry => entry.Key == Shell.Settings.SelectedAccount);
            _list.SelectedItem = index >= 0 ? index : 0;
        }
        else
        {
            _list.SelectedItem = null;
        }

        UpdateDetail();
        Shell.SetStatus($"账号 {_entries.Count} 个 · 当前：{CurrentName()}");
    }

    private string CurrentName()
    {
        return _entries.FirstOrDefault(entry => entry.Key == Shell.Settings.SelectedAccount)?.Display ?? "未选择";
    }

    private AccountEntry? Selected =>
        _list.SelectedItem is int index && index >= 0 && index < _entries.Count ? _entries[index] : null;

    private void UpdateDetail()
    {
        AccountEntry? entry = Selected;
        if (entry is null)
        {
            _detail.Text = "还没有账号。\n\n可以添加离线账号，或使用微软 / 皮肤站登录。";
            return;
        }

        _detail.Text = string.Join(Environment.NewLine, new[]
        {
            entry.Display,
            new string('─', Math.Min(40, Math.Max(10, entry.Display.Length))),
            string.Empty,
            entry.Detail,
            string.Empty,
            entry.Key == Shell.Settings.SelectedAccount ? "当前使用的账号" : "Enter 设为当前账号",
        });
    }

    private void AddOffline()
    {
        string? name = Ui.Prompt("添加离线账号", "用户名（1–16 位字母、数字或下划线）");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            AccountEntry entry = Shell.Accounts.AddOffline(name);
            Shell.Settings.SelectedAccount = entry.Key;
            Shell.Settings.Save();
            Refresh();
        }
        catch (Exception exception)
        {
            Ui.Error("添加离线账号失败", exception.Message);
        }
    }

    private async Task LoginMicrosoftAsync()
    {
        (bool ok, MicrosoftAccount? account) = await Ui.RunTaskAsync(
            "微软账号登录",
            "正在请求设备码…",
            async (progress, token) =>
            {
                MicrosoftAccount result = await Shell.Accounts.Handle.AuthenticateAsync(
                    (info, _) =>
                    {
                        progress.Report($"在浏览器打开 {info.VerificationUri}，输入代码 {info.UserCode}");
                        Ui.Post(() => Ui.Info("微软账号登录",
                            $"请在浏览器打开：\n{info.VerificationUri}\n\n输入代码：{info.UserCode}\n\n" +
                            $"代码 {info.ExpiresIn.TotalMinutes:0} 分钟内有效，完成后回到终端等待即可。"));
                        return Task.CompletedTask;
                    },
                    step => progress.Report("登录阶段：" + step),
                    token).ConfigureAwait(false);
                return result;
            }).ConfigureAwait(false);

        if (ok && account is not null)
        {
            Shell.Accounts.SaveMicrosoft(account);
            Shell.Settings.SelectedAccount = "microsoft:" + account.Uuid;
            Shell.Settings.Save();
            Refresh();
            Ui.Info("登录成功", $"欢迎，{account.Username}！");
        }
    }

    private async Task LoginAuthlibAsync()
    {
        Dictionary<string, string>? values = Ui.Form("皮肤站登录",
            ("api", "皮肤站地址（例如 https://skin.example.com）", "https://", false),
            ("user", "用户名 / 邮箱", string.Empty, false),
            ("pass", "密码", string.Empty, true));
        if (values is null)
        {
            return;
        }

        string api = values["api"].Trim();
        string user = values["user"].Trim();
        string password = values["pass"];
        if (api.Length == 0 || user.Length == 0 || password.Length == 0)
        {
            Ui.Error("皮肤站登录", "地址、用户名与密码都不能为空。");
            return;
        }

        (bool ok, AuthlibLoginResult? result) = await Ui.RunTaskAsync(
            "皮肤站登录",
            "正在连接皮肤站…",
            async (progress, token) =>
            {
                AuthlibServerInfo server = await Shell.Accounts.Handle.Authlib
                    .ResolveServerAsync(api, token).ConfigureAwait(false);
                progress.Report($"服务器：{server.ServerName}");
                AuthlibLoginResult login = await Shell.Accounts.Handle.Authlib.AuthenticateAsync(
                    server.ApiRoot, user, password, Shell.Accounts.Handle.GetOrCreateClientToken(), token).ConfigureAwait(false);
                return login;
            }).ConfigureAwait(false);

        if (!ok || result is null)
        {
            return;
        }

        if (result.Profiles.Count == 0)
        {
            Ui.Info("皮肤站登录", "登录成功，但该账号没有任何游戏角色。");
            return;
        }

        string? picked = result.Profiles.Count == 1
            ? $"{result.Profiles[0].Name} · {result.Profiles[0].Id}"
            : Ui.Select("选择游戏角色", result.Profiles.Select(profile => $"{profile.Name} · {profile.Id}").ToList());
        if (picked is null)
        {
            return;
        }

        AuthlibProfileInfo? profile = result.Profiles.FirstOrDefault(item => $"{item.Name} · {item.Id}" == picked);
        if (profile is null)
        {
            return;
        }

        AuthlibCredential credential = new()
        {
            Username = user,
            ProfileName = profile.Name,
            ProfileUuid = profile.Id,
            AccessToken = result.AccessToken,
            ApiRoot = api,
            ServerName = result.ServerName,
        };
        Shell.Accounts.SaveAuthlib(credential);
        Shell.Settings.SelectedAccount = "authlib:" + credential.ProfileUuid + "@" + credential.ApiRoot.TrimEnd('/');
        Shell.Settings.Save();
        Refresh();
        Ui.Info("登录成功", $"已添加皮肤站账号：{profile.Name}");
    }

    private void SetCurrent()
    {
        AccountEntry? entry = Selected;
        if (entry is null)
        {
            return;
        }

        Shell.Settings.SelectedAccount = entry.Key;
        Shell.Settings.Save();
        Refresh();
    }

    private void RemoveSelected()
    {
        AccountEntry? entry = Selected;
        if (entry is null)
        {
            return;
        }

        if (!Ui.Confirm("删除账号", $"确定删除 {entry.Display} 吗？\n\n仅移除本机保存的账号信息。"))
        {
            return;
        }

        try
        {
            Shell.Accounts.Remove(entry);
            if (Shell.Settings.SelectedAccount == entry.Key)
            {
                Shell.Settings.SelectedAccount = string.Empty;
                Shell.Settings.Save();
            }

            Refresh();
        }
        catch (Exception exception)
        {
            Ui.Error("删除账号失败", exception.Message);
        }
    }
}
