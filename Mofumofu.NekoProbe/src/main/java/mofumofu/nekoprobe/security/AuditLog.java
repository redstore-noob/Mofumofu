package mofumofu.nekoprobe.security;

import mofumofu.nekoprobe.api.ApiException;
import mofumofu.nekoprobe.api.Json;

import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.channels.FileChannel;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.time.Clock;
import java.time.Instant;
import java.util.LinkedHashMap;
import java.util.Map;

public final class AuditLog {
    private final Path path;
    private final Clock clock;

    public AuditLog(Path stateDirectory, Clock clock) {
        this.path = stateDirectory.resolve("audit.ndjson");
        this.clock = clock;
    }

    public void before(String tokenId, String action, Map<String, ?> parameters) {
        append(tokenId, action, parameters, "accepted");
    }

    public void after(String tokenId, String action, Map<String, ?> parameters, boolean succeeded) {
        append(tokenId, action, parameters, succeeded ? "succeeded" : "failed");
    }

    private synchronized void append(String tokenId, String action, Map<String, ?> parameters, String result) {
        Map<String, Object> entry = new LinkedHashMap<>();
        entry.put("eventId", SecurityUtils.randomHex(16));
        entry.put("timestamp", Instant.now(clock));
        entry.put("tokenId", tokenId);
        entry.put("action", action);
        entry.put("parameters", parameters == null ? Map.of() : parameters);
        entry.put("result", result);
        byte[] line = (Json.write(entry) + "\n").getBytes(java.nio.charset.StandardCharsets.UTF_8);
        try {
            Files.createDirectories(path.getParent());
            if (Files.isSymbolicLink(path)) {
                throw new IOException("Symbolic link audit target");
            }
            try (FileChannel channel = FileChannel.open(path, StandardOpenOption.CREATE, StandardOpenOption.WRITE,
                    StandardOpenOption.APPEND)) {
                while (channel.position() < channel.size()) {
                    channel.position(channel.size());
                }
                ByteBuffer buffer = ByteBuffer.wrap(line);
                while (buffer.hasRemaining()) {
                    channel.write(buffer);
                }
                channel.force(true);
            }
            AtomicFiles.makePrivate(path);
        } catch (IOException | RuntimeException e) {
            throw new ApiException(503, "audit_unavailable", "The audit log is unavailable; the write was not authorized.");
        }
    }
}
