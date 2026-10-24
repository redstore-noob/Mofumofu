using Mofumofu.Launcher.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Xml;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using Avalonia.VisualTree;
using SukiUI.Controls;
using MofuCore.Tools;
using CoreServerFiles = MofuCore.Server.ServerFiles;
using CoreFileEntry = MofuCore.Server.ServerFileEntry;
using CoreRewind = MofuCore.Tools.RewindSystem;
using CoreRewindKind = MofuCore.Tools.RewindKind;
using CoreRewindSnapshot = MofuCore.Tools.RewindSnapshot;

using Mofumofu.Launcher.Models;
using Mofumofu.Launcher.Mock;
using Mofumofu.Launcher.Services;
using L = Mofumofu.Launcher.Localization.Localization;
using CoreBackup = MofuCore.Server.ServerBackup;
using CoreBackupInfo = MofuCore.Server.ServerBackupInfo;
using CoreCreateOptions = MofuCore.Server.CreateOptions;
using CoreInstallOptions = MofuCore.Server.ServerDownload.InstallOptions;
using CoreInstallProgress = MofuCore.Server.ServerDownload.InstallProgress;
using CoreOpEntry = MofuCore.Server.OpEntry;
using CorePlayers = MofuCore.Server.ServerPlayers;
using CoreProperties = MofuCore.Server.ServerProperties;
using CoreProperty = MofuCore.Server.ServerProperty;
using CoreServer = MofuCore.Server.ServerManage;
using CoreServerDownload = MofuCore.Server.ServerDownload;
using CoreServerInfo = MofuCore.Server.ServerInfo;
using CoreServerPackage = MofuCore.Server.ServerPackage;
using CoreWhitelistEntry = MofuCore.Server.WhitelistEntry;

namespace Mofumofu.Launcher.Views;

/// <summary>
/// 服务器页：顶部统计 chips + 左栏服务器列表 + 右栏详情（启停、玩家、白名单/OP、控制台、备份）。
/// 列表全部来自 MofuCore 的本机服务器实例，没有示例数据。
/// </summary>
public partial class ServersView : UserControl
{

    private readonly List<ServerRow> _allServers = new();
    private readonly ObservableCollection<ServerRow> _visibleServers = new();
    private readonly ObservableCollection<ConsoleRow> _consoleRows = new();
    private readonly DispatcherTimer _refreshTimer;

    private bool _ready;
    private bool _localMode;
    private bool _playerRefreshBusy;
    private int _playerRefreshToken;
    private int _refreshTick;
    private string? _selectedServerId;
    private string _activeDetailTab = "overview";
    private string _fileRelativePath = string.Empty;
    private string? _openFileRelativePath;
    private bool _editorDirty;
    private static bool _customHighlightingReady;
    private readonly Dictionary<string, Func<string>> _propertyGetters = new(StringComparer.OrdinalIgnoreCase);

    public ServersView()
    {
        InitializeComponent();

        ConsoleList.ItemsSource = _consoleRows;
        FileEditor.TextArea.Caret.PositionChanged += (_, _) => UpdateEditorCaret();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += OnRefreshTick;
        _refreshTimer.Start();

        LoadServers();
        RefreshServerList();
        ServerList.SelectedIndex = _visibleServers.Count > 0 ? 0 : -1;

        _ready = true;
        UpdateChips();

        if (ServerList.SelectedItem is ServerRow selected)
        {
            LoadDetail(selected);
        }
    }

    // ---------------- 数据源：本机真实服务器 / Mock 回退 ----------------

    private bool LoadServers()
    {
        _allServers.Clear();
        _localMode = false;

        try
        {
            IReadOnlyList<CoreServerInfo> infos = CoreServer.ListServerInfos();
            if (infos.Count > 0)
            {
                _localMode = true;
                foreach (CoreServerInfo info in infos)
                {
                    _allServers.Add(ServerRow.FromCore(info, ReadMotd(info.Id)));
                }
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine($"[servers] ListServerInfos failed: {e.Message}");
        }

        FallbackHint.IsVisible = false;
        return _localMode;
    }

    private static string ReadMotd(string serverId)
    {
        try
        {
            foreach (CoreProperty property in CoreProperties.Get(serverId))
            {
                if (string.Equals(property.Key, "motd", StringComparison.OrdinalIgnoreCase))
                {
                    return string.IsNullOrWhiteSpace(property.Value) ? "—" : property.Value;
                }
            }
        }
        catch (Exception)
        {
            // server.properties 尚未生成时忽略
        }

        return "—";
    }

    // ---------------- 列表 / 统计 ----------------

    private void RefreshServerList()
    {
        var query = (ServerSearch.Text ?? string.Empty).Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? _allServers
            : _allServers.Where(s => s.Matches(query)).ToList();

        var selectedId = (ServerList.SelectedItem as ServerRow)?.ServerId;
        var selectedName = (ServerList.SelectedItem as ServerRow)?.Name;

        _visibleServers.Clear();
        foreach (ServerRow server in filtered)
        {
            _visibleServers.Add(server);
        }

        ServerList.ItemsSource = _visibleServers;
        CountText.Text = $"{filtered.Count} / {_allServers.Count}";

        var index = selectedId is not null
            ? filtered.FindIndex(s => s.ServerId == selectedId)
            : filtered.FindIndex(s => s.Name == selectedName);
        if (index >= 0)
        {
            ServerList.SelectedIndex = index;
        }
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        RefreshServerList();
        if (ServerList.SelectedIndex < 0 && _visibleServers.Count > 0) ServerList.SelectedIndex = 0;
    }

    private void UpdateChips()
    {
        var running = _allServers.Count(s => s.Online);
        var stopped = _allServers.Count - running;
        var players = _allServers.Where(s => s.Online).Sum(s => s.Players);

        RunningChipText.Text = L.T("Servers.Chip.Running", "运行中 {0}", running);
        TotalChipText.Text = L.T("Servers.Chip.Total", "共 {0} 台服务器", _allServers.Count);
        StoppedChipText.Text = L.T("Servers.Chip.Stopped", "已停止 {0}", stopped);
        PlayersChipText.Text = L.T("Servers.Chip.Players", "在线玩家 {0}", players);
        Header.Subtitle = _localMode
            ? L.T("Servers.Page.SubtitleLocal", "{0} 台服务器 · {1} 台运行中 · {2} 名玩家在线",
                _allServers.Count, running, players)
            : L.T("Servers.Page.NoLocal", "未找到本机服务器");
    }

    private void OnServerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (ServerList.SelectedItem is ServerRow row) LoadDetail(row);
    }

    // ---------------- 详情 ----------------

    private void LoadDetail(ServerRow row)
    {
        _selectedServerId = row.ServerId;

        DetailName.Text = row.Name;
        DetailAddress.Text = row.Address;
        DetailAddress2.Text = row.Address;
        DetailVersion.Text = row.Version;
        DetailMotd.Text = row.Motd;
        DetailPlayers.Text = L.T("Servers.Detail.PlayersCount", "{0} / {1} 名玩家", row.Players, row.MaxPlayers);
        ApplyStatusVisuals(row);
        UpdateToggleVisual(row);

        ClearConsole();
        if (row.ServerId is { } id)
        {
            LoadConsoleHistory(id);
            RefreshOnlinePlayers(row);
            RefreshBackups(row);
        }
        else
        {
            BackupSection.IsVisible = false;
            RebuildUnavailablePlayers(row);
        }

        ScrollConsoleToEnd();

        // 详情切换：内容轻微上滑淡入（QA 关动画时内容直接可见）
        UiAnimations.Reset(DetailBody);
        UiAnimations.FadeSlideIn(DetailBody, 0.995, 6, 170);
    }

    private void ApplyStatusVisuals(ServerRow row)
    {
        DetailStatus.Text = row.StatusText;
        DetailBadge.Background = row.StatusBg;
        DetailStatus.Foreground = row.StatusFg;
    }

    private void UpdateToggleVisual(ServerRow row)
    {
        var online = row.Online;
        ToggleIcon.Kind = online ? "IconStop" : "IconPlay";
        ToggleText.Text = online
            ? L.T("Servers.Detail.Stop", "停止服务")
            : L.T("Servers.Detail.Start", "启动服务");
        ToggleButton.Classes.Set("primary", !online);
        ToggleButton.Classes.Set("ghost", online);
    }

    // ---------------- 控制台 ----------------

    private void ClearConsole()
    {
        _consoleRows.Clear();
    }

    /// <summary>首次进入时用 Poll 的环形缓冲回填历史日志，之后实时行走 ConsoleLine 事件。</summary>
    private void LoadConsoleHistory(string serverId)
    {
        try
        {
            var snapshot = CoreServer.Poll(serverId, 0);
            foreach (string line in snapshot.LogLines)
            {
                AppendConsoleLine(line);
            }

            if (_consoleRows.Count == 0)
            {
                AppendSystemLine(L.T("Servers.Console.Empty",
                    "暂无控制台输出（服务器未启动或尚未产生日志）"));
            }
        }
        catch (Exception e)
        {
            AppendSystemLine(L.T("Servers.Console.LoadFailed", "读取控制台日志失败：{0}", e.Message));
        }
    }

    private void OnCoreConsoleLine(string serverId, string line)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnCoreConsoleLine(serverId, line));
            return;
        }

        if (!_ready || !_localMode) return;
        if (serverId != _selectedServerId) return;
        AppendConsoleLine(line);
    }

    private void AppendConsoleLine(string line)
    {
        _consoleRows.Add(new ConsoleRow(line));
        if (_consoleRows.Count > 600)
        {
            _consoleRows.RemoveAt(0);
        }

        ScrollConsoleToEnd();
    }

    private void AppendSystemLine(string text)
    {
        _consoleRows.Add(ConsoleRow.System(text));
        ScrollConsoleToEnd();
    }

    private void ScrollConsoleToEnd()
    {
        Dispatcher.UIThread.Post(() => ConsoleScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private async void OnConsoleSendClick(object? sender, RoutedEventArgs e) =>
        await SendConsoleInputAsync();

    private async void OnConsoleInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SendConsoleInputAsync();
    }

    private async Task SendConsoleInputAsync()
    {
        if (ServerList.SelectedItem is not ServerRow row) return;

        var command = (ConsoleInput.Text ?? string.Empty).Trim();
        if (command.Length == 0) return;

        ConsoleInput.Text = string.Empty;

        if (row.ServerId is not { } id) return;

        AppendSystemLine(L.T("Servers.Console.CommandEcho", "> {0}", command));
        try
        {
            await CoreServer.SendCommandAsync(id, command);
        }
        catch (Exception e)
        {
            AppendSystemLine(e.Message);
        }
    }

    // ---------------- 玩家 / 白名单 / OP ----------------

    private void RebuildUnavailablePlayers(ServerRow row)
    {
        PlayersPanel.Children.Clear();
        PlayersPanel.Children.Add(new TextBlock
        {
            Text = row.Online
                ? L.T("Servers.Players.Unavailable", "在线玩家暂不可用（RCON 未连接）")
                : L.T("Servers.Players.ServerStopped", "服务器已停止"),
            FontSize = 11,
            Foreground = ThemeTokens.Brush("NekoMutedBrush"),
            Margin = new Thickness(2, 4, 0, 0),
        });
    }

    private void RebuildOnlinePlayers(ServerRow row, IReadOnlyList<string> online)
    {
        PlayersPanel.Children.Clear();
        DetailPlayers.Text = L.T("Servers.Detail.PlayersCount", "{0} / {1} 名玩家", row.Players, row.MaxPlayers);

        if (!row.Online)
        {
            PlayersPanel.Children.Add(new TextBlock
            {
                Text = L.T("Servers.Players.ServerStopped", "服务器已停止"),
                FontSize = 11,
                Foreground = ThemeTokens.Brush("NekoMutedBrush"),
                Margin = new Thickness(2, 4, 0, 0),
            });
            return;
        }

        if (online.Count == 0)
        {
            PlayersPanel.Children.Add(new TextBlock
            {
                Text = L.T("Servers.Players.NoneOnline", "暂无在线玩家"),
                FontSize = 11,
                Foreground = ThemeTokens.Brush("NekoMutedBrush"),
                Margin = new Thickness(2, 4, 0, 0),
            });
            return;
        }

        foreach (string name in online)
        {
            AddPlayerChip(name);
        }
    }

    private void AddPlayerChip(string name)
    {
        var chip = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 4, 9, 4),
            Background = ThemeTokens.Brush("NekoFieldSoftBrush"),
            Margin = new Thickness(0, 0, 6, 6),
            Child = new TextBlock
            {
                Text = name,
                FontSize = 11,
                Foreground = ThemeTokens.Brush("NekoTextBrush"),
            },
        };

        PlayersPanel.Children.Add(chip);
        UiAnimations.FadeSlideIn(chip, 0.9, 4, 140);
    }

    /// <summary>在线玩家名来自 ServerPlayers.Get（RCON / 名单），放后台线程避免阻塞 UI。</summary>
    private void RefreshOnlinePlayers(ServerRow row)
    {
        if (row.ServerId is not { } id)
        {
            RebuildUnavailablePlayers(row);
            return;
        }
        if (_playerRefreshBusy) return;

        _playerRefreshBusy = true;
        var token = ++_playerRefreshToken;
        _ = Task.Run(() =>
        {
            List<string> online = [];
            try
            {
                online = CorePlayers.Get(id).Online;
            }
            catch (Exception)
            {
                // 服务器未运行或 RCON 不可达时保持空列表
            }

            Dispatcher.UIThread.Post(() =>
            {
                _playerRefreshBusy = false;
                if (token != _playerRefreshToken) return;
                if (ServerList.SelectedItem is not ServerRow current || current.ServerId != id) return;
                RebuildOnlinePlayers(current, online);
            });
        });
    }

    private async void OnWhitelistAddClick(object? sender, RoutedEventArgs e) =>
        await ModifyWhitelistAsync(true);

    private async void OnWhitelistRemoveClick(object? sender, RoutedEventArgs e) =>
        await ModifyWhitelistAsync(false);

    private async Task ModifyWhitelistAsync(bool add)
    {
        if (ServerList.SelectedItem is not ServerRow row) return;
        if (row.ServerId is not { } id) return;

        var name = (PlayerNameInput.Text ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            Shell.Toast(L.T("Servers.Toast.EnterPlayerName", "先在输入框里填玩家名"));
            return;
        }

        try
        {
            if (CoreServer.IsRunning(id))
            {
                await CorePlayers.RunCommandAsync(id, $"whitelist {(add ? "add" : "remove")} {name}");
            }
            else
            {
                var summary = await Task.Run(() => CorePlayers.Get(id));
                List<CoreWhitelistEntry> entries = summary.Whitelist.ToList();
                CoreWhitelistEntry? existing = entries.FirstOrDefault(entry =>
                    string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
                if (add)
                {
                    if (existing is null)
                    {
                        entries.Add(new CoreWhitelistEntry { Name = name });
                    }
                }
                else if (existing is not null)
                {
                    entries.Remove(existing);
                }

                CorePlayers.SaveWhitelist(id, entries);
            }

            PlayerNameInput.Text = string.Empty;
            Shell.Toast(add
                ? L.T("Servers.Toast.WhitelistAdded", "已加入白名单：{0}", name)
                : L.T("Servers.Toast.WhitelistRemoved", "已移出白名单：{0}", name));
        }
        catch (Exception e)
        {
            Shell.Toast(e.Message);
        }
    }

    private async void OnOpAddClick(object? sender, RoutedEventArgs e) => await ModifyOpAsync(true);

    private async void OnOpRemoveClick(object? sender, RoutedEventArgs e) => await ModifyOpAsync(false);

    private async Task ModifyOpAsync(bool add)
    {
        if (ServerList.SelectedItem is not ServerRow row) return;
        if (row.ServerId is not { } id) return;

        var name = (PlayerNameInput.Text ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            Shell.Toast(L.T("Servers.Toast.EnterPlayerName", "先在输入框里填玩家名"));
            return;
        }

        try
        {
            if (CoreServer.IsRunning(id))
            {
                await CorePlayers.RunCommandAsync(id, $"{(add ? "op" : "deop")} {name}");
            }
            else
            {
                var summary = await Task.Run(() => CorePlayers.Get(id));
                List<CoreOpEntry> entries = summary.Ops.ToList();
                CoreOpEntry? existing = entries.FirstOrDefault(entry =>
                    string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
                if (add)
                {
                    if (existing is null)
                    {
                        entries.Add(new CoreOpEntry { Name = name, Level = 4 });
                    }
                }
                else if (existing is not null)
                {
                    entries.Remove(existing);
                }

                CorePlayers.SaveOps(id, entries);
            }

            PlayerNameInput.Text = string.Empty;
            Shell.Toast(add
                ? L.T("Servers.Toast.OpAdded", "已设为 OP：{0}", name)
                : L.T("Servers.Toast.OpRemoved", "已取消 OP：{0}", name));
        }
        catch (Exception e)
        {
            Shell.Toast(e.Message);
        }
    }

    // ---------------- 备份 ----------------

    private void RefreshBackups(ServerRow row)
    {
        BackupPanel.Children.Clear();

        if (row.ServerId is not { } id)
        {
            BackupSection.IsVisible = false;
            return;
        }

        BackupSection.IsVisible = true;

        IReadOnlyList<CoreBackupInfo> backups;
        try
        {
            backups = CoreBackup.List(id);
        }
        catch (Exception)
        {
            backups = [];
        }

        if (backups.Count == 0)
        {
            BackupPanel.Children.Add(new TextBlock
            {
                Text = L.T("Servers.Backup.Empty", "暂无备份，点下方「备份世界」创建"),
                FontSize = 11,
                Foreground = ThemeTokens.Brush("NekoMutedBrush"),
                Margin = new Thickness(2, 2, 0, 0),
            });
            return;
        }

        foreach (CoreBackupInfo backup in backups.Take(3))
        {
            var name = new TextBlock
            {
                Text = $"{backup.Name} · {FormatSize(backup.SizeBytes)}",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var restore = new Button
            {
                Content = L.T("Servers.Backup.Restore", "恢复"),
                Classes = { "ghost" },
                Height = 26,
                Padding = new Thickness(9, 0, 9, 0),
                FontSize = 10.5,
                Tag = backup.Name,
            };
            var delete = new Button
            {
                Content = L.T("Servers.Backup.Delete", "删除"),
                Classes = { "ghost" },
                Height = 26,
                Padding = new Thickness(9, 0, 9, 0),
                FontSize = 10.5,
                Tag = backup.Name,
            };
            restore.Click += OnRestoreBackupClick;
            delete.Click += OnDeleteBackupClick;

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { restore, delete },
            };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(name);
            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);
            BackupPanel.Children.Add(grid);
        }
    }

    private async void OnRestoreBackupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string backupName }) return;
        if (ServerList.SelectedItem is not ServerRow { ServerId: { } id } row) return;

        try
        {
            Shell.Toast(L.T("Servers.Toast.Restoring", "正在恢复备份 {0}…", backupName));
            await CoreBackup.RestoreAsync(id, backupName);
            Shell.Toast(L.T("Servers.Toast.Restored", "备份已恢复（恢复前已自动创建安全备份）"));
            RefreshBackups(row);
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Servers.Toast.RestoreFailed", "恢复失败：{0}", ex.Message));
        }
    }

    private void OnDeleteBackupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string backupName }) return;
        if (ServerList.SelectedItem is not ServerRow { ServerId: { } id } row) return;

        try
        {
            CoreBackup.Delete(id, backupName);
            Shell.Toast(L.T("Servers.Toast.BackupDeleted", "已删除备份 {0}", backupName));
            RefreshBackups(row);
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Servers.Toast.BackupDeleteFailed", "删除备份失败：{0}", ex.Message));
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.#} KB",
        _ => $"{bytes} B",
    };

    // ---------------- 定时刷新（状态 / 在线数 / 日志尾部） ----------------

    private void OnRefreshTick(object? sender, EventArgs e)
    {
        if (!_ready) return;
        if (ServerList.SelectedItem is not ServerRow row) return;
        if (row.ServerId is not { } id) return;

        var status = CoreServer.GetStatus(id);
        var changed = row.Status != status;
        row.Status = status;

        var snapshot = CoreServer.Poll(id, long.MaxValue);
        if (row.Players != snapshot.Players)
        {
            row.Players = snapshot.Players;
            changed = true;
        }

        if (changed)
        {
            ApplyStatusVisuals(row);
            UpdateToggleVisual(row);
            DetailPlayers.Text = L.T("Servers.Detail.PlayersCount", "{0} / {1} 名玩家",
                row.Players, row.MaxPlayers);
            UpdateChips();
        }

        _refreshTick++;
        if (_refreshTick % 5 == 0)
        {
            RefreshOnlinePlayers(row);
        }
    }

    // ---------------- 操作 ----------------

    private async void OnToggleServerClick(object? sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerRow row) return;

        if (row.ServerId is not { } id) return;

        ToggleButton.IsEnabled = false;
        try
        {
            if (row.Online)
            {
                Shell.Toast(L.T("Servers.Toast.Stopping", "正在停止「{0}」…", row.Name));
                await CoreServer.StopAsync(id);
                Shell.Toast(L.T("Servers.Toast.Stopped", "已停止「{0}」", row.Name));
            }
            else
            {
                Shell.Toast(L.T("Servers.Toast.Starting", "正在启动「{0}」…", row.Name));
                await CoreServer.StartAsync(id);
                Shell.Toast(L.T("Servers.Toast.Started", "已启动「{0}」", row.Name));
            }

            row.Status = CoreServer.GetStatus(id);
            ApplyStatusVisuals(row);
            UpdateToggleVisual(row);
            UpdateChips();
            RefreshOnlinePlayers(row);
            RefreshBackups(row);
        }
        catch (Exception ex)
        {
            Shell.Toast(ex.Message);
        }
        finally
        {
            ToggleButton.IsEnabled = true;
        }
    }

    private async void OnRestartClick(object? sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerRow row) return;

        if (row.ServerId is not { } id) return;

        if (!CoreServer.IsRunning(id))
        {
            Shell.Toast(L.T("Servers.Toast.NotRunning", "服务器未在运行"));
            return;
        }

        try
        {
            Shell.Toast(L.T("Servers.Toast.Restarting", "正在重启「{0}」…", row.Name));
            await CoreServer.RestartAsync(id);
            row.Status = CoreServer.GetStatus(id);
            ApplyStatusVisuals(row);
            UpdateToggleVisual(row);
            Shell.Toast(L.T("Servers.Toast.Restarted", "已重启「{0}」", row.Name));
        }
        catch (Exception ex)
        {
            Shell.Toast(ex.Message);
        }
    }

    private void OnCopyAddressClick(object? sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerRow row) return;

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
            {
                _ = clipboard.SetTextAsync(row.Address);
                Shell.Toast(L.T("Servers.Toast.Copied", "已复制服务器地址：{0}", row.Address));
                return;
            }
        }
        catch (Exception)
        {
            // 剪贴板不可用时退回提示
        }

        Shell.Toast(L.T("Servers.Toast.Address", "服务器地址：{0}", row.Address));
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        LoadServers();
        RefreshServerList();
        ServerList.SelectedIndex = _visibleServers.Count > 0 ? 0 : -1;
        UpdateChips();
        if (ServerList.SelectedItem is ServerRow row) LoadDetail(row);

        Shell.Toast(_localMode
            ? L.T("Servers.Toast.Refreshed", "已刷新，共 {0} 台本机服务器", _allServers.Count)
            : L.T("Servers.Toast.NoLocal", "未找到本机服务器"));
    }

    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is not { } storage)
            {
                Shell.Toast(L.T("Servers.Import.NoPicker", "当前环境无法打开文件选择器"));
                return;
            }

            IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = L.T("Servers.Import.Pick", "选择 .mofuser 服务器包"),
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new FilePickerFileType("Mofumofu server package")
                        {
                            Patterns = ["*.mofuser", "*.zip"],
                        },
                    ],
                });
            if (files.Count == 0) return;

            string path = files[0].TryGetLocalPath() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path))
            {
                Shell.Toast(L.T("Servers.Import.NoFile", "无法读取所选文件"));
                return;
            }

            string id = await CoreServerPackage.ImportAsync(path);
            LoadServers();
            RefreshServerList();
            int index = IndexOfVisibleServer(id);
            ServerList.SelectedIndex = index >= 0 ? index : (_visibleServers.Count > 0 ? 0 : -1);
            UpdateChips();
            if (ServerList.SelectedItem is ServerRow imported) LoadDetail(imported);
            Shell.Toast(L.T("Servers.Import.Done", "已导入服务器包：{0}", id));
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Servers.Import.Failed", "导入服务器包失败：{0}", ex.Message));
        }
    }

    private int IndexOfVisibleServer(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return -1;
        for (var index = 0; index < _visibleServers.Count; index++)
        {
            if (string.Equals(_visibleServers[index].ServerId, id, StringComparison.Ordinal)) return index;
        }
        return -1;
    }

    private async void OnCreateServerClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Shell.Toast(L.T("Servers.Create.Loading", "正在获取可用的 Minecraft 版本…"));
            IReadOnlyList<string> versions = await CoreServerDownload.ListMinecraftVersionsAsync(
                CoreServerDownload.CoreKind.Vanilla);
            if (versions.Count == 0)
            {
                Shell.Toast(L.T("Servers.Create.NoVersions", "没有获取到可用的 Minecraft 版本"));
                return;
            }

            var nameBox = new TextBox
            {
                Height = 34,
                Text = L.T("Servers.Create.DefaultName", "Mofu Server"),
            };
            var coreCombo = new ComboBox
            {
                ItemsSource = new List<string>
                {
                    L.T("Servers.Core.Vanilla", "Vanilla（原版）"),
                    "Paper",
                    "Fabric",
                    "NeoForge",
                },
                SelectedIndex = 0,
                Height = 34,
            };
            var versionCombo = new ComboBox
            {
                ItemsSource = versions.Take(100).ToList(),
                SelectedIndex = 0,
                Height = 34,
            };
            var portBox = new TextBox { Height = 34, Text = "25565" };
            var eulaCheck = new CheckBox
            {
                Content = L.T("Servers.Create.Eula", "我已阅读并同意 Minecraft EULA"),
                FontSize = 12,
            };
            var statusText = new TextBlock
            {
                FontSize = 11.5,
                Foreground = ThemeTokens.Brush("NekoMutedBrush"),
                TextWrapping = TextWrapping.Wrap,
            };
            var progressBar = new ProgressBar
            {
                Height = 6,
                Minimum = 0,
                Maximum = 100,
                IsVisible = false,
            };
            var createButton = new Button
            {
                Content = L.T("Servers.Create.Create", "创建服务器"),
                Classes = { "primary" },
                HorizontalAlignment = HorizontalAlignment.Left,
            };

            var panel = new StackPanel { Spacing = 7, Width = 400 };
            void AddLabel(string text) => panel.Children.Add(new TextBlock
            {
                Text = text,
                Classes = { "field-label" },
            });

            AddLabel(L.T("Servers.Create.Name", "服务器名称"));
            panel.Children.Add(nameBox);
            AddLabel(L.T("Servers.Create.Core", "服务端核心"));
            panel.Children.Add(coreCombo);
            AddLabel(L.T("Servers.Create.Version", "Minecraft 版本"));
            panel.Children.Add(versionCombo);
            AddLabel(L.T("Servers.Create.Port", "端口"));
            panel.Children.Add(portBox);
            panel.Children.Add(eulaCheck);
            panel.Children.Add(createButton);
            panel.Children.Add(progressBar);
            panel.Children.Add(statusText);

            createButton.Click += async (_, _) =>
            {
                if (createButton.IsEnabled is false) return;
                if (eulaCheck.IsChecked != true)
                {
                    statusText.Text = L.T("Servers.Create.EulaRequired",
                        "需要先勾选同意 Minecraft EULA 才能创建服务器");
                    return;
                }

                createButton.IsEnabled = false;
                progressBar.IsVisible = true;
                try
                {
                    string name = (nameBox.Text ?? string.Empty).Trim();
                    if (name.Length == 0) name = L.T("Servers.Create.DefaultName", "Mofu Server");
                    string minecraftVersion = versionCombo.SelectedItem as string ?? versions[0];
                    int coreIndex = Math.Max(0, coreCombo.SelectedIndex);
                    (string coreId, CoreServerDownload.CoreKind kind) = coreIndex switch
                    {
                        1 => (CoreServer.CorePaper, CoreServerDownload.CoreKind.Paper),
                        2 => (CoreServer.CoreFabric, CoreServerDownload.CoreKind.Fabric),
                        3 => (CoreServer.CoreNeoForge, CoreServerDownload.CoreKind.NeoForge),
                        _ => (CoreServer.CoreVanilla, CoreServerDownload.CoreKind.Vanilla),
                    };
                    if (!int.TryParse((portBox.Text ?? string.Empty).Trim(), out int port)
                        || port <= 0 || port > 65535)
                    {
                        port = 25565;
                    }

                    string serverId = await CoreServer.CreateServerAsync(
                        new CoreCreateOptions
                        {
                            Name = name,
                            Core = coreId,
                            McVersion = minecraftVersion,
                            Port = port,
                            MaxPlayers = 20,
                            AcceptEula = true,
                        },
                        async (directory, config, token) =>
                        {
                            var task = DownloadTaskService.Start(
                                L.T("Servers.Create.TaskTitle", "服务器核心 · {0}", name),
                                L.T("Servers.Create.Preparing", "正在准备服务端…"), canPause: true);
                            using CancellationTokenSource linked =
                                CancellationTokenSource.CreateLinkedTokenSource(token, task.Token);
                            var progress = new Progress<CoreInstallProgress>(value =>
                            {
                                task.Report(value.Percentage, value.Detail);
                                Dispatcher.UIThread.Post(() =>
                                {
                                    progressBar.Value = value.Percentage;
                                    if (!string.IsNullOrWhiteSpace(value.Detail))
                                    {
                                        statusText.Text = value.Detail;
                                    }
                                });
                            });
                            try
                            {
                                await CoreServerDownload.InstallAsync(
                                    new CoreInstallOptions
                                    {
                                        Core = kind,
                                        MinecraftVersion = minecraftVersion,
                                        CoreVersion = string.Empty,
                                        ServerDirectory = directory,
                                        AcceptEula = true,
                                        Port = config.Port,
                                        MaxPlayers = config.MaxPlayers,
                                        Motd = config.Name,
                                    },
                                    progress,
                                    linked.Token);
                                task.Complete();
                            }
                            catch (OperationCanceledException)
                            {
                                task.MarkCancelled();
                                throw;
                            }
                            catch (Exception ex)
                            {
                                task.Fail(ex.Message);
                                throw;
                            }
                        },
                        CancellationToken.None);

                    statusText.Text = L.T("Servers.Create.Done", "服务器创建完成");
                    LoadServers();
                    RefreshServerList();
                    int index = IndexOfVisibleServer(serverId);
                    ServerList.SelectedIndex = index >= 0 ? index : (_visibleServers.Count > 0 ? 0 : -1);
                    UpdateChips();
                    if (ServerList.SelectedItem is ServerRow created) LoadDetail(created);
                    Shell.Toast(L.T("Servers.Create.Done", "服务器创建完成"));
                }
                catch (Exception ex)
                {
                    statusText.Text = L.T("Servers.Create.Failed", "创建服务器失败：{0}", ex.Message);
                }
                finally
                {
                    createButton.IsEnabled = true;
                    progressBar.IsVisible = false;
                }
            };

            Shell.Dialog(panel, L.T("Servers.Create.Title", "新建服务器"));
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Servers.Create.Failed", "创建服务器失败：{0}", ex.Message));
        }
    }

    private async void OnBackupClick(object? sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerRow row) return;
        if (row.ServerId is not { } id) return;

        try
        {
            Shell.Toast(L.T("Servers.Toast.BackingUp", "正在创建备份…"));
            CoreBackupInfo info = await CoreBackup.CreateAsync(id);
            Shell.Toast(L.T("Servers.Toast.BackupDone", "备份完成：{0}", info.Name));
            RefreshBackups(row);
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Servers.Toast.BackupFailed", "备份失败：{0}", ex.Message));
        }
    }

    private void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerRow row) return;
        if (row.ServerId is not { } id) return;

        try
        {
            var directory = Path.GetFullPath(CoreServer.ServerDirectory(id));
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            Shell.Toast(L.T("Servers.Toast.OpenedFolder", "已打开服务器目录：{0}", directory));
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Servers.Toast.OpenFolderFailed", "打开目录失败：{0}", ex.Message));
        }
    }

    private void OnConfigClick(object? sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerRow row) return;
        if (row.ServerId is not { } id) return;

        string path = Path.Combine(Path.GetFullPath(CoreServer.ServerDirectory(id)), "server.properties");
        try
        {
            if (!File.Exists(path))
            {
                Shell.Toast(L.T("Servers.Toast.ConfigMissing", "配置文件不存在：{0}", path));
                return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            Shell.Toast(L.T("Servers.Toast.ConfigOpened", "已打开配置文件：{0}", path));
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Servers.Toast.OpenFolderFailed", "打开目录失败：{0}", ex.Message));
        }
    }

    // ---------------- 生命周期：事件订阅 / 定时器 ----------------

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        CoreServer.ConsoleLine += OnCoreConsoleLine;
        _refreshTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CoreServer.ConsoleLine -= OnCoreConsoleLine;
        _refreshTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }
    // ================= 详情标签页 =================

    private void OnDetailTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tab }) ShowDetailTab(tab);
    }

    private void ShowDetailTab(string tab)
    {
        _activeDetailTab = tab;
        OverviewPane.IsVisible = tab == "overview";
        FilesPane.IsVisible = tab == "files";
        BackupPane.IsVisible = tab == "backups";
        PropertiesPane.IsVisible = tab == "properties";
        ActionsPane.IsVisible = tab == "actions";

        SetTabActive(TabOverview, tab == "overview");
        SetTabActive(TabFiles, tab == "files");
        SetTabActive(TabBackups, tab == "backups");
        SetTabActive(TabProperties, tab == "properties");
        SetTabActive(TabActions, tab == "actions");

        if (tab == "files") RefreshFiles();
        else if (tab == "backups") RefreshRewindBackups();
        else if (tab == "properties") BuildPropertiesEditor();

        Control? pane = tab switch
        {
            "overview" => OverviewPane,
            "files" => FilesPane,
            "backups" => BackupPane,
            "properties" => PropertiesPane,
            "actions" => ActionsPane,
            _ => null,
        };
        UiAnimations.Reset(pane);
        UiAnimations.FadeSlideIn(pane, 0.995, 5, 160);
    }

    private static void SetTabActive(Button button, bool active)
    {
        button.Classes.Set("primary", active);
        button.Classes.Set("ghost", !active);
    }

    // ================= 文件管理器 + 文本编辑器 =================

    private sealed record FileEntryRow(string Name, bool IsDir, long Size, string Relative)
    {
        public string SizeDisplay => IsDir ? string.Empty : FormatSize(Size);

        public static string FormatSize(long bytes) => bytes switch
        {
            < 1024 => bytes + " B",
            < 1024 * 1024 => (bytes / 1024.0).ToString("0.#") + " KB",
            _ => (bytes / 1024.0 / 1024.0).ToString("0.#") + " MB",
        };
    }

    private void RefreshFiles()
    {
        FileList.ItemsSource = null;
        _openFileRelativePath = null;
        FileEditor.Text = string.Empty;
        FileEditorTitle.Text = string.Empty;
        FileEditorStatus.Text = string.Empty;
        FileEditorOverlay.IsVisible = false;

        if (!_localMode || _selectedServerId is not { } id)
        {
            FileStatus.Text = L.T("Servers.Files.LocalOnly", "仅本机服务器支持文件管理");
            return;
        }

        try
        {
            var rows = CoreServerFiles.List(id, _fileRelativePath)
                .Select(entry => new FileEntryRow(
                    entry.Name,
                    entry.IsDir,
                    entry.Size,
                    string.IsNullOrEmpty(_fileRelativePath)
                        ? entry.Name
                        : Path.Combine(_fileRelativePath, entry.Name)))
                .ToList();

            FileList.ItemsSource = rows;
            FileBreadcrumb.Text = "/ " + _fileRelativePath.Replace('\\', '/');
            FileStatus.Text = L.T("Servers.Files.Count", "{0} 项", rows.Count);
            UiAnimations.Reset(FileList);
            UiAnimations.FadeSlideIn(FileList, 0.995, 3, 120);
        }
        catch (Exception error)
        {
            FileStatus.Text = L.T("Servers.Files.ListFailed", "读取目录失败：{0}", error.Message);
        }
    }

    private void OnFilesRefreshClick(object? sender, RoutedEventArgs e) => RefreshFiles();

    private void OnFileUpClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_fileRelativePath)) return;
        _fileRelativePath = Path.GetDirectoryName(_fileRelativePath) ?? string.Empty;
        RefreshFiles();
    }

    private void OnFileListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (FileList.SelectedItem is FileEntryRow entry) OpenFileEntry(entry);
    }

    private void OpenFileEntry(FileEntryRow entry)
    {
        if (_selectedServerId is not { } id) return;

        if (entry.IsDir)
        {
            _fileRelativePath = entry.Relative;
            RefreshFiles();
            return;
        }

        try
        {
            FileEditor.Text = CoreServerFiles.ReadText(id, entry.Relative);
            _openFileRelativePath = entry.Relative;
            _editorDirty = false;
            FileEditorTitle.Text = entry.Relative;
            ApplySyntaxHighlighting(entry.Relative);
            UpdateEditorCaret();
            FileEditorStatus.Text = L.T("Servers.Files.Opened", "已打开 {0}", entry.Relative);
            FileEditorOverlay.IsVisible = true;
            FileEditor.Focus();
        }
        catch (Exception error)
        {
            FileStatus.Text = L.T("Servers.Files.OpenFailed", "打开失败：{0}", error.Message);
        }
    }

    private void OnFileSaveClick(object? sender, RoutedEventArgs e) => SaveCurrentFile();

    private void SaveCurrentFile()
    {
        if (_selectedServerId is not { } id || _openFileRelativePath is null)
        {
            FileEditorStatus.Text = L.T("Servers.Files.NoFile", "先双击打开一个文本文件");
            return;
        }

        try
        {
            CoreServerFiles.WriteText(id, _openFileRelativePath, FileEditor.Text ?? string.Empty);
            _editorDirty = false;
            FileEditorStatus.Text = L.T("Servers.Files.Saved", "已保存 {0}", _openFileRelativePath);
            Shell.Toast(L.T("Servers.Files.SavedToast", "已保存 {0}", _openFileRelativePath));
        }
        catch (Exception error)
        {
            FileEditorStatus.Text = L.T("Servers.Files.SaveFailed", "保存失败：{0}", error.Message);
        }
    }

    private void OnFileEditorCloseClick(object? sender, RoutedEventArgs e) => CloseFileEditor();

    private void OnFileEditorOverlayKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseFileEditor();
    }

    private void OnFileEditorOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, FileEditorOverlay)) return;
        CloseFileEditor();
    }

    private void CloseFileEditor()
    {
        FileEditorOverlay.IsVisible = false;
        FileEditorStatus.Text = string.Empty;
        FileEditorCaret.Text = string.Empty;
        _editorDirty = false;
    }

    // ================= 编辑器高亮与布局 =================

    private static readonly Dictionary<string, string> HighlightNames = new(StringComparer.OrdinalIgnoreCase)
    {
        [".json"] = "Json",
        [".jsonc"] = "Json",
        [".xml"] = "XML",
        [".xaml"] = "XML",
        [".html"] = "HTML",
        [".htm"] = "HTML",
        [".css"] = "CSS",
        [".js"] = "JavaScript",
        [".cs"] = "C#",
        [".java"] = "Java",
        [".py"] = "Python",
        [".ps1"] = "PowerShell",
        [".md"] = "MarkDown",
        [".properties"] = "Properties",
        [".yml"] = "YAML",
        [".yaml"] = "YAML",
    };

    private const string PropertiesHighlighting = """
<?xml version="1.0"?>
<SyntaxDefinition name="Properties" extensions=".properties"
    xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
  <Color name="Comment" foreground="#6A9955" fontStyle="italic" />
  <Color name="Key" foreground="#9CDCFE" />
  <Color name="Value" foreground="#CE9178" />
  <RuleSet>
    <Span color="Comment" begin="#" />
    <Rule color="Key">^[ \t]*[^=:\s#][^=:\r\n]*(?=\s*[=:])</Rule>
    <Rule color="Value">[=:][^\r\n#]*</Rule>
  </RuleSet>
</SyntaxDefinition>
""";

    private const string YamlHighlighting = """
<?xml version="1.0"?>
<SyntaxDefinition name="YAML" extensions=".yml,.yaml"
    xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
  <Color name="Comment" foreground="#6A9955" fontStyle="italic" />
  <Color name="Key" foreground="#9CDCFE" />
  <Color name="String" foreground="#CE9178" />
  <Color name="Number" foreground="#B5CEA8" />
  <RuleSet>
    <Span color="Comment" begin="#" />
    <Rule color="String">"[^"]*"|'[^']*'</Rule>
    <Rule color="Number">\b\d+(\.\d+)?\b</Rule>
    <Rule color="Key">^[ \t-]*[A-Za-z0-9_.\-]+(?=\s*:)</Rule>
  </RuleSet>
</SyntaxDefinition>
""";

    private static void EnsureCustomHighlighting()
    {
        if (_customHighlightingReady) return;
        _customHighlightingReady = true;

        HighlightingManager manager = HighlightingManager.Instance;
        Register("Properties", [".properties"], PropertiesHighlighting);
        Register("YAML", [".yml", ".yaml"], YamlHighlighting);

        void Register(string name, string[] extensions, string xshd)
        {
            if (manager.GetDefinition(name) is not null) return;
            using XmlReader reader = XmlReader.Create(new StringReader(xshd));
            IHighlightingDefinition definition = HighlightingLoader.Load(reader, manager);
            manager.RegisterHighlighting(name, extensions, definition);
        }
    }

    private void ApplySyntaxHighlighting(string relativePath)
    {
        EnsureCustomHighlighting();

        string extension = Path.GetExtension(relativePath).ToLowerInvariant();
        HighlightingManager manager = HighlightingManager.Instance;
        IHighlightingDefinition? definition = HighlightNames.TryGetValue(extension, out string? name)
            ? manager.GetDefinition(name)
            : manager.GetDefinitionByExtension(extension);

        FileEditor.SyntaxHighlighting = definition;
        FileEditorSyntax.Text = definition is null
            ? L.T("Servers.Files.SyntaxPlain", "语法：纯文本")
            : L.T("Servers.Files.Syntax", "语法：{0}", definition.Name);
    }

    private void OnFileEditorTextChanged(object? sender, EventArgs e)
    {
        if (!FileEditorOverlay.IsVisible) return;

        _editorDirty = true;
        FileEditorStatus.Text = L.T("Servers.Files.Unsaved", "● 未保存（Ctrl+S 保存）");
        UpdateEditorCaret();
    }

    private void UpdateEditorCaret()
    {
        if (!FileEditorOverlay.IsVisible) return;

        var caret = FileEditor.TextArea.Caret;
        FileEditorCaret.Text = L.T("Servers.Files.Caret", "行 {0} · 列 {1} · 共 {2} 行",
            caret.Line, caret.Column, FileEditor.Document.LineCount);
    }

    private void OnFileWrapChanged(object? sender, RoutedEventArgs e)
    {
        FileEditor.WordWrap = FileWrapToggle.IsChecked == true;
    }

    private void OnFileEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseFileEditor();
            return;
        }

        if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            SaveCurrentFile();
        }
    }

    // ================= 文件右键操作 =================

    private void OnFileListContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (!_localMode || _selectedServerId is not { } id) return;

        FileEntryRow? entry = null;
        if (e.TryGetPosition(FileList, out Point point) &&
            FileList.InputHitTest(point) is Visual hit)
        {
            entry = hit.FindAncestorOfType<ListBoxItem>(true)?.DataContext as FileEntryRow;
        }

        var items = new List<MenuItem>();
        var flyout = new MenuFlyout();

        void Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            items.Add(item);
        }

        if (entry is not null)
        {
            Add(L.T("Servers.Files.Open", "打开"), () => OpenFileEntry(entry));

            if (entry.IsDir)
            {
                Add(L.T("Servers.Files.NewFile", "新建文件"), () => CreateNewFile(entry.Relative));
                Add(L.T("Servers.Files.NewFolder", "新建文件夹"), () => CreateNewFolder(entry.Relative));
            }

            Add(L.T("Servers.Files.Rename", "重命名"), () => RenameEntry(entry));
            Add(L.T("Servers.Files.Delete", "删除"), () => DeleteEntry(entry));
            Add(L.T("Servers.Files.CopyPath", "复制路径"), () => CopyEntryPath(entry));
        }

        Add(L.T("Servers.Files.NewFile", "新建文件"), () => CreateNewFile(_fileRelativePath));
        Add(L.T("Servers.Files.NewFolder", "新建文件夹"), () => CreateNewFolder(_fileRelativePath));
        Add(L.T("Servers.Files.Refresh", "刷新"), RefreshFiles);

        foreach (MenuItem item in items) flyout.Items.Add(item);
        e.Handled = true;
        flyout.ShowAt(FileList, true);
    }

    private void CreateNewFile(string parentRelative)
    {
        if (_selectedServerId is not { } id) return;

        ShowNameDialog(L.T("Servers.Files.NewFile", "新建文件"), L.T("Servers.Files.NamePrompt", "文件名"), string.Empty, name =>
        {
            string relative = CombineRelative(parentRelative, name);
            CoreServerFiles.CreateTextFile(id, relative);
            _fileRelativePath = parentRelative;
            RefreshFiles();
            OpenFileEntry(new FileEntryRow(name, false, 0, relative));
        });
    }

    private void CreateNewFolder(string parentRelative)
    {
        if (_selectedServerId is not { } id) return;

        ShowNameDialog(L.T("Servers.Files.NewFolder", "新建文件夹"), L.T("Servers.Files.NamePrompt", "文件夹名"), string.Empty, name =>
        {
            CoreServerFiles.CreateDirectory(id, CombineRelative(parentRelative, name));
            _fileRelativePath = parentRelative;
            RefreshFiles();
        });
    }

    private void RenameEntry(FileEntryRow entry)
    {
        if (_selectedServerId is not { } id) return;

        ShowNameDialog(L.T("Servers.Files.Rename", "重命名"), L.T("Servers.Files.NamePrompt", "新名称"), entry.Name, name =>
        {
            string renamed = CoreServerFiles.Rename(id, entry.Relative, name);
            if (string.Equals(_openFileRelativePath, entry.Relative, StringComparison.OrdinalIgnoreCase))
            {
                _openFileRelativePath = renamed;
                FileEditorTitle.Text = renamed;
            }
            if (string.Equals(_fileRelativePath, entry.Relative, StringComparison.OrdinalIgnoreCase))
            {
                _fileRelativePath = renamed;
            }
            RefreshFiles();
            Shell.Toast(L.T("Servers.Files.Renamed", "已重命名为 {0}", name));
        });
    }

    private void DeleteEntry(FileEntryRow entry)
    {
        if (_selectedServerId is not { } id) return;

        ShowConfirmDialog(
            L.T("Servers.Files.Delete", "删除"),
            L.T("Servers.Files.DeleteConfirm", "确定要删除「{0}」吗？文件夹会连同内容一起删除。", entry.Name),
            () =>
            {
                CoreServerFiles.Delete(id, entry.Relative);

                if (_openFileRelativePath is not null &&
                    (_openFileRelativePath.Equals(entry.Relative, StringComparison.OrdinalIgnoreCase) ||
                     _openFileRelativePath.StartsWith(entry.Relative + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                {
                    CloseFileEditor();
                    _openFileRelativePath = null;
                }

                RefreshFiles();
                Shell.Toast(L.T("Servers.Files.Deleted", "已删除 {0}", entry.Name));
            });
    }

    private async void CopyEntryPath(FileEntryRow entry)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) return;
            await clipboard.SetTextAsync(entry.Relative);
            Shell.Toast(L.T("Servers.Files.PathCopied", "已复制路径：{0}", entry.Relative));
        }
        catch (Exception error)
        {
            Shell.Toast(error.Message);
        }
    }

    private static string CombineRelative(string parent, string name) =>
        string.IsNullOrEmpty(parent) ? name : Path.Combine(parent, name);

    private void ShowNameDialog(string title, string label, string initial, Action<string> onConfirm)
    {
        var input = new TextBox
        {
            Text = initial,
            Height = 32,
            MinWidth = 300,
        };

        var content = new StackPanel { Spacing = 10, Width = 380 };
        content.Children.Add(new TextBlock { Text = label });
        content.Children.Add(input);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var cancel = new Button
        {
            Content = L.T("Common.Cancel", "取消"),
            Classes = { "ghost" },
            Height = 30,
            Padding = new Thickness(12, 0),
        };
        cancel.Click += (_, _) => DismissDialog(cancel);

        var confirm = new Button
        {
            Content = L.T("Common.Ok", "确定"),
            Classes = { "primary" },
            Height = 30,
            Padding = new Thickness(12, 0),
        };
        confirm.Click += (_, _) =>
        {
            string value = input.Text?.Trim() ?? string.Empty;
            if (value.Length == 0) return;
            DismissDialog(confirm);
            try
            {
                onConfirm(value);
            }
            catch (Exception error)
            {
                Shell.Toast(error.Message);
            }
        };

        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        content.Children.Add(buttons);
        Shell.Dialog(content, title);
        input.Focus();
    }

    private void ShowConfirmDialog(string title, string message, Action onConfirm)
    {
        var content = new StackPanel { Spacing = 12, Width = 400 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var cancel = new Button
        {
            Content = L.T("Common.Cancel", "取消"),
            Classes = { "ghost" },
            Height = 30,
            Padding = new Thickness(12, 0),
        };
        cancel.Click += (_, _) => DismissDialog(cancel);

        var confirm = new Button
        {
            Content = L.T("Common.Ok", "确定"),
            Classes = { "primary" },
            Height = 30,
            Padding = new Thickness(12, 0),
        };
        confirm.Click += (_, _) =>
        {
            DismissDialog(confirm);
            try
            {
                onConfirm();
            }
            catch (Exception error)
            {
                Shell.Toast(error.Message);
            }
        };

        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        content.Children.Add(buttons);
        Shell.Dialog(content, title);
    }

    // ================= Rewind 世界备份 =================

    private void OnRewindRefreshClick(object? sender, RoutedEventArgs e) => RefreshRewindBackups();

    private void RefreshRewindBackups()
    {
        RewindPanel.Children.Clear();

        if (!_localMode || _selectedServerId is not { } id)
        {
            RewindHint.Text = L.T("Servers.Rewind.LocalOnly", "仅本机服务器支持 Rewind 备份");
            return;
        }

        try
        {
            string worldDirectory = CoreServer.WorldDirectory(id);
            IReadOnlyList<CoreRewindSnapshot> snapshots = CoreRewind.ListSnapshots(worldDirectory);
            RewindHint.Text = L.T("Servers.Rewind.Hint",
                "快照保存在 Mofumofu/rewind，只记录世界存档目录；恢复与删除前请先停止服务器。");

            if (snapshots.Count == 0)
            {
                RewindPanel.Children.Add(new TextBlock
                {
                    Text = L.T("Servers.Rewind.Empty", "还没有快照，点「创建快照」手动备份一次。"),
                    Classes = { "caption" },
                });
                return;
            }

            UiAnimations.Reset(RewindPanel);
            UiAnimations.FadeSlideIn(RewindPanel, 0.995, 4, 150);

            foreach (CoreRewindSnapshot snapshot in snapshots)
            {
                var info = new StackPanel { Spacing = 3 };
                info.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(snapshot.Label)
                        ? L.T("Servers.Rewind.DefaultLabel", "服务器世界快照")
                        : snapshot.Label,
                    FontSize = 12.5,
                    FontWeight = FontWeight.SemiBold,
                });
                info.Children.Add(new TextBlock
                {
                    Text = $"{snapshot.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm} · {snapshot.TotalSizeDisplay} · {snapshot.ReasonLabel}",
                    Classes = { "caption" },
                });

                var actions = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                var restore = new Button
                {
                    Content = L.T("Servers.Rewind.Restore", "恢复"),
                    Classes = { "ghost" },
                    Height = 28,
                    Padding = new Thickness(10, 0),
                };
                restore.Click += async (_, _) => await RestoreRewindSnapshotAsync(snapshot);

                var delete = new Button
                {
                    Content = L.T("Servers.Rewind.Delete", "删除"),
                    Classes = { "ghost" },
                    Height = 28,
                    Padding = new Thickness(10, 0),
                    Foreground = ThemeTokens.Brush("NekoDangerBrush"),
                };
                delete.Click += async (_, _) => await DeleteRewindSnapshotAsync(snapshot);

                actions.Children.Add(restore);
                actions.Children.Add(delete);

                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                row.Children.Add(info);
                Grid.SetColumn(actions, 1);
                row.Children.Add(actions);

                var card = new Border
                {
                    Child = row,
                    Padding = new Thickness(12, 8),
                    CornerRadius = new CornerRadius(10),
                    Background = ThemeTokens.Brush("NekoFieldSoftBrush"),
                };
                RewindPanel.Children.Add(card);
            }
        }
        catch (Exception error)
        {
            RewindHint.Text = L.T("Servers.Rewind.LoadFailed", "读取快照失败：{0}", error.Message);
        }
    }

    private async void OnRewindCreateClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedServerId is not { } id) return;
        try
        {
            if (CoreServer.IsRunning(id))
            {
                Shell.Toast(L.T("Servers.Rewind.StopFirst", "请先停止服务器再创建快照"));
                return;
            }

            string worldDirectory = CoreServer.WorldDirectory(id);
            await CoreRewind.CreateSnapshotAsync(
                worldDirectory,
                CoreRewindKind.Save,
                L.T("Servers.Rewind.ManualLabel", "服务器手动备份"));
            RefreshRewindBackups();
            Shell.Toast(L.T("Servers.Rewind.Created", "已创建服务器世界快照"));
        }
        catch (Exception error)
        {
            Shell.Toast(L.T("Servers.Rewind.CreateFailed", "创建快照失败：{0}", error.Message));
        }
    }

    private async Task RestoreRewindSnapshotAsync(CoreRewindSnapshot snapshot)
    {
        if (_selectedServerId is not { } id) return;

        try
        {
            if (CoreServer.IsRunning(id))
            {
                Shell.Toast(L.T("Servers.Rewind.StopFirst", "请先停止服务器再恢复快照"));
                return;
            }

            string worldDirectory = CoreServer.WorldDirectory(id);
            await CoreRewind.RollbackAsync(worldDirectory, snapshot.Id, CoreRewindKind.Save);
            RefreshRewindBackups();
            Shell.Toast(L.T("Servers.Rewind.Restored", "已恢复到所选快照（恢复前自动创建了安全快照）"));
        }
        catch (Exception error)
        {
            Shell.Toast(L.T("Servers.Rewind.RestoreFailed", "恢复失败：{0}", error.Message));
        }
    }

    private async Task DeleteRewindSnapshotAsync(CoreRewindSnapshot snapshot)
    {
        if (_selectedServerId is not { } id) return;
        try
        {
            string worldDirectory = CoreServer.WorldDirectory(id);
            await CoreRewind.DeleteSnapshotAsync(worldDirectory, snapshot.Id);
            RefreshRewindBackups();
            Shell.Toast(L.T("Servers.Rewind.Deleted", "已删除快照"));
        }
        catch (Exception error)
        {
            Shell.Toast(L.T("Servers.Rewind.DeleteFailed", "删除快照失败：{0}", error.Message));
        }
    }

    // ================= server.properties 图形化 =================

    private sealed record PropertySpec(
        string Key,
        string Label,
        string Comment,
        string[]? Options = null,
        bool IsBool = false);

    private static readonly PropertySpec[] CommonPropertySpecs =
    [
        new("motd", "服务器标语 (MOTD)", "显示在多人游戏列表里的服务器描述，支持颜色代码。"),
        new("server-port", "服务器端口", "玩家连接使用的端口，默认 25565；改完需要让防火墙放行。"),
        new("max-players", "最大玩家数", "同时在线玩家上限。"),
        new("online-mode", "正版验证", "开启后只允许正版账号加入；离线/盗版服请关闭。", IsBool: true),
        new("white-list", "启用白名单", "开启后只有白名单里的玩家能进入。", IsBool: true),
        new("difficulty", "游戏难度", "和平 / 简单 / 普通 / 困难。", ["peaceful", "easy", "normal", "hard"]),
        new("gamemode", "默认游戏模式", "生存 / 创造 / 冒险 / 旁观。", ["survival", "creative", "adventure", "spectator"]),
        new("pvp", "允许 PVP", "玩家之间是否可以互相造成伤害。", IsBool: true),
        new("allow-flight", "允许飞行", "关闭时使用飞行类 Mod/插件可能被踢出。", IsBool: true),
        new("level-name", "世界存档文件夹", "服务端实际读取的世界目录名，对应 world 文件夹。"),
        new("view-distance", "视距", "服务端向玩家发送的区块距离，越大越吃性能。"),
        new("simulation-distance", "模拟距离", "实体与红石模拟的区块距离。"),
        new("spawn-protection", "出生点保护半径", "非 OP 玩家不能破坏出生点周围方块的范围。"),
        new("enable-command-block", "启用命令方块", "是否允许命令方块执行。", IsBool: true),
    ];

    private void BuildPropertiesEditor()
    {
        PropertiesPanel.Children.Clear();
        _propertyGetters.Clear();

        if (!_localMode || _selectedServerId is not { } id)
        {
            PropertiesStatus.Text = L.T("Servers.Properties.LocalOnly", "仅本机服务器支持属性编辑");
            return;
        }

        try
        {
            List<CoreProperty> properties = CoreProperties.Get(id).ToList();
            Dictionary<string, string> values = properties
                .GroupBy(property => property.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);

            AddPropertiesSection(L.T("Servers.Properties.Common", "常用设置（带说明）"));

            var commonKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PropertySpec spec in CommonPropertySpecs)
            {
                commonKeys.Add(spec.Key);
                values.TryGetValue(spec.Key, out string? value);
                AddPropertyControl(spec, value ?? string.Empty);
            }

            AddPropertiesSection(L.T("Servers.Properties.All", "全部属性"));
            foreach (CoreProperty property in properties)
            {
                if (commonKeys.Contains(property.Key)) continue;
                AddPropertyTextRow(property.Key, property.Value);
            }

            PropertiesStatus.Text = L.T("Servers.Properties.Count", "{0} 项属性", properties.Count);
            UiAnimations.Reset(PropertiesPanel);
            UiAnimations.FadeSlideIn(PropertiesPanel, 0.995, 4, 150);
        }
        catch (Exception error)
        {
            PropertiesStatus.Text = L.T("Servers.Properties.LoadFailed", "读取 server.properties 失败：{0}", error.Message);
        }
    }

    private void AddPropertiesSection(string title)
    {
        PropertiesPanel.Children.Add(new TextBlock
        {
            Text = title,
            Classes = { "section-title" },
            Margin = new Thickness(2, 4, 0, 2),
        });
    }

    private void AddPropertyControl(PropertySpec spec, string value)
    {
        Control control;
        Func<string> getter;

        if (spec.IsBool)
        {
            var toggle = new ToggleSwitch
            {
                IsChecked = bool.TryParse(value, out bool parsed) && parsed,
                VerticalAlignment = VerticalAlignment.Center,
            };
            control = toggle;
            getter = () => toggle.IsChecked == true ? "true" : "false";
        }
        else if (spec.Options is { Length: > 0 } options)
        {
            var combo = new ComboBox
            {
                ItemsSource = options,
                SelectedItem = options.FirstOrDefault(item =>
                    string.Equals(item, value, StringComparison.OrdinalIgnoreCase)) ?? options[0],
                MinWidth = 170,
                Height = 32,
                VerticalAlignment = VerticalAlignment.Center,
            };
            control = combo;
            getter = () => combo.SelectedItem as string ?? options[0];
        }
        else
        {
            var box = new TextBox
            {
                Text = value,
                MinWidth = 170,
                Height = 32,
                VerticalAlignment = VerticalAlignment.Center,
            };
            control = box;
            getter = () => box.Text ?? string.Empty;
        }

        AddPropertyRow(spec.Key, spec.Label, spec.Comment, control, getter);
    }

    private void AddPropertyTextRow(string key, string value)
    {
        var box = new TextBox
        {
            Text = value,
            MinWidth = 170,
            Height = 32,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AddPropertyRow(key, key, null, box, () => box.Text ?? string.Empty);
    }

    private void AddPropertyRow(string key, string label, string? comment, Control control, Func<string> getter)
    {
        _propertyGetters[key] = getter;

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
        });
        if (!string.IsNullOrWhiteSpace(comment))
        {
            text.Children.Add(new TextBlock
            {
                Text = comment,
                Classes = { "caption" },
                TextWrapping = TextWrapping.Wrap,
            });
        }

        control.Margin = new Thickness(10, 0, 0, 0);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,220") };
        grid.Children.Add(text);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);

        PropertiesPanel.Children.Add(new Border
        {
            Child = grid,
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(10),
            Background = ThemeTokens.Brush("NekoFieldSoftBrush"),
        });
    }

    private void OnPropertiesReloadClick(object? sender, RoutedEventArgs e) => BuildPropertiesEditor();

    private void OnPropertiesSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedServerId is not { } id) return;

        try
        {
            var values = _propertyGetters
                .Select(pair => new CoreProperty { Key = pair.Key, Value = pair.Value() })
                .ToList();
            CoreProperties.Set(id, values);
            PropertiesStatus.Text = L.T("Servers.Properties.Saved", "已保存 server.properties");
            Shell.Toast(L.T("Servers.Properties.Saved", "已保存 server.properties"));
            LoadDetailIfSelected();
        }
        catch (Exception error)
        {
            PropertiesStatus.Text = L.T("Servers.Properties.SaveFailed", "保存失败：{0}", error.Message);
        }
    }

    private void LoadDetailIfSelected()
    {
        if (ServerList.SelectedItem is ServerRow row) LoadDetail(row);
    }

    // ================= 导出 / 删除 =================

    private async void OnExportServerClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedServerId is not { } id || !_localMode) return;

        try
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is not { } storage)
            {
                Shell.Toast(L.T("Servers.Export.NoPicker", "当前环境无法打开文件选择器"));
                return;
            }

            ServerRow? row = _visibleServers.FirstOrDefault(server => server.ServerId == id);
            string name = row?.Name ?? id;

            IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = L.T("Servers.Export.Pick", "导出服务器包"),
                SuggestedFileName = name + ".mofuser",
                DefaultExtension = "mofuser",
                FileTypeChoices =
                [
                    new FilePickerFileType("Mofumofu server package")
                    {
                        Patterns = ["*.mofuser"],
                    },
                ],
            });

            if (file?.TryGetLocalPath() is not { Length: > 0 } path) return;

            await CoreServerPackage.ExportAsync(id, path);
            Shell.Toast(L.T("Servers.Export.Done", "已导出服务器包：{0}", path));
        }
        catch (Exception error)
        {
            Shell.Toast(L.T("Servers.Export.Failed", "导出失败：{0}", error.Message));
        }
    }

    private void OnDeleteServerClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedServerId is not { } id || !_localMode) return;

        if (CoreServer.IsRunning(id))
        {
            Shell.Toast(L.T("Servers.Delete.Running", "请先停止服务器再删除"));
            return;
        }

        ServerRow? row = _visibleServers.FirstOrDefault(server => server.ServerId == id);
        string name = row?.Name ?? id;

        var content = new StackPanel { Spacing = 12, Width = 380 };
        content.Children.Add(new TextBlock
        {
            Text = L.T("Servers.Delete.Confirm", "确定要删除服务器「{0}」吗？目录、存档与备份都会被删除，无法恢复。", name),
            TextWrapping = TextWrapping.Wrap,
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var cancel = new Button
        {
            Content = L.T("Common.Cancel", "取消"),
            Classes = { "ghost" },
            Height = 32,
            Padding = new Thickness(12, 0),
        };
        cancel.Click += (_, _) => DismissDialog(cancel);

        var confirm = new Button
        {
            Content = L.T("Servers.Delete.Action", "删除"),
            Classes = { "primary" },
            Height = 32,
            Padding = new Thickness(12, 0),
        };
        confirm.Click += (_, _) =>
        {
            DismissDialog(confirm);
            try
            {
                CoreServer.DeleteServer(id);
                LoadServers();
                RefreshServerList();
                UpdateChips();
                ServerList.SelectedIndex = _visibleServers.Count > 0 ? 0 : -1;
                Shell.Toast(L.T("Servers.Delete.Done", "已删除服务器「{0}」", name));
            }
            catch (Exception error)
            {
                Shell.Toast(L.T("Servers.Delete.Failed", "删除失败：{0}", error.Message));
            }
        };

        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        content.Children.Add(buttons);

        Shell.Dialog(content, L.T("Servers.Delete.Title", "删除服务器"));
    }

    private static void DismissDialog(Control control) =>
        control.FindAncestorOfType<SukiDialogHost>(true)?.Manager?.DismissDialog();

}

/// <summary>服务器列表行：核心状态/在线数变化时刷新徽标、副标题与配色。</summary>
public sealed class ServerRow : INotifyPropertyChanged
{
    private string _status = CoreServer.StatusStopped;
    private int _players;

    private ServerRow()
    {
    }

    public static ServerRow FromCore(CoreServerInfo info, string motd) => new()
    {
        ServerId = info.Id,
        IsLocal = true,
        Name = info.Name,
        Address = $"127.0.0.1:{info.Port}",
        Version = BuildVersion(info.McVersion, info.Core, info.CoreVersion),
        Motd = motd,
        MaxPlayers = info.MaxPlayers,
        Players = info.Players,
        Status = info.Status,
    };

    public string? ServerId { get; private init; }

    public bool IsLocal { get; private init; }

    public string Name { get; private init; } = string.Empty;

    public string Address { get; private init; } = string.Empty;

    public string Version { get; private init; } = string.Empty;

    public string Motd { get; private init; } = string.Empty;

    public int MaxPlayers { get; private init; }

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            Raise(nameof(Status));
            Raise(nameof(Online));
            Raise(nameof(StatusText));
            Raise(nameof(StatusBg));
            Raise(nameof(StatusFg));
            Raise(nameof(SubLine));
        }
    }

    public int Players
    {
        get => _players;
        set
        {
            if (_players == value) return;
            _players = value;
            Raise(nameof(Players));
            Raise(nameof(SubLine));
        }
    }

    public bool Online =>
        _status is CoreServer.StatusRunning or CoreServer.StatusStarting or CoreServer.StatusStopping;

    public string StatusText => _status switch
    {
        CoreServer.StatusRunning => L.T("Servers.Status.Running", "运行中"),
        CoreServer.StatusStarting => L.T("Servers.Status.Starting", "启动中"),
        CoreServer.StatusStopping => L.T("Servers.Status.Stopping", "停止中"),
        _ => L.T("Servers.Status.Stopped", "已停止"),
    };

    public IBrush StatusBg => ThemeTokens.Brush(_status switch
    {
        CoreServer.StatusRunning => "NekoSuccessSoftBrush",
        CoreServer.StatusStarting or CoreServer.StatusStopping => "NekoWarningSoftBrush",
        _ => "NekoFieldBrush",
    });

    public IBrush StatusFg => ThemeTokens.Brush(_status switch
    {
        CoreServer.StatusRunning => "NekoSuccessBrush",
        CoreServer.StatusStarting or CoreServer.StatusStopping => "NekoWarningBrush",
        _ => "NekoMutedBrush",
    });

    public string SubLine => $"{Version} · {Address} · {_players}/{MaxPlayers}";

    public bool Matches(string query) =>
        Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Address.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Version.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string BuildVersion(string mcVersion, string core, string coreVersion)
    {
        var coreName = core switch
        {
            CoreServer.CorePaper => "Paper",
            CoreServer.CoreFabric => "Fabric",
            CoreServer.CoreNeoForge => "NeoForge",
            _ => "Vanilla",
        };

        return string.IsNullOrWhiteSpace(coreVersion)
            ? $"{mcVersion} · {coreName}"
            : $"{mcVersion} · {coreName} {coreVersion}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

/// <summary>控制台日志行（等级决定颜色）。支持解析 Minecraft 服务端原始日志行。</summary>
public sealed class ConsoleRow
{
    private static readonly Regex LogLinePattern = new(
        @"^\[(?<time>\d{2}:\d{2}:\d{2})\]\s*\[[^\]]*?(?<level>INFO|WARN|WARNING|ERROR|FATAL|DEBUG)\]\s*:?\s*(?<text>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LevelOnlyPattern = new(
        @"(?<level>INFO|WARN|WARNING|ERROR|FATAL|DEBUG)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public ConsoleRow(ServerConsoleLine line)
    {
        Time = line.Time;
        _levelKey = NormalizeLevel(line.Level);
        Text = line.Text;
        LevelBrush = LevelBrushOf(_levelKey);
    }

    public ConsoleRow(string rawLine)
    {
        var input = rawLine ?? string.Empty;
        Match match = LogLinePattern.Match(input);
        if (match.Success)
        {
            Time = match.Groups["time"].Value;
            _levelKey = NormalizeLevel(match.Groups["level"].Value);
            Text = match.Groups["text"].Value;
        }
        else
        {
            Match levelMatch = LevelOnlyPattern.Match(input);
            Time = DateTime.Now.ToString("HH:mm:ss");
            _levelKey = levelMatch.Success ? NormalizeLevel(levelMatch.Groups["level"].Value) : "INFO";
            Text = input;
        }

        LevelBrush = LevelBrushOf(_levelKey);
    }

    private ConsoleRow(string time, string level, string text)
    {
        Time = time;
        _levelKey = level;
        Text = text;
        LevelBrush = LevelBrushOf(level);
    }

    public static ConsoleRow System(string text) =>
        new(DateTime.Now.ToString("HH:mm:ss"), "INFO", text);

    private readonly string _levelKey;

    public string Time { get; }

    public string Text { get; }

    /// <summary>等级名来自语言包（内容仍是服务端原始日志文本，不翻译）。</summary>
    public string Level => _levelKey switch
    {
        "WARN" => L.T("Servers.Console.Level.Warn", "WARN"),
        "ERROR" => L.T("Servers.Console.Level.Error", "ERROR"),
        "DEBUG" => L.T("Servers.Console.Level.Debug", "DEBUG"),
        _ => L.T("Servers.Console.Level.Info", "INFO"),
    };

    public IBrush LevelBrush { get; }

    private static string NormalizeLevel(string level) => (level ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "WARN" or "WARNING" => "WARN",
        "ERROR" or "FATAL" => "ERROR",
        "DEBUG" => "DEBUG",
        _ => "INFO",
    };

    private static IBrush LevelBrushOf(string level) => ThemeTokens.Brush(level switch
    {
        "WARN" => "NekoWarningBrush",
        "ERROR" => "NekoDangerBrush",
        "DEBUG" => "NekoMutedBrush",
        _ => "NekoInfoBrush",
    });


}
