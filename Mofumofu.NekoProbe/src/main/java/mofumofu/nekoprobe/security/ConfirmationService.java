package mofumofu.nekoprobe.security;

import mofumofu.nekoprobe.api.ApiException;
import mofumofu.nekoprobe.api.Json;
import mofumofu.nekoprobe.api.Protocol;

import java.time.Clock;
import java.time.Instant;
import java.time.temporal.ChronoUnit;
import java.util.HashMap;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.TreeMap;

public final class ConfirmationService {
    private final String instanceId;
    private final Clock clock;
    private final Map<String, Confirmation> confirmations = new HashMap<>();
    private final Map<String, State> terminalStates = new HashMap<>();

    public ConfirmationService(String instanceId, Clock clock) {
        this.instanceId = instanceId;
        this.clock = clock;
    }

    public synchronized Prepared prepare(AuthContext auth, String action, String scope, Map<String, ?> parameters) {
        auth.requireScope(scope);
        cleanup();
        Instant now = clock.instant().truncatedTo(ChronoUnit.MILLIS);
        String id = SecurityUtils.randomHex(32);
        String canonical = canonical(parameters);
        Confirmation confirmation = new Confirmation(id, instanceId, auth.tokenId(), scope, action,
                SecurityUtils.sha256Hex(canonical), canonical, now, now.plusMillis(Protocol.CONFIRMATION_MILLIS));
        confirmations.put(id, confirmation);
        Map<String, Object> response = Json.message();
        response.put("confirmationId", id);
        response.put("action", action);
        response.put("summary", summary(action, parameters));
        response.put("createdAt", confirmation.createdAt());
        response.put("expiresAt", confirmation.expiresAt());
        response.put("expiresInSeconds", 60);
        return new Prepared(response, confirmation);
    }

    public synchronized Consumed consume(String id, AuthContext auth) {
        cleanup();
        if (id == null || !SecurityUtils.isLowerHex(id, 64)) {
            throw new ApiException(404, "confirmation_not_found", "Confirmation was not found.");
        }
        State terminal = terminalStates.get(id);
        if (terminal != null) {
            if (terminal == State.USED) {
                throw new ApiException(409, "confirmation_already_used", "Confirmation has already been consumed.");
            }
            throw new ApiException(410, "confirmation_expired", "Confirmation has expired.");
        }
        Confirmation value = confirmations.remove(id);
        if (value == null) {
            throw new ApiException(404, "confirmation_not_found", "Confirmation was not found.");
        }
        Instant now = clock.instant();
        if (!now.isBefore(value.expiresAt())) {
            terminalStates.put(id, State.EXPIRED);
            throw new ApiException(410, "confirmation_expired", "Confirmation has expired.");
        }
        terminalStates.put(id, State.USED);
        if (!value.instanceId().equals(instanceId) || !value.tokenId().equals(auth.tokenId())) {
            throw new ApiException(403, "forbidden", "Confirmation belongs to a different instance or token.");
        }
        auth.requireScope(value.scope());
        return new Consumed(value.action(), value.canonicalParameters(), value.scope());
    }

    public void verifyParameters(Consumed consumed, Map<String, ?> parameters) {
        if (!consumed.parametersHash().equals(SecurityUtils.sha256Hex(canonical(parameters)))) {
            throw ApiException.conflict("conflict", "Confirmation parameters do not match the prepared operation.");
        }
    }

    private void cleanup() {
        Instant cutoff = clock.instant().minus(10, ChronoUnit.MINUTES);
        confirmations.values().removeIf(value -> value.expiresAt().isBefore(cutoff));
        Iterator<Map.Entry<String, State>> iterator = terminalStates.entrySet().iterator();
        while (iterator.hasNext() && terminalStates.size() > 4096) {
            iterator.next();
            iterator.remove();
        }
    }

    private static String canonical(Map<String, ?> parameters) {
        return Json.write(parameters == null ? Map.of() : new TreeMap<>(parameters));
    }

    private static String summary(String action, Map<String, ?> parameters) {
        Object target = parameters == null ? null
                : parameters.containsKey("path") ? parameters.get("path") : parameters.get("backupId");
        return action + (target == null ? "" : ": " + target);
    }

    private enum State { USED, EXPIRED }

    private record Confirmation(String id, String instanceId, String tokenId, String scope, String action,
                                String parametersHash, String canonicalParameters, Instant createdAt, Instant expiresAt) {
    }

    public record Prepared(Map<String, Object> response, String confirmationId) {
        private Prepared(Map<String, Object> response, Confirmation confirmation) {
            this(response, confirmation.id());
        }
    }

    public record Consumed(String action, String canonicalParameters, String scope) {
        private String parametersHash() {
            return SecurityUtils.sha256Hex(canonicalParameters);
        }
    }
}
