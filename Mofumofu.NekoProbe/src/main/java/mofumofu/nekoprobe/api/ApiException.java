package mofumofu.nekoprobe.api;

import java.util.LinkedHashMap;
import java.util.Map;

public final class ApiException extends RuntimeException {
    private final int status;
    private final String code;
    private final Map<String, String> details;

    public ApiException(int status, String code, String message) {
        this(status, code, message, Map.of());
    }

    public ApiException(int status, String code, String message, Map<String, String> details) {
        super(message, null, false, false);
        this.status = status;
        this.code = code;
        this.details = Map.copyOf(details);
    }

    public int status() {
        return status;
    }

    public String code() {
        return code;
    }

    public Map<String, Object> body(String requestId) {
        Map<String, Object> body = Json.message();
        body.put("code", code);
        body.put("message", getMessage());
        if (!details.isEmpty()) {
            body.put("details", new LinkedHashMap<>(details));
        }
        body.put("requestId", requestId);
        return body;
    }

    public static ApiException badRequest(String message) {
        return new ApiException(400, "invalid_request", message);
    }

    public static ApiException forbidden() {
        return new ApiException(403, "insufficient_scope", "The token does not grant the required scope.");
    }

    public static ApiException notFound(String what) {
        return new ApiException(404, "not_found", what + " was not found.");
    }

    public static ApiException conflict(String code, String message) {
        return new ApiException(409, code, message);
    }

    public static ApiException internal() {
        return new ApiException(500, "internal_error", "The operation could not be completed.");
    }
}
