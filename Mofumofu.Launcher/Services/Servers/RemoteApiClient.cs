using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mofumofu.RemoteProtocol;

namespace Mofumofu.Launcher.Services.Servers;

public sealed class RemoteApiException : Exception
{
    public RemoteApiException(string message, HttpStatusCode? statusCode = null, string? code = null,
        Exception? innerException = null) : base(message, innerException)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public HttpStatusCode? StatusCode { get; }
    public string? Code { get; }
    public bool IsAuthenticationFailure => StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}

public sealed class RemoteApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _serverBaseUrl;

    public RemoteApiClient(string baseUrl, string instanceId, HttpMessageHandler? handler = null)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps && !(baseUri.Scheme == Uri.UriSchemeHttp && IsLoopback(baseUri))))
        {
            throw new ArgumentException("远程地址必须是 HTTPS；仅回环测试地址允许 HTTP。", nameof(baseUrl));
        }
        if (string.IsNullOrWhiteSpace(instanceId)) throw new ArgumentException("实例标识不能为空。", nameof(instanceId));

        InstanceId = instanceId;
        BaseUrl = baseUrl.TrimEnd('/');
        _serverBaseUrl = BaseUrl + ProtocolConstants.GetServerBasePath(instanceId);
        _http = handler is null ? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(12),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        }) : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(2);
    }

    public string InstanceId { get; }
    public string BaseUrl { get; }

    public Task<HandshakeResponse> HandshakeAsync(string token, CancellationToken cancellationToken = default) =>
        SendAsync<HandshakeResponse>(HttpMethod.Post, "/handshake", new HandshakeRequest
        {
            ClientName = "Mofumofu.Launcher",
            ClientVersion = typeof(RemoteApiClient).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            RequestedCapabilities = [ProtocolCapabilities.Read, ProtocolCapabilities.Console,
                ProtocolCapabilities.Players, ProtocolCapabilities.Files, ProtocolCapabilities.Properties,
                ProtocolCapabilities.Backup, ProtocolCapabilities.Control, ProtocolCapabilities.Events],
        }, token, cancellationToken);

    public Task<StatusResponse> GetStatusAsync(string token, CancellationToken cancellationToken = default) =>
        SendAsync<StatusResponse>(HttpMethod.Get, "/status", null, token, cancellationToken);

    public Task<OnlinePlayersResponse> GetOnlinePlayersAsync(string token, CancellationToken cancellationToken = default) =>
        SendAsync<OnlinePlayersResponse>(HttpMethod.Get, "/players/online", null, token, cancellationToken);

    public Task<PlayerListResponse> GetPlayerListAsync(string kind, string token,
        CancellationToken cancellationToken = default) =>
        SendAsync<PlayerListResponse>(HttpMethod.Get, "/players/" + Uri.EscapeDataString(kind), null, token, cancellationToken);

    public Task<PlayerMutationResponse> AddPlayerAsync(string kind, string name, string token,
        string? revision = null, string? reason = null, CancellationToken cancellationToken = default) =>
        SendAsync<PlayerMutationResponse>(HttpMethod.Post, $"/players/{Uri.EscapeDataString(kind)}/add",
            new PlayerAddRequest
            {
                PlayerName = name,
                ExpectedRevision = revision,
                OpLevel = kind == ProtocolPlayerLists.Ops ? 4 : null,
                Reason = reason,
            }, token, cancellationToken);

    public Task<PlayerMutationResponse> RemovePlayerAsync(string kind, string name, string token,
        string? revision = null, CancellationToken cancellationToken = default) =>
        SendAsync<PlayerMutationResponse>(HttpMethod.Post, $"/players/{Uri.EscapeDataString(kind)}/remove",
            new PlayerRemoveRequest { PlayerName = name, ExpectedRevision = revision }, token, cancellationToken);

    public Task<FileListResponse> GetFilesAsync(string path, string token,
        CancellationToken cancellationToken = default) =>
        SendAsync<FileListResponse>(HttpMethod.Get, "/files?path=" + Uri.EscapeDataString(path), null, token, cancellationToken);

    public Task<FileContentResponse> ReadFileAsync(string path, string token,
        CancellationToken cancellationToken = default) =>
        SendAsync<FileContentResponse>(HttpMethod.Get, "/files/content?path=" + Uri.EscapeDataString(path), null, token, cancellationToken);

    public Task<FileWriteResponse> WriteFileAsync(string path, string content, string? revision, string token,
        CancellationToken cancellationToken = default) =>
        SendAsync<FileWriteResponse>(HttpMethod.Put, "/files/content", new FileWriteRequest
        {
            Path = path,
            Content = content,
            ExpectedRevision = revision ?? string.Empty,
        }, token, cancellationToken);

    public Task<FileMutationResponse> CreateFileAsync(string path, bool directory, string token,
        CancellationToken cancellationToken = default) =>
        SendAsync<FileMutationResponse>(HttpMethod.Post, "/files/create",
            new FileCreateRequest { Path = path, IsDirectory = directory }, token, cancellationToken);

    public Task<FileMutationResponse> RenameFileAsync(string path, string newName, string? revision, string token,
        CancellationToken cancellationToken = default) =>
        SendAsync<FileMutationResponse>(HttpMethod.Post, "/files/rename",
            new FileRenameRequest { Path = path, NewName = newName, ExpectedRevision = revision ?? string.Empty },
            token, cancellationToken);

    public Task<ServerPropertiesResponse> GetPropertiesAsync(string token,
        CancellationToken cancellationToken = default) =>
        SendAsync<ServerPropertiesResponse>(HttpMethod.Get, "/properties", null, token, cancellationToken);

    public Task<BackupListResponse> GetBackupsAsync(string token, CancellationToken cancellationToken = default) =>
        SendAsync<BackupListResponse>(HttpMethod.Get, "/backups", null, token, cancellationToken);

    public Task<BackupCreateResponse> CreateBackupAsync(string token, CancellationToken cancellationToken = default) =>
        SendAsync<BackupCreateResponse>(HttpMethod.Post, "/backups", new BackupCreateRequest(), token, cancellationToken);

    public Task<ConfirmationExecuteResponse> ControlAsync(string action, string token,
        CancellationToken cancellationToken = default) => ConfirmAsync("/control/prepare",
        new ControlRequest { Action = action }, token, cancellationToken);

    public Task<ConfirmationExecuteResponse> DeleteFileAsync(string path, string? revision, string token,
        CancellationToken cancellationToken = default) => ConfirmAsync("/files/delete/prepare",
        new FileDeleteRequest { Path = path, ExpectedRevision = revision ?? string.Empty }, token, cancellationToken);

    public Task<ConfirmationExecuteResponse> RestoreBackupAsync(string backupId, string token,
        CancellationToken cancellationToken = default) => ConfirmAsync("/backups/restore/prepare",
        new BackupRestoreRequest { BackupId = backupId }, token, cancellationToken);

    public Task<ConfirmationExecuteResponse> DeleteBackupAsync(string backupId, string token,
        CancellationToken cancellationToken = default) => ConfirmAsync("/backups/delete/prepare",
        new BackupDeleteRequest { BackupId = backupId }, token, cancellationToken);

    public Task<ConfirmationExecuteResponse> SetPropertiesAsync(Dictionary<string, string?> changes,
        string? revision, string token, CancellationToken cancellationToken = default) => ConfirmAsync("/properties/prepare",
        new ServerPropertiesPatchRequest { Changes = changes, ExpectedRevision = revision ?? string.Empty },
        token, cancellationToken);

    public Task<TokenIssuedResponse> DeriveTokenAsync(string rootToken, string name, IReadOnlyList<string> scopes,
        CancellationToken cancellationToken = default) => SendAsync<TokenIssuedResponse>(HttpMethod.Post,
        "/tokens/derive", new TokenDeriveRequest { Name = name, Scopes = scopes.ToList() }, rootToken, cancellationToken);

    public Task<OperationResponse> GetOperationAsync(string id, string token,
        CancellationToken cancellationToken = default) => SendAsync<OperationResponse>(HttpMethod.Get,
        "/operations/" + Uri.EscapeDataString(id), null, token, cancellationToken);

    public Uri BuildWebSocketUri(string? cursor)
    {
        var builder = new UriBuilder(_serverBaseUrl + ProtocolConstants.WebSocketPath)
        {
            Scheme = BaseUrl.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
        };
        string query = "apiVersion=" + Uri.EscapeDataString(ProtocolConstants.ApiVersion);
        if (!string.IsNullOrWhiteSpace(cursor)) query += "&cursor=" + Uri.EscapeDataString(cursor);
        builder.Query = query;
        return builder.Uri;
    }

    public async Task<T> SendAsync<T>(HttpMethod method, string relativePath, object? body, string token,
        CancellationToken cancellationToken = default)
    {
        string separator = relativePath.Contains('?') ? "&" : "?";
        string url = _serverBaseUrl + relativePath + separator + "apiVersion=" +
                     Uri.EscapeDataString(ProtocolConstants.ApiVersion);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = new StringContent(ProtocolJson.Serialize(body), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException error)
        {
            throw new RemoteApiException("无法连接远程服务器。请检查域名、TLS 证书、Caddy 反代，以及面板是否允许安装插件并开放插件端口。",
                innerException: error);
        }

        using (response)
        {
            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                ApiError? apiError = null;
                try { apiError = ProtocolJson.Deserialize<ApiError>(json); } catch { }
                string message = apiError?.Message is { Length: > 0 } detail
                    ? $"{apiError.Code}: {detail}"
                    : $"远程请求失败（HTTP {(int)response.StatusCode}）。";
                throw new RemoteApiException(message, response.StatusCode, apiError?.Code);
            }

            try
            {
                T result = ProtocolJson.Deserialize<T>(json);
                if (result is IProtocolMessage message) ProtocolVersion.EnsureCompatible(message.ApiVersion);
                return result;
            }
            catch (ProtocolVersionException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new RemoteApiException("服务端返回了无法识别的协议响应，请检查 Agent/Probe 与启动器版本。",
                    response.StatusCode, ProtocolErrors.InvalidApiVersion, error);
            }
        }
    }

    private async Task<ConfirmationExecuteResponse> ConfirmAsync<T>(string preparePath, T request, string token,
        CancellationToken cancellationToken)
    {
        ConfirmationPrepareResponse prepared = await SendAsync<ConfirmationPrepareResponse>(HttpMethod.Post,
            preparePath, request, token, cancellationToken);
        ProtocolVersion.EnsureCompatible(prepared.ApiVersion);
        return await SendAsync<ConfirmationExecuteResponse>(HttpMethod.Post, "/confirm/execute",
            new ConfirmationExecuteRequest { ConfirmationId = prepared.ConfirmationId }, token, cancellationToken);
    }

    private static bool IsLoopback(Uri uri) => uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
        ? IPAddress.IsLoopback(IPAddress.Parse(uri.Host))
        : string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _http.Dispose();
}
