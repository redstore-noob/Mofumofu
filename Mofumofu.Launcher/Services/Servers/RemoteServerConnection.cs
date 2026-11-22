using System;
using System.Collections.Generic;

namespace Mofumofu.Launcher.Services.Servers;

public sealed record RemoteServerConnection
{
    public string InstanceId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = string.Empty;
    public string DailyToken { get; init; } = string.Empty;
    public string CommandToken { get; init; } = string.Empty;
    public List<string> Capabilities { get; init; } = [];
    public List<string> Scopes { get; init; } = [];
    public string DeploymentMode { get; init; } = string.Empty;
    public string McVersion { get; init; } = string.Empty;
    public string Motd { get; init; } = "—";
    public int MaxPlayers { get; init; }
    public string ImplementationVersion { get; init; } = string.Empty;

    public string BackendKey => $"remote:{InstanceId}@{BaseUrl}";
    public bool NeedsPairing => string.IsNullOrWhiteSpace(DailyToken) || string.IsNullOrWhiteSpace(CommandToken);
}
