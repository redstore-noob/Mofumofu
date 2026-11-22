using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MofuCore.Server;

namespace Mofumofu.Launcher.Services.Servers;

public sealed record BackendRegistration(IServerBackend Backend, ServerDescription Description)
{
    public string Motd => Description.Motd;
}

public static class ServerBackendRegistry
{
    public static async Task<IReadOnlyList<BackendRegistration>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        List<BackendRegistration> result = (await LoadLocalAsync(cancellationToken)).ToList();
        try
        {
            foreach (RemoteServerConnection connection in RemoteServerStore.List())
            {
                var backend = new RemoteServerBackend(connection);
                result.Add(new BackendRegistration(backend, backend.CurrentDescription));
            }
        }
        catch (Exception error)
        {
            Debug.WriteLine($"[servers] Load remote registrations failed: {error.Message}");
        }
        return result;
    }

    public static async Task<IReadOnlyList<BackendRegistration>> LoadLocalAsync(
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<BackendRegistration> result = [];
            foreach (ServerInfo info in ServerManage.ListServerInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var backend = new LocalServerBackend(info);
                result.Add(new BackendRegistration(backend, backend.CurrentDescription));
            }
            return (IReadOnlyList<BackendRegistration>)result;
        }, cancellationToken);
    }
}
