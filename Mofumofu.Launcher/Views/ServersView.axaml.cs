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
using AvaloniaEdit.Search;
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
using Mofumofu.Launcher.Services.Servers;
using L = Mofumofu.Launcher.Localization.Localization;
using CoreBackup = MofuCore.Server.ServerBackup;
using CoreBackupInfo = MofuCore.Server.ServerBackupInfo;
using CoreOpEntry = MofuCore.Server.OpEntry;
using CorePlayers = MofuCore.Server.ServerPlayers;
using CoreProperties = MofuCore.Server.ServerProperties;
using CoreProperty = MofuCore.Server.ServerProperty;
using CoreServer = MofuCore.Server.ServerManage;
using CoreServerInfo = MofuCore.Server.ServerInfo;
using CoreServerPackage = MofuCore.Server.ServerPackage;
using CoreWhitelistEntry = MofuCore.Server.WhitelistEntry;

namespace Mofumofu.Launcher.Views;

public partial class ServersView : UserControl
{

    private readonly List<ServerRow> _allServers = new();
    private readonly ObservableCollection<ServerRow> _visibleServers = new();
    private readonly ObservableCollection<ConsoleRow> _consoleRows = new();
    private readonly DispatcherTimer _refreshTimer;

    private bool _ready;
    private bool _localMode;
    private bool _playerRefreshBusy;
    private bool _statusRefreshBusy;
    private int _playerRefreshToken;
    private int _refreshTick;
    private string? _selectedServerId;
    private string? _selectedBackendKey;
    private IServerBackend? _consoleBackend;
    private long _lastConsoleCursor;
    private string _activeDetailTab = "overview";
    private string _fileRelativePath = string.Empty;
    private string? _openFileRelativePath;
    private string? _openFileRevision;
    private string? _propertiesRevision;
    private bool _editorDirty;
    private double _editorFontSize = 13;
    private SearchPanel? _searchPanel;
    private static bool _customHighlightingReady;
    private readonly Dictionary<string, Func<string>> _propertyGetters = new(StringComparer.OrdinalIgnoreCase);

    public ServersView()
    {
        InitializeComponent();

        ConsoleList.ItemsSource = _consoleRows;
        FileEditor.TextArea.Caret.PositionChanged += (_, _) => UpdateEditorCaret();
        ConfigureEditor();

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
        else
        {
            ShowEmptyDetail();
        }
    }

    private bool LoadServers()
    {
        if (_consoleBackend is not null)
        {
            _consoleBackend.ConsoleLine -= OnBackendConsoleLine;
            _consoleBackend.ConsoleReset -= OnBackendConsoleReset;
            _consoleBackend = null;
        }
        foreach (ServerRow row in _allServers) row.Backend.Dispose();
        _allServers.Clear();
        _localMode = false;

        try
        {
            IReadOnlyList<BackendRegistration> registrations = ServerBackendRegistry
                .LoadAsync().GetAwaiter().GetResult();
            if (registrations.Count > 0)
            {
                _localMode = registrations.Any(registration => registration.Backend.IsLocal);
                foreach (BackendRegistration registration in registrations)
                {
                    _allServers.Add(ServerRow.FromBackend(registration.Backend, registration.Description));
                }
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine($"[servers] LoadLocalAsync failed: {e.Message}");
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
        }

        return "—";
    }

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
        ListEmptyHint.IsVisible = filtered.Count == 0;
        ListEmptyTitle.Text = _allServers.Count == 0
            ? L.T("Servers.Empty.Title", "还没有服务器")
            : L.T("Servers.List.Empty", "没有找到匹配的服务器");
        ListEmptyText.Text = _allServers.Count == 0
            ? L.T("Servers.Empty.Hint", "新建一台服务器，和好友一起开玩吧。")
            : L.T("Servers.List.NoMatchHint", "换个关键词试试吧");

        var index = selectedId is not null
            ? filtered.FindIndex(s => s.ServerId == selectedId)
            : filtered.FindIndex(s => s.Name == selectedName);
        if (index >= 0)
        {
            ServerList.SelectedIndex = index;
        }
        else if (filtered.Count == 0)
        {
            ServerList.SelectedIndex = -1;
            _selectedServerId = null;
            ShowEmptyDetail();
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

        RunningChipText.Text = running.ToString();
        TotalChipText.Text = _allServers.Count.ToString();
        StoppedChipText.Text = stopped.ToString();
        PlayersChipText.Text = players.ToString();
    }

    private void ShowEmptyDetail()
    {
        DetailContent.IsVisible = false;
        DetailEmpty.IsVisible = true;
    }

    private void ShowDetailContent()
    {
        DetailEmpty.IsVisible = false;
        DetailContent.IsVisible = true;
    }

    private void OnServerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (ServerList.SelectedItem is ServerRow row) LoadDetail(row);
        else ShowEmptyDetail();
    }

    private async void LoadDetail(ServerRow row)
    {
        _selectedServerId = row.ServerId;
        _selectedBackendKey = row.Backend.BackendKey;
        if (_consoleBackend is not null)
        {
            _consoleBackend.ConsoleLine -= OnBackendConsoleLine;
            _consoleBackend.ConsoleReset -= OnBackendConsoleReset;
        }
        _consoleBackend = row.Backend;
        _consoleBackend.ConsoleLine += OnBackendConsoleLine;
        _consoleBackend.ConsoleReset += OnBackendConsoleReset;
        ShowDetailContent();

        DetailName.Text = row.Name;
        DetailAddress.Text = row.Address;
        DetailVersion.Text = row.Version;
        DetailMotd.Text = row.Motd;
        DetailPlayers.Text = L.T("Servers.Detail.PlayersCount", "{0} / {1} 名玩家", row.Players, row.MaxPlayers);
        ApplyStatusVisuals(row);
        UpdateToggleVisual(row);
        UpdateAccessVisuals(row);
        UnpairButton.IsVisible = !row.IsLocal;
        TabActions.IsVisible = row.IsLocal;
        TabBackups.IsVisible = row.IsLocal;
        if (!row.IsLocal)
        {
            _ = RefreshRemoteDescriptionAsync(row);
        }
        if ((!row.IsLocal && _activeDetailTab is "actions" or "backups") ||
            (_activeDetailTab == "files" && !TabFiles.IsVisible) ||
            (_activeDetailTab == "properties" && !TabProperties.IsVisible))
        {
            ShowDetailTab("overview");
        }

        ClearConsole();
        if (row.ServerId is not null)
        {
            await LoadConsoleHistory(row);
            RefreshOnlinePlayers(row);
            RefreshBackups(row);
        }
        else
        {
            BackupSection.IsVisible = false;
            RebuildUnavailablePlayers(row);
        }

        ScrollConsoleToEnd();

        UiAnimations.Reset(DetailBody);
        UiAnimations.FadeSlideIn(DetailBody, 0.995, 6, 170);
    }

    private void ApplyStatusVisuals(ServerRow row)
    {
        DetailStatus.Text = row.StatusText;
        DetailBadge.Background = row.StatusBg;
        DetailStatus.Foreground = row.StatusFg;
    }

    private static bool HasAccess(ServerRow row, string capability, string scope) =>
        row.Capabilities.Contains(capability) && row.Scopes.Contains(scope);

    private void UpdateToggleVisual(ServerRow row)
    {
        var online = row.Online;
        bool hasControlCapability = row.Capabilities.Contains(ServerCapability.Control);
        bool canControl = hasControlCapability && row.Scopes.Contains(ServerScope.Control);
        ToggleIcon.Kind = online ? "IconStop" : "IconPlay";
        ToggleText.Text = !hasControlCapability
            ? L.T("Servers.Remote.PanelManaged", "面板接管")
            : !canControl
                ? L.T("Servers.Remote.InsufficientScope", "当前令牌未授予所需权限")
                : online
                    ? L.T("Servers.Detail.Stop", "停止服务")
                    : L.T("Servers.Detail.Start", "启动服务");
        ToggleButton.IsEnabled = canControl;
        RestartButton.IsEnabled = canControl;
        string? tip = canControl
            ? null
            : hasControlCapability
                ? L.T("Servers.Remote.InsufficientScope", "当前令牌未授予所需权限")
                : L.T("Servers.Remote.PanelManagedHint", "请在服务器面板中启停或重启");
        ToolTip.SetTip(ToggleButton, tip);
        ToolTip.SetTip(RestartButton, tip);
        ToggleButton.Classes.Set("primary", !online && canControl);
        ToggleButton.Classes.Set("ghost", online || !canControl);
    }

    private async Task RefreshRemoteDescriptionAsync(ServerRow row)
    {
        try
        {
            ServerDescription description = await row.Backend.GetDescriptionAsync();
            if (ServerList.SelectedItem is not ServerRow current ||
                current.Backend.BackendKey != row.Backend.BackendKey) return;
            DetailVersion.Text = ServerRow.BuildVersion(description.McVersion, description.Core,
                description.CoreVersion);
            DetailMotd.Text = description.Motd;
            DetailPlayers.Text = L.T("Servers.Detail.PlayersCount", "{0} / {1} 名玩家",
                row.Players, description.MaxPlayers);
            UpdateToggleVisual(row);
            UpdateAccessVisuals(row);
            if (row.ServerId is not null) RefreshBackups(row);
        }
        catch (Exception)
        {
            // 无法握手时保留配对时缓存的描述，并按已存 scopes 继续降级。
        }
    }

    private void UpdateAccessVisuals(ServerRow row)
    {
        bool canCommand = HasAccess(row, ServerCapability.Command, ServerScope.Command);
        bool canPlayers = HasAccess(row, ServerCapability.Players, ServerScope.Players);
        bool canFiles = HasAccess(row, ServerCapability.Files, ServerScope.Files);
        bool canProperties = HasAccess(row, ServerCapability.Properties, ServerScope.Files);
        bool canBackup = HasAccess(row, ServerCapability.Backup, ServerScope.Backup);
        string scopeHint = L.T("Servers.Remote.InsufficientScope", "当前令牌未授予所需权限");

        TabFiles.IsVisible = canFiles;
        TabProperties.IsVisible = canProperties;
        ConsoleInput.IsEnabled = canCommand;
        ConsoleSendButton.IsEnabled = canCommand;
        ToolTip.SetTip(ConsoleInput, canCommand ? null : scopeHint);
        ToolTip.SetTip(ConsoleSendButton, canCommand ? null : scopeHint);

        PlayerNameInput.IsEnabled = canPlayers;
        foreach (Button button in new[]
                 {
                     WhitelistAddButton, WhitelistRemoveButton, OpAddButton, OpRemoveButton,
                     BanAddButton, BanRemoveButton,
                 })
        {
            button.IsEnabled = canPlayers;
            ToolTip.SetTip(button, canPlayers ? null : scopeHint);
        }

        FilesUpButton.IsEnabled = canFiles;
        FilesRefreshButton.IsEnabled = canFiles;
        FileSaveButton.IsEnabled = canFiles;
        PropertiesSaveButton.IsEnabled = canProperties;
        PropertiesReloadButton.IsEnabled = canProperties;
        QuickBackupButton.IsEnabled = canBackup;
        ToolTip.SetTip(QuickBackupButton, canBackup ? null : scopeHint);
        OpenFolderButton.IsVisible = row.IsLocal;
        ConfigButton.IsVisible = row.IsLocal;
        BackupSection.IsVisible = canBackup;
    }

    private void ClearConsole()
    {
        _consoleRows.Clear();
        _lastConsoleCursor = 0;
    }

    private async Task LoadConsoleHistory(ServerRow row)
    {
        try
        {
            BackendStatus snapshot = await row.Backend.GetStatusAsync(0);
            if (_selectedBackendKey != row.Backend.BackendKey) return;
            foreach (string line in snapshot.LogLines)
            {
                AppendConsoleLine(line);
            }
            _lastConsoleCursor = snapshot.NextCursor;

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

    private void OnBackendConsoleLine(object? sender, ServerConsoleEventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnBackendConsoleLine(sender, e));
            return;
        }

        if (!_ready || !ReferenceEquals(sender, _consoleBackend)) return;
        if (e.Cursor > 0 && e.Cursor <= _lastConsoleCursor) return;
        _lastConsoleCursor = Math.Max(_lastConsoleCursor, e.Cursor);
        AppendConsoleLine(e.Line);
    }

    private void OnBackendConsoleReset(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnBackendConsoleReset(sender, e));
            return;
        }
        if (ReferenceEquals(sender, _consoleBackend))
        {
            _lastConsoleCursor = 0;
            ClearConsole();
        }
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
        if (ServerList.SelectedItem is not ServerRow row ||
            !HasAccess(row, ServerCapability.Command, ServerScope.Command)) return;

        var command = (ConsoleInput.Text ?? string.Empty).Trim();
        if (command.Length == 0) return;

        ConsoleInput.Text = string.Empty;

        if (row.ServerId is not { } id) return;

        AppendSystemLine(L.T("Servers.Console.CommandEcho", "> {0}", command));
        try
        {
            await row.Backend.SendCommandAsync(command);
        }
        catch (Exception e)
        {
            AppendSystemLine(e.Message);
        }
    }

    private void RebuildUnavailablePlayers(ServerRow row)
    {
        PlayersPanel.Children.Clear();
        PlayersPanel.Children.Add(new TextBlock
        {
            Text = !HasAccess(row, ServerCapability.Players, ServerScope.Players)
                ? L.T("Servers.Remote.InsufficientScope", "当前令牌未授予所需权限")
                : row.Status == "unreachable"
                    ? L.T("Servers.Remote.UnreachableHint", "连接中断；面板服请检查插件、端口映射和 Caddy 反代")
                    : row.Online
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

    private async void RefreshOnlinePlayers(ServerRow row)
    {
        if (row.ServerId is null || !HasAccess(row, ServerCapability.Players, ServerScope.Players))
        {
            RebuildUnavailablePlayers(row);
            return;
        }
        if (_playerRefreshBusy) return;

        _playerRefreshBusy = true;
        var token = ++_playerRefreshToken;
        try
        {
            BackendPlayers summary = await row.Backend.GetPlayersAsync();
            if (token != _playerRefreshToken) return;
            if (ServerList.SelectedItem is not ServerRow current ||
                current.Backend.BackendKey != row.Backend.BackendKey) return;
            RebuildOnlinePlayers(current, summary.Online.Select(player => player.Name).ToList());
        }
        catch (Exception)
        {
            if (token == _playerRefreshToken) RebuildUnavailablePlayers(row);
        }
        finally
        {
            _playerRefreshBusy = false;
        }
    }

    private async void OnWhitelistAddClick(object? sender, RoutedEventArgs e) =>
        await ModifyWhitelistAsync(true);

    private async void OnWhitelistRemoveClick(object? sender, RoutedEventArgs e) =>
        await ModifyWhitelistAsync(false);

    private async Task ModifyWhitelistAsync(bool add)
    {
        if (ServerList.SelectedItem is not ServerRow row ||
            !HasAccess(row, ServerCapability.Players, ServerScope.Players)) return;
        if (row.ServerId is not { } id) return;

        var name = (PlayerNameInput.Text ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            Shell.Toast(L.T("Servers.Toast.EnterPlayerName", "先在输入框里填玩家名"));
            return;
        }

        try
        {
            await row.Backend.ModifyWhitelistAsync(name, add);

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
        if (ServerList.SelectedItem is not ServerRow row ||
            !HasAccess(row, ServerCapability.Players, ServerScope.Players)) return;
        if (row.ServerId is not { } id) return;

        var name = (PlayerNameInput.Text ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            Shell.Toast(L.T("Servers.Toast.EnterPlayerName", "先在输入框里填玩家名"));
            return;
        }

        try
        {
            await row.Backend.ModifyOpAsync(name, add);

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

    private async void OnBanAddClick(object? sender, RoutedEventArgs e) => await ModifyBanAsync(true);

    private async void OnBanRemoveClick(object? sender, RoutedEventArgs e) => await ModifyBanAsync(false);

    private async Task ModifyBanAsync(bool add)
    {
        if (ServerList.SelectedItem is not ServerRow row || row.ServerId is null ||
            !HasAccess(row, ServerCapability.Players, ServerScope.Players)) return;
        var name = (PlayerNameInput.Text ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            Shell.Toast(L.T("Servers.Toast.EnterPlayerName", "先在输入框里填玩家名"));
            return;
        }

        try
        {
            await row.Backend.ModifyBanAsync(name, add, add ? "Banned by Mofumofu" : null);
            PlayerNameInput.Text = string.Empty;
            Shell.Toast(add
                ? L.T("Servers.Toast.BanAdded", "已封禁玩家：{0}", name)
                : L.T("Servers.Toast.BanRemoved", "已解除封禁：{0}", name));
        }
        catch (Exception error)
        {
            Shell.Toast(error.Message);
        }
    }

    private async void RefreshBackups(ServerRow row)
    {
        BackupPanel.Children.Clear();

        if (row.ServerId is null || !HasAccess(row, ServerCapability.Backup, ServerScope.Backup))
        {
            BackupSection.IsVisible = false;
            return;
        }

        BackupSection.IsVisible = true;

        IReadOnlyList<BackendBackup> backups;
        try
        {
            backups = await row.Backend.GetBackupsAsync();
            if (ServerList.SelectedItem is not ServerRow current ||
                current.Backend.BackendKey != row.Backend.BackendKey) return;
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

        foreach (BackendBackup backup in backups.Take(3))
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

        UiAnimations.Stagger(BackupPanel.Children, step: 45, milliseconds: 180);
    }

    private async void OnRestoreBackupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string backupName }) return;
        if (ServerList.SelectedItem is not ServerRow { ServerId: not null } row ||
            !HasAccess(row, ServerCapability.Backup, ServerScope.Backup)) return;
        if (ConfirmRemoteAction(row, "恢复备份",
                L.T("Servers.Remote.RestoreConfirm", "将恢复备份 {0}。面板服需要在重启后提交恢复，继续吗？", backupName),
                () => RestoreBackupAsync(row, backupName))) return;
        await RestoreBackupAsync(row, backupName);
    }

    private async Task RestoreBackupAsync(ServerRow row, string backupName)
    {
        try
        {
            Shell.Toast(L.T("Servers.Toast.Restoring", "正在恢复备份 {0}…", backupName));
            BackendOperation result = await row.Backend.RestoreBackupAsync(backupName);
            Shell.Toast(result.PendingRestart
                ? L.T("Servers.Remote.RestorePending", "恢复已预提交，请到面板重启服务器")
                : L.T("Servers.Toast.Restored", "备份已恢复（恢复前已自动创建安全备份）"));
            RefreshBackups(row);
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Servers.Toast.RestoreFailed", "恢复失败：{0}", ex.Message));
        }
    }

    private async void OnDeleteBackupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string backupName }) return;
        if (ServerList.SelectedItem is not ServerRow { ServerId: not null } row ||
            !HasAccess(row, ServerCapability.Backup, ServerScope.Backup)) return;
        if (ConfirmRemoteAction(row, "删除备份",
                L.T("Servers.Remote.DeleteBackupConfirm", "将永久删除远程备份 {0}，继续吗？", backupName),
                () => DeleteBackupAsync(row, backupName))) return;
        await DeleteBackupAsync(row, backupName);
    }

    private async Task DeleteBackupAsync(ServerRow row, string backupName)
    {
        try
        {
            await row.Backend.DeleteBackupAsync(backupName);
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

    private async void OnRefreshTick(object? sender, EventArgs e)
    {
        if (!_ready || _statusRefreshBusy) return;
        if (ServerList.SelectedItem is not ServerRow row || row.ServerId is null) return;

        _statusRefreshBusy = true;
        try
        {
            BackendStatus snapshot = await row.Backend.GetStatusAsync(long.MaxValue);
            if (ServerList.SelectedItem is not ServerRow current ||
                current.Backend.BackendKey != row.Backend.BackendKey) return;
            var changed = row.Status != snapshot.Status;
            row.Status = snapshot.Status;

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
            if (_refreshTick % 5 == 0) RefreshOnlinePlayers(row);
        }
        catch (Exception)
        {
            // 远程后端的连接状态由 RemoteServerBackend 事件与详情刷新呈现。
        }
        finally
        {
            _statusRefreshBusy = false;
        }
    }

    private async void OnToggleServerClick(object? sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerRow row || row.ServerId is null ||
            !HasAccess(row, ServerCapability.Control, ServerScope.Control)) return;
        if (ConfirmRemoteAction(row, row.Online ? "停止服务器" : "启动服务器",
                L.T("Servers.Remote.ControlConfirm", "将远程{0}「{1}」，继续吗？", row.Online ? "停止" : "启动", row.Name),
                () => ToggleServerAsync(row))) return;
        await ToggleServerAsync(row);
    }

    private async Task ToggleServerAsync(ServerRow row)
    {
        ToggleButton.IsEnabled = false;
        try
        {
            if (row.Online)
            {
                Shell.Toast(L.T("Servers.Toast.Stopping", "正在停止「{0}」…", row.Name));
                await row.Backend.StopAsync();
                Shell.Toast(L.T("Servers.Toast.Stopped", "已停止「{0}」", row.Name));
            }
            else
            {
                Shell.Toast(L.T("Servers.Toast.Starting", "正在启动「{0}」…", row.Name));
                await row.Backend.StartAsync();
                Shell.Toast(L.T("Servers.Toast.Started", "已启动「{0}」", row.Name));
            }

            row.Status = (await row.Backend.GetStatusAsync(long.MaxValue)).Status;
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
            UpdateToggleVisual(row);
        }
    }

    private async void OnRestartClick(object? sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerRow row || row.ServerId is null ||
            !HasAccess(row, ServerCapability.Control, ServerScope.Control)) return;
        if (!row.Online)
        {
            Shell.Toast(L.T("Servers.Toast.NotRunning", "服务器未在运行"));
            return;
        }
        if (ConfirmRemoteAction(row, "重启服务器",
                L.T("Servers.Remote.RestartConfirm", "将远程重启「{0}」，继续吗？", row.Name),
                () => RestartServerAsync(row))) return;
        await RestartServerAsync(row);
    }

    private async Task RestartServerAsync(ServerRow row)
    {
        try
        {
            Shell.Toast(L.T("Servers.Toast.Restarting", "正在重启「{0}」…", row.Name));
            await row.Backend.RestartAsync();
            row.Status = (await row.Backend.GetStatusAsync(long.MaxValue)).Status;
            ApplyStatusVisuals(row);
            UpdateToggleVisual(row);
            Shell.Toast(L.T("Servers.Toast.Restarted", "已重启「{0}」", row.Name));
        }
        catch (Exception ex)
        {
            Shell.Toast(ex.Message);
        }
    }

    private bool ConfirmRemoteAction(ServerRow row, string title, string message, Func<Task> action)
    {
        if (row.IsLocal) return false;
        ShowConfirmDialog(title, message, () => _ = action());
        return true;
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
        else ShowEmptyDetail();

        Shell.Toast(L.T("Servers.Toast.Refreshed", "已刷新，共 {0} 台服务器", _allServers.Count));
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

    private void OnPairRemoteServerClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new RemoteServerPairDialog();
        dialog.ServerPaired += connection =>
        {
            LoadServers();
            RefreshServerList();
            int index = _visibleServers.ToList().FindIndex(server =>
                server.Backend.BackendKey == connection.BackendKey);
            ServerList.SelectedIndex = index >= 0 ? index : (_visibleServers.Count > 0 ? 0 : -1);
            UpdateChips();
            if (ServerList.SelectedItem is ServerRow paired) LoadDetail(paired);
            Shell.Toast(L.T("Servers.Remote.PairDone", "配对完成：{0}", connection.Name));
        };
        Shell.Dialog(dialog, L.T("Servers.Remote.PairTitle", "连接远程服务器"), sender as Control);
    }

    private void OnCreateServerClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new ServerCreateDialog();
        dialog.ServerCreated += serverId =>
        {
            LoadServers();
            RefreshServerList();
            int index = IndexOfVisibleServer(serverId);
            ServerList.SelectedIndex = index >= 0
                ? index
                : (_visibleServers.Count > 0 ? 0 : -1);
            UpdateChips();
            if (ServerList.SelectedItem is ServerRow created) LoadDetail(created);
            Shell.Toast(L.T("Servers.Create.Done", "服务器创建完成"));
        };
        Shell.Dialog(dialog, L.T("Servers.Create.Title", "新建服务器"), sender as Control);
    }

    private async void OnBackupClick(object? sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerRow row ||
            !HasAccess(row, ServerCapability.Backup, ServerScope.Backup)) return;
        if (row.ServerId is not { } id) return;

        try
        {
            Shell.Toast(L.T("Servers.Toast.BackingUp", "正在创建备份…"));
            BackendBackup info = await row.Backend.CreateBackupAsync();
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
        if (ServerList.SelectedItem is not ServerRow { IsLocal: true } row) return;
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
        if (ServerList.SelectedItem is not ServerRow { IsLocal: true } row) return;
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

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _refreshTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_consoleBackend is not null)
        {
            _consoleBackend.ConsoleLine -= OnBackendConsoleLine;
            _consoleBackend.ConsoleReset -= OnBackendConsoleReset;
        }
        _consoleBackend = null;
        _refreshTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

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
        UiAnimations.FadeSwap(pane, 160);
    }

    private static void SetTabActive(Button button, bool active)
    {
        button.Classes.Set("active", active);
    }

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

    private async void RefreshFiles()
    {
        // 有未保存的修改时不刷新面板，避免直接丢弃编辑器里的内容
        if (_editorDirty) return;

        FileList.ItemsSource = null;
        _openFileRelativePath = null;
        FileEditor.Text = string.Empty;
        FileEditorTitle.Text = string.Empty;
        FileEditorStatus.Text = string.Empty;
        FileEditorDirty.IsVisible = false;
        FileEditorOverlay.IsVisible = false;

        if (ServerList.SelectedItem is not ServerRow row ||
            !HasAccess(row, ServerCapability.Files, ServerScope.Files))
        {
            FileStatus.Text = L.T("Servers.Files.Unavailable", "当前服务器不支持文件管理");
            return;
        }

        try
        {
            IReadOnlyList<BackendFile> entries = await row.Backend.GetFilesAsync(_fileRelativePath);
            if (_selectedBackendKey != row.Backend.BackendKey) return;
            var rows = entries.Select(entry => new FileEntryRow(
                    entry.Name,
                    entry.IsDirectory,
                    entry.SizeBytes,
                    entry.RelativePath))
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

    private async void OpenFileEntry(FileEntryRow entry)
    {
        if (ServerList.SelectedItem is not ServerRow row) return;

        if (entry.IsDir)
        {
            _fileRelativePath = entry.Relative;
            RefreshFiles();
            return;
        }

        try
        {
            BackendFileContent content = await row.Backend.ReadFileAsync(entry.Relative);
            if (_selectedBackendKey != row.Backend.BackendKey) return;
            FileEditor.Text = content.Content;
            _openFileRelativePath = entry.Relative;
            _openFileRevision = content.Revision;
            _editorDirty = false;
            FileEditorDirty.IsVisible = false;
            FileEditorTitle.Text = entry.Relative;
            ApplySyntaxHighlighting(entry.Relative);
            UpdateEditorCaret();
            UpdateEditorZoom();
            FileEditorStatus.Text = L.T("Servers.Files.Opened", "已打开 {0}", entry.Relative);
            FileEditorOverlay.IsVisible = true;
            FileEditor.Focus();
            FileEditor.ScrollToHome();
        }
        catch (Exception error)
        {
            FileStatus.Text = L.T("Servers.Files.OpenFailed", "打开失败：{0}", error.Message);
        }
    }

    private void ConfigureEditor()
    {
        FileEditor.Options.ConvertTabsToSpaces = true;
        FileEditor.Options.IndentationSize = 4;
        FileEditor.Options.EnableHyperlinks = false;
        FileEditor.Options.EnableEmailHyperlinks = false;
        FileEditor.Options.EnableRectangularSelection = true;
        FileEditor.Options.EnableTextDragDrop = true;
        FileEditor.Options.AllowScrollBelowDocument = true;
        _editorFontSize = FileEditor.FontSize;
        UpdateEditorZoom();

        try
        {
            _searchPanel = SearchPanel.Install(FileEditor);
        }
        catch (Exception error)
        {
            Debug.WriteLine($"[servers] SearchPanel install failed: {error.Message}");
        }
    }

    private async void OnFileSaveClick(object? sender, RoutedEventArgs e) => await SaveCurrentFile();

    private async Task<bool> SaveCurrentFile()
    {
        if (ServerList.SelectedItem is not ServerRow row || _openFileRelativePath is null ||
            !HasAccess(row, ServerCapability.Files, ServerScope.Files))
        {
            FileEditorStatus.Text = L.T("Servers.Files.NoFile", "先双击打开一个文本文件");
            return false;
        }

        try
        {
            await row.Backend.WriteFileAsync(_openFileRelativePath, FileEditor.Text ?? string.Empty, _openFileRevision);
            _editorDirty = false;
            FileEditorDirty.IsVisible = false;
            FileEditorStatus.Text = L.T("Servers.Files.Saved", "已保存 {0}", _openFileRelativePath);
            Shell.Toast(L.T("Servers.Files.SavedToast", "已保存 {0}", _openFileRelativePath));
            return true;
        }
        catch (Exception error)
        {
            FileEditorStatus.Text = L.T("Servers.Files.SaveFailed", "保存失败：{0}", error.Message);
            return false;
        }
    }

    private void OnFileFindClick(object? sender, RoutedEventArgs e) => OpenSearchPanel();

    private void OpenSearchPanel()
    {
        try
        {
            _searchPanel ??= SearchPanel.Install(FileEditor);
            _searchPanel.Open();

            string selected = FileEditor.SelectedText;
            if (!string.IsNullOrWhiteSpace(selected) && !selected.Contains('\n'))
            {
                _searchPanel.SearchPattern = selected;
            }
        }
        catch (Exception error)
        {
            Debug.WriteLine($"[servers] SearchPanel open failed: {error.Message}");
        }
    }

    private void OnFileReloadClick(object? sender, RoutedEventArgs e) => RequestReloadFile();

    private void RequestReloadFile()
    {
        if (_openFileRelativePath is null) return;

        if (_editorDirty)
        {
            ShowConfirmDialog(
                L.T("Servers.Files.Reload", "重新载入"),
                L.T("Servers.Files.ReloadConfirm", "放弃未保存的更改并重新从磁盘载入吗？"),
                ReloadCurrentFile);
            return;
        }

        ReloadCurrentFile();
    }

    private async void ReloadCurrentFile()
    {
        if (ServerList.SelectedItem is not ServerRow row || _openFileRelativePath is null) return;

        try
        {
            BackendFileContent content = await row.Backend.ReadFileAsync(_openFileRelativePath);
            FileEditor.Text = content.Content;
            _openFileRevision = content.Revision;
            _editorDirty = false;
            FileEditorDirty.IsVisible = false;
            UpdateEditorCaret();
            FileEditorStatus.Text = L.T("Servers.Files.Reloaded", "已重新载入 {0}", _openFileRelativePath);
        }
        catch (Exception error)
        {
            FileEditorStatus.Text = L.T("Servers.Files.ReloadFailed", "重新载入失败：{0}", error.Message);
        }
    }

    private void OnFileZoomInClick(object? sender, RoutedEventArgs e) => SetEditorFontSize(_editorFontSize + 1);

    private void OnFileZoomOutClick(object? sender, RoutedEventArgs e) => SetEditorFontSize(_editorFontSize - 1);

    private void OnFileEditorPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        e.Handled = true;
        SetEditorFontSize(_editorFontSize + (e.Delta.Y > 0 ? 1 : -1));
    }

    private void SetEditorFontSize(double size)
    {
        _editorFontSize = Math.Clamp(size, 10, 24);
        FileEditor.FontSize = _editorFontSize;
        UpdateEditorZoom();
    }

    private void UpdateEditorZoom()
    {
        FileEditorZoom.Text = L.T("Servers.Files.Zoom", "字号 {0}", (int)_editorFontSize);
    }

    private void OnFileEditorCloseClick(object? sender, RoutedEventArgs e) => RequestCloseFileEditor();

    private void OnFileEditorOverlayKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        RequestCloseFileEditor();
    }

    private void OnFileEditorOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, FileEditorOverlay)) return;
        RequestCloseFileEditor();
    }

    private void RequestCloseFileEditor()
    {
        if (!_editorDirty)
        {
            CloseFileEditor();
            return;
        }

        string name = _openFileRelativePath ?? FileEditorTitle.Text ?? string.Empty;
        var content = MofuDialog.Stack(width: 420);
        content.Children.Add(MofuDialog.Message(
            L.T("Servers.Files.UnsavedMessage", "「{0}」还有未保存的更改，关闭后会丢失。", name)));

        var cancel = MofuDialog.Secondary(
            L.T("Common.Cancel", "取消"), () => MofuDialog.Close(content));
        var discard = MofuDialog.Secondary(
            L.T("Servers.Files.Discard", "不保存"), () =>
            {
                MofuDialog.Close(content);
                CloseFileEditor();
            });
        var save = MofuDialog.Primary(
            L.T("Servers.Files.SaveAndClose", "保存并关闭"), async () =>
            {
                MofuDialog.Close(content);
                if (await SaveCurrentFile()) CloseFileEditor();
            });

        content.Children.Add(MofuDialog.Footer(cancel, discard, save));
        Shell.Dialog(content, L.T("Servers.Files.UnsavedTitle", "未保存的更改"));
    }

    private void CloseFileEditor()
    {
        FileEditorOverlay.IsVisible = false;
        FileEditorStatus.Text = string.Empty;
        FileEditorCaret.Text = string.Empty;
        FileEditorZoom.Text = string.Empty;
        FileEditorDirty.IsVisible = false;
        _editorDirty = false;
    }

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
        FileEditorDirty.IsVisible = true;
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

    private async void OnFileEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            RequestCloseFileEditor();
            return;
        }

        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (!control) return;

        if (e.Key == Key.S)
        {
            e.Handled = true;
            await SaveCurrentFile();
        }
        else if (e.Key is Key.OemPlus or Key.Add)
        {
            e.Handled = true;
            SetEditorFontSize(_editorFontSize + 1);
        }
        else if (e.Key is Key.OemMinus or Key.Subtract)
        {
            e.Handled = true;
            SetEditorFontSize(_editorFontSize - 1);
        }
        else if (e.Key is Key.D0 or Key.NumPad0)
        {
            e.Handled = true;
            _editorFontSize = 13;
            FileEditor.FontSize = _editorFontSize;
            UpdateEditorZoom();
        }
    }

    private void OnFileListContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerRow row ||
            !HasAccess(row, ServerCapability.Files, ServerScope.Files)) return;

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
        if (ServerList.SelectedItem is not ServerRow row) return;

        ShowNameDialog(L.T("Servers.Files.NewFile", "新建文件"), L.T("Servers.Files.NamePrompt", "文件名"), string.Empty, async name =>
        {
            string relative = CombineRelative(parentRelative, name);
            await row.Backend.CreateTextFileAsync(relative);
            _fileRelativePath = parentRelative;
            RefreshFiles();
            OpenFileEntry(new FileEntryRow(name, false, 0, relative));
        });
    }

    private void CreateNewFolder(string parentRelative)
    {
        if (ServerList.SelectedItem is not ServerRow row) return;

        ShowNameDialog(L.T("Servers.Files.NewFolder", "新建文件夹"), L.T("Servers.Files.NamePrompt", "文件夹名"), string.Empty, async name =>
        {
            await row.Backend.CreateDirectoryAsync(CombineRelative(parentRelative, name));
            _fileRelativePath = parentRelative;
            RefreshFiles();
        });
    }

    private void RenameEntry(FileEntryRow entry)
    {
        if (ServerList.SelectedItem is not ServerRow row) return;

        ShowNameDialog(L.T("Servers.Files.Rename", "重命名"), L.T("Servers.Files.NamePrompt", "新名称"), entry.Name, async name =>
        {
            string renamed = await row.Backend.RenameFileAsync(entry.Relative, name);
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
        if (ServerList.SelectedItem is not ServerRow row) return;

        ShowConfirmDialog(
            L.T("Servers.Files.Delete", "删除"),
            L.T("Servers.Files.DeleteConfirm", "确定要删除「{0}」吗？文件夹会连同内容一起删除。", entry.Name),
            async () =>
            {
                try
                {
                    await row.Backend.DeleteFileAsync(entry.Relative);

                    if (_openFileRelativePath is not null &&
                        (_openFileRelativePath.Equals(entry.Relative, StringComparison.OrdinalIgnoreCase) ||
                         _openFileRelativePath.StartsWith(entry.Relative + "/", StringComparison.OrdinalIgnoreCase)))
                    {
                        CloseFileEditor();
                        _openFileRelativePath = null;
                    }

                    RefreshFiles();
                    Shell.Toast(L.T("Servers.Files.Deleted", "已删除 {0}", entry.Name));
                }
                catch (Exception error)
                {
                    Shell.Toast(error.Message);
                }
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
        string.IsNullOrEmpty(parent) ? name : parent.TrimEnd('/') + "/" + name;

    private void ShowNameDialog(string title, string label, string initial, Func<string, Task> onConfirm)
    {
        var input = MofuDialog.Input(initial);

        var content = MofuDialog.Stack(width: 380);
        content.Children.Add(MofuDialog.Field(label, input));

        var cancel = MofuDialog.Secondary(L.T("Common.Cancel", "取消"), () => MofuDialog.Close(content));
        var confirm = MofuDialog.Primary(L.T("Common.Ok", "确定"), async () =>
        {
            string value = input.Text?.Trim() ?? string.Empty;
            if (value.Length == 0) return;
            MofuDialog.Close(content);
            try
            {
                await onConfirm(value);
            }
            catch (Exception error)
            {
                Shell.Toast(error.Message);
            }
        });

        content.Children.Add(MofuDialog.Footer(cancel, confirm));
        Shell.Dialog(content, title);
        input.Focus();
    }

    private void ShowConfirmDialog(string title, string message, Action onConfirm)
    {
        var content = MofuDialog.Stack(width: 400);
        content.Children.Add(MofuDialog.Message(message));

        var cancel = MofuDialog.Secondary(L.T("Common.Cancel", "取消"), () => MofuDialog.Close(content));
        var confirm = MofuDialog.Primary(L.T("Common.Ok", "确定"), () =>
        {
            MofuDialog.Close(content);
            try
            {
                onConfirm();
            }
            catch (Exception error)
            {
                Shell.Toast(error.Message);
            }
        });

        content.Children.Add(MofuDialog.Footer(cancel, confirm));
        Shell.Dialog(content, title);
    }

    private void OnRewindRefreshClick(object? sender, RoutedEventArgs e) => RefreshRewindBackups();

    private void RefreshRewindBackups()
    {
        RewindPanel.Children.Clear();

        if (ServerList.SelectedItem is not ServerRow { IsLocal: true } || _selectedServerId is not { } id)
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
        if (ServerList.SelectedItem is not ServerRow { IsLocal: true } || _selectedServerId is not { } id) return;
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
        if (ServerList.SelectedItem is not ServerRow { IsLocal: true } || _selectedServerId is not { } id) return;

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
        if (ServerList.SelectedItem is not ServerRow { IsLocal: true } || _selectedServerId is not { } id) return;
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

    private async void BuildPropertiesEditor()
    {
        PropertiesPanel.Children.Clear();
        _propertyGetters.Clear();

        if (ServerList.SelectedItem is not ServerRow row ||
            !HasAccess(row, ServerCapability.Properties, ServerScope.Files))
        {
            PropertiesStatus.Text = L.T("Servers.Properties.Unavailable", "当前服务器不支持属性编辑");
            return;
        }

        try
        {
            BackendProperties model = await row.Backend.GetPropertiesAsync();
            if (_selectedBackendKey != row.Backend.BackendKey) return;
            _propertiesRevision = model.Revision;
            IReadOnlyList<BackendProperty> properties = model.Values;
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
            foreach (BackendProperty property in properties)
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

    private async void OnPropertiesSaveClick(object? sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerRow row ||
            !HasAccess(row, ServerCapability.Properties, ServerScope.Files)) return;
        if (ConfirmRemoteAction(row, "覆盖远程配置",
                L.T("Servers.Remote.PropertiesConfirm", "配置将写盘并在重启后生效，继续吗？"),
                () => SavePropertiesAsync(row))) return;
        await SavePropertiesAsync(row);
    }

    private async Task SavePropertiesAsync(ServerRow row)
    {
        try
        {
            var values = _propertyGetters
                .Select(pair => new BackendProperty(pair.Key, pair.Value()))
                .ToList();
            BackendOperation result = await row.Backend.SetPropertiesAsync(values, _propertiesRevision);
            string message = result.PendingRestart
                ? L.T("Servers.Properties.RestartRequired", "已保存，重启后生效")
                : L.T("Servers.Properties.Saved", "已保存 server.properties");
            PropertiesStatus.Text = message;
            Shell.Toast(message);
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

    private async void OnExportServerClick(object? sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerRow { IsLocal: true } || _selectedServerId is not { } id) return;

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

    private void OnUnpairServerClick(object? sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerRow { IsLocal: false } row) return;

        var content = MofuDialog.Stack(width: 380);
        content.Children.Add(MofuDialog.Message(
            L.T("Servers.Remote.UnpairConfirm",
                "确定要解除与「{0}」的配对吗？只会删除本机保存的令牌，服务器端不受影响。", row.Name)));

        var cancel = MofuDialog.Secondary(L.T("Common.Cancel", "取消"), () => MofuDialog.Close(content));
        var confirm = MofuDialog.Danger(L.T("Servers.Remote.Unpair", "解除配对"), () =>
        {
            MofuDialog.Close(content);
            try
            {
                RemoteServerStore.Delete(row.Backend.BackendKey);
                row.Backend.Dispose();
                LoadServers();
                RefreshServerList();
                UpdateChips();
                ServerList.SelectedIndex = _visibleServers.Count > 0 ? 0 : -1;
                if (ServerList.SelectedItem is ServerRow remaining) LoadDetail(remaining);
                else ShowEmptyDetail();
                Shell.Toast(L.T("Servers.Remote.Unpaired", "已解除与「{0}」的配对", row.Name));
            }
            catch (Exception error)
            {
                Shell.Toast(L.T("Servers.Remote.UnpairFailed", "解除配对失败：{0}", error.Message));
            }
        });

        content.Children.Add(MofuDialog.Footer(cancel, confirm));
        Shell.Dialog(content, L.T("Servers.Remote.UnpairTitle", "解除远程配对"), sender as Control);
    }

    private void OnDeleteServerClick(object? sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerRow { IsLocal: true } || _selectedServerId is not { } id) return;

        if (CoreServer.IsRunning(id))
        {
            Shell.Toast(L.T("Servers.Delete.Running", "请先停止服务器再删除"));
            return;
        }

        ServerRow? row = _visibleServers.FirstOrDefault(server => server.ServerId == id);
        string name = row?.Name ?? id;

        var content = MofuDialog.Stack(width: 380);
        content.Children.Add(MofuDialog.Message(
            L.T("Servers.Delete.Confirm", "确定要删除服务器「{0}」吗？目录、存档与备份都会被删除，无法恢复。", name)));

        var cancel = MofuDialog.Secondary(L.T("Common.Cancel", "取消"), () => MofuDialog.Close(content));
        var confirm = MofuDialog.Danger(L.T("Servers.Delete.Action", "删除"), () =>
        {
            MofuDialog.Close(content);
            try
            {
                CoreServer.DeleteServer(id);
                LoadServers();
                RefreshServerList();
                UpdateChips();
                ServerList.SelectedIndex = _visibleServers.Count > 0 ? 0 : -1;
                if (ServerList.SelectedItem is ServerRow remaining) LoadDetail(remaining);
                else ShowEmptyDetail();
                Shell.Toast(L.T("Servers.Delete.Done", "已删除服务器「{0}」", name));
            }
            catch (Exception error)
            {
                Shell.Toast(L.T("Servers.Delete.Failed", "删除失败：{0}", error.Message));
            }
        });

        content.Children.Add(MofuDialog.Footer(cancel, confirm));

        Shell.Dialog(content, L.T("Servers.Delete.Title", "删除服务器"), sender as Control);
    }

    private static void DismissDialog(Control control) =>
        control.FindAncestorOfType<MofuDialogHost>(true)?.Manager?.DismissDialog();

}

public sealed class ServerRow : INotifyPropertyChanged
{
    private string _status = CoreServer.StatusStopped;
    private int _players;

    private ServerRow()
    {
    }

    public static ServerRow FromBackend(IServerBackend backend, ServerDescription description) => new()
    {
        Backend = backend,
        ServerId = description.Id,
        Name = description.Name,
        Address = description.Address,
        Version = BuildVersion(description.McVersion, description.Core, description.CoreVersion),
        Motd = description.Motd,
        MaxPlayers = description.MaxPlayers,
    };

    public IServerBackend Backend { get; private init; } = null!;

    public string? ServerId { get; private init; }

    public bool IsLocal => Backend.IsLocal;

    public IReadOnlyList<string> Capabilities => Backend.Capabilities;

    public IReadOnlyList<string> Scopes => Backend.Scopes;

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
        "crashed" => L.T("Servers.Status.Crashed", "已崩溃"),
        "unreachable" => L.T("Servers.Status.Unreachable", "连接中断"),
        _ => L.T("Servers.Status.Stopped", "已停止"),
    };

    public IBrush StatusBg => ThemeTokens.Brush(_status switch
    {
        CoreServer.StatusRunning => "NekoSuccessSoftBrush",
        CoreServer.StatusStarting or CoreServer.StatusStopping or "crashed" or "unreachable" => "NekoWarningSoftBrush",
        _ => "NekoFieldBrush",
    });

    public IBrush StatusFg => ThemeTokens.Brush(_status switch
    {
        CoreServer.StatusRunning => "NekoSuccessBrush",
        CoreServer.StatusStarting or CoreServer.StatusStopping or "crashed" or "unreachable" => "NekoWarningBrush",
        _ => "NekoMutedBrush",
    });

    public string SubLine => $"{Version} · {Address} · {_players}/{MaxPlayers}";

    public bool Matches(string query) =>
        Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Address.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Version.Contains(query, StringComparison.OrdinalIgnoreCase);

    internal static string BuildVersion(string mcVersion, string core, string coreVersion)
    {
        var coreName = core switch
        {
            CoreServer.CorePaper => "Paper",
            CoreServer.CoreFabric => "Fabric",
            CoreServer.CoreNeoForge => "NeoForge",
            "remote" => "Remote",
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
