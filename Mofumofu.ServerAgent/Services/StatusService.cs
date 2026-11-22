using MofuCore.Server;
using Mofumofu.RemoteProtocol;
using Mofumofu.ServerAgent.Core;
using Mofumofu.ServerAgent.Files;

namespace Mofumofu.ServerAgent.Services;

public sealed class StatusService
{
    private readonly AgentOptions _options;
    private readonly ICoreGateway _core;
    private readonly PropertyPatchService _properties;
    private readonly IClock _clock;

    public StatusService(AgentOptions options, ICoreGateway core, PropertyPatchService properties, IClock clock)
    {
        _options = options;
        _core = core;
        _properties = properties;
        _clock = clock;
    }

    public async Task<StatusResponse> GetAsync(CancellationToken cancellationToken)
    {
        ServerInfo info = _core.GetServerInfo(_options.InstanceId)
            ?? throw ApiException.NotFound("The server instance no longer exists.");
        ServerSnapshot snapshot = _core.Poll(_options.InstanceId, 0);
        string? motd = null;
        try
        {
            ServerPropertiesResponse properties = await _properties.GetAsync(cancellationToken);
            properties.Properties.TryGetValue("motd", out motd);
        }
        catch (ApiException error) when (error.Code == ProtocolErrors.NotFound)
        {
            // server.properties may not exist until the first launch.
        }

        bool active = snapshot.Status is ServerManage.StatusRunning or ServerManage.StatusStarting
            or ServerManage.StatusStopping;
        return new StatusResponse
        {
            InstanceId = _options.InstanceId,
            State = MapState(snapshot.Status),
            OnlinePlayers = snapshot.Players,
            MaxPlayers = info.MaxPlayers,
            CpuPercent = active ? snapshot.CpuPercent : null,
            MemoryUsedBytes = active ? (long)(snapshot.MemoryMb * 1024 * 1024) : null,
            MemoryMaxBytes = null,
            Tps = null,
            UptimeSeconds = null,
            MinecraftVersion = info.McVersion,
            Motd = motd,
            ObservedAt = _clock.UtcNow,
        };
    }

    public static string MapState(string coreStatus) => coreStatus switch
    {
        ServerManage.StatusRunning => ProtocolServerStates.Running,
        ServerManage.StatusStarting => ProtocolServerStates.Starting,
        ServerManage.StatusStopping => ProtocolServerStates.Stopping,
        _ => ProtocolServerStates.Offline,
    };
}
