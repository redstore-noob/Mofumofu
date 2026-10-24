using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MofuCore.Launch;
using MofuCore.OnlineGame;
using Mofumofu.Launcher.Services;
using SukiUI.Controls;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views.Widgets;

/// <summary>桌面小组件卡片：收藏的外部服务器（多台，Server List Ping 实时状态）。</summary>
internal static partial class WidgetCards
{
    public static Control QuickServers()
    {
        var rowsHost = new StackPanel { Spacing = 6 };

        var busy = false;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var (card, valueText) = WidgetChrome.CardWithValue(
            L.T("Home.Servers.Loading", "读取中…"),
            actions,
            rowsHost);

        async void Refresh()
        {
            if (busy) return;
            busy = true;
            try
            {
                var servers = LocalSettings.Current.QuickServers;
                rowsHost.Children.Clear();

                if (servers.Count == 0)
                {
                    valueText.Text = L.T("Home.Servers.Count", "{0} 台服务器", 0);
                    rowsHost.Children.Add(WidgetChrome.Empty(L.T("Home.Servers.EmptyHint", "还没有收藏服务器；点右上角 + 添加（支持 host:port）")));
                    return;
                }

                var online = 0;
                foreach (var server in servers)
                {
                    var row = new ServerRowPanel(server, Refresh);
                    rowsHost.Children.Add(row);
                    _ = row.PingAsync(() => online++);
                }

                valueText.Text = L.T("Home.Servers.Count", "{0} 台服务器", servers.Count);
            }
            catch
            {
                valueText.Text = L.T("Home.Servers.Count", "{0} 台服务器", 0);
                rowsHost.Children.Clear();
                rowsHost.Children.Add(WidgetChrome.Empty(L.T("Home.Servers.EmptyHint", "还没有收藏服务器；点右上角 + 添加（支持 host:port）")));
            }
            finally
            {
                busy = false;
            }
        }

        actions.Children.Add(WidgetChrome.IconButton("IconPlus",
            L.T("Home.Servers.AddTip", "添加服务器"), ShowAddServerDialog));
        actions.Children.Add(WidgetChrome.IconButton("IconFluentArrowClockwise",
            L.T("Home.Servers.RefreshTip", "重新 ping 全部服务器"), Refresh));

        WidgetChrome.OnBecameVisible(card, Refresh);
        Refresh();
        return card;
    }

    /// <summary>添加服务器弹窗：名字 + 地址（host / host:port），保存到本地设置。</summary>
    private static void ShowAddServerDialog()
    {
        var nameBox = new TextBox
        {
            PlaceholderText = L.T("Home.Servers.NameHint", "显示名字（可留空，用地址）"),
            Width = 320,
        };
        var addressBox = new TextBox
        {
            PlaceholderText = L.T("Home.Servers.AddressHint", "例如：mc.hypixel.net 或 127.0.0.1:25565"),
            Width = 320,
        };

        void Submit()
        {
            var address = addressBox.Text?.Trim() ?? string.Empty;
            if (address.Length == 0)
            {
                Shell.Toast(L.T("Home.Servers.AddressRequired", "请填写服务器地址"));
                return;
            }

            var name = nameBox.Text?.Trim() ?? string.Empty;
            if (name.Length == 0) name = address;

            LocalSettings.Current.QuickServers.Add(new QuickServerEntry { Name = name, Address = address });
            LocalSettings.Save();
            Shell.Toast(L.T("Home.Servers.Added", "已添加 {0}", name));
            CloseActiveDialog(addressBox);
        }

        var addButton = new Button
        {
            Content = L.T("Common.Add", "添加"),
            Classes = { "primary" },
            Height = 34,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            MinWidth = 96,
        };
        addButton.Click += (_, _) => Submit();

        var cancelButton = new Button
        {
            Content = L.T("Common.Cancel", "取消"),
            Classes = { "ghost" },
            Height = 34,
            MinWidth = 80,
        };
        cancelButton.Click += (_, _) => CloseActiveDialog(cancelButton);

        var form = new StackPanel
        {
            Spacing = 12,
            Width = 340,
            Children =
            {
                new TextBlock
                {
                    Text = L.T("Home.Servers.AddTitle", "添加服务器"),
                    FontSize = 15,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeTokens.Brush("NekoTextBrush"),
                },
                nameBox,
                addressBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancelButton, addButton },
                },
            },
        };

        addressBox.KeyDown += (_, args) =>
        {
            if (args.Key == Avalonia.Input.Key.Enter) Submit();
        };

        Shell.Dialog(form, L.T("Home.Servers.AddTitle", "添加服务器"));
    }

    /// <summary>弹窗内容里的按钮：取它所在的 SukiDialogHost 关掉当前弹窗。</summary>
    private static void CloseActiveDialog(object? source)    {
        if (source is Visual visual)
        {
            var host = visual.FindAncestorOfType<SukiDialogHost>(true);
            host?.Manager?.DismissDialog();
        }
    }

    /// <summary>一行服务器：图标（favicon）/ 名字 / 彩色 MOTD / 在线人数 / 一键进服 / 删除。</summary>
    private sealed class ServerRowPanel : Border
    {
        private readonly QuickServerEntry _server;
        private readonly Action _refresh;
        private readonly Border _iconHost;
        private readonly TextBlock _nameText;
        private readonly TextBlock _motdHost;
        private readonly Border _playersPill;
        private readonly TextBlock _playersText;
        private readonly ToolTip _toolTip = new();

        internal ServerRowPanel(QuickServerEntry server, Action refresh)
        {
            _server = server;
            _refresh = refresh;

            _nameText = WidgetChrome.Text(server.Name, WidgetChrome.BodySize, "NekoTextBrush", FontWeight.SemiBold);
            _nameText.TextTrimming = TextTrimming.CharacterEllipsis;
            _nameText.MaxLines = 1;

            _motdHost = new TextBlock
            {
                FontSize = WidgetChrome.CaptionSize + 0.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                Foreground = ThemeTokens.Brush("NekoMutedBrush"),
            };

            _playersText = WidgetChrome.Text("…", 11, "NekoAccentBrush", FontWeight.SemiBold);
            _playersPill = WidgetChrome.Pill("…", "NekoDimBrush", "NekoFieldSoftBrush");
            _playersPill.Child = _playersText;
            ToolTip.SetTip(this, _toolTip);

            _iconHost = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(8),
                Background = ThemeTokens.Brush("NekoFieldBrush"),
                Child = WidgetChrome.Icon("IconFluentServer", 18, "NekoAccentBrush"),
            };

            var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(_nameText);
            texts.Children.Add(_motdHost);

            var join = WidgetChrome.IconButton("IconPlay",
                L.T("Home.Servers.JoinTip", "启动游戏并进入 {0}", server.Name), Join, 26);
            var remove = WidgetChrome.IconButton("IconFluentDismiss",
                L.T("Home.Servers.RemoveTip", "移除 {0}", server.Name), Remove, 22);

            var trailing = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
            };
            trailing.Children.Add(_playersPill);
            trailing.Children.Add(join);
            trailing.Children.Add(remove);

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            row.Children.Add(_iconHost);
            Grid.SetColumn(texts, 1);
            texts.Margin = new Thickness(8, 0, 6, 0);
            row.Children.Add(texts);
            Grid.SetColumn(trailing, 2);
            row.Children.Add(trailing);

            Background = ThemeTokens.Brush("NekoFieldSoftBrush");
            CornerRadius = new CornerRadius(8);
            Padding = new Thickness(8, 6);
            Child = row;
        }

        /// <summary>后台 ping 并把结果刷回 UI；onOnline 在服务器在线时回调一次。</summary>
        internal async Task PingAsync(Action onOnline)
        {
            var result = await ServerListPing.PingAsync(_server.Address).ConfigureAwait(true);
            if (result.Online) onOnline();

            _playersText.Text = result.Online
                ? L.T("Home.Servers.Players", "{0}/{1}", result.OnlinePlayers, result.MaxPlayers)
                : L.T("Home.Servers.Offline", "离线");
            _playersText.Foreground = ThemeTokens.Brush(result.Online ? "NekoSuccessBrush" : "NekoDimBrush");

            _toolTip.Content = result.Online && result.VersionName is { } version
                ? L.T("Home.Servers.ToolTip", "{0} · {1}", _server.Address, version)
                : _server.Address;

            if (result.FaviconPng is { Length: > 0 } png)
            {
                try
                {
                    using var stream = new MemoryStream(png);
                    _iconHost.Child = new Image
                    {
                        Source = new Bitmap(stream),
                        Stretch = Stretch.Uniform,
                        Width = 28,
                        Height = 28,
                    };
                }
                catch
                {
                    // 图标解码失败时保留默认图标
                }
            }

            MotdRenderer.Apply(_motdHost, result.Motd, new MotdOptions
            {
                FontSize = WidgetChrome.CaptionSize + 0.5,
                MaxLines = 1,
                Wrapping = TextWrapping.NoWrap,
                Foreground = ThemeTokens.Brush("NekoMutedBrush"),
            });
            _motdHost.TextTrimming = TextTrimming.CharacterEllipsis;
        }

        private void Join()
        {
            var (host, port) = ServerListPing.ParseAddress(_server.Address);
            var arguments = GameLaunch.BuildServerArguments(host, port);
            WidgetQuickLaunch.Launch(
                null,
                arguments,
                L.T("Home.Servers.Joining", "正在启动并进入 {0}", _server.Name));
        }

        private void Remove()
        {
            LocalSettings.Current.QuickServers.RemoveAll(entry =>
                string.Equals(entry.Address, _server.Address, StringComparison.Ordinal)
                && string.Equals(entry.Name, _server.Name, StringComparison.Ordinal));
            LocalSettings.Save();
            Shell.Toast(L.T("Home.Servers.Removed", "已移除 {0}", _server.Name));
            _refresh();
        }
    }
}
