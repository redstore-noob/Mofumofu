using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Mofumofu.Launcher.Controls;
using Mofumofu.Launcher.Services;
using L = Mofumofu.Launcher.Localization.Localization;
using CoreCreateOptions = MofuCore.Server.CreateOptions;
using CoreInstallOptions = MofuCore.Server.ServerDownload.InstallOptions;
using CoreInstallProgress = MofuCore.Server.ServerDownload.InstallProgress;
using CoreServer = MofuCore.Server.ServerManage;
using CoreServerDownload = MofuCore.Server.ServerDownload;

namespace Mofumofu.Launcher.Views;

public partial class ServerCreateDialog : UserControl
{
    private readonly ObservableCollection<VersionOption> _visibleVersions = new();
    private IReadOnlyList<string> _allVersions = [];
    private CancellationTokenSource? _loadCts;
    private string _coreTag = "Vanilla";
    private bool _ready;
    private bool _busy;

    public ServerCreateDialog()
    {
        InitializeComponent();

        VersionList.ItemsSource = _visibleVersions;
        NameBox.Text = L.T("Servers.Create.DefaultName", "Mofu Server");
        SetCoreActive("Vanilla");

        Loaded += OnLoaded;
    }

    /// <summary>服务器创建完成时回调，参数为服务器 ID。</summary>
    public event Action<string>? ServerCreated;

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_ready) return;
        _ready = true;
        _ = LoadVersionsAsync();
        NameBox.Focus();
    }

    private void SetCoreActive(string tag)
    {
        _coreTag = tag;
        CoreVanillaButton.Classes.Set("active", tag == "Vanilla");
        CorePaperButton.Classes.Set("active", tag == "Paper");
        CoreFabricButton.Classes.Set("active", tag == "Fabric");
        CoreNeoForgeButton.Classes.Set("active", tag == "NeoForge");
    }

    private async void OnCoreClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string tag } || tag == _coreTag) return;
        SetCoreActive(tag);
        await LoadVersionsAsync();
    }

    private static CoreServerDownload.CoreKind KindOf(string tag) => tag switch
    {
        "Paper" => CoreServerDownload.CoreKind.Paper,
        "Fabric" => CoreServerDownload.CoreKind.Fabric,
        "NeoForge" => CoreServerDownload.CoreKind.NeoForge,
        _ => CoreServerDownload.CoreKind.Vanilla,
    };

    private async Task LoadVersionsAsync()
    {
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        _allVersions = [];
        _visibleVersions.Clear();
        VersionCountText.Text = "—";
        VersionEmptyText.Text = L.T("Servers.Create.LoadingVersions", "正在载入版本列表…");
        VersionEmptyText.IsVisible = true;
        VersionHint.Text = string.Empty;
        CreateButton.IsEnabled = false;

        try
        {
            IReadOnlyList<string> versions = await CoreServerDownload.ListMinecraftVersionsAsync(
                KindOf(_coreTag), cts.Token);
            if (cts.IsCancellationRequested) return;

            _allVersions = versions;
            ApplyVersionFilter(selectFirst: true);

            if (versions.Count == 0)
            {
                VersionHint.Text = L.T("Servers.Create.NoVersions", "没有获取到可用的 Minecraft 版本");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            VersionHint.Text = L.T("Servers.Create.LoadFailed", "获取版本列表失败：{0}", ex.Message);
            VersionEmptyText.IsVisible = true;
            VersionEmptyText.Text = L.T("Servers.Create.NoMatch", "没有匹配的版本");
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                CreateButton.IsEnabled = !_busy && _allVersions.Count > 0;
            }
        }
    }

    private void ApplyVersionFilter(bool selectFirst)
    {
        string query = (VersionSearch.Text ?? string.Empty).Trim();
        VersionOption? previous = VersionList.SelectedItem as VersionOption;
        string? previousName = previous?.Name;

        _visibleVersions.Clear();
        for (int index = 0; index < _allVersions.Count; index++)
        {
            string name = _allVersions[index];
            if (query.Length > 0 && !name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            _visibleVersions.Add(new VersionOption { Name = name, IsLatest = index == 0 });
        }

        VersionCountText.Text = L.T("Servers.Create.VersionCount", "{0} 个可用版本", _visibleVersions.Count);
        VersionEmptyText.Text = _allVersions.Count == 0
            ? L.T("Servers.Create.LoadingVersions", "正在载入版本列表…")
            : L.T("Servers.Create.NoMatch", "没有匹配的版本");
        VersionEmptyText.IsVisible = _visibleVersions.Count == 0;

        if (_visibleVersions.Count == 0)
        {
            VersionList.SelectedIndex = -1;
            return;
        }

        if (!selectFirst && previousName is not null)
        {
            int found = -1;
            for (int index = 0; index < _visibleVersions.Count; index++)
            {
                if (_visibleVersions[index].Name == previousName)
                {
                    found = index;
                    break;
                }
            }
            if (found >= 0)
            {
                VersionList.SelectedIndex = found;
                return;
            }
        }

        VersionList.SelectedIndex = 0;
    }

    private void OnVersionSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        ApplyVersionFilter(selectFirst: false);
    }

    private void OnVersionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (VersionList.SelectedItem is not null)
        {
            VersionHint.Text = string.Empty;
        }
    }

    private void OnEulaClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(CoreServerDownload.EulaUrl) { UseShellExecute = true });
        }
        catch
        {
            // 打不开浏览器时静默忽略，不影响创建流程。
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => MofuDialog.Close(this);

    private async void OnCreateClick(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (EulaCheck.IsChecked != true)
        {
            SetError(L.T("Servers.Create.EulaRequired", "需要先勾选同意 Minecraft EULA 才能创建服务器"));
            return;
        }
        if (VersionList.SelectedItem is not VersionOption version)
        {
            SetError(L.T("Servers.Create.SelectVersion", "请选择一个 Minecraft 版本"));
            return;
        }
        if (!int.TryParse((PortBox.Text ?? string.Empty).Trim(), out int port) || port is <= 0 or > 65535)
        {
            SetError(L.T("Servers.Create.PortInvalid", "端口需要是 1 - 65535 之间的数字"));
            return;
        }

        string name = (NameBox.Text ?? string.Empty).Trim();
        if (name.Length == 0) name = L.T("Servers.Create.DefaultName", "Mofu Server");

        string minecraftVersion = version.Name;
        (string coreId, CoreServerDownload.CoreKind kind) = _coreTag switch
        {
            "Paper" => (CoreServer.CorePaper, CoreServerDownload.CoreKind.Paper),
            "Fabric" => (CoreServer.CoreFabric, CoreServerDownload.CoreKind.Fabric),
            "NeoForge" => (CoreServer.CoreNeoForge, CoreServerDownload.CoreKind.NeoForge),
            _ => (CoreServer.CoreVanilla, CoreServerDownload.CoreKind.Vanilla),
        };

        _busy = true;
        CreateButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        CreateProgress.IsVisible = true;
        CreateProgress.Value = 0;
        SetInfo(L.T("Servers.Create.Preparing", "正在准备服务端…"));

        try
        {
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
                            CreateProgress.Value = value.Percentage;
                            if (!string.IsNullOrWhiteSpace(value.Detail))
                            {
                                SetInfo(value.Detail);
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

            SetInfo(L.T("Servers.Create.Done", "服务器创建完成"));
            ServerCreated?.Invoke(serverId);
            MofuDialog.Close(this);
        }
        catch (Exception ex)
        {
            SetError(L.T("Servers.Create.Failed", "创建服务器失败：{0}", ex.Message));
        }
        finally
        {
            _busy = false;
            CreateButton.IsEnabled = _allVersions.Count > 0;
            CancelButton.IsEnabled = true;
            CreateProgress.IsVisible = false;
        }
    }

    private void SetError(string message)
    {
        StatusText.Foreground = ThemeTokens.Brush("NekoDangerBrush");
        StatusText.Text = message;
    }

    private void SetInfo(string message)
    {
        StatusText.Foreground = ThemeTokens.Brush("NekoMutedBrush");
        StatusText.Text = message;
    }

    private sealed class VersionOption
    {
        public string Name { get; init; } = string.Empty;

        public bool IsLatest { get; init; }
    }
}
