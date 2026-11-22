package mofumofu.nekoprobe;

import org.yaml.snakeyaml.Yaml;

import java.io.IOException;
import java.io.InputStream;
import java.net.URI;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.HashMap;
import java.util.Map;
import java.util.Set;

public record ProbeConfig(
        String instanceId,
        int port,
        String publicUrl,
        Path stateDirectory,
        URI agentCallback) {
    private static final Set<String> CONFIG_KEYS = Set.of("instanceId", "port", "publicUrl", "stateDir", "agentCallback");

    public static ProbeConfig load(Path serverRoot, Path defaultDataDirectory) {
        return load(serverRoot, defaultDataDirectory, System.getenv());
    }

    public static ProbeConfig load(Path serverRoot, Path defaultDataDirectory, Map<String, String> environment) {
        Path root = serverRoot.toAbsolutePath().normalize();
        Map<String, String> file = readConfig(defaultDataDirectory.resolve("config.yml"));
        String defaultInstance = root.getFileName() == null ? "server" : root.getFileName().toString();
        String instance = value(environment, "MOFU_NEKO_INSTANCE_ID", file, "instanceId", defaultInstance).trim();
        if (instance.isEmpty() || instance.length() > 128 || instance.contains("/") || instance.contains("\\")
                || instance.contains("\0") || instance.equals(".") || instance.equals("..")) {
            throw new IllegalArgumentException("NekoProbe instanceId is invalid");
        }
        int port;
        try {
            port = Integer.parseInt(value(environment, "MOFU_NEKO_PORT", file, "port", "28765").trim());
        } catch (NumberFormatException e) {
            throw new IllegalArgumentException("NekoProbe port is invalid");
        }
        if (port < 1 || port > 65535) {
            throw new IllegalArgumentException("NekoProbe port is invalid");
        }
        String publicUrl = value(environment, "MOFU_NEKO_PUBLIC_URL", file, "publicUrl", "").trim();
        if (!publicUrl.isEmpty()) {
            URI parsed = URI.create(publicUrl);
            if (!parsed.isAbsolute() || parsed.getHost() == null
                    || !("http".equalsIgnoreCase(parsed.getScheme()) || "https".equalsIgnoreCase(parsed.getScheme()))) {
                throw new IllegalArgumentException("NekoProbe publicUrl must be an absolute HTTP(S) URL");
            }
        }
        String state = value(environment, "MOFU_NEKO_STATE_DIR", file, "stateDir", "").trim();
        Path configuredState = state.isEmpty() ? defaultDataDirectory : Path.of(state);
        if (!configuredState.isAbsolute()) {
            configuredState = root.resolve(configuredState);
        }
        Path stateDirectory = configuredState.toAbsolutePath().normalize();
        if (stateDirectory.equals(root)) {
            throw new IllegalArgumentException("NekoProbe stateDir must not be the server root");
        }
        String callback = value(environment, "MOFU_NEKO_AGENT_CALLBACK", file, "agentCallback", "").trim();
        URI callbackUri = null;
        if (!callback.isEmpty()) {
            callbackUri = URI.create(callback);
            if (!callbackUri.isAbsolute() || !"127.0.0.1".equals(callbackUri.getHost())
                    || !("http".equalsIgnoreCase(callbackUri.getScheme()) || "https".equalsIgnoreCase(callbackUri.getScheme()))) {
                throw new IllegalArgumentException("NekoProbe agentCallback must be an absolute loopback HTTP(S) URL");
            }
        }
        return new ProbeConfig(instance, port, publicUrl, stateDirectory, callbackUri);
    }

    private static Map<String, String> readConfig(Path path) {
        if (!Files.exists(path)) {
            return Map.of();
        }
        try {
            if (Files.size(path) > 64 * 1024) {
                throw new IllegalArgumentException("NekoProbe config.yml is too large");
            }
            try (InputStream input = Files.newInputStream(path)) {
                Object loaded = new Yaml().load(input);
                if (loaded == null) {
                    return Map.of();
                }
                if (!(loaded instanceof Map<?, ?> mapping)) {
                    throw new IllegalArgumentException("NekoProbe config.yml must be a flat mapping");
                }
                Map<String, String> values = new HashMap<>();
                for (Map.Entry<?, ?> entry : mapping.entrySet()) {
                    if (!(entry.getKey() instanceof String key) || !CONFIG_KEYS.contains(key)
                            || (entry.getValue() != null && !(entry.getValue() instanceof String)
                            && !(entry.getValue() instanceof Number) && !(entry.getValue() instanceof Boolean))) {
                        throw new IllegalArgumentException("NekoProbe config.yml contains an invalid key or value");
                    }
                    values.put(key, entry.getValue() == null ? "" : entry.getValue().toString());
                }
                return values;
            }
        } catch (IOException e) {
            throw new IllegalArgumentException("NekoProbe config.yml could not be read", e);
        }
    }

    private static String value(Map<String, String> environment, String environmentKey,
                                Map<String, String> file, String fileKey, String fallback) {
        if (environment.containsKey(environmentKey)) {
            return environment.get(environmentKey);
        }
        return file.getOrDefault(fileKey, fallback);
    }
}
