package mofumofu.nekoprobe.bukkit;

import mofumofu.nekoprobe.api.ApiException;
import mofumofu.nekoprobe.api.Json;
import mofumofu.nekoprobe.security.SecurityUtils;

import java.time.Clock;
import java.time.Instant;
import java.util.ArrayList;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

public final class OperationRegistry {
    private final Clock clock;
    private final ConsoleRing journal;
    private final Map<String, Map<String, Object>> operations = new LinkedHashMap<>();

    public OperationRegistry(Clock clock, ConsoleRing journal) {
        this.clock = clock;
        this.journal = journal;
    }

    public synchronized Map<String, Object> create(String kind) {
        Map<String, Object> operation = new LinkedHashMap<>();
        operation.put("id", SecurityUtils.randomHex(16));
        operation.put("kind", kind);
        operation.put("state", "queued");
        operation.put("createdAt", Instant.now(clock));
        operations.put(operation.get("id").toString(), operation);
        trim();
        journal.appendOperation(copy(operation));
        return copy(operation);
    }

    public synchronized Map<String, Object> start(String id) {
        Map<String, Object> operation = required(id);
        operation.put("state", "running");
        operation.put("startedAt", Instant.now(clock));
        journal.appendOperation(copy(operation));
        return copy(operation);
    }

    public synchronized Map<String, Object> succeed(String id, Map<String, ?> metadata) {
        Map<String, Object> operation = required(id);
        operation.put("state", "succeeded");
        operation.put("completedAt", Instant.now(clock));
        operation.put("progressPercent", 100.0);
        if (metadata != null && !metadata.isEmpty()) {
            operation.put("metadata", new LinkedHashMap<>(metadata));
        }
        journal.appendOperation(copy(operation));
        return copy(operation);
    }

    public synchronized Map<String, Object> fail(String id, String code, String message) {
        Map<String, Object> operation = required(id);
        operation.put("state", "failed");
        operation.put("completedAt", Instant.now(clock));
        Map<String, Object> error = Json.message();
        error.put("code", code);
        error.put("message", message);
        operation.put("error", error);
        journal.appendOperation(copy(operation));
        return copy(operation);
    }

    public synchronized Map<String, Object> get(String id) {
        return copy(required(id));
    }

    public synchronized List<Map<String, Object>> list() {
        List<Map<String, Object>> result = new ArrayList<>();
        operations.values().forEach(value -> result.add(copy(value)));
        return result;
    }

    private Map<String, Object> required(String id) {
        Map<String, Object> operation = operations.get(id);
        if (operation == null) {
            throw new ApiException(404, "operation_not_found", "Operation was not found.");
        }
        return operation;
    }

    private void trim() {
        while (operations.size() > 512) {
            Iterator<String> iterator = operations.keySet().iterator();
            String remove = null;
            while (iterator.hasNext()) {
                String id = iterator.next();
                String state = operations.get(id).get("state").toString();
                if (state.equals("succeeded") || state.equals("failed")) {
                    remove = id;
                    break;
                }
            }
            if (remove == null) {
                remove = iteratorFirst();
            }
            operations.remove(remove);
        }
    }

    private String iteratorFirst() {
        return operations.keySet().iterator().next();
    }

    private static Map<String, Object> copy(Map<String, Object> value) {
        return Json.MAPPER.convertValue(value, Json.MAPPER.getTypeFactory().constructMapType(LinkedHashMap.class, String.class, Object.class));
    }
}
