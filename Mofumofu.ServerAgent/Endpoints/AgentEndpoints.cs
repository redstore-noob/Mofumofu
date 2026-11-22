using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Mofumofu.RemoteProtocol;
using Mofumofu.ServerAgent.Events;
using Mofumofu.ServerAgent.Files;
using Mofumofu.ServerAgent.Operations;
using Mofumofu.ServerAgent.Probe;
using Mofumofu.ServerAgent.Security;
using Mofumofu.ServerAgent.Services;
using Mofumofu.ServerAgent.Core;

namespace Mofumofu.ServerAgent.Endpoints;

public static class AgentEndpoints
{
    public static void Map(WebApplication app)
    {
        const string prefix = "/v1/servers/{instanceId}";
        app.MapPost(prefix + "/handshake", (HttpContext context, AgentEndpointHandlers handlers) =>
            handlers.Handshake(context)).Secure(ProtocolScopes.Read, RateCategory.Read);
        app.MapGet(prefix + "/status", (HttpContext context, AgentEndpointHandlers handlers) =>
            handlers.Status(context)).Secure(ProtocolScopes.Read, RateCategory.Read);
        app.MapPost(prefix + "/console/commands", (HttpContext context, AgentEndpointHandlers handlers) =>
            handlers.Command(context)).Secure(ProtocolScopes.Command, RateCategory.Write);

        app.MapGet(prefix + "/players/online", (HttpContext context, AgentEndpointHandlers handlers) =>
            handlers.OnlinePlayers(context)).Secure(ProtocolScopes.Read, RateCategory.Read);
        app.MapGet(prefix + "/players/{kind}", (HttpContext context, AgentEndpointHandlers handlers) =>
            handlers.PlayerList(context)).Secure(ProtocolScopes.Players, RateCategory.Read);
        app.MapPost(prefix + "/players/{kind}/add", (HttpContext context, AgentEndpointHandlers handlers) =>
            handlers.PlayerAdd(context)).Secure(ProtocolScopes.Players, RateCategory.Write);
        app.MapPost(prefix + "/players/{kind}/remove", (HttpContext context, AgentEndpointHandlers handlers) =>
            handlers.PlayerRemove(context)).Secure(ProtocolScopes.Players, RateCategory.Write);

        app.MapGet(prefix + "/files", (HttpContext context, AgentEndpointHandlers handlers) =>
            handlers.Files(context)).Secure(ProtocolScopes.Files, RateCategory.Read);
        app.MapGet(prefix + "/files/content", (HttpContext context, AgentEndpointHandlers handlers) =>
            handlers.FileContent(context)).Secure(ProtocolScopes.Files, RateCategory.Read);
        app.MapPost(prefix + "/files/create", (HttpContext context, AgentEndpointHandlers handlers) =>
            handlers.FileCreate(context)).Secure(ProtocolScopes.Files, RateCategory.Write);
        app.MapPost(prefix + "/files/rename", (HttpContext context, AgentEndpointHandlers handlers) =>
            handlers.FileRename(context)).Secure(ProtocolScopes.Files, RateCategory.Write);
        app.MapPut(prefix + "/files/content", (HttpContext context, AgentEndpointHandlers handlers) =>
            handlers.FileWrite(context)).Secure(ProtocolScopes.Files, RateCategory.Write);
        app.MapPost(prefix + "/files/delete/prepare", (HttpContext context, AgentEndpointHandlers handlers) =>
            handlers.FileDeletePrepare(context)).Secure(ProtocolScopes.Files, RateCategory.Sensitive);

        app.MapGet(prefix + "/properties", (HttpContext context, AgentEndpointHandlers handlers) =>
            handlers.Properties(context)).Secure(ProtocolScopes.Files, RateCategory.Read);
        app.MapPost(prefix + "/properties/prepare", (HttpContext context, AgentEndpointHandlers handlers) =>
            handlers.PropertiesPrepare(context)).Secure(ProtocolScopes.Files, RateCategory.Sensitive);

        app.MapGet(prefix + "/backups", (HttpContext context, AgentEndpointHandlers handlers) =>
            handlers.Backups(context)).Secure(ProtocolScopes.Backup, RateCategory.Sensitive);
        app.MapPost(prefix + "/backups", (HttpContext context, AgentEndpointHandlers handlers) =>
            handlers.BackupCreate(context)).Secure(ProtocolScopes.Backup, RateCategory.Sensitive);
        app.MapPost(prefix + "/backups/restore/prepare", (HttpContext context, AgentEndpointHandlers handlers) =>
            handlers.BackupRestorePrepare(context)).Secure(ProtocolScopes.Backup, RateCategory.Sensitive);
        app.MapPost(prefix + "/backups/delete/prepare", (HttpContext context, AgentEndpointHandlers handlers) =>
            handlers.BackupDeletePrepare(context)).Secure(ProtocolScopes.Backup, RateCategory.Sensitive);

        app.MapPost(prefix + "/control/prepare", (HttpContext context, AgentEndpointHandlers handlers) =>
            handlers.ControlPrepare(context)).Secure(ProtocolScopes.Control, RateCategory.Sensitive);
        app.MapPost(prefix + "/confirm/execute", (HttpContext context, AgentEndpointHandlers handlers) =>
            handlers.ConfirmExecute(context)).Secure(string.Empty, RateCategory.Sensitive);
        app.MapGet(prefix + "/operations/{operationId}", (HttpContext context, AgentEndpointHandlers handlers) =>
            handlers.Operation(context)).Secure(string.Empty, RateCategory.Read);

        app.MapGet(prefix + "/tokens", (HttpContext context, AgentEndpointHandlers handlers) =>
            handlers.TokenList(context)).Secure(string.Empty, RateCategory.Read, root: true);
        app.MapPost(prefix + "/tokens/derive", (HttpContext context, AgentEndpointHandlers handlers) =>
            handlers.TokenDerive(context)).Secure(string.Empty, RateCategory.Write, root: true);
        app.MapPost(prefix + "/tokens/revoke", (HttpContext context, AgentEndpointHandlers handlers) =>
            handlers.TokenRevoke(context)).Secure(string.Empty, RateCategory.Write, root: true);
        app.MapPost(prefix + "/tokens/rotate", (HttpContext context, AgentEndpointHandlers handlers) =>
            handlers.TokenRotate(context)).Secure(string.Empty, RateCategory.Write, root: true);

        app.MapGet(prefix + "/ws", (HttpContext context, AgentEndpointHandlers handlers) =>
            handlers.WebSocket(context)).Secure(ProtocolScopes.Read, RateCategory.Read);
        app.MapPost(ProbeEventService.Path, (HttpContext context, AgentEndpointHandlers handlers) =>
            handlers.ProbeEvent(context));
    }

    private static RouteHandlerBuilder Secure(this RouteHandlerBuilder builder, string scope,
        RateCategory category, bool root = false) =>
        builder.WithMetadata(new AgentSecurityMetadata(scope, category, root));
}

public sealed class AgentEndpointHandlers
{
    private readonly AgentOptions _options;
    private readonly ICoreGateway _core;
    private readonly IClock _clock;
    private readonly AuditExecutor _audit;
    private readonly SafeFileService _files;
    private readonly PropertyPatchService _properties;
    private readonly PlayerService _players;
    private readonly BackupService _backups;
    private readonly StatusService _status;
    private readonly ConfirmationService _confirmations;
    private readonly OperationService _operations;
    private readonly EventJournal _journal;
    private readonly ServerMonitorService _monitor;
    private readonly IProbeGateway _probe;
    private readonly ProbeEventService _probeEvents;

    public AgentEndpointHandlers(AgentOptions options, ICoreGateway core, IClock clock, AuditExecutor audit,
        SafeFileService files, PropertyPatchService properties, PlayerService players, BackupService backups,
        StatusService status, ConfirmationService confirmations, OperationService operations,
        EventJournal journal, ServerMonitorService monitor, IProbeGateway probe, ProbeEventService probeEvents)
    {
        _options = options;
        _core = core;
        _clock = clock;
        _audit = audit;
        _files = files;
        _properties = properties;
        _players = players;
        _backups = backups;
        _status = status;
        _confirmations = confirmations;
        _operations = operations;
        _journal = journal;
        _monitor = monitor;
        _probe = probe;
        _probeEvents = probeEvents;
    }

    public Task<IResult> Handshake(HttpContext context) => Read<HandshakeRequest>(context, request =>
    {
        AuthenticationContext authentication = Authentication(context);
        MofuCore.Server.ServerInfo info = RequireInfo();
        string version = typeof(AgentEndpointHandlers).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? typeof(AgentEndpointHandlers).Assembly.GetName().Version?.ToString() ?? "unknown";
        return Result(new HandshakeResponse
        {
            InstanceId = _options.InstanceId,
            ServerName = info.Name,
            DeploymentMode = ProtocolDeploymentModes.Agent,
            ServerImplementationVersion = version,
            Capabilities = [.. ProtocolCapabilities.Known],
            Scopes = authentication.Scopes.OrderBy(scope => scope, StringComparer.Ordinal).ToList(),
            ServerTime = _clock.UtcNow,
            WebSocketPath = ProtocolConstants.WebSocketPath,
            TokenId = authentication.TokenId,
        });
    });

    public async Task<IResult> Status(HttpContext context) => Result(await _status.GetAsync(context.RequestAborted));

    public Task<IResult> Command(HttpContext context) => Read<ConsoleCommandRequest>(context, async request =>
    {
        if (!TokenUtilities.IsValidCommand(request.Command))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, ProtocolErrors.InvalidCommand,
                "Commands must be 1-256 non-control UTF-16 code units and not blank.");
        }

        if (!_core.IsRunning(_options.InstanceId))
        {
            throw new ApiException(StatusCodes.Status409Conflict, ProtocolErrors.ServerOffline,
                "The server is not running.");
        }

        AuthenticationContext authentication = Authentication(context);
        return await _audit.ExecuteAsync(authentication, "console.command",
            new Dictionary<string, string> { ["commandLength"] = request.Command.Length.ToString() },
            async token =>
            {
                await _core.RunCommandAsync(_options.InstanceId, request.Command);
                return Result(new ConsoleCommandResponse { Accepted = true, SentAt = _clock.UtcNow });
            }, context.RequestAborted);
    });

    public async Task<IResult> OnlinePlayers(HttpContext context) =>
        Result(await _players.OnlineAsync(Authentication(context), context.RequestAborted));

    public Task<IResult> PlayerList(HttpContext context)
    {
        string kind = Route(context, "kind");
        return Task.FromResult(Result(_players.GetList(kind)));
    }

    public Task<IResult> PlayerAdd(HttpContext context) => Read<PlayerAddRequest>(context, async request =>
    {
        string kind = Route(context, "kind");
        AuthenticationContext authentication = Authentication(context);
        return await _audit.ExecuteAsync(authentication, $"players.{kind}.add",
            new Dictionary<string, string>
            {
                ["playerName"] = request.PlayerName ?? string.Empty,
                ["expectedRevision"] = request.ExpectedRevision ?? string.Empty,
            }, token => PlayerResult(() => _players.AddAsync(kind, request, token)), context.RequestAborted);
    });

    public Task<IResult> PlayerRemove(HttpContext context) => Read<PlayerRemoveRequest>(context, async request =>
    {
        string kind = Route(context, "kind");
        AuthenticationContext authentication = Authentication(context);
        return await _audit.ExecuteAsync(authentication, $"players.{kind}.remove",
            new Dictionary<string, string>
            {
                ["playerName"] = request.PlayerName ?? string.Empty,
                ["expectedRevision"] = request.ExpectedRevision ?? string.Empty,
            }, token => PlayerResult(() => _players.RemoveAsync(kind, request, token)), context.RequestAborted);
    });

    public async Task<IResult> Files(HttpContext context) =>
        Result(await _files.ListAsync(context.Request.Query["path"].ToString(), context.RequestAborted));

    public async Task<IResult> FileContent(HttpContext context)
    {
        SafeFileContent content = await _files.ReadAsync(context.Request.Query["path"].ToString(), context.RequestAborted);
        return Result(new FileContentResponse
        {
            Path = content.Path,
            Content = content.Content,
            Revision = content.Revision,
            SizeBytes = content.SizeBytes,
            ModifiedAt = content.ModifiedAt,
        });
    }

    public Task<IResult> FileCreate(HttpContext context) => Read<FileCreateRequest>(context, async request =>
    {
        AuthenticationContext authentication = Authentication(context);
        Dictionary<string, string> parameters = new()
        {
            ["path"] = request.Path ?? string.Empty,
            ["isDirectory"] = request.IsDirectory.ToString(),
        };
        return await _audit.ExecuteAsync(authentication, "file.create", parameters, async token =>
        {
            SafeFileMutation mutation = await _files.CreateAsync(request.Path ?? string.Empty,
                request.IsDirectory, token);
            return Result(new FileMutationResponse
            {
                Path = mutation.Path,
                Revision = mutation.Revision,
                IsDirectory = mutation.IsDirectory,
                ModifiedAt = mutation.ModifiedAt,
            });
        }, context.RequestAborted);
    });

    public Task<IResult> FileRename(HttpContext context) => Read<FileRenameRequest>(context, async request =>
    {
        AuthenticationContext authentication = Authentication(context);
        Dictionary<string, string> parameters = new()
        {
            ["path"] = request.Path ?? string.Empty,
            ["newName"] = request.NewName ?? string.Empty,
            ["expectedRevision"] = request.ExpectedRevision ?? string.Empty,
        };
        return await _audit.ExecuteAsync(authentication, "file.rename", parameters, async token =>
        {
            SafeFileMutation mutation = await _files.RenameAsync(request.Path ?? string.Empty,
                request.NewName ?? string.Empty, request.ExpectedRevision ?? string.Empty, token);
            return Result(new FileMutationResponse
            {
                Path = mutation.Path,
                Revision = mutation.Revision,
                IsDirectory = mutation.IsDirectory,
                ModifiedAt = mutation.ModifiedAt,
            });
        }, context.RequestAborted);
    });

    public Task<IResult> FileWrite(HttpContext context) => Read<FileWriteRequest>(context, async request =>
    {
        AuthenticationContext authentication = Authentication(context);
        Dictionary<string, string> parameters = new()
        {
            ["path"] = request.Path ?? string.Empty,
            ["expectedRevision"] = request.ExpectedRevision ?? string.Empty,
            ["contentBytes"] = Encoding.UTF8.GetByteCount(request.Content ?? string.Empty).ToString(),
        };
        return await _audit.ExecuteAsync(authentication, "file.write", parameters, async token =>
        {
            SafeFileContent content = await _files.WriteAsync(request.Path ?? string.Empty,
                request.Content ?? string.Empty, request.ExpectedRevision ?? string.Empty, token);
            return Result(new FileWriteResponse
            {
                Path = content.Path,
                Revision = content.Revision,
                SizeBytes = content.SizeBytes,
                ModifiedAt = content.ModifiedAt,
            });
        }, context.RequestAborted);
    });

    public Task<IResult> FileDeletePrepare(HttpContext context) => Read<FileDeleteRequest>(context, async request =>
    {
        SafeFileMutation current = await _files.GetItemAsync(request.Path, context.RequestAborted);
        EnsureRevision(current.Revision, request.ExpectedRevision);
        AuthenticationContext authentication = Authentication(context);
        Dictionary<string, string> parameters = new()
        {
            ["path"] = current.Path,
            ["revision"] = current.Revision,
        };
        return await _audit.ExecuteAsync(authentication, "file.delete.prepare", parameters, token =>
        {
            ConfirmationPrepareResponse response = _confirmations.Prepare(_options.InstanceId, authentication,
                ProtocolScopes.Files, ProtocolConfirmationActions.FileDelete, $"Delete {current.Path}", parameters,
                current.Revision, executeToken => _operations.StartAsync("file.delete", ProtocolScopes.Files,
                    async operationToken =>
                    {
                        await _files.DeleteAsync(current.Path, current.Revision, operationToken);
                        return new OperationCompletion("File deleted.");
                    }));
            return Task.FromResult(Result(response));
        }, context.RequestAborted);
    });

    public async Task<IResult> Properties(HttpContext context) =>
        Result(await _properties.GetAsync(context.RequestAborted));

    public Task<IResult> PropertiesPrepare(HttpContext context) => Read<ServerPropertiesPatchRequest>(context,
        async request =>
        {
            ServerPropertiesResponse current = await _properties.GetAsync(context.RequestAborted);
            EnsureRevision(current.Revision, request.ExpectedRevision);
            AuthenticationContext authentication = Authentication(context);
            Dictionary<string, string> parameters = PropertyPatchService.AuditChanges(request.Changes);
            parameters["expectedRevision"] = request.ExpectedRevision ?? string.Empty;
            return await _audit.ExecuteAsync(authentication, "properties.patch.prepare", parameters, token =>
            {
                ConfirmationPrepareResponse response = _confirmations.Prepare(_options.InstanceId, authentication,
                    ProtocolScopes.Files, ProtocolConfirmationActions.PropertiesPatch,
                    "Patch server.properties; restart required", parameters, current.Revision,
                    executeToken => _operations.StartAsync("properties.patch",
                        ProtocolScopes.Files, async operationToken =>
                        {
                            await _properties.PatchAsync(current.Revision, request.Changes, operationToken);
                            return new OperationCompletion("Properties patched; restart required.");
                        }));
                return Task.FromResult(Result(response));
            }, context.RequestAborted);
        });

    public async Task<IResult> Backups(HttpContext context) => Result(await _backups.ListAsync(context.RequestAborted));

    public Task<IResult> BackupCreate(HttpContext context) => Read<BackupCreateRequest>(context, async request =>
        await _audit.ExecuteAsync(Authentication(context), "backup.create",
            new Dictionary<string, string> { ["labelLength"] = (request.Label?.Length ?? 0).ToString() },
            async token => Result(await _backups.CreateAsync(request, token)), context.RequestAborted));

    public Task<IResult> BackupRestorePrepare(HttpContext context) => Read<BackupRestoreRequest>(context, request =>
        PrepareBackup(context, request.BackupId, restore: true));

    public Task<IResult> BackupDeletePrepare(HttpContext context) => Read<BackupDeleteRequest>(context, request =>
        PrepareBackup(context, request.BackupId, restore: false));

    public Task<IResult> ControlPrepare(HttpContext context) => Read<ControlRequest>(context, async request =>
    {
        string action = request.Action;
        if (action is not (ProtocolControlActions.Start or ProtocolControlActions.Stop
            or ProtocolControlActions.Restart or ProtocolControlActions.Kill))
        {
            throw ApiException.InvalidRequest("control/prepare accepts start, stop, restart, or kill.");
        }

        bool running = _core.IsRunning(_options.InstanceId);
        if (action == ProtocolControlActions.Start && running)
        {
            throw new ApiException(StatusCodes.Status409Conflict, ProtocolErrors.ServerRunning,
                "The server is already running.");
        }

        if (action != ProtocolControlActions.Start && !running)
        {
            throw new ApiException(StatusCodes.Status409Conflict, ProtocolErrors.ServerOffline,
                "The server is not running.");
        }

        AuthenticationContext authentication = Authentication(context);
        Dictionary<string, string> parameters = new() { ["action"] = action };
        string confirmationAction = action switch
        {
            ProtocolControlActions.Start => ProtocolConfirmationActions.ControlStart,
            ProtocolControlActions.Stop => ProtocolConfirmationActions.ControlStop,
            ProtocolControlActions.Restart => ProtocolConfirmationActions.ControlRestart,
            _ => ProtocolConfirmationActions.ControlKill,
        };
        return await _audit.ExecuteAsync(authentication, confirmationAction + ".prepare", parameters, token =>
        {
            ConfirmationPrepareResponse response = _confirmations.Prepare(_options.InstanceId, authentication,
                ProtocolScopes.Control, confirmationAction, $"Confirm server {action}", parameters,
                StatusService.MapState(_core.Poll(_options.InstanceId, 0).Status),
                executeToken => _operations.StartAsync("control." + action, ProtocolScopes.Control,
                    async operationToken =>
                    {
                        bool isRunning = _core.IsRunning(_options.InstanceId);
                        if (action == ProtocolControlActions.Start)
                        {
                            if (isRunning)
                            {
                                throw new ApiException(StatusCodes.Status409Conflict, ProtocolErrors.ServerRunning,
                                    "The server started before the confirmed operation executed.");
                            }

                            await _core.StartAsync(_options.InstanceId, operationToken);
                            _properties.NotifyServerStarted();
                            return new OperationCompletion("Server start completed.");
                        }

                        if (!isRunning)
                        {
                            throw new ApiException(StatusCodes.Status409Conflict, ProtocolErrors.ServerOffline,
                                "The server stopped before the confirmed operation executed.");
                        }

                        _monitor.NoteIntentionalStop();
                        if (action == ProtocolControlActions.Restart)
                        {
                            await _core.RestartAsync(_options.InstanceId, operationToken);
                            _properties.NotifyServerStarted();
                        }
                        else
                        {
                            await _core.StopAsync(_options.InstanceId, force: action == ProtocolControlActions.Kill);
                        }

                        return new OperationCompletion($"Server {action} completed.");
                    }));
            return Task.FromResult(Result(response));
        }, context.RequestAborted);
    });

    public Task<IResult> ConfirmExecute(HttpContext context) => Read<ConfirmationExecuteRequest>(context,
        async request =>
        {
            AuthenticationContext authentication = Authentication(context);
            Dictionary<string, string> parameters = new() { ["confirmationId"] = request.ConfirmationId ?? string.Empty };
            return await _audit.ExecuteAsync(authentication, "confirmation.execute", parameters, async token =>
                Result(new ConfirmationExecuteResponse
                {
                    Operation = await _confirmations.ExecuteAsync(_options.InstanceId, authentication,
                        request.ConfirmationId ?? string.Empty, token),
                }), context.RequestAborted);
        });

    public Task<IResult> Operation(HttpContext context)
    {
        string id = Route(context, "operationId");
        string requiredScope = _operations.GetRequiredScope(id);
        AuthenticationContext authentication = Authentication(context);
        if (!authentication.Scopes.Contains(requiredScope))
        {
            throw new ApiException(StatusCodes.Status403Forbidden, ProtocolErrors.InsufficientScope,
                "The operation requires its original scope.");
        }

        return Task.FromResult(Result(new OperationResponse { Operation = _operations.Get(id, requiredScope) }));
    }

    public Task<IResult> TokenList(HttpContext context) => ForwardToken<TokenListResponse>(context,
        HttpMethod.Get, "/tokens", body: null, "tokens.list");

    public Task<IResult> TokenDerive(HttpContext context) => Read<TokenDeriveRequest>(context, request =>
        ForwardToken<TokenIssuedResponse>(context, HttpMethod.Post, "/tokens/derive", request, "tokens.derive",
            new Dictionary<string, string>
            {
                ["name"] = request.Name ?? string.Empty,
                ["scopes"] = string.Join(',', request.Scopes ?? []),
            }));

    public Task<IResult> TokenRevoke(HttpContext context) => Read<TokenRevokeRequest>(context, request =>
        ForwardToken<TokenRevokeResponse>(context, HttpMethod.Post, "/tokens/revoke", request, "tokens.revoke",
            new Dictionary<string, string> { ["targetTokenId"] = request.TokenId ?? string.Empty }));

    public Task<IResult> TokenRotate(HttpContext context) => Read<TokenRotateRequest>(context, request =>
        ForwardToken<TokenRotateResponse>(context, HttpMethod.Post, "/tokens/rotate", request, "tokens.rotate",
            new Dictionary<string, string> { ["targetTokenId"] = request.TokenId ?? string.Empty }));

    public async Task<IResult> WebSocket(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            throw ApiException.InvalidRequest("A WebSocket upgrade is required.");
        }

        AuthenticationContext authentication = Authentication(context);
        StatusResponse status = await _status.GetAsync(context.RequestAborted);
        OnlinePlayersResponse players = await _players.OnlineAsync(authentication, context.RequestAborted);
        IReadOnlyList<Mofumofu.RemoteProtocol.ConsoleEntry> recent = await _journal.RecentConsoleAsync(100,
            context.RequestAborted);
        IReadOnlyList<OperationInfo> active = _operations.Active();
        string? cursor = context.Request.Query.ContainsKey("cursor")
            ? context.Request.Query["cursor"].ToString()
            : null;
        JournalSubscription subscription = await _journal.OpenAsync(cursor, current => new SnapshotFrame
        {
            Cursor = current,
            SentAt = _clock.UtcNow,
            Status = status,
            Players = [.. players.Players],
            RecentConsole = [.. recent],
            ActiveOperations = [.. active],
        }, context.RequestAborted);
        using (subscription)
        using (System.Net.WebSockets.WebSocket socket = await context.WebSockets.AcceptWebSocketAsync())
        {
            CancellationToken cancellation = context.RequestAborted;
            Task receive = ReceiveCloseAsync(socket, cancellation);
            foreach (WebSocketFrame frame in subscription.InitialFrames)
            {
                await SendFrameAsync(socket, frame, cancellation);
            }

            while (!cancellation.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                Task<bool> next = subscription.Reader.WaitToReadAsync(cancellation).AsTask();
                Task completed = await Task.WhenAny(next, receive);
                if (completed == receive || !await next)
                {
                    break;
                }

                while (subscription.Reader.TryRead(out WebSocketFrame? frame))
                {
                    await SendFrameAsync(socket, frame, cancellation);
                }
            }

            if (socket.State == WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
        }

        return Results.Empty;
    }

    public async Task<IResult> ProbeEvent(HttpContext context)
    {
        if (context.Connection.RemoteIpAddress is { } remote && !IPAddress.IsLoopback(remote))
        {
            throw new ApiException(StatusCodes.Status403Forbidden, ProtocolErrors.Forbidden,
                "The internal Probe endpoint is loopback-only.");
        }

        byte[] body = await ReadRawBody(context);
        bool duplicate = await _probeEvents.AcceptAsync(context.Request.Method, context.Request.Path.Value ?? string.Empty,
            context.Request.Headers[ProbeEventService.InstanceHeader].ToString(),
            context.Request.Headers[ProbeEventService.TimestampHeader].ToString(),
            context.Request.Headers[ProbeEventService.NonceHeader].ToString(),
            context.Request.Headers[ProbeEventService.SignatureHeader].ToString(), body, context.RequestAborted);
        return Result(new AgentAcknowledgementResponse { Accepted = true, Duplicate = duplicate },
            duplicate ? StatusCodes.Status200OK : StatusCodes.Status202Accepted);
    }

    private Task<IResult> PrepareBackup(HttpContext context, string backupId, bool restore)
    {
        BackupMetadata backup = _backups.Get(backupId);
        if (restore && _core.IsRunning(_options.InstanceId))
        {
            throw new ApiException(StatusCodes.Status409Conflict, ProtocolErrors.ServerRunning,
                "The server must be stopped before restoring a backup.");
        }

        AuthenticationContext authentication = Authentication(context);
        Dictionary<string, string> parameters = new()
        {
            ["backupId"] = backup.Id,
            ["sha256"] = backup.Sha256,
        };
        string action = restore ? ProtocolConfirmationActions.BackupRestore : ProtocolConfirmationActions.BackupDelete;
        return _audit.ExecuteAsync(authentication, action + ".prepare", parameters, token =>
        {
            ConfirmationPrepareResponse response = _confirmations.Prepare(_options.InstanceId, authentication,
                ProtocolScopes.Backup, action,
                restore ? $"Restore backup {backup.Id} while offline" : $"Delete backup {backup.Id}", parameters,
                backup.Sha256, executeToken => restore
                    ? _backups.RestoreAsync(backup.Id, backup.Sha256, executeToken)
                    : _operations.StartAsync("backup.delete", ProtocolScopes.Backup, operationToken =>
                    {
                        _backups.Delete(backup.Id, backup.Sha256);
                        return Task.FromResult(new OperationCompletion("Backup deleted."));
                    }));
            return Task.FromResult(Result(response));
        }, context.RequestAborted);
    }

    private async Task<IResult> ForwardToken<T>(HttpContext context, HttpMethod method, string suffix,
        object? body, string action, Dictionary<string, string>? parameters = null)
    {
        AuthenticationContext authentication = Authentication(context);
        parameters ??= new Dictionary<string, string>();
        return await _audit.ExecuteAsync(authentication, action, parameters, async token =>
        {
            try
            {
                ProbeResponse response = await _probe.SendAsync(method,
                    ProtocolConstants.GetServerBasePath(_options.InstanceId) + suffix,
                    authentication.Authorization, body,
                    body is null ? "apiVersion=" + ProtocolConstants.ApiVersion : null, token);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw RemoteError(response);
                }

                T value = response.Deserialize<T>();
                if (value is IProtocolMessage message)
                {
                    ProtocolRequest.ValidateVersion(message.ApiVersion);
                }

                return Result(value);
            }
            catch (ProbeUnavailableException)
            {
                throw new ApiException(StatusCodes.Status409Conflict, ProtocolErrors.ServerOffline,
                    "Token management requires an online Probe.");
            }
            catch (JsonException)
            {
                throw new ApiException(StatusCodes.Status500InternalServerError, ProtocolErrors.InternalError,
                    "Probe returned an invalid protocol response.");
            }
        }, context.RequestAborted);
    }

    private async Task<IResult> Read<T>(HttpContext context, Func<T, Task<IResult>> handler)
        where T : class, IProtocolMessage => await handler(await ProtocolRequest.ReadBodyAsync<T>(context,
        context.RequestAborted));

    private async Task<IResult> Read<T>(HttpContext context, Func<T, IResult> handler)
        where T : class, IProtocolMessage => handler(await ProtocolRequest.ReadBodyAsync<T>(context,
        context.RequestAborted));

    private static async Task<IResult> PlayerResult(Func<Task<PlayerMutationResponse>> action) => Result(await action());

    private static async Task<IResult> ControlResult(Func<Task<OperationInfo>> action, string actionName) =>
        Result(new ControlResponse { Action = actionName, Operation = await action() });

    private static byte[] ReadRawBodyBytes(byte[] body) => body;

    private static async Task<byte[]> ReadRawBody(HttpContext context)
    {
        if (context.Request.ContentLength > AgentOptions.MaxRequestBodyBytes)
        {
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, ProtocolErrors.PayloadTooLarge,
                "The Probe event body is too large.");
        }

        await using MemoryStream buffer = new();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
        if (buffer.Length > AgentOptions.MaxRequestBodyBytes)
        {
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, ProtocolErrors.PayloadTooLarge,
                "The Probe event body is too large.");
        }

        return buffer.ToArray();
    }

    private static async Task SendFrameAsync(System.Net.WebSockets.WebSocket socket, WebSocketFrame frame,
        CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(frame, ProtocolJson.Options);
        await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task ReceiveCloseAsync(System.Net.WebSockets.WebSocket socket,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException)
        {
        }
    }

    private static ApiException RemoteError(ProbeResponse response)
    {
        ApiError error = response.Deserialize<ApiError>();
        return new ApiException((int)response.StatusCode, error.Code, error.Message, error.Details);
    }

    private static void EnsureRevision(string actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw ApiException.RevisionConflict(expected ?? string.Empty);
        }
    }

    private MofuCore.Server.ServerInfo RequireInfo() => _core.GetServerInfo(_options.InstanceId)
        ?? throw ApiException.NotFound("The server instance no longer exists.");

    private static AuthenticationContext Authentication(HttpContext context) =>
        RequestSecurityMiddleware.Authentication(context);

    private static string Route(HttpContext context, string key) =>
        context.Request.RouteValues[key]?.ToString() ?? string.Empty;

    private static IResult Result<T>(T value, int statusCode = StatusCodes.Status200OK) =>
        ProtocolRequest.Json(value, statusCode);
}
