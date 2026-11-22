package mofumofu.nekoprobe.http;

import com.fasterxml.jackson.databind.JsonNode;
import mofumofu.nekoprobe.api.ApiException;
import mofumofu.nekoprobe.api.Protocol;

import java.time.Instant;
import java.time.format.DateTimeParseException;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.Map;
import java.util.Set;

final class HttpInput {
    private HttpInput() {
    }

    static void version(JsonNode body) {
        if (body == null || body.isNull() || body.isMissingNode()) {
            return;
        }
        if (!body.isObject()) {
            throw ApiException.badRequest("JSON request body must be an object.");
        }
        JsonNode version = body.get("apiVersion");
        if (version == null || !version.isTextual()) {
            throw new ApiException(400, "invalid_api_version", "apiVersion is required.");
        }
        if (!Protocol.API_VERSION.equals(version.textValue())) {
            throw new ApiException(400, "unsupported_api_version", "This endpoint supports apiVersion 1.0.");
        }
    }

    static JsonNode object(JsonNode body) {
        if (body == null || !body.isObject()) {
            throw ApiException.badRequest("A JSON object request body is required.");
        }
        return body;
    }

    static String text(JsonNode body, String field) {
        JsonNode value = object(body).get(field);
        return value == null || value.isNull() ? null : value.isTextual() ? value.textValue() : invalid(field);
    }

    static String requiredText(JsonNode body, String field) {
        String value = text(body, field);
        if (value == null) {
            throw ApiException.badRequest(field + " is required.");
        }
        return value;
    }

    static int integer(JsonNode body, String field, int fallback) {
        JsonNode value = object(body).get(field);
        if (value == null || value.isNull()) {
            return fallback;
        }
        if (!value.isIntegralNumber() || !value.canConvertToInt()) {
            throw ApiException.badRequest(field + " must be an integer.");
        }
        return value.intValue();
    }

    static boolean bool(JsonNode body, String field, boolean fallback) {
        JsonNode value = object(body).get(field);
        if (value == null || value.isNull()) {
            return fallback;
        }
        if (!value.isBoolean()) {
            throw ApiException.badRequest(field + " must be a boolean.");
        }
        return value.booleanValue();
    }

    static Set<String> scopes(JsonNode body) {
        JsonNode value = object(body).get("scopes");
        if (value == null || !value.isArray()) {
            throw ApiException.badRequest("scopes must be an array.");
        }
        Set<String> scopes = new LinkedHashSet<>();
        value.forEach(item -> {
            if (!item.isTextual()) {
                throw ApiException.badRequest("scopes must contain strings.");
            }
            scopes.add(item.textValue());
        });
        return scopes;
    }

    static Instant instant(JsonNode body, String field) {
        String value = text(body, field);
        if (value == null) {
            return null;
        }
        try {
            return Instant.parse(value);
        } catch (DateTimeParseException e) {
            throw ApiException.badRequest(field + " must be an RFC3339 UTC timestamp.");
        }
    }

    static Map<String, String> stringMap(JsonNode body, String field) {
        JsonNode value = object(body).get(field);
        if (value == null || !value.isObject()) {
            throw ApiException.badRequest(field + " must be an object.");
        }
        Map<String, String> result = new LinkedHashMap<>();
        value.fields().forEachRemaining(entry -> {
            if (!entry.getValue().isTextual()) {
                throw ApiException.badRequest(field + " values must be strings.");
            }
            result.put(entry.getKey(), entry.getValue().textValue());
        });
        return result;
    }

    static Map<String, String> nullableStringMap(JsonNode body, String field) {
        JsonNode value = object(body).get(field);
        if (value == null || !value.isObject()) {
            throw ApiException.badRequest(field + " must be an object.");
        }
        Map<String, String> result = new LinkedHashMap<>();
        value.fields().forEachRemaining(entry -> result.put(entry.getKey(),
                entry.getValue().isNull() ? null : entry.getValue().isTextual() ? entry.getValue().textValue() : invalid(field)));
        return result;
    }

    static Map<String, Object> parameters(JsonNode body) {
        JsonNode value = object(body).get("parameters");
        if (value != null) {
            if (!value.isObject()) {
                throw ApiException.badRequest("parameters must be an object.");
            }
            return mofumofu.nekoprobe.api.Json.MAPPER.convertValue(value,
                    mofumofu.nekoprobe.api.Json.MAPPER.getTypeFactory().constructMapType(LinkedHashMap.class, String.class, Object.class));
        }
        Map<String, Object> result = new LinkedHashMap<>();
        body.fields().forEachRemaining(entry -> {
            if (!entry.getKey().equals("apiVersion") && !entry.getKey().equals("action")) {
                result.put(entry.getKey(), mofumofu.nekoprobe.api.Json.MAPPER.convertValue(entry.getValue(), Object.class));
            }
        });
        return result;
    }

    private static String invalid(String field) {
        throw ApiException.badRequest(field + " must be a string.");
    }
}
