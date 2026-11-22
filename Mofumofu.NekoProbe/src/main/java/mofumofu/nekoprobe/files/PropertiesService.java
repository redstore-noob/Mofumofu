package mofumofu.nekoprobe.files;

import mofumofu.nekoprobe.api.ApiException;
import mofumofu.nekoprobe.api.Json;
import mofumofu.nekoprobe.security.AtomicFiles;
import mofumofu.nekoprobe.security.SecurityUtils;

import java.io.IOException;
import java.io.StringReader;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.LinkOption;
import java.nio.file.Path;
import java.time.Instant;
import java.util.Locale;
import java.util.Map;
import java.util.Properties;
import java.util.Set;
import java.util.TreeMap;

public final class PropertiesService {
    private static final Set<String> KNOWN_KEYS = Set.of(
            "allow-flight", "allow-nether", "broadcast-console-to-ops", "broadcast-rcon-to-ops", "difficulty",
            "enable-command-block", "enable-jmx-monitoring", "enable-query", "enable-rcon", "enable-status",
            "enforce-secure-profile", "enforce-whitelist", "entity-broadcast-range-percentage", "force-gamemode",
            "function-permission-level", "gamemode", "generate-structures", "generator-settings", "hardcore",
            "hide-online-players", "initial-disabled-packs", "initial-enabled-packs", "level-name", "level-seed",
            "level-type", "log-ips", "max-chained-neighbor-updates", "max-players", "max-tick-time", "max-world-size",
            "motd", "network-compression-threshold", "online-mode", "op-permission-level", "pause-when-empty-seconds",
            "player-idle-timeout", "prevent-proxy-connections", "pvp", "query.port", "rate-limit", "rcon.port",
            "region-file-compression", "require-resource-pack", "resource-pack", "resource-pack-id", "resource-pack-prompt",
            "resource-pack-sha1", "server-ip", "server-port", "simulation-distance", "spawn-animals", "spawn-monsters",
            "spawn-npcs", "spawn-protection", "sync-chunk-writes", "text-filtering-config", "use-native-transport",
            "view-distance", "white-list");
    private static final Set<String> BOOLEAN_KEYS = Set.of(
            "allow-flight", "allow-nether", "broadcast-console-to-ops", "broadcast-rcon-to-ops", "enable-command-block",
            "enable-jmx-monitoring", "enable-query", "enable-rcon", "enable-status", "enforce-secure-profile",
            "enforce-whitelist", "force-gamemode", "generate-structures", "hardcore", "hide-online-players", "log-ips",
            "online-mode", "prevent-proxy-connections", "pvp", "require-resource-pack", "spawn-animals", "spawn-monsters",
            "spawn-npcs", "sync-chunk-writes", "use-native-transport", "white-list");

    private final Path path;

    public PropertiesService(Path serverRoot) {
        this.path = serverRoot.resolve("server.properties").toAbsolutePath().normalize();
    }

    public synchronized Map<String, Object> get() {
        Loaded loaded = load();
        Map<String, Object> response = Json.message();
        response.put("revision", loaded.revision());
        response.put("restartRequired", false);
        response.put("properties", publicProperties(loaded.values()));
        return response;
    }

    public synchronized Map<String, Object> patch(String expectedRevision, Map<String, String> changes) {
        if (changes == null || changes.isEmpty() || changes.size() > 256) {
            throw ApiException.badRequest("At least one and at most 256 property changes are required.");
        }
        Loaded loaded = load();
        if (expectedRevision == null || !SecurityUtils.fixedTimeEquals(expectedRevision.toLowerCase(Locale.ROOT), loaded.revision())) {
            throw ApiException.conflict("revision_conflict", "server.properties has changed since it was read.");
        }
        TreeMap<String, String> values = new TreeMap<>(loaded.values());
        for (Map.Entry<String, String> change : changes.entrySet()) {
            String key = change.getKey();
            validateKey(key);
            if ("rcon.password".equalsIgnoreCase(key)) {
                throw new ApiException(403, "forbidden", "rcon.password cannot be read or changed through this API.");
            }
            String value = change.getValue();
            if (value == null) {
                if (KNOWN_KEYS.contains(key)) {
                    throw ApiException.badRequest("Known server properties cannot be removed.");
                }
                values.remove(key);
            } else {
                validateValue(key, value);
                values.put(key, value);
            }
        }
        byte[] bytes = serialize(values);
        try {
            AtomicFiles.write(path, bytes, false);
        } catch (IOException e) {
            throw ApiException.internal();
        }
        Map<String, Object> response = Json.message();
        response.put("revision", SecurityUtils.sha256Hex(bytes));
        response.put("restartRequired", true);
        response.put("appliedAt", Instant.now());
        response.put("properties", publicProperties(values));
        return response;
    }

    private Loaded load() {
        try {
            if (Files.isSymbolicLink(path)) {
                throw new ApiException(400, "invalid_path", "server.properties must not be a symbolic link.");
            }
            if (!Files.exists(path, LinkOption.NOFOLLOW_LINKS)) {
                return new Loaded(new TreeMap<>(), SecurityUtils.sha256Hex(new byte[0]));
            }
            if (Files.size(path) > 1024 * 1024) {
                throw new ApiException(413, "payload_too_large", "server.properties exceeds the supported size.");
            }
            byte[] bytes = Files.readAllBytes(path);
            Properties properties = new Properties();
            properties.load(new StringReader(StandardCharsets.UTF_8.newDecoder()
                    .onMalformedInput(java.nio.charset.CodingErrorAction.REPORT)
                    .onUnmappableCharacter(java.nio.charset.CodingErrorAction.REPORT)
                    .decode(java.nio.ByteBuffer.wrap(bytes)).toString()));
            TreeMap<String, String> values = new TreeMap<>();
            properties.stringPropertyNames().forEach(key -> values.put(key, properties.getProperty(key)));
            return new Loaded(values, SecurityUtils.sha256Hex(bytes));
        } catch (ApiException e) {
            throw e;
        } catch (IOException e) {
            throw ApiException.internal();
        }
    }

    private static TreeMap<String, String> publicProperties(Map<String, String> values) {
        TreeMap<String, String> visible = new TreeMap<>(values);
        visible.keySet().removeIf("rcon.password"::equalsIgnoreCase);
        return visible;
    }

    private static void validateKey(String key) {
        if (key == null || key.isEmpty() || key.length() > 128 || !key.matches("[A-Za-z0-9._-]+")) {
            throw ApiException.badRequest("A property key is invalid.");
        }
    }

    private static void validateValue(String key, String value) {
        if (value.length() > 4096 || value.chars().anyMatch(Character::isISOControl)) {
            throw ApiException.badRequest("A property value is invalid.");
        }
        if (BOOLEAN_KEYS.contains(key) && !("true".equals(value) || "false".equals(value))) {
            throw ApiException.badRequest("Property " + key + " must be true or false.");
        }
        if (key.equals("difficulty") && !Set.of("peaceful", "easy", "normal", "hard").contains(value)) {
            throw ApiException.badRequest("difficulty is invalid.");
        }
        if (key.equals("gamemode") && !Set.of("survival", "creative", "adventure", "spectator").contains(value)) {
            throw ApiException.badRequest("gamemode is invalid.");
        }
        if (Set.of("server-port", "query.port", "rcon.port").contains(key)) {
            validateInteger(key, value, 0, 65535);
        } else if (Set.of("max-players", "view-distance", "simulation-distance", "spawn-protection", "max-world-size",
                "player-idle-timeout", "rate-limit", "max-tick-time", "op-permission-level", "function-permission-level",
                "entity-broadcast-range-percentage", "network-compression-threshold", "pause-when-empty-seconds",
                "max-chained-neighbor-updates").contains(key)) {
            validateInteger(key, value, 0, Integer.MAX_VALUE);
        }
    }

    private static void validateInteger(String key, String value, int minimum, int maximum) {
        try {
            long parsed = Long.parseLong(value);
            if (parsed < minimum || parsed > maximum) {
                throw new NumberFormatException();
            }
        } catch (NumberFormatException e) {
            throw ApiException.badRequest("Property " + key + " must be an integer in its supported range.");
        }
    }

    private static byte[] serialize(TreeMap<String, String> values) {
        StringBuilder output = new StringBuilder();
        for (Map.Entry<String, String> entry : values.entrySet()) {
            output.append(escape(entry.getKey(), true)).append('=').append(escape(entry.getValue(), false)).append('\n');
        }
        return output.toString().getBytes(StandardCharsets.UTF_8);
    }

    private static String escape(String value, boolean key) {
        StringBuilder result = new StringBuilder(value.length());
        for (int i = 0; i < value.length(); i++) {
            char c = value.charAt(i);
            switch (c) {
                case '\\' -> result.append("\\\\");
                case '\t' -> result.append("\\t");
                case '\n' -> result.append("\\n");
                case '\r' -> result.append("\\r");
                case '\f' -> result.append("\\f");
                case '=', ':', '#', '!' -> result.append('\\').append(c);
                case ' ' -> {
                    if (key || i == 0) {
                        result.append('\\');
                    }
                    result.append(c);
                }
                default -> result.append(c);
            }
        }
        return result.toString();
    }

    private record Loaded(TreeMap<String, String> values, String revision) {
    }
}
