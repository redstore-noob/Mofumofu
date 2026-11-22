package mofumofu.nekoprobe.security;

import mofumofu.nekoprobe.api.ApiException;
import mofumofu.nekoprobe.api.Json;
import mofumofu.nekoprobe.api.Protocol;

import java.io.IOException;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Clock;
import java.time.Instant;
import java.time.temporal.ChronoUnit;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;

public final class TokenService {
    private final Path registryPath;
    private final Path pairingPath;
    private final String instanceId;
    private final Clock clock;
    private final Map<String, Instant> lastUsed = new ConcurrentHashMap<>();
    private TokenRegistry.Data registry;

    public TokenService(Path stateDirectory, String instanceId, Clock clock) {
        this.registryPath = stateDirectory.resolve("registry.json");
        this.pairingPath = stateDirectory.resolve("pairing.txt");
        this.instanceId = instanceId;
        this.clock = clock;
    }

    public synchronized void initialize(String publicUrl, int port) throws IOException {
        if (Files.exists(registryPath)) {
            registry = load();
            return;
        }
        Files.createDirectories(registryPath.getParent());
        String plaintext = SecurityUtils.newToken();
        Instant now = now();
        TokenRegistry.Data created = TokenRegistry.newData(instanceId, SecurityUtils.randomHex(32));
        List<TokenRegistry.Entry> tokens = new ArrayList<>();
        tokens.add(new TokenRegistry.Entry(SecurityUtils.randomHex(16), SecurityUtils.sha256Hex(plaintext),
                Protocol.SCOPES.stream().sorted().toList(), now, null, false, true));
        created = new TokenRegistry.Data(1, instanceId, 1, created.linkSecret(), tokens);
        persist(created);
        registry = created;

        String base = publicUrl == null || publicUrl.isBlank() ? "http://127.0.0.1:" + port : publicUrl;
        String pairing = "mofumofu://pair?host=" + encode(base)
                + "&token=" + plaintext
                + "&instance=" + encode(instanceId)
                + "&api=" + Protocol.API_VERSION
                + "&caps=" + encode(String.join(",", Protocol.CAPABILITIES)) + "\n";
        try {
            AtomicFiles.write(pairingPath, pairing.getBytes(StandardCharsets.UTF_8), true);
        } catch (IOException e) {
            Files.deleteIfExists(registryPath);
            registry = null;
            throw e;
        }
    }

    public synchronized AuthContext authenticate(String plaintext) {
        ensureReady();
        if (!SecurityUtils.isToken(plaintext)) {
            throw unauthorized();
        }
        String candidate = SecurityUtils.sha256Hex(plaintext);
        Instant now = now();
        TokenRegistry.Entry match = null;
        for (TokenRegistry.Entry token : registry.tokens()) {
            boolean equal = SecurityUtils.fixedTimeEquals(candidate, token.hash());
            if (equal) {
                match = token;
            }
        }
        if (match == null || match.revoked() || (match.notAfter() != null && !now.isBefore(match.notAfter()))) {
            throw unauthorized();
        }
        lastUsed.put(match.tokenId(), now);
        return new AuthContext(match.tokenId(), Set.copyOf(match.scopes()), match.root(), now);
    }

    public String linkSecret() {
        ensureReady();
        return registry.linkSecret();
    }

    public synchronized List<Map<String, Object>> list() {
        ensureReady();
        return registry.tokens().stream()
                .sorted(Comparator.comparing(TokenRegistry.Entry::createdAt))
                .map(this::info)
                .toList();
    }

    public synchronized IssuedToken derive(AuthContext actor, String name, Set<String> scopes, Instant notAfter) {
        actor.requireRoot();
        ensureReady();
        validateName(name);
        validateScopes(scopes);
        Instant now = now();
        if (notAfter != null && !notAfter.isAfter(now)) {
            throw ApiException.badRequest("Token expiration must be in the future.");
        }
        String plaintext = SecurityUtils.newToken();
        TokenRegistry.Entry entry = new TokenRegistry.Entry(SecurityUtils.randomHex(16), SecurityUtils.sha256Hex(plaintext),
                scopes.stream().sorted().toList(), now, notAfter, false, false);
        TokenRegistry.Data updated = append(registry, entry);
        persistAndSwap(updated);
        names.put(entry.tokenId(), name);
        return new IssuedToken(plaintext, info(entry));
    }

    public synchronized Map<String, Object> revoke(AuthContext actor, String tokenId) {
        actor.requireRoot();
        ensureReady();
        TokenRegistry.Data updated = registry.copy();
        boolean found = false;
        List<TokenRegistry.Entry> tokens = new ArrayList<>();
        Instant now = now();
        for (TokenRegistry.Entry entry : updated.tokens()) {
            if (entry.tokenId().equals(tokenId)) {
                found = true;
                if (entry.root() && countActiveRoots(updated, now) <= 1 && !entry.revoked()) {
                    throw ApiException.conflict("conflict", "The last active root token cannot be revoked.");
                }
                entry = entry.with(entry.scopes(), entry.notAfter(), true, entry.root());
            }
            tokens.add(entry);
        }
        if (!found) {
            throw ApiException.notFound("Token");
        }
        updated = withTokens(updated, tokens);
        persistAndSwap(updated);
        tombstones.put(tokenId, now);
        Map<String, Object> result = Json.message();
        result.put("tokenId", tokenId);
        result.put("revokedAt", now);
        return result;
    }

    public synchronized RotatedToken rotate(AuthContext actor, String tokenId, int graceSeconds) {
        actor.requireRoot();
        ensureReady();
        if (graceSeconds != 300) {
            throw ApiException.badRequest("Token rotation grace period is fixed at 300 seconds.");
        }
        TokenRegistry.Entry previous = registry.tokens().stream().filter(t -> t.tokenId().equals(tokenId)).findFirst()
                .orElseThrow(() -> ApiException.notFound("Token"));
        if (previous.revoked() || (previous.notAfter() != null && !now().isBefore(previous.notAfter()))) {
            throw ApiException.conflict("invalid_token", "An expired or revoked token cannot be rotated.");
        }
        Instant now = now();
        Instant previousExpiry = now.plus(Protocol.ROTATION_GRACE_MILLIS, ChronoUnit.MILLIS);
        String plaintext = SecurityUtils.newToken();
        TokenRegistry.Entry replacement = new TokenRegistry.Entry(SecurityUtils.randomHex(16), SecurityUtils.sha256Hex(plaintext),
                previous.scopes(), now, previous.notAfter(), false, previous.root());
        List<TokenRegistry.Entry> tokens = new ArrayList<>();
        for (TokenRegistry.Entry entry : registry.tokens()) {
            tokens.add(entry.tokenId().equals(tokenId) ? entry.with(entry.scopes(), previousExpiry, false, entry.root()) : entry);
        }
        tokens.add(replacement);
        TokenRegistry.Data updated = withTokens(registry.copy(), tokens);
        persistAndSwap(updated);
        return new RotatedToken(plaintext, info(replacement), previousExpiry);
    }

    private TokenRegistry.Data load() throws IOException {
        byte[] bytes = Files.readAllBytes(registryPath);
        if (bytes.length > 1024 * 1024) {
            throw new IOException("Registry is too large");
        }
        final TokenRegistry.Data loaded;
        try {
            loaded = Json.MAPPER.readValue(bytes, TokenRegistry.Data.class);
        } catch (IOException e) {
            throw new IOException("Invalid token registry", e);
        }
        validate(loaded);
        return loaded;
    }

    private void validate(TokenRegistry.Data value) throws IOException {
        if (value == null || value.schemaVersion() != 1 || !instanceId.equals(value.instanceId())
                || value.generation() < 1 || !SecurityUtils.isLowerHex(value.linkSecret(), 64) || value.tokens() == null
                || value.tokens().isEmpty() || value.tokens().size() > 1024) {
            throw new IOException("Invalid token registry");
        }
        Set<String> ids = new LinkedHashSet<>();
        for (TokenRegistry.Entry token : value.tokens()) {
            if (token == null || token.tokenId() == null || token.tokenId().isBlank() || !ids.add(token.tokenId())
                    || !SecurityUtils.isLowerHex(token.hash(), 64) || token.createdAt() == null || token.scopes() == null
                    || !Protocol.SCOPES.containsAll(token.scopes()) || token.scopes().isEmpty()) {
                throw new IOException("Invalid token registry");
            }
        }
    }

    private void persistAndSwap(TokenRegistry.Data updated) {
        try {
            persist(updated);
            registry = updated;
        } catch (IOException e) {
            throw new ApiException(503, "registry_unavailable", "Token registry could not be persisted; no change was applied.");
        }
    }

    private void persist(TokenRegistry.Data value) throws IOException {
        AtomicFiles.write(registryPath, Json.bytes(value), true);
    }

    private TokenRegistry.Data append(TokenRegistry.Data current, TokenRegistry.Entry entry) {
        List<TokenRegistry.Entry> tokens = new ArrayList<>(current.tokens());
        tokens.add(entry);
        return withTokens(current.copy(), tokens);
    }

    private TokenRegistry.Data withTokens(TokenRegistry.Data value, List<TokenRegistry.Entry> tokens) {
        return new TokenRegistry.Data(1, instanceId, value.generation() + 1, value.linkSecret(), List.copyOf(tokens));
    }

    private Map<String, Object> info(TokenRegistry.Entry entry) {
        Map<String, Object> value = new LinkedHashMap<>();
        value.put("id", entry.tokenId());
        value.put("name", names.getOrDefault(entry.tokenId(), entry.root() ? "root" : "derived"));
        value.put("isRoot", entry.root());
        value.put("scopes", entry.scopes());
        value.put("createdAt", entry.createdAt());
        value.put("expiresAt", entry.notAfter());
        value.put("lastUsedAt", lastUsed.get(entry.tokenId()));
        value.put("revokedAt", entry.revoked() ? tombstones.get(entry.tokenId()) : null);
        return value;
    }

    private final Map<String, String> names = new ConcurrentHashMap<>();
    private final Map<String, Instant> tombstones = new ConcurrentHashMap<>();

    private long countActiveRoots(TokenRegistry.Data value, Instant now) {
        return value.tokens().stream().filter(TokenRegistry.Entry::root).filter(t -> !t.revoked())
                .filter(t -> t.notAfter() == null || now.isBefore(t.notAfter())).count();
    }

    private static void validateName(String name) {
        if (name == null || name.isBlank() || name.length() > 64 || name.chars().anyMatch(Character::isISOControl)) {
            throw ApiException.badRequest("Token name must contain 1 to 64 printable characters.");
        }
    }

    private static void validateScopes(Set<String> scopes) {
        if (scopes == null || scopes.isEmpty() || !Protocol.SCOPES.containsAll(scopes)) {
            throw ApiException.badRequest("Token scopes are invalid.");
        }
    }

    private Instant now() {
        return clock.instant().truncatedTo(ChronoUnit.MILLIS);
    }

    private void ensureReady() {
        if (registry == null) {
            throw new IllegalStateException("Token service is not initialized");
        }
    }

    private static ApiException unauthorized() {
        return new ApiException(401, "unauthorized", "Authentication failed.");
    }

    private static String encode(String value) {
        return URLEncoder.encode(value, StandardCharsets.UTF_8);
    }

    public record IssuedToken(String token, Map<String, Object> tokenInfo) {
    }

    public record RotatedToken(String token, Map<String, Object> tokenInfo, Instant previousTokenExpiresAt) {
    }
}
