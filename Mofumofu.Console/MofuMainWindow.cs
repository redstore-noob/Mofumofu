using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mofumofu.Console;

public class MofuMainWindow : Window, IMofuShell
{
    private static readonly (string Key, string Label)[] Pages =
    [
        ("home", "主页"),
        ("instances", "实例"),
        ("download", "下载"),
        ("accounts", "账号"),
        ("servers", "服务器"),
        ("rewind", "回档"),
        ("online", "联机"),
        ("tools", "工具"),
        ("settings", "设置"),
    ];

    private readonly ListView _nav;
    private readonly View _content;
    private readonly Label _status;
    private readonly ObservableCollection<string> _navSource = new(Pages.Select(page => page.Label));
    private readonly Dictionary<string, MofuPage> _pages = new(StringComparer.Ordinal);
    private MofuPage? _current;
    private string _currentKey = "home";
    private bool _switching;

    public MofuMainWindow()
    {
        Title = "Mofumofu TUI";
        Width = Dim.Fill();
        Height = Dim.Fill();

        Settings = TuiSettings.Load();
        Accounts = new AccountService();

        MenuBar menu = new()
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
        };
        menu.Add(new MenuBarItem("文件", new[]
        {
            new MenuItem("退出", "Ctrl+Q", () => Application.RequestStop()),
        }));
        Key[] pageKeys = [Key.F1, Key.F2, Key.F3, Key.F4, Key.F5, Key.F6, Key.F7, Key.F8, Key.F9];
        menu.Add(new MenuBarItem("页面", Pages.Select((page, index) =>
            new MenuItem(page.Label, "F" + (index + 1), () => ShowPage(page.Key), pageKeys[index])).ToArray()));
        menu.Add(new MenuBarItem("帮助", new[]
        {
            new MenuItem("快捷键", "", ShowHelp),
        }));

        _nav = new ListView
        {
            X = 0,
            Y = 1,
            Width = 18,
            Height = Dim.Fill(1),
            Title = " 导航 ",
            BorderStyle = LineStyle.Single,
        };
        _nav.SetSource(_navSource);
        _nav.ValueChanged += (_, args) =>
        {
            if (_switching)
            {
                return;
            }

            int index = args.NewValue ?? -1;
            if (index >= 0 && index < Pages.Length)
            {
                ShowPage(Pages[index].Key);
            }
        };

        _content = new View
        {
            X = 18,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
        };

        _status = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            Text = " F1–F9 切换页面 · Ctrl+Q 退出",
        };

        Add(menu, _nav, _content, _status);

        KeyDown += (_, key) =>
        {
            if (key.KeyCode == (KeyCode.Q | KeyCode.CtrlMask))
            {
                key.Handled = true;
                Application.RequestStop();
                return;
            }

            int functionIndex = (int)key.KeyCode - (int)KeyCode.F1;
            if (functionIndex >= 0 && functionIndex < Pages.Length)
            {
                key.Handled = true;
                ShowPage(Pages[functionIndex].Key);
            }
        };

        ShowPage(Settings.SelectedPage);

        Initialized += (_, _) => Ui.StartPump();
    }

    public TuiSettings Settings { get; }

    public AccountService Accounts { get; }

    public void SetStatus(string text)
    {
        _status.Text = text;
    }

    public void ShowPage(string key)
    {
        if (!Pages.Any(page => page.Key == key))
        {
            key = "home";
        }

        if (_current is not null && _currentKey == key)
        {
            _current.OnShown();
            return;
        }

        _current?.OnHidden();
        if (_current is not null)
        {
            _content.Remove(_current);
        }

        MofuPage page = GetOrCreatePage(key);
        page.X = 0;
        page.Y = 0;
        page.Width = Dim.Fill();
        page.Height = Dim.Fill();
        _content.Add(page);
        _current = page;
        _currentKey = key;
        Settings.SelectedPage = key;
        Settings.Save();

        int navIndex = Array.FindIndex(Pages, item => item.Key == key);
        if (navIndex >= 0)
        {
            _switching = true;
            _nav.SelectedItem = navIndex;
            _switching = false;
        }

        page.OnShown();
        page.SetFocus();
    }

    private MofuPage GetOrCreatePage(string key)
    {
        if (_pages.TryGetValue(key, out MofuPage? existing))
        {
            return existing;
        }

        MofuPage page = key switch
        {
            "instances" => new Pages.InstancesPage(this),
            "download" => new Pages.DownloadPage(this),
            "accounts" => new Pages.AccountsPage(this),
            "servers" => new Pages.ServersPage(this),
            "rewind" => new Pages.RewindPage(this),
            "online" => new Pages.OnlinePage(this),
            "tools" => new Pages.ToolsPage(this),
            "settings" => new Pages.SettingsPage(this),
            _ => new Pages.HomePage(this),
        };
        _pages[key] = page;
        return page;
    }

    private static void ShowHelp()
    {
        Ui.Info("快捷键",
            "F1–F9：切换页面\n" +
            "Ctrl+Q：退出\n" +
            "Tab / Shift+Tab：切换焦点\n" +
            "↑ ↓：选择列表项\n" +
            "Enter：执行当前项\n" +
            "Esc：关闭对话框 / 取消当前任务");
    }
}
