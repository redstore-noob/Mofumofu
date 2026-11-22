namespace Mofumofu.RemoteProtocol;

public static class ProtocolConstants
{
    public const string ApiVersion = "1.0";
    public const int ApiMajorVersion = 1;
    public const string ServerCollectionPath = "/v1/servers";
    public const string ServerBasePath = "/v1/servers/{instanceId}";
    public const string WebSocketPath = "/ws";
    public const int TokenRandomBytes = 32;
    public const int TokenHexLength = TokenRandomBytes * 2;
    public const int MaxPlayerNameLength = 16;
    public const int MaxCommandLength = 256;
    public const int ConfirmationLifetimeSeconds = 60;
    public const int DefaultTokenRotationGraceSeconds = 300;

    public static string GetServerBasePath(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            throw new ArgumentException("Instance ID cannot be empty.", nameof(instanceId));
        }

        return $"{ServerCollectionPath}/{Uri.EscapeDataString(instanceId)}";
    }
}

/// <remarks>Capabilities are open-ended strings. Receivers must ignore values they do not know.</remarks>
public static class ProtocolCapabilities
{
    public const string Read = "read";
    public const string Players = "players";
    public const string Files = "files";
    public const string Backup = "backup";
    public const string Control = "control";
    public const string Command = "command";
    public const string Console = "console";
    public const string Events = "events";
    public const string Properties = "properties";
    public const string Tokens = "tokens";

    public static IReadOnlyList<string> Known { get; } =
        [Read, Players, Files, Backup, Control, Command, Console, Events, Properties, Tokens];
}

public static class ProtocolScopes
{
    public const string Read = "read";
    public const string Players = "players";
    public const string Files = "files";
    public const string Backup = "backup";
    public const string Control = "control";
    public const string Command = "command";

    public static IReadOnlyList<string> All { get; } = [Read, Players, Files, Backup, Control, Command];
}

public static class ProtocolDeploymentModes
{
    public const string Agent = "agent";
    public const string Probe = "probe";
}

public static class ProtocolServerStates
{
    public const string Offline = "offline";
    public const string Starting = "starting";
    public const string Running = "running";
    public const string Stopping = "stopping";
    public const string Crashed = "crashed";
}

public static class ProtocolControlActions
{
    public const string Start = "start";
    public const string Stop = "stop";
    public const string Restart = "restart";
    public const string Kill = "kill";
}

public static class ProtocolPlayerLists
{
    public const string Whitelist = "whitelist";
    public const string Ops = "ops";
    public const string Bans = "bans";
}

public static class ProtocolOperationStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

public static class ProtocolConfirmationActions
{
    public const string ControlStart = "control.start";
    public const string ControlStop = "control.stop";
    public const string ControlRestart = "control.restart";
    public const string ControlKill = "control.kill";
    public const string BackupRestore = "backup.restore";
    public const string BackupDelete = "backup.delete";
    public const string FileDelete = "file.delete";
    public const string PropertiesPatch = "properties.patch";
}

public static class ProtocolEventTypes
{
    public const string PlayerJoined = "player.joined";
    public const string PlayerLeft = "player.left";
    public const string ServerStateChanged = "server.state_changed";
    public const string ServerCrashed = "server.crashed";
    public const string ServerAutoRestarted = "server.auto_restarted";
    public const string BackupRestoreReady = "backup.restore_ready";
}

public static class WebSocketFrameTypes
{
    public const string Console = "console";
    public const string Event = "event";
    public const string Snapshot = "snapshot";
    public const string Reset = "reset";
    public const string Operation = "operation";
}
