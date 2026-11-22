package mofumofu.nekoprobe.http;

import com.fasterxml.jackson.databind.JsonNode;
import mofumofu.nekoprobe.ProbeConfig;
import mofumofu.nekoprobe.api.ApiException;
import mofumofu.nekoprobe.api.Json;
import mofumofu.nekoprobe.api.Protocol;
import mofumofu.nekoprobe.backup.BackupService;
import mofumofu.nekoprobe.bukkit.ConsoleRing;
import mofumofu.nekoprobe.bukkit.EventService;
import mofumofu.nekoprobe.bukkit.OperationRegistry;
import mofumofu.nekoprobe.bukkit.ServerGateway;
import mofumofu.nekoprobe.files.PropertiesService;
import mofumofu.nekoprobe.files.SafeFileService;
import mofumofu.nekoprobe.security.AuditLog;
import mofumofu.nekoprobe.security.AuthContext;
import mofumofu.nekoprobe.security.ConfirmationService;
import mofumofu.nekoprobe.security.SecurityUtils;
import mofumofu.nekoprobe.security.TokenRateLimiter;
import mofumofu.nekoprobe.security.TokenService;

import java.nio.charset.StandardCharsets;
import java.time.Clock;
import java.time.Instant;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.function.Supplier;

public final class ProbeRouter {
    private final ProbeConfig config;
    private final String pluginVersion;
    private final Clock clock;
    private final long startedNanos;
    private final TokenService tokens;
    private final TokenRateLimiter rateLimiter;
    private final AuditLog audit;
    private final ConfirmationService confirmations;
    private final ServerGateway gateway;
    private final SafeFileService files;
    private final PropertiesService properties;
    private final BackupService backups;
    private final OperationRegistry operations;
    private final ConsoleRing journal;
    private final EventService events;

    public ProbeRouter(ProbeConfig config, String pluginVersion, Clock clock, long startedNanos, TokenService tokens,
                       TokenRateLimiter rateLimiter, AuditLog audit, ConfirmationService confirmations,
                       ServerGateway gateway, SafeFileService files, PropertiesService properties, BackupService backups,
                       OperationRegistry operations, ConsoleRing journal, EventService events) {
        this.config = config;
        this.pluginVersion = pluginVersion;
        this.clock = clock;
        this.startedNanos = startedNanos;
        this.tokens = tokens;
        this.rateLimiter = rateLimiter;
        this.audit = audit;
        this.confirmations = confirmations;
        this.gateway = gateway;
        this.files = files;
        this.properties = properties;
        this.backups = backups;
        this.operations = operations;
        this.journal = journal;
        this.events = events;
    }

    public AuthContext authenticate(String authorization) {
        if (authorization == null || !authorization.startsWith("Bearer ") || authorization.length() <= 7
                || authorization.indexOf(' ', 7) >= 0) {
            throw new ApiException(401, "unauthorized", "Authentication failed.");
        }
        AuthContext auth = tokens.authenticate(authorization.substring(7));
        rateLimiter.check(auth.tokenId());
        return auth;
    }

    public Map<String, Object> route(String method, String path, Map<String, List<String>> query,
                                     JsonNode body, AuthContext auth) {
        HttpInput.version(body);
        if (path.equals("/handshake")) {
            requireMethod(method, "GET", "POST");
            auth.requireScope("read");
            return handshake(auth);
        }
        if (path.equals("/status")) {
            requireMethod(method, "GET");
            auth.requireScope("read");
            return status();
        }
        if (path.equals("/console")) {
            requireMethod(method, "GET");
            auth.requireScope("read");
            return console(query);
        }
        if (path.equals("/commands") || path.equals("/console/commands") || path.equals("/console/command")) {
            requireMethod(method, "POST");
            return command(body, auth);
        }
        if (path.equals("/players") || path.equals("/players/online")) {
            requireMethod(method, "GET");
            auth.requireScope("read");
            return onlinePlayers();
        }
        if (path.startsWith("/players/")) {
            return players(method, path.substring("/players/".length()), body, auth);
        }
        if (path.equals("/files") || path.equals("/files/content") || path.equals("/files/read")
                || path.equals("/files/directories") || path.equals("/files/create")
                || path.equals("/files/rename") || path.equals("/files/delete/prepare")) {
            return files(method, path, query, body, auth);
        }
        if (path.equals("/properties") || path.equals("/properties/prepare")) {
            return properties(method, path, body, auth);
        }
        if (path.equals("/backups") || path.equals("/backups/restore/prepare")
                || path.equals("/backups/delete/prepare") || path.startsWith("/backups/")) {
            return backups(method, path, body, auth);
        }
        if (path.equals("/confirm/prepare") || path.equals("/prepare")) {
            requireMethod(method, "POST");
            return prepare(body, auth);
        }
        if (path.equals("/confirm/execute") || path.equals("/execute")) {
            requireMethod(method, "POST");
            return execute(body, auth);
        }
        if (path.equals("/operations") || path.startsWith("/operations/")) {
            requireMethod(method, "GET");
            auth.requireScope("read");
            if (path.equals("/operations")) {
                Map<String, Object> response = Json.message();
                response.put("operations", operations.list());
                return response;
            }
            Map<String, Object> response = Json.message();
            response.put("operation", operations.get(path.substring("/operations/".length())));
            return response;
        }
        if (path.equals("/tokens") || path.equals("/tokens/derive") || path.equals("/tokens/revoke")
                || path.equals("/tokens/rotate") || path.startsWith("/tokens/")) {
            return tokens(method, path, body, auth);
        }
        if (path.equals("/control") || path.startsWith("/control/")) {
            throw new ApiException(400, "capability_not_supported", "NekoProbe never controls the server process lifecycle.");
        }
        throw ApiException.notFound("Endpoint");
    }

    public Map<String, Object> snapshot() {
        Map<String, Object> snapshot = new LinkedHashMap<>();
        snapshot.put("status", status());
        snapshot.put("players", onlinePlayers().get("players"));
        snapshot.put("recentConsole", journal.recentConsole(100));
        snapshot.put("activeOperations", operations.list().stream()
                .filter(operation -> List.of("queued", "running").contains(operation.get("state"))).toList());
        return snapshot;
    }

    private Map<String, Object> handshake(AuthContext auth) {
        Map<String, Object> info = gateway.serverInfo();
        Map<String, Object> response = Json.message();
        response.put("instanceId", config.instanceId());
        response.put("serverName", info.get("serverName"));
        response.put("deploymentMode", "probe");
        response.put("serverImplementationVersion", info.get("serverImplementationVersion"));
        response.put("capabilities", Protocol.CAPABILITIES);
        response.put("scopes", auth.scopes().stream().sorted().toList());
        response.put("serverTime", Instant.now(clock));
        response.put("webSocketPath", basePath() + "/ws");
        response.put("tokenId", auth.tokenId());
        response.put("component", Map.of("name", "NekoProbe", "version", pluginVersion));
        response.put("javaVersion", Runtime.version().toString());
        return response;
    }

    private Map<String, Object> status() {
        Map<String, Object> current = gateway.status();
        Map<String, Object> response = Json.message();
        response.put("instanceId", config.instanceId());
        response.putAll(current);
        response.put("uptimeSeconds", Math.max(0, (System.nanoTime() - startedNanos) / 1_000_000_000L));
        response.put("observedAt", Instant.now(clock));
        return response;
    }

    private Map<String, Object> console(Map<String, List<String>> query) {
        int limit = queryInt(query, "limit", 200, 1, 1000);
        ConsoleRing.ReadResult result = journal.readAfter(query(query, "cursor"), limit);
        Map<String, Object> response = Json.message();
        response.put("entries", result.consoleEntries());
        response.put("cursor", result.cursor());
        response.put("reset", result.reset());
        return response;
    }

    private Map<String, Object> command(JsonNode body, AuthContext auth) {
        auth.requireScope("command");
        String command = SecurityUtils.validCommand(HttpInput.requiredText(body, "command"));
        Map<String, Object> operation = operations.create("command.execute");
        String operationId = operation.get("id").toString();
        Map<String, Object> auditParameters = Map.of("length", command.length(), "sha256", SecurityUtils.sha256Hex(command));
        return audited(auth, "command.execute", auditParameters, () -> {
            operations.start(operationId);
            try {
                boolean accepted = gateway.dispatchCommand(command);
                if (accepted) {
                    operations.succeed(operationId, Map.of());
                } else {
                    operations.fail(operationId, "command_rejected", "The server rejected the command.");
                }
                Map<String, Object> response = Json.message();
                response.put("accepted", accepted);
                response.put("sentAt", Instant.now(clock));
                response.put("operationId", operationId);
                return response;
            } catch (RuntimeException e) {
                failOperation(operationId, e);
                throw e;
            }
        });
    }

    private Map<String, Object> onlinePlayers() {
        Map<String, Object> response = Json.message();
        response.putAll(gateway.onlinePlayers());
        response.put("observedAt", Instant.now(clock));
        return response;
    }

    private Map<String, Object> players(String method, String path, JsonNode body, AuthContext auth) {
        String[] segments = path.split("/");
        String kind = segments[0];
        boolean addSegment = segments.length == 2 && segments[1].equals("add");
        boolean removeSegment = segments.length == 2 && segments[1].equals("remove");
        if (!List.of("whitelist", "ops", "bans").contains(kind) || segments.length > 2
                || (segments.length == 2 && !addSegment && !removeSegment)) {
            throw ApiException.notFound("Player list");
        }
        auth.requireScope("players");
        if (method.equals("GET") && segments.length == 1) {
            return withApi(gateway.playerList(kind));
        }
        boolean remove = method.equals("DELETE") || (method.equals("POST") && removeSegment);
        if (remove) {
            String name = SecurityUtils.validPlayerName(HttpInput.requiredText(body, "playerName"));
            String revision = HttpInput.text(body, "expectedRevision");
            return audited(auth, "players." + kind + ".remove", Map.of("playerName", name),
                    () -> withApi(gateway.removePlayer(kind, name, revision)));
        }
        if (method.equals("POST") && (segments.length == 1 || addSegment)) {
            String name = SecurityUtils.validPlayerName(HttpInput.requiredText(body, "playerName"));
            String revision = HttpInput.text(body, "expectedRevision");
            int opLevel = HttpInput.integer(body, "opLevel", 4);
            if (opLevel < 0 || opLevel > 4) {
                throw ApiException.badRequest("opLevel must be between 0 and 4.");
            }
            boolean bypass = HttpInput.bool(body, "bypassesPlayerLimit", false);
            String reason = HttpInput.text(body, "reason");
            Instant expiresAt = HttpInput.instant(body, "expiresAt");
            return audited(auth, "players." + kind + ".add", Map.of("playerName", name),
                    () -> withApi(gateway.addPlayer(kind, name, revision, opLevel, bypass, reason, expiresAt)));
        }
        throw methodNotAllowed();
    }

    private Map<String, Object> files(String method, String path, Map<String, List<String>> query,
                                      JsonNode body, AuthContext auth) {
        auth.requireScope("files");
        if (path.equals("/files") && method.equals("GET")) {
            return files.list(query(query, "path") == null ? "" : query(query, "path"));
        }
        if ((path.equals("/files/content") || path.equals("/files/read")) && method.equals("GET")) {
            return files.read(requiredQuery(query, "path"));
        }
        if ((path.equals("/files") || path.equals("/files/content")) && (method.equals("PUT") || method.equals("PATCH"))) {
            String filePath = HttpInput.requiredText(body, "path");
            String content = HttpInput.requiredText(body, "content");
            String revision = HttpInput.text(body, "expectedRevision");
            return audited(auth, "file.write", contentAudit(filePath, content, revision),
                    () -> files.write(filePath, content, revision));
        }
        if ((path.equals("/files") || path.equals("/files/directories")
                || path.equals("/files/create")) && method.equals("POST")) {
            String filePath = HttpInput.requiredText(body, "path");
            boolean directory = path.equals("/files/directories")
                    || HttpInput.bool(body, "isDirectory", HttpInput.bool(body, "directory", false));
            String content = directory ? null : HttpInput.text(body, "content");
            return audited(auth, directory ? "directory.create" : "file.create", safePathAudit(filePath),
                    () -> files.create(filePath, directory, content));
        }
        if (path.equals("/files/rename") && method.equals("POST")) {
            String filePath = HttpInput.requiredText(body, "path");
            String newPath = HttpInput.text(body, "newPath");
            if (newPath == null) {
                newPath = siblingPath(filePath, HttpInput.requiredText(body, "newName"));
            }
            String revision = HttpInput.text(body, "expectedRevision");
            Map<String, Object> parameters = new LinkedHashMap<>();
            parameters.put("path", filePath);
            parameters.put("newPath", newPath);
            String target = newPath;
            return audited(auth, "file.rename", parameters, () -> files.rename(filePath, target, revision));
        }
        if ((path.equals("/files/delete/prepare") && method.equals("POST"))
                || (path.equals("/files") && method.equals("DELETE"))) {
            Map<String, Object> parameters = new LinkedHashMap<>();
            parameters.put("path", files.normalize(HttpInput.requiredText(body, "path")));
            parameters.put("expectedRevision", HttpInput.text(body, "expectedRevision"));
            return prepareConfirmation(auth, "file.delete", "files", parameters);
        }
        throw methodNotAllowed();
    }

    private Map<String, Object> properties(String method, String path, JsonNode body, AuthContext auth) {
        auth.requireScope("files");
        if (path.equals("/properties") && method.equals("GET")) {
            return properties.get();
        }
        if (path.equals("/properties") && method.equals("PATCH")) {
            String revision = HttpInput.requiredText(body, "expectedRevision");
            Map<String, String> changes = HttpInput.nullableStringMap(body, "changes");
            Map<String, Object> auditChanges = new LinkedHashMap<>();
            changes.forEach((key, value) -> auditChanges.put(key,
                    value == null ? "removed" : Map.of("sha256", SecurityUtils.sha256Hex(value), "length", value.length())));
            Map<String, Object> auditParameters = new LinkedHashMap<>();
            auditParameters.put("expectedRevision", revision);
            auditParameters.put("changes", auditChanges);
            return audited(auth, "properties.patch", auditParameters, () -> properties.patch(revision, changes));
        }
        if (path.equals("/properties/prepare") && method.equals("POST")) {
            String revision = HttpInput.text(body, "expectedRevision");
            Map<String, String> changes = HttpInput.nullableStringMap(body, "changes");
            Map<String, Object> parameters = new LinkedHashMap<>();
            parameters.put("expectedRevision", revision == null ? "" : revision);
            parameters.put("changes", changes);
            return prepareConfirmation(auth, "properties.patch", "files", parameters, propertiesAudit(parameters));
        }
        throw methodNotAllowed();
    }

    private Map<String, Object> backups(String method, String path, JsonNode body, AuthContext auth) {
        auth.requireScope("backup");
        if (path.equals("/backups") && method.equals("GET")) {
            return backups.list();
        }
        if (path.equals("/backups") && method.equals("POST")) {
            String label = HttpInput.text(body, "label");
            if (label == null || label.isBlank()) {
                label = "Remote backup";
            }
            String description = HttpInput.text(body, "description");
            String minecraftVersion = String.valueOf(gateway.serverInfo().get("minecraftVersion"));
            String finalLabel = label;
            Map<String, Object> operation = operations.create("backup.create");
            String operationId = operation.get("id").toString();
            return audited(auth, "backup.create", Map.of("label", label), () -> {
                operations.start(operationId);
                try {
                    Map<String, Object> created = backups.create(finalLabel, description, minecraftVersion);
                    @SuppressWarnings("unchecked")
                    Map<String, Object> metadata = (Map<String, Object>) created.get("backup");
                    Map<String, Object> completed = operations.succeed(operationId, Map.of("backupId", metadata.get("id")));
                    created.put("operation", completed);
                    return created;
                } catch (RuntimeException e) {
                    failOperation(operationId, e);
                    throw e;
                }
            });
        }
        if (path.equals("/backups/restore/prepare") && method.equals("POST")) {
            String id = HttpInput.requiredText(body, "backupId");
            backups.verifiedPaths(id);
            return prepareConfirmation(auth, "backup.restore", "backup", Map.of("backupId", id));
        }
        if (path.equals("/backups/delete/prepare") && method.equals("POST")) {
            String id = HttpInput.requiredText(body, "backupId");
            backups.verifiedPaths(id);
            return prepareConfirmation(auth, "backup.delete", "backup", Map.of("backupId", id));
        }
        String remainder = path.substring("/backups/".length());
        String[] segments = remainder.split("/");
        if (segments.length == 2 && segments[1].equals("restore") && method.equals("POST")) {
            Map<String, Object> parameters = Map.of("backupId", segments[0]);
            backups.verifiedPaths(segments[0]);
            return prepareConfirmation(auth, "backup.restore", "backup", parameters);
        }
        if (segments.length == 1 && method.equals("DELETE")) {
            Map<String, Object> parameters = Map.of("backupId", segments[0]);
            backups.verifiedPaths(segments[0]);
            return prepareConfirmation(auth, "backup.delete", "backup", parameters);
        }
        throw ApiException.notFound("Backup endpoint");
    }

    private Map<String, Object> prepare(JsonNode body, AuthContext auth) {
        String action = HttpInput.requiredText(body, "action");
        Map<String, Object> parameters = HttpInput.parameters(body);
        String scope = switch (action) {
            case "file.delete" -> "files";
            case "properties.patch" -> "files";
            case "backup.restore", "backup.delete" -> "backup";
            case "control.stop", "control.restart", "control.kill" ->
                    throw new ApiException(400, "capability_not_supported", "NekoProbe never controls the server process lifecycle.");
            default -> throw ApiException.badRequest("Confirmation action is invalid.");
        };
        Map<String, Object> normalized = normalizeConfirmation(action, parameters);
        return prepareConfirmation(auth, action, scope, normalized);
    }

    private Map<String, Object> prepareConfirmation(AuthContext auth, String action, String scope,
                                                     Map<String, Object> parameters) {
        return prepareConfirmation(auth, action, scope, parameters, parameters);
    }

    private Map<String, Object> prepareConfirmation(AuthContext auth, String action, String scope,
                                                     Map<String, Object> parameters,
                                                     Map<String, Object> auditParameters) {
        return audited(auth, "confirmation.prepare." + action, auditParameters, () -> {
            Map<String, Object> normalized = normalizeConfirmation(action, parameters);
            return confirmations.prepare(auth, action, scope, normalized).response();
        });
    }

    private Map<String, Object> execute(JsonNode body, AuthContext auth) {
        String id = HttpInput.requiredText(body, "confirmationId");
        return audited(auth, "confirmation.execute", Map.of("confirmationId", id), () -> {
            ConfirmationService.Consumed consumed = confirmations.consume(id, auth);
            Map<String, Object> parameters = Json.MAPPER.convertValue(Json.read(consumed.canonicalParameters()),
                    Json.MAPPER.getTypeFactory().constructMapType(LinkedHashMap.class, String.class, Object.class));
            String kind = consumed.action();
            Map<String, Object> operation = operations.create(kind);
            String operationId = operation.get("id").toString();
            operations.start(operationId);
            try {
                Map<String, Object> result = switch (kind) {
                    case "backup.restore" -> backups.prepareRestore(parameter(parameters, "backupId"));
                    case "backup.delete" -> backups.delete(parameter(parameters, "backupId"));
                    case "file.delete" -> files.delete(parameter(parameters, "path"), parameters.get("expectedRevision") == null
                            ? null : parameters.get("expectedRevision").toString());
                    case "properties.patch" -> {
                        Map<String, String> changes = new LinkedHashMap<>();
                        if (parameters.get("changes") instanceof Map<?, ?> rawChanges) {
                            rawChanges.forEach((key, value) ->
                                    changes.put(String.valueOf(key), value == null ? null : String.valueOf(value)));
                        }
                        Object revision = parameters.get("expectedRevision");
                        yield properties.patch(revision == null ? null : revision.toString(), changes);
                    }
                    default -> throw ApiException.badRequest("Confirmation action is invalid.");
                };
                Map<String, Object> metadata = new LinkedHashMap<>();
                result.forEach((key, value) -> {
                    if (key.equals("apiVersion") || key.equals("properties") || value == null
                            || value instanceof Map<?, ?> || value instanceof List<?>) {
                        return;
                    }
                    metadata.put(key, String.valueOf(value));
                });
                Map<String, Object> completed = operations.succeed(operationId, metadata);
                if (kind.equals("backup.restore")) {
                    events.publish("backup.restore_ready", "Restore is ready for the next server restart",
                            Map.of("backupId", parameter(parameters, "backupId")));
                }
                Map<String, Object> response = Json.message();
                response.put("operation", completed);
                response.putAll(result);
                response.put("apiVersion", Protocol.API_VERSION);
                return response;
            } catch (RuntimeException e) {
                failOperation(operationId, e);
                throw e;
            }
        });
    }

    private Map<String, Object> normalizeConfirmation(String action, Map<String, Object> parameters) {
        Map<String, Object> normalized = new LinkedHashMap<>();
        switch (action) {
            case "file.delete" -> {
                normalized.put("path", files.normalize(parameter(parameters, "path")));
                Object revision = parameters.get("expectedRevision");
                normalized.put("expectedRevision", revision == null ? "" : revision.toString());
            }
            case "backup.restore" -> {
                String id = parameter(parameters, "backupId");
                backups.verifiedPaths(id);
                normalized.put("backupId", id);
            }
            case "backup.delete" -> {
                String id = parameter(parameters, "backupId");
                backups.verifiedPaths(id);
                normalized.put("backupId", id);
            }
            case "properties.patch" -> {
                Object revision = parameters.get("expectedRevision");
                normalized.put("expectedRevision", revision == null ? "" : revision.toString());
                Map<String, Object> changes = new LinkedHashMap<>();
                if (parameters.get("changes") instanceof Map<?, ?> rawChanges) {
                    rawChanges.forEach((key, value) ->
                            changes.put(String.valueOf(key), value == null ? null : String.valueOf(value)));
                }
                normalized.put("changes", changes);
            }
            default -> throw ApiException.badRequest("Confirmation action is invalid.");
        }
        return normalized;
    }

    private Map<String, Object> tokens(String method, String path, JsonNode body, AuthContext auth) {
        auth.requireRoot();
        if (path.equals("/tokens") && method.equals("GET")) {
            Map<String, Object> response = Json.message();
            response.put("tokens", tokens.list());
            return response;
        }
        if ((path.equals("/tokens") || path.equals("/tokens/derive")) && method.equals("POST")) {
            String name = HttpInput.requiredText(body, "name");
            var scopes = HttpInput.scopes(body);
            Instant expiresAt = HttpInput.instant(body, "expiresAt");
            return audited(auth, "token.derive", Map.of("name", name, "scopes", scopes.stream().sorted().toList()), () -> {
                TokenService.IssuedToken issued = tokens.derive(auth, name, scopes, expiresAt);
                Map<String, Object> response = Json.message();
                response.put("token", issued.token());
                response.put("tokenInfo", issued.tokenInfo());
                return response;
            });
        }
        boolean revokeRoute = path.equals("/tokens/revoke") || path.endsWith("/revoke");
        boolean rotateRoute = path.equals("/tokens/rotate") || path.endsWith("/rotate");
        if (method.equals("POST") && (revokeRoute || rotateRoute)) {
            String tokenId = HttpInput.text(body, "tokenId");
            if (tokenId == null) {
                String[] segments = path.split("/");
                if (segments.length >= 3) {
                    tokenId = segments[2];
                }
            }
            if (tokenId == null) {
                throw ApiException.badRequest("tokenId is required.");
            }
            String finalTokenId = tokenId;
            if (revokeRoute) {
                return audited(auth, "token.revoke", Map.of("targetTokenId", tokenId),
                        () -> tokens.revoke(auth, finalTokenId));
            }
            int grace = HttpInput.integer(body, "gracePeriodSeconds", 300);
            return audited(auth, "token.rotate", Map.of("targetTokenId", tokenId), () -> {
                TokenService.RotatedToken rotated = tokens.rotate(auth, finalTokenId, grace);
                Map<String, Object> response = Json.message();
                response.put("token", rotated.token());
                response.put("tokenInfo", rotated.tokenInfo());
                response.put("previousTokenExpiresAt", rotated.previousTokenExpiresAt());
                return response;
            });
        }
        throw methodNotAllowed();
    }

    private Map<String, Object> audited(AuthContext auth, String action, Map<String, ?> parameters,
                                        Supplier<Map<String, Object>> operation) {
        audit.before(auth.tokenId(), action, parameters);
        try {
            Map<String, Object> result = operation.get();
            audit.after(auth.tokenId(), action, parameters, true);
            return result;
        } catch (RuntimeException e) {
            try {
                audit.after(auth.tokenId(), action, parameters, false);
            } catch (ApiException ignored) {
                // Preserve the original stable error; the initial accepted record remains append-only.
            }
            throw e;
        }
    }

    private void failOperation(String id, RuntimeException failure) {
        String code = failure instanceof ApiException apiException ? apiException.code() : "operation_failed";
        String message = failure instanceof ApiException ? failure.getMessage() : "The operation could not be completed.";
        operations.fail(id, code, message);
    }

    private Map<String, Object> contentAudit(String path, String content, String revision) {
        Map<String, Object> parameters = new LinkedHashMap<>(safePathAudit(path));
        parameters.put("sizeBytes", content.getBytes(StandardCharsets.UTF_8).length);
        parameters.put("sha256", SecurityUtils.sha256Hex(content));
        parameters.put("expectedRevision", revision);
        return parameters;
    }

    private static Map<String, Object> safePathAudit(String path) {
        return Map.of("path", path);
    }

    private static Map<String, Object> propertiesAudit(Map<String, Object> parameters) {
        Map<String, Object> auditChanges = new LinkedHashMap<>();
        if (parameters.get("changes") instanceof Map<?, ?> rawChanges) {
            rawChanges.forEach((key, value) -> auditChanges.put(String.valueOf(key), value == null
                    ? "removed"
                    : Map.of("sha256", SecurityUtils.sha256Hex(String.valueOf(value)),
                             "length", String.valueOf(value).length())));
        }
        Map<String, Object> audit = new LinkedHashMap<>();
        audit.put("expectedRevision", parameters.get("expectedRevision"));
        audit.put("changes", auditChanges);
        return audit;
    }

    private static String siblingPath(String path, String newName) {
        if (newName.isEmpty() || newName.contains("/") || newName.contains("\\")
                || newName.equals(".") || newName.equals("..")) {
            throw ApiException.badRequest("newName must be a single path segment.");
        }
        int slash = path.lastIndexOf('/');
        return slash < 0 ? newName : path.substring(0, slash + 1) + newName;
    }

    private static String parameter(Map<String, Object> parameters, String name) {
        Object value = parameters.get(name);
        if (!(value instanceof String string) || string.isEmpty()) {
            throw ApiException.badRequest(name + " is required.");
        }
        return string;
    }

    private static Map<String, Object> withApi(Map<String, Object> value) {
        Map<String, Object> response = Json.message();
        response.putAll(value);
        return response;
    }

    private String basePath() {
        return "/v1/servers/" + config.instanceId();
    }

    private static String query(Map<String, List<String>> query, String name) {
        List<String> values = query.get(name);
        return values == null || values.isEmpty() ? null : values.getFirst();
    }

    private static String requiredQuery(Map<String, List<String>> query, String name) {
        String value = query(query, name);
        if (value == null) {
            throw ApiException.badRequest(name + " query parameter is required.");
        }
        return value;
    }

    private static int queryInt(Map<String, List<String>> query, String name, int fallback, int minimum, int maximum) {
        String value = query(query, name);
        if (value == null) {
            return fallback;
        }
        try {
            int parsed = Integer.parseInt(value);
            if (parsed < minimum || parsed > maximum) {
                throw new NumberFormatException();
            }
            return parsed;
        } catch (NumberFormatException e) {
            throw ApiException.badRequest(name + " query parameter is invalid.");
        }
    }

    private static void requireMethod(String actual, String... expected) {
        if (!List.of(expected).contains(actual)) {
            throw methodNotAllowed();
        }
    }

    private static ApiException methodNotAllowed() {
        return new ApiException(405, "method_not_allowed", "HTTP method is not allowed for this endpoint.");
    }
}
