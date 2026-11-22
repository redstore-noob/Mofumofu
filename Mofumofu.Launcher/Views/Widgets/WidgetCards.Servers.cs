using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
using Mofumofu.Launcher.Controls;

namespace Mofumofu.Launcher.Views.Widgets;

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

        void JoinWith(QuickServerEntry server, string instanceId)
        {
            var (host, port) = ServerListPing.ParseAddress(server.Address);
            var arguments = GameLaunch.BuildServerArguments(host, port);
            WidgetQuickLaunch.Launch(
                instanceId,
                arguments,
                L.T("Home.Servers.Joining", "正在启动并进入 {0}", server.Name));
        }

        async void ShowVersionPicker(QuickServerEntry server, Control anchor)
        {
            IReadOnlyList<WidgetBackend.InstanceInfo> instances;
            var failed = false;
            try
            {
                instances = await WidgetBackend.LoadInstancesAsync();
            }
            catch
            {
                instances = Array.Empty<WidgetBackend.InstanceInfo>();
                failed = true;
            }

            var title = WidgetChrome.Text(server.Name, 13, "NekoTextBrush", FontWeight.SemiBold);
            title.MaxLines = 1;
            title.TextTrimming = TextTrimming.CharacterEllipsis;

            var hint = WidgetChrome.Text(
                L.T("Home.Servers.ChooseVersionHint", "用哪个游戏版本进入 {0}？", server.Address),
                WidgetChrome.CaptionSize,
                "NekoDimBrush");

            var list = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 2) };
            var scroll = new ScrollViewer
            {
                Content = list,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 220,
            };

            var cancel = new Button
            {
                Content = L.T("Common.Cancel", "取消"),
                Classes = { "ghost" },
                Height = 30,
                Padding = new Thickness(14, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 8, 0, 0),
            };

            var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
            body.Children.Add(title);
            Grid.SetRow(hint, 1);
            hint.Margin = new Thickness(0, 3, 0, 0);
            body.Children.Add(hint);
            Grid.SetRow(scroll, 2);
            body.Children.Add(scroll);
            Grid.SetRow(cancel, 3);
            body.Children.Add(cancel);

            var panel = MofuCard.Popover(body, new Thickness(12, 10, 12, 10));
            panel.Width = 300;
            panel.MaxHeight = 340;

            cancel.Click += (_, _) => MofuPopover.Close(panel);

            if (instances.Count == 0)
            {
                list.Children.Add(WidgetChrome.Empty(failed
                    ? L.T("Home.Servers.VersionsFailed", "读取版本列表失败")
                    : L.T("Home.Servers.NoVersions", "还没有安装任何游戏版本")));

                if (!failed)
                {
                    list.Children.Add(WidgetChrome.LinkButton(
                        L.T("Home.Servers.GoDownload", "前往下载页"),
                        () =>
                        {
                            MofuPopover.Close(panel);
                            Shell.Go("download");
                        },
                        "IconFluentArrowDown"));
                }
            }
            else
            {
                foreach (var instance in instances.Take(8))
                {
                    var item = WidgetChrome.ListItem(
                        "IconFluentCube",
                        instance.Id,
                        L.T("Home.Servers.InstanceMeta", "{0} · {1}", instance.Loader, instance.DisplayVersion),
                        WidgetChrome.Icon("IconPlay", 12, "NekoAccentBrush"),
                        24);

                    var choose = new Button
                    {
                        Content = item,
                        Classes = { "ghost" },
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Padding = new Thickness(8, 5),
                        Background = ThemeTokens.Brush("NekoFieldSoftBrush"),
                        CornerRadius = new CornerRadius(7),
                        BorderThickness = new Thickness(0),
                    };
                    choose.Click += (_, _) =>
                    {
                        MofuPopover.Close(panel);
                        JoinWith(server, instance.Id);
                    };
                    list.Children.Add(choose);
                }
            }

            MofuPopover.Show(anchor, panel, dismissOnBackdrop: true);
        }

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
                    var row = new ServerRowPanel(server, Refresh, ShowVersionPicker);
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

    private static void ShowAddServerDialog(Control anchor)
    {
        var nameBox = MofuDialog.Input(hint: L.T("Home.Servers.NameHint", "显示名字（可留空，用地址）"));
        var addressBox = MofuDialog.Input(hint: L.T("Home.Servers.AddressHint", "例如：mc.hypixel.net 或 127.0.0.1:25565"));

        var form = MofuDialog.Stack();

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
            MofuDialog.Close(form);
        }

        var cancelButton = MofuDialog.Secondary(L.T("Common.Cancel", "取消"), () => MofuDialog.Close(form));
        var addButton = MofuDialog.Primary(L.T("Common.Add", "添加"), Submit);

        form.Children.Add(MofuDialog.Field(L.T("Home.Servers.NameLabel", "显示名字"), nameBox));
        form.Children.Add(MofuDialog.Field(L.T("Home.Servers.AddressLabel", "服务器地址"), addressBox));
        form.Children.Add(MofuDialog.Footer(cancelButton, addButton));

        MofuDialog.SubmitOnEnter(addressBox, Submit);

        Shell.Dialog(form, L.T("Home.Servers.AddTitle", "添加服务器"), anchor);
    }

    private static void CloseActiveDialog(object? source)    {
        if (source is Visual visual)
        {
            var host = visual.FindAncestorOfType<MofuDialogHost>(true);
            host?.Manager?.DismissDialog();
        }
    }

    private sealed class ServerRowPanel : Border
    {
        private static readonly Regex ServerVersionRegex = new(
            @"(?:1\.\d+(?:\.\d+)?(?:[-_ ]?(?:pre|rc)\d+)?|\d+\.\d+\.\d+|\d{2}w\d{2}[a-z])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly (string Token, string Label)[] ServerLoaderTokens =
        [
            ("neoforge", "NeoForge"),
            ("lite loader", "LiteLoader"),
            ("liteloader", "LiteLoader"),
            ("quilt", "Quilt"),
            ("fabric", "Fabric"),
            ("forge", "Forge"),
            ("purpur", "Purpur"),
            ("folia", "Folia"),
            ("paper", "Paper"),
            ("spigot", "Spigot"),
            ("bukkit", "Bukkit"),
            ("mohist", "Mohist"),
            ("arclight", "Arclight"),
            ("catserver", "CatServer"),
            ("leaves", "Leaves"),
            ("velocity", "Velocity"),
            ("waterfall", "Waterfall"),
            ("bungeecord", "BungeeCord"),
            ("vanilla", "原版"),
        ];

        private readonly QuickServerEntry _server;
        private readonly Action _refresh;
        private readonly Action<QuickServerEntry, Control> _joinRequested;
        private readonly Border _iconHost;
        private readonly TextBlock _nameText;
        private readonly TextBlock _motdHost;
        private readonly Border _playersPill;
        private readonly TextBlock _playersText;
        private readonly StackPanel _versionRow;
        private readonly Border _loaderPill;
        private readonly TextBlock _loaderText;
        private readonly Border _mcPill;
        private readonly TextBlock _mcText;
        private readonly ToolTip _toolTip = new();

        internal ServerRowPanel(QuickServerEntry server, Action refresh, Action<QuickServerEntry, Control> joinRequested)
        {
            _server = server;
            _refresh = refresh;
            _joinRequested = joinRequested;

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
            _playersPill.HorizontalAlignment = HorizontalAlignment.Right;
            ToolTip.SetTip(this, _toolTip);

            var (loaderPill, loaderText) = MiniPill();
            var (mcPill, mcText) = MiniPill();
            _loaderPill = loaderPill;
            _loaderText = loaderText;
            _mcPill = mcPill;
            _mcText = mcText;

            _versionRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Right,
                IsVisible = false,
                Children = { _loaderPill, _mcPill },
            };

            _iconHost = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(8),
                Background = ThemeTokens.Brush("NekoFieldBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Child = WidgetChrome.Icon("IconFluentServer", 18, "NekoAccentBrush"),
            };

            var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(_nameText);
            texts.Children.Add(_motdHost);

            Button? joinButton = null;
            var join = WidgetChrome.IconButton("IconPlay",
                L.T("Home.Servers.JoinTip", "启动游戏并进入 {0}", server.Name),
                () =>
                {
                    if (joinButton is not null) _joinRequested(_server, joinButton);
                }, 26);
            joinButton = join;
            var remove = WidgetChrome.IconButton("IconFluentDismiss",
                L.T("Home.Servers.RemoveTip", "移除 {0}", server.Name), Remove, 22);

            var stats = new StackPanel
            {
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children = { _playersPill, _versionRow },
            };

            var trailing = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
            };
            trailing.Children.Add(stats);
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

            var (loader, mcVersion) = ParseServerVersion(result.Online ? result.VersionName : null);
            _loaderText.Text = loader ?? string.Empty;
            _mcText.Text = mcVersion ?? string.Empty;
            _loaderPill.IsVisible = loader is not null;
            _mcPill.IsVisible = mcVersion is not null;
            _versionRow.IsVisible = loader is not null || mcVersion is not null;

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
                }
            }

            var multiLine = CountMotdLines(result.Motd) > 1;
            _motdHost.MaxLines = multiLine ? 0 : 1;
            _motdHost.TextTrimming = multiLine ? TextTrimming.None : TextTrimming.CharacterEllipsis;
            MotdRenderer.Apply(_motdHost, result.Motd, new MotdOptions
            {
                FontSize = WidgetChrome.CaptionSize + 0.5,
                MaxLines = multiLine ? 0 : 1,
                Wrapping = multiLine ? TextWrapping.Wrap : TextWrapping.NoWrap,
                Foreground = ThemeTokens.Brush("NekoMutedBrush"),
            });
        }

        private static (Border Host, TextBlock Text) MiniPill()
        {
            var text = new TextBlock
            {
                Text = string.Empty,
                FontSize = WidgetChrome.CaptionSize - 0.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeTokens.Brush("NekoMutedBrush"),
            };

            var host = new Border
            {
                Background = ThemeTokens.Brush("NekoFieldBrush"),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(5, 1, 5, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = text,
            };

            return (host, text);
        }

        private static int CountMotdLines(string? motd)
        {
            var text = MotdRenderer.Strip(motd);
            if (text.Length == 0) return 0;

            var lines = 1;
            foreach (var ch in text)
            {
                if (ch == '\n') lines++;
            }

            return lines;
        }

        private static (string? Loader, string? McVersion) ParseServerVersion(string? versionName)
        {
            var name = (versionName ?? string.Empty).Trim();
            if (name.Length == 0) return (null, null);

            string? loader = null;
            foreach (var (token, label) in ServerLoaderTokens)
            {
                if (!name.Contains(token, StringComparison.OrdinalIgnoreCase)) continue;
                loader = label;
                break;
            }

            var tokens = ServerVersionRegex.Matches(name).Select(match => match.Value).ToList();

            string? loaderVersion = null;
            string? mcVersion = null;

            if (loader is not null)
            {
                if (tokens.Count >= 2)
                {
                    loaderVersion = tokens[0];
                    mcVersion = tokens[^1];
                }
                else if (tokens.Count == 1)
                {
                    if (LooksLikeMinecraftVersion(tokens[0])) mcVersion = tokens[0];
                    else loaderVersion = tokens[0];
                }
            }
            else if (tokens.Count >= 1)
            {
                mcVersion = tokens[^1];
            }

            var loaderText = loader is null
                ? null
                : loaderVersion is null ? loader : $"{loader} {loaderVersion}";

            return (loaderText, mcVersion);
        }

        private static bool LooksLikeMinecraftVersion(string value) =>
            value.StartsWith("1.", StringComparison.Ordinal)
            || (value.Length >= 6 && value[2] == 'w');

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
