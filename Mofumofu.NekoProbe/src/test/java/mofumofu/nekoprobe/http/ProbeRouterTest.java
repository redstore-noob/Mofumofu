package mofumofu.nekoprobe.http;

import com.fasterxml.jackson.databind.JsonNode;
import mofumofu.nekoprobe.ProbeConfig;
import mofumofu.nekoprobe.api.Json;
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
import mofumofu.nekoprobe.security.TokenRateLimiter;
import mofumofu.nekoprobe.security.TokenService;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Clock;
import java.time.Instant;
import java.time.ZoneOffset;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

public final class ProbeRouterTest {
    private static final Clock CLOCK = Clock.fixed(Instant.parse("2026-11-22T08:00:00Z"), ZoneOffset.UTC);

    @TempDir
    Path root;

    private Path state;
    private ProbeRouter router;
    private AuthContext auth;
    private FakeGateway gateway;

    @BeforeEach
    void setUp() throws Exception {
        state = root.resolve("state");
        Files.createDirectories(state);
        ProbeConfig config = new ProbeConfig("survival", 0, "https://example.test", state, null);
        ConsoleRing journal = new ConsoleRing(256, CLOCK);
        gateway = new FakeGateway();
        router = new ProbeRouter(config, "test", CLOCK, System.nanoTime(),
                new TokenService(state, "survival", CLOCK), new TokenRateLimiter(1000, CLOCK),
                new AuditLog(state, CLOCK), new ConfirmationService("survival", CLOCK), gateway,
                new SafeFileService(root, state), new PropertiesService(root),
                new BackupService(root, state, CLOCK, gateway), new OperationRegistry(CLOCK, journal),
                journal, new EventService(CLOCK, journal, null));
        auth = new AuthContext("test-token", Set.of("read", "players", "files", "backup", "command"), true,
                CLOCK.instant());
    }

    @Test
    void canonicalFileRoutesCreateRenameAndDeleteWithConfirmation() {
        Map<String, Object> created = router.route("POST", "/files/create", Map.of(),
                body("{\"path\":\"new.txt\",\"isDirectory\":false}"), auth);
        assertEquals("new.txt", created.get("path"));
        assertEquals(false, created.get("isDirectory"));

        Map<String, Object> renamed = router.route("POST", "/files/rename", Map.of(),
                body("{\"path\":\"new.txt\",\"newName\":\"renamed.txt\",\"expectedRevision\":\""
                        + created.get("revision") + "\"}"), auth);
        assertEquals("renamed.txt", renamed.get("path"));
        assertEquals("renamed.txt", renamed.get("newPath"));

        Map<String, Object> prepared = router.route("POST", "/files/delete/prepare", Map.of(),
                body("{\"path\":\"renamed.txt\",\"expectedRevision\":\"" + renamed.get("revision") + "\"}"),
                auth);
        assertNotNull(prepared.get("confirmationId"));

        Map<String, Object> executed = execute(prepared);
        assertEquals("succeeded", operation(executed).get("state"));
        assertFalse(Files.exists(root.resolve("renamed.txt")));
    }

    @Test
    void canonicalPlayerRoutesListOnlineAndAddEntries() {
        Map<String, Object> online = router.route("GET", "/players/online", Map.of(), null, auth);
        assertEquals(0, online.get("onlineCount"));

        Map<String, Object> added = router.route("POST", "/players/whitelist/add", Map.of(),
                body("{\"playerName\":\"Steve\",\"expectedRevision\":\"r1\"}"), auth);
        assertEquals("whitelist", added.get("kind"));
        assertEquals("Steve", map(added.get("entry")).get("name"));
        assertEquals(1, gateway.added.size());
    }

    @Test
    void propertiesPrepareAndExecuteBoundRevision() {
        Map<String, Object> before = router.route("GET", "/properties", Map.of(), null, auth);
        Map<String, Object> prepared = router.route("POST", "/properties/prepare", Map.of(),
                body("{\"expectedRevision\":\"" + before.get("revision")
                        + "\",\"changes\":{\"motd\":\"Hello\"}}"), auth);

        Map<String, Object> executed = execute(prepared);
        Map<String, Object> operation = operation(executed);
        assertEquals("succeeded", operation.get("state"));
        assertEquals("true", map(operation.get("metadata")).get("restartRequired"));

        Map<String, Object> after = router.route("GET", "/properties", Map.of(), null, auth);
        assertEquals("Hello", map(after.get("properties")).get("motd"));
    }

    @Test
    void canonicalBackupPrepareRoutesAcceptProtocolRequests() throws Exception {
        Files.writeString(root.resolve("world.txt"), "data");
        Map<String, Object> backup = router.route("POST", "/backups", Map.of(),
                body("{\"label\":\"test\"}"), auth);
        String id = (String) map(backup.get("backup")).get("id");
        assertNotNull(id);

        Map<String, Object> restore = router.route("POST", "/backups/restore/prepare", Map.of(),
                body("{\"backupId\":\"" + id + "\"}"), auth);
        assertNotNull(restore.get("confirmationId"));
        assertEquals("backup.restore", restore.get("action"));

        Map<String, Object> delete = router.route("POST", "/backups/delete/prepare", Map.of(),
                body("{\"backupId\":\"" + id + "\"}"), auth);
        assertNotNull(delete.get("confirmationId"));
        assertEquals("backup.delete", delete.get("action"));
    }

    private Map<String, Object> execute(Map<String, Object> prepared) {
        return router.route("POST", "/confirm/execute", Map.of(),
                body("{\"confirmationId\":\"" + prepared.get("confirmationId") + "\"}"), auth);
    }

    private static JsonNode body(String json) {
        return Json.read("{\"apiVersion\":\"1.0\"," + json.substring(1));
    }

    @SuppressWarnings("unchecked")
    private static Map<String, Object> map(Object value) {
        return (Map<String, Object>) value;
    }

    private static Map<String, Object> operation(Map<String, Object> executed) {
        return map(executed.get("operation"));
    }

    private static final class FakeGateway implements ServerGateway {
        private final List<String> added = new ArrayList<>();

        @Override
        public Map<String, Object> status() {
            return Map.of("state", "running", "onlinePlayers", 0, "maxPlayers", 20);
        }

        @Override
        public Map<String, Object> serverInfo() {
            return Map.of("serverName", "Test", "serverImplementationVersion", "Paper test",
                    "minecraftVersion", "1.21.4");
        }

        @Override
        public Map<String, Object> onlinePlayers() {
            return Map.of("onlineCount", 0, "maxPlayers", 20, "players", List.of());
        }

        @Override
        public boolean dispatchCommand(String command) {
            return true;
        }

        @Override
        public Map<String, Object> playerList(String kind) {
            return Map.of("kind", kind, "revision", "r1", "entries", List.of());
        }

        @Override
        public Map<String, Object> addPlayer(String kind, String playerName, String expectedRevision,
                                             Integer opLevel, Boolean bypassesPlayerLimit, String reason,
                                             Instant expiresAt) {
            added.add(playerName);
            Map<String, Object> entry = new LinkedHashMap<>();
            entry.put("name", playerName);
            return Map.of("kind", kind, "revision", "r2", "entry", entry, "removed", false);
        }

        @Override
        public Map<String, Object> removePlayer(String kind, String playerName, String expectedRevision) {
            Map<String, Object> entry = new LinkedHashMap<>();
            entry.put("name", playerName);
            return Map.of("kind", kind, "revision", "r2", "entry", entry, "removed", true);
        }

        @Override
        public void saveOff() {
        }

        @Override
        public void saveAllFlush() {
        }

        @Override
        public void saveOn() {
        }
    }
}
