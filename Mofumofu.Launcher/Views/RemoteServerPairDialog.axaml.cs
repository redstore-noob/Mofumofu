using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Mofumofu.Launcher.Controls;
using Mofumofu.Launcher.Services.Servers;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views;

public partial class RemoteServerPairDialog : UserControl
{
    private bool _busy;

    public RemoteServerPairDialog()
    {
        InitializeComponent();
        Unloaded += (_, _) => PairBox.Text = string.Empty;
    }

    public event Action<RemoteServerConnection>? ServerPaired;

    private async void OnPairClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || string.IsNullOrWhiteSpace(PairBox.Text)) return;
        _busy = true;
        PairButton.IsEnabled = false;
        PairStatus.Text = L.T("Servers.Remote.Pairing", "正在验证服务端并派生最小权限令牌…");
        try
        {
            RemoteServerConnection connection = await RemotePairingService.PairAsync(PairBox.Text);
            PairBox.Text = string.Empty;
            PairStatus.Text = L.T("Servers.Remote.PairDone", "配对完成：{0}", connection.Name);
            ServerPaired?.Invoke(connection);
            MofuDialog.Close(this);
        }
        catch (Exception error)
        {
            PairStatus.Text = L.T("Servers.Remote.PairFailed", "配对失败：{0}", error.Message);
        }
        finally
        {
            _busy = false;
            PairButton.IsEnabled = true;
        }
    }
}
