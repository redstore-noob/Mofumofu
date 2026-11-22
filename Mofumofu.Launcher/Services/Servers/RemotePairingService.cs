using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mofumofu.RemoteProtocol;

namespace Mofumofu.Launcher.Services.Servers;

public sealed record RemotePairingRequest(string BaseUrl, string InstanceId, string RootToken);

public static class RemotePairingService
{
    public static RemotePairingRequest Parse(string pairingLink)
    {
        if (!Uri.TryCreate(pairingLink?.Trim(), UriKind.Absolute, out Uri? uri) ||
            !string.Equals(uri.Scheme, "mofumofu", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "pair", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("配对链接无效，应使用 mofumofu://pair 链接。");
        }

        Dictionary<string, string> values = [];
        foreach (string part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = part.IndexOf('=');
            if (separator <= 0) continue;
            string key = Uri.UnescapeDataString(part[..separator]);
            string value = Uri.UnescapeDataString(part[(separator + 1)..].Replace('+', ' '));
            values[key] = value;
        }

        string host = values.GetValueOrDefault("host") ?? string.Empty;
        if (!host.Contains("://", StringComparison.Ordinal)) host = "https://" + host;
        string instance = values.GetValueOrDefault("instance") ?? string.Empty;
        string token = values.GetValueOrDefault("token") ?? string.Empty;
        ProtocolVersion.EnsureCompatible(values.GetValueOrDefault("api") ?? ProtocolConstants.ApiVersion);
        if (string.IsNullOrWhiteSpace(instance) || !TokenUtilities.IsValidToken(token))
            throw new ArgumentException("配对链接缺少有效的实例标识或根令牌。");
        return new RemotePairingRequest(host, instance, token);
    }

    public static async Task<RemoteServerConnection> PairAsync(string pairingLink,
        CancellationToken cancellationToken = default)
    {
        RemotePairingRequest pairing = Parse(pairingLink);
        using var client = new RemoteApiClient(pairing.BaseUrl, pairing.InstanceId);
        HandshakeResponse handshake = await client.HandshakeAsync(pairing.RootToken, cancellationToken);
        if (!string.Equals(handshake.InstanceId, pairing.InstanceId, StringComparison.Ordinal))
            throw new RemoteApiException("服务端实例标识与配对链接不一致。", code: ProtocolErrors.InvalidRequest);
        if (!handshake.Capabilities.Contains(ProtocolCapabilities.Tokens))
            throw new RemoteApiException("服务端未声明 token 管理能力，无法安全派生子令牌。",
                code: ProtocolErrors.CapabilityNotSupported);

        var dailyScopes = new List<string>
        {
            ProtocolScopes.Read, ProtocolScopes.Players, ProtocolScopes.Files, ProtocolScopes.Backup,
        };
        if (handshake.Capabilities.Contains(ProtocolCapabilities.Control)) dailyScopes.Add(ProtocolScopes.Control);

        TokenIssuedResponse daily = await client.DeriveTokenAsync(pairing.RootToken,
            "Mofumofu 日常管理", dailyScopes, cancellationToken);
        TokenIssuedResponse console = await client.DeriveTokenAsync(pairing.RootToken,
            "Mofumofu 控制台", [ProtocolScopes.Read, ProtocolScopes.Command], cancellationToken);

        StatusResponse? status = null;
        try { status = await client.GetStatusAsync(daily.Token, cancellationToken); } catch { }
        var connection = new RemoteServerConnection
        {
            InstanceId = handshake.InstanceId,
            Name = string.IsNullOrWhiteSpace(handshake.ServerName) ? handshake.InstanceId : handshake.ServerName,
            BaseUrl = pairing.BaseUrl.TrimEnd('/'),
            DailyToken = daily.Token,
            CommandToken = console.Token,
            Capabilities = handshake.Capabilities,
            Scopes = daily.TokenInfo.Scopes
                .Union(console.TokenInfo.Scopes, StringComparer.Ordinal)
                .ToList(),
            DeploymentMode = handshake.DeploymentMode,
            ImplementationVersion = handshake.ServerImplementationVersion,
            McVersion = status?.MinecraftVersion ?? string.Empty,
            Motd = string.IsNullOrWhiteSpace(status?.Motd) ? "—" : status.Motd,
            MaxPlayers = status?.MaxPlayers ?? 0,
        };
        RemoteServerStore.Save(connection);
        return connection;
    }
}
