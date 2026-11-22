using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MofuCore.Config;
using Mofumofu.Launcher.Services.Agent;

namespace Mofumofu.Launcher.Services.Servers;

public static class RemoteServerStore
{
    private sealed class StoredConnection
    {
        public string InstanceId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = string.Empty;
        public string DailyToken { get; set; } = string.Empty;
        public string CommandToken { get; set; } = string.Empty;
        public List<string> Capabilities { get; set; } = [];
        public List<string> Scopes { get; set; } = [];
        public string DeploymentMode { get; set; } = string.Empty;
        public string McVersion { get; set; } = string.Empty;
        public string Motd { get; set; } = "—";
        public int MaxPlayers { get; set; }
        public string ImplementationVersion { get; set; } = string.Empty;
    }

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static string StorePath => Path.Combine(YamlRead.ConfigDirectory, "remote-servers.json");

    public static IReadOnlyList<RemoteServerConnection> List()
    {
        lock (Gate)
        {
            if (!File.Exists(StorePath)) return [];
            try
            {
                var stored = JsonSerializer.Deserialize<List<StoredConnection>>(File.ReadAllText(StorePath)) ?? [];
                return stored.Select(item => new RemoteServerConnection
                {
                    InstanceId = item.InstanceId,
                    Name = item.Name,
                    BaseUrl = item.BaseUrl,
                    DailyToken = Unprotect(item.DailyToken),
                    CommandToken = Unprotect(item.CommandToken),
                    Capabilities = item.Capabilities ?? [],
                    Scopes = item.Scopes ?? [],
                    DeploymentMode = item.DeploymentMode,
                    McVersion = item.McVersion,
                    Motd = item.Motd,
                    MaxPlayers = item.MaxPlayers,
                    ImplementationVersion = item.ImplementationVersion,
                }).Where(item => !string.IsNullOrWhiteSpace(item.InstanceId) &&
                                 !string.IsNullOrWhiteSpace(item.BaseUrl)).ToList();
            }
            catch (Exception) when (!File.Exists(StorePath))
            {
                return [];
            }
        }
    }

    public static void Save(RemoteServerConnection connection)
    {
        lock (Gate)
        {
            List<RemoteServerConnection> connections = List().ToList();
            connections.RemoveAll(item => item.BackendKey == connection.BackendKey);
            connections.Add(connection);
            Write(connections);
        }
    }

    public static void Delete(string backendKey)
    {
        lock (Gate)
        {
            List<RemoteServerConnection> connections = List().ToList();
            connections.RemoveAll(item => item.BackendKey == backendKey);
            Write(connections);
        }
    }

    private static void Write(IEnumerable<RemoteServerConnection> connections)
    {
        Directory.CreateDirectory(YamlRead.ConfigDirectory);
        var stored = connections.Select(item => new StoredConnection
        {
            InstanceId = item.InstanceId,
            Name = item.Name,
            BaseUrl = item.BaseUrl,
            DailyToken = AgentSecretProtector.Protect(item.DailyToken),
            CommandToken = AgentSecretProtector.Protect(item.CommandToken),
            Capabilities = item.Capabilities,
            Scopes = item.Scopes,
            DeploymentMode = item.DeploymentMode,
            McVersion = item.McVersion,
            Motd = item.Motd,
            MaxPlayers = item.MaxPlayers,
            ImplementationVersion = item.ImplementationVersion,
        }).ToList();
        string temp = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(stored, JsonOptions));
        File.Move(temp, StorePath, true);
    }

    private static string Unprotect(string value)
    {
        string plaintext = AgentSecretProtector.Unprotect(value);
        return AgentSecretProtector.LooksEncrypted(plaintext) ? string.Empty : plaintext;
    }
}
