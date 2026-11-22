package mofumofu.nekoprobe.backup;

import mofumofu.nekoprobe.api.ApiException;
import mofumofu.nekoprobe.api.Json;
import mofumofu.nekoprobe.security.AtomicFiles;
import mofumofu.nekoprobe.security.SecurityUtils;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.channels.FileChannel;
import java.nio.file.Files;
import java.nio.file.LinkOption;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.nio.file.StandardOpenOption;
import java.time.Clock;
import java.time.Instant;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.stream.Stream;
import java.util.zip.ZipEntry;
import java.util.zip.ZipOutputStream;

public final class BackupService {
    private final Path root;
    private final Path backupsDirectory;
    private final Path stateDirectory;
    private final Path pendingPath;
    private final Clock clock;
    private final BackupControl control;
    private final Set<String> protectedPaths;

    public BackupService(Path root, Path stateDirectory, Clock clock, BackupControl control) {
        this.root = root.toAbsolutePath().normalize();
        this.backupsDirectory = this.root.resolve("backups");
        this.stateDirectory = stateDirectory.toAbsolutePath().normalize();
        this.pendingPath = this.stateDirectory.resolve("pendingRestore.json");
        this.clock = clock;
        this.control = control;
        Set<String> protectedValues = new HashSet<>(Set.of("backups", ".nekoprobe-restore", "logs"));
        protectedValues.add(stateRelative());
        this.protectedPaths = Set.copyOf(protectedValues);
    }

    public synchronized Map<String, Object> list() {
        List<Map<String, Object>> backups = new ArrayList<>();
        try {
            Files.createDirectories(backupsDirectory);
            try (Stream<Path> files = Files.list(backupsDirectory)) {
                for (Path metadata : files.filter(p -> p.getFileName().toString().endsWith(".json")).sorted().toList()) {
                    String id = metadata.getFileName().toString().substring(0, metadata.getFileName().toString().length() - 5);
                    if (!validId(id)) {
                        continue;
                    }
                    Map<String, Object> value = readMetadata(id);
                    if (Files.isRegularFile(paths(id).archive(), LinkOption.NOFOLLOW_LINKS)) {
                        backups.add(value);
                    }
                }
            }
        } catch (ApiException e) {
            throw e;
        } catch (IOException e) {
            throw ApiException.internal();
        }
        backups.sort((left, right) -> Instant.parse(right.get("createdAt").toString())
                .compareTo(Instant.parse(left.get("createdAt").toString())));
        Map<String, Object> response = Json.message();
        response.put("backups", backups);
        return response;
    }

    public synchronized Map<String, Object> create(String label, String description, String minecraftVersion) {
        validateLabel(label, description);
        try {
            if (control != null) {
                control.saveOff();
                control.saveAllFlush();
            }
            return createArchive(label, description, minecraftVersion);
        } finally {
            if (control != null) {
                control.saveOn();
            }
        }
    }

    public synchronized Map<String, Object> createCold(String label, String minecraftVersion) {
        return createArchive(label, "Automatic safety backup before restore", minecraftVersion);
    }

    public synchronized Map<String, Object> delete(String backupId) {
        BackupPaths paths = paths(backupId);
        if (!Files.exists(paths.archive(), LinkOption.NOFOLLOW_LINKS)) {
            throw ApiException.notFound("Backup");
        }
        try {
            Files.delete(paths.metadata());
            Files.delete(paths.archive());
        } catch (IOException e) {
            throw ApiException.internal();
        }
        Map<String, Object> response = Json.message();
        response.put("backupId", backupId);
        response.put("deleted", true);
        return response;
    }

    public synchronized Map<String, Object> prepareRestore(String backupId) {
        BackupPaths paths = paths(backupId);
        Map<String, Object> metadata = readMetadata(backupId);
        verify(paths, metadata);
        ZipSecurity.inspect(paths.archive(), protectedPaths);
        Map<String, Object> pending = new LinkedHashMap<>();
        pending.put("backupId", backupId);
        pending.put("createdAt", Instant.now(clock));
        try {
            Files.createDirectories(stateDirectory);
            AtomicFiles.write(pendingPath, Json.bytes(pending), true);
        } catch (IOException e) {
            throw new ApiException(503, "restore_state_unavailable", "Pending restore could not be persisted.");
        }
        Map<String, Object> response = Json.message();
        response.put("backupId", backupId);
        response.put("pendingRestart", true);
        return response;
    }

    public PendingRestore pendingRestore() {
        if (!Files.exists(pendingPath, LinkOption.NOFOLLOW_LINKS)) {
            return null;
        }
        try {
            PendingRestore pending = Json.MAPPER.readValue(Files.readAllBytes(pendingPath), PendingRestore.class);
            if (pending == null || !validId(pending.backupId()) || pending.createdAt() == null) {
                throw new IOException("Invalid pending restore");
            }
            return pending;
        } catch (IOException | RuntimeException e) {
            throw new IllegalStateException("Pending restore state is invalid");
        }
    }

    public void clearPendingRestore() throws IOException {
        Files.deleteIfExists(pendingPath);
    }

    public BackupPaths verifiedPaths(String backupId) {
        BackupPaths paths = paths(backupId);
        verify(paths, readMetadata(backupId));
        ZipSecurity.inspect(paths.archive(), protectedPaths);
        return paths;
    }

    public Set<String> protectedPaths() {
        return protectedPaths;
    }

    private Map<String, Object> createArchive(String label, String description, String minecraftVersion) {
        String id = UUID.randomUUID().toString();
        BackupPaths paths = paths(id);
        Path temporary = backupsDirectory.resolve("." + id + ".tmp");
        long entries = 0;
        long totalBytes = 0;
        try {
            Files.createDirectories(backupsDirectory);
            if (Files.isSymbolicLink(backupsDirectory)) {
                throw new IOException("Backup directory link");
            }
            Files.deleteIfExists(temporary);
            try (ZipOutputStream zip = new ZipOutputStream(new BufferedOutputStream(Files.newOutputStream(temporary,
                    StandardOpenOption.CREATE_NEW, StandardOpenOption.WRITE)))) {
                try (Stream<Path> walk = Files.walk(root)) {
                    for (Path path : walk.sorted().toList()) {
                        if (path.equals(root) || Files.isSymbolicLink(path) || !Files.isRegularFile(path, LinkOption.NOFOLLOW_LINKS)) {
                            continue;
                        }
                        String relative = relative(path);
                        if (isProtected(relative)) {
                            continue;
                        }
                        if (++entries > ZipSecurity.MAX_ENTRIES) {
                            throw new ApiException(413, "backup_limit_exceeded", "Backup contains too many entries.");
                        }
                        long size = Files.size(path);
                        if (size > ZipSecurity.MAX_ENTRY_BYTES || totalBytes > ZipSecurity.MAX_TOTAL_BYTES - size) {
                            throw new ApiException(413, "backup_limit_exceeded", "Backup exceeds the archive size limit.");
                        }
                        totalBytes += size;
                        ZipEntry entry = new ZipEntry(relative);
                        entry.setTime(Files.getLastModifiedTime(path, LinkOption.NOFOLLOW_LINKS).toMillis());
                        zip.putNextEntry(entry);
                        try (InputStream input = new BufferedInputStream(Files.newInputStream(path))) {
                            input.transferTo(zip);
                        }
                        zip.closeEntry();
                    }
                }
            }
            try (FileChannel channel = FileChannel.open(temporary, StandardOpenOption.WRITE)) {
                channel.force(true);
            }
            Files.move(temporary, paths.archive(), StandardCopyOption.ATOMIC_MOVE);
            long size = Files.size(paths.archive());
            String sha256 = sha256(paths.archive());
            Map<String, Object> metadata = new LinkedHashMap<>();
            metadata.put("id", id);
            metadata.put("label", label);
            metadata.put("description", description);
            metadata.put("createdAt", Instant.now(clock));
            metadata.put("sizeBytes", size);
            metadata.put("sha256", sha256);
            metadata.put("minecraftVersion", minecraftVersion);
            AtomicFiles.write(paths.metadata(), Json.bytes(metadata), false);
            Map<String, Object> response = Json.message();
            response.put("backup", metadata);
            return response;
        } catch (ApiException e) {
            throw e;
        } catch (IOException e) {
            throw ApiException.internal();
        } finally {
            try {
                Files.deleteIfExists(temporary);
            } catch (IOException ignored) {
                // Temporary cleanup failure does not expose a partial backup.
            }
        }
    }

    private Map<String, Object> readMetadata(String id) {
        BackupPaths paths = paths(id);
        try {
            if (Files.isSymbolicLink(paths.metadata()) || Files.size(paths.metadata()) > 64 * 1024) {
                throw new IOException("Invalid metadata");
            }
            @SuppressWarnings("unchecked")
            Map<String, Object> metadata = Json.MAPPER.readValue(Files.readAllBytes(paths.metadata()), Map.class);
            if (!id.equals(text(metadata.get("id"))) || text(metadata.get("label")) == null
                    || text(metadata.get("createdAt")) == null || text(metadata.get("sha256")) == null
                    || !(metadata.get("sizeBytes") instanceof Number)) {
                throw new IOException("Invalid metadata");
            }
            Instant.parse(text(metadata.get("createdAt")));
            return metadata;
        } catch (IOException | RuntimeException e) {
            if (e instanceof ApiException apiException) {
                throw apiException;
            }
            throw ApiException.notFound("Backup");
        }
    }

    private void verify(BackupPaths paths, Map<String, Object> metadata) {
        try {
            if (Files.isSymbolicLink(paths.archive()) || !Files.isRegularFile(paths.archive(), LinkOption.NOFOLLOW_LINKS)) {
                throw ApiException.notFound("Backup");
            }
            long actualSize = Files.size(paths.archive());
            if (actualSize != ((Number) metadata.get("sizeBytes")).longValue()
                    || !SecurityUtils.fixedTimeEquals(sha256(paths.archive()), text(metadata.get("sha256")))) {
                throw ApiException.conflict("backup_corrupt", "Backup checksum or size does not match its metadata.");
            }
        } catch (ApiException e) {
            throw e;
        } catch (IOException e) {
            throw ApiException.internal();
        }
    }

    private BackupPaths paths(String id) {
        if (!validId(id)) {
            throw ApiException.notFound("Backup");
        }
        return new BackupPaths(backupsDirectory.resolve(id + ".zip"), backupsDirectory.resolve(id + ".json"));
    }

    private String stateRelative() {
        if (stateDirectory.startsWith(root) && !stateDirectory.equals(root)) {
            return root.relativize(stateDirectory).toString().replace(root.getFileSystem().getSeparator(), "/");
        }
        return "";
    }

    private boolean isProtected(String relative) {
        String lower = relative.toLowerCase(Locale.ROOT);
        for (String protectedPath : protectedPaths) {
            String value = protectedPath.toLowerCase(Locale.ROOT);
            if (!value.isEmpty() && (lower.equals(value) || lower.startsWith(value + "/"))) {
                return true;
            }
        }
        return false;
    }

    private String relative(Path path) {
        return root.relativize(path).toString().replace(root.getFileSystem().getSeparator(), "/");
    }

    private static String sha256(Path path) throws IOException {
        try (InputStream input = Files.newInputStream(path)) {
            java.security.MessageDigest digest = java.security.MessageDigest.getInstance("SHA-256");
            byte[] buffer = new byte[128 * 1024];
            int read;
            while ((read = input.read(buffer)) != -1) {
                digest.update(buffer, 0, read);
            }
            return java.util.HexFormat.of().formatHex(digest.digest());
        } catch (java.security.NoSuchAlgorithmException e) {
            throw new IllegalStateException(e);
        }
    }

    private static boolean validId(String id) {
        if (id == null || id.length() != 36) {
            return false;
        }
        try {
            return UUID.fromString(id).toString().equals(id.toLowerCase(Locale.ROOT)) && id.equals(id.toLowerCase(Locale.ROOT));
        } catch (IllegalArgumentException e) {
            return false;
        }
    }

    private static void validateLabel(String label, String description) {
        if (label == null || label.isBlank() || label.length() > 64 || label.chars().anyMatch(Character::isISOControl)
                || (description != null && (description.length() > 512 || description.chars().anyMatch(Character::isISOControl)))) {
            throw ApiException.badRequest("Backup label or description is invalid.");
        }
    }

    private static String text(Object value) {
        return value instanceof String string ? string : null;
    }

    public record BackupPaths(Path archive, Path metadata) {
    }

    public record PendingRestore(String backupId, Instant createdAt) {
    }

    public interface BackupControl {
        void saveOff();

        void saveAllFlush();

        void saveOn();
    }
}
