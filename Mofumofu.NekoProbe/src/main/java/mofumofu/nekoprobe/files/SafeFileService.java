package mofumofu.nekoprobe.files;

import mofumofu.nekoprobe.api.ApiException;
import mofumofu.nekoprobe.api.Json;
import mofumofu.nekoprobe.api.Protocol;
import mofumofu.nekoprobe.security.AtomicFiles;
import mofumofu.nekoprobe.security.SecurityUtils;

import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.nio.file.AtomicMoveNotSupportedException;
import java.nio.file.FileVisitOption;
import java.nio.file.Files;
import java.nio.file.InvalidPathException;
import java.nio.file.LinkOption;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.time.Instant;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.stream.Stream;

public final class SafeFileService {
    private static final Set<String> DENIED_FILES = Set.of(
            "server.json", "server.properties", "eula.txt", "eula", "whitelist.json", "ops.json",
            "banned-players.json", "banned-ips.json", "registry.json", "pairing", "pairing.txt", "audit.ndjson", "link.secret");
    private static final Set<String> DENIED_DIRECTORIES = Set.of("logs", "backups", "tokens", "auth", "audit", ".nekoprobe-restore");
    private static final Set<String> WORLD_BINARY_EXTENSIONS = Set.of(
            ".dat", ".dat_old", ".mca", ".mcc", ".nbt", ".db", ".ldb", ".sst", ".zip", ".jar");

    private final Path root;
    private final Path realRoot;
    private final Path stateDirectory;
    private final String protectedStatePath;

    public SafeFileService(Path serverRoot, Path stateDirectory) {
        try {
            this.root = serverRoot.toAbsolutePath().normalize();
            this.realRoot = this.root.toRealPath();
            this.stateDirectory = stateDirectory.toAbsolutePath().normalize();
            this.protectedStatePath = protectedStatePath();
        } catch (IOException e) {
            throw new IllegalArgumentException("Server root is unavailable", e);
        }
    }

    public synchronized Map<String, Object> list(String requestedPath) {
        Resolved resolved = resolve(requestedPath, true, true);
        if (!Files.isDirectory(resolved.path(), LinkOption.NOFOLLOW_LINKS)) {
            throw ApiException.badRequest("The requested path is not a directory.");
        }
        List<Map<String, Object>> entries = new ArrayList<>();
        try (Stream<Path> children = Files.list(resolved.path())) {
            for (Path child : children.sorted(Comparator.comparing(p -> p.getFileName().toString())).toList()) {
                if (Files.isSymbolicLink(child)) {
                    continue;
                }
                String relative = relative(child);
                if (isProtected(relative)) {
                    continue;
                }
                Map<String, Object> entry = new java.util.LinkedHashMap<>();
                entry.put("path", relative);
                boolean directory = Files.isDirectory(child, LinkOption.NOFOLLOW_LINKS);
                entry.put("isDirectory", directory);
                entry.put("sizeBytes", directory ? null : Files.size(child));
                entry.put("modifiedAt", Files.getLastModifiedTime(child, LinkOption.NOFOLLOW_LINKS).toInstant());
                entry.put("revision", directory ? null : revision(child));
                entry.put("writable", true);
                entries.add(entry);
            }
        } catch (IOException e) {
            throw ApiException.internal();
        }
        Map<String, Object> response = Json.message();
        response.put("path", resolved.relative());
        response.put("entries", entries);
        return response;
    }

    public synchronized Map<String, Object> read(String requestedPath) {
        Resolved resolved = resolve(requestedPath, true, false);
        if (!Files.isRegularFile(resolved.path(), LinkOption.NOFOLLOW_LINKS)) {
            throw ApiException.notFound("File");
        }
        try {
            byte[] bytes = readLimited(resolved.path());
            String content = decodeUtf8(bytes);
            Map<String, Object> response = Json.message();
            response.put("path", resolved.relative());
            response.put("content", content);
            response.put("encoding", "utf-8");
            response.put("revision", SecurityUtils.sha256Hex(bytes));
            response.put("sizeBytes", bytes.length);
            response.put("modifiedAt", Files.getLastModifiedTime(resolved.path(), LinkOption.NOFOLLOW_LINKS).toInstant());
            return response;
        } catch (ApiException e) {
            throw e;
        } catch (IOException e) {
            throw ApiException.internal();
        }
    }

    public synchronized Map<String, Object> write(String requestedPath, String content, String expectedRevision) {
        if (content == null) {
            throw ApiException.badRequest("File content is required.");
        }
        byte[] bytes = content.getBytes(StandardCharsets.UTF_8);
        if (bytes.length > Protocol.MAX_TEXT_BYTES) {
            throw new ApiException(413, "payload_too_large", "Text files are limited to 2 MiB.");
        }
        Resolved resolved = resolve(requestedPath, false, false);
        boolean exists = Files.exists(resolved.path(), LinkOption.NOFOLLOW_LINKS);
        if (exists && Files.isDirectory(resolved.path(), LinkOption.NOFOLLOW_LINKS)) {
            throw ApiException.conflict("conflict", "A directory exists at the requested path.");
        }
        verifyRevision(resolved.path(), expectedRevision, exists);
        try {
            AtomicFiles.write(resolved.path(), bytes, false);
            Resolved verify = resolve(requestedPath, true, false);
            Map<String, Object> response = Json.message();
            response.put("path", verify.relative());
            response.put("revision", SecurityUtils.sha256Hex(bytes));
            response.put("sizeBytes", bytes.length);
            response.put("modifiedAt", Files.getLastModifiedTime(verify.path(), LinkOption.NOFOLLOW_LINKS).toInstant());
            return response;
        } catch (IOException e) {
            throw ApiException.internal();
        }
    }

    public synchronized Map<String, Object> create(String requestedPath, boolean directory, String content) {
        Resolved resolved = resolve(requestedPath, false, false);
        if (Files.exists(resolved.path(), LinkOption.NOFOLLOW_LINKS)) {
            throw ApiException.conflict("conflict", "The requested path already exists.");
        }
        try {
            if (directory) {
                Files.createDirectory(resolved.path());
            } else {
                if (content == null) {
                    content = "";
                }
                byte[] bytes = content.getBytes(StandardCharsets.UTF_8);
                if (bytes.length > Protocol.MAX_TEXT_BYTES) {
                    throw new ApiException(413, "payload_too_large", "Text files are limited to 2 MiB.");
                }
                AtomicFiles.write(resolved.path(), bytes, false);
            }
        } catch (ApiException e) {
            throw e;
        } catch (IOException e) {
            throw ApiException.internal();
        }
        return mutation(resolved.relative(), directory);
    }

    public synchronized Map<String, Object> rename(String requestedPath, String newPath, String expectedRevision) {
        Resolved source = resolve(requestedPath, true, false);
        Resolved target = resolve(newPath, false, false);
        if (Files.exists(target.path(), LinkOption.NOFOLLOW_LINKS)) {
            throw ApiException.conflict("conflict", "The destination already exists.");
        }
        if (target.path().startsWith(source.path())) {
            throw ApiException.badRequest("A directory cannot be moved into itself.");
        }
        if (!protectedStatePath.isEmpty() && (protectedStatePath.equals(source.relative().toLowerCase(Locale.ROOT))
                || protectedStatePath.startsWith(source.relative().toLowerCase(Locale.ROOT) + "/"))) {
            throw new ApiException(403, "forbidden", "A parent of the protected state directory cannot be renamed.");
        }
        verifyRevision(source.path(), expectedRevision, true);
        try {
            Files.move(source.path(), target.path(), StandardCopyOption.ATOMIC_MOVE);
        } catch (AtomicMoveNotSupportedException e) {
            throw ApiException.conflict("conflict", "Atomic rename is not available for this path.");
        } catch (IOException e) {
            throw ApiException.internal();
        }
        Map<String, Object> response = mutation(target.relative(),
                Files.isDirectory(target.path(), LinkOption.NOFOLLOW_LINKS));
        response.put("newPath", target.relative());
        return response;
    }

    public synchronized Map<String, Object> delete(String requestedPath, String expectedRevision) {
        Resolved resolved = resolve(requestedPath, true, false);
        verifyRevision(resolved.path(), expectedRevision, true);
        try {
            if (Files.isDirectory(resolved.path(), LinkOption.NOFOLLOW_LINKS)) {
                try (Stream<Path> walk = Files.walk(resolved.path())) {
                    List<Path> paths = walk.sorted(Comparator.reverseOrder()).toList();
                    if (paths.size() > 50_000) {
                        throw new ApiException(413, "payload_too_large", "Directory contains too many entries.");
                    }
                    for (Path path : paths) {
                        if (Files.isSymbolicLink(path)) {
                            throw new ApiException(400, "invalid_path", "Symbolic links are not allowed.");
                        }
                        String relative = relative(path);
                        if (isProtected(relative)) {
                            throw new ApiException(403, "forbidden", "The requested path is protected.");
                        }
                    }
                    for (Path path : paths) {
                        Files.delete(path);
                    }
                }
            } else {
                Files.delete(resolved.path());
            }
        } catch (ApiException e) {
            throw e;
        } catch (IOException e) {
            throw ApiException.internal();
        }
        Map<String, Object> response = Json.message();
        response.put("path", resolved.relative());
        response.put("deleted", true);
        return response;
    }

    public String normalize(String requestedPath) {
        return resolve(requestedPath, false, true).relative();
    }

    private Resolved resolve(String requested, boolean mustExist, boolean allowRoot) {
        String normalizedInput = validateInput(requested, allowRoot);
        Path candidate = root.resolve(normalizedInput).normalize();
        if (!candidate.startsWith(root)) {
            throw invalidPath();
        }
        String relative = relative(candidate);
        if (isProtected(relative)) {
            throw new ApiException(403, "forbidden", "The requested path is protected.");
        }
        Path current = root;
        String[] segments = relative.isEmpty() ? new String[0] : relative.split("/");
        for (String segment : segments) {
            current = current.resolve(segment);
            if (Files.isSymbolicLink(current)) {
                throw invalidPath();
            }
            if (!Files.exists(current, LinkOption.NOFOLLOW_LINKS)) {
                break;
            }
        }
        if (mustExist && !Files.exists(candidate, LinkOption.NOFOLLOW_LINKS)) {
            throw ApiException.notFound("Path");
        }
        if (Files.exists(candidate, LinkOption.NOFOLLOW_LINKS)) {
            try {
                Path real = candidate.toRealPath();
                if (!real.startsWith(realRoot)) {
                    throw invalidPath();
                }
            } catch (IOException e) {
                throw invalidPath();
            }
        }
        Path parent = candidate.getParent();
        if (parent != null && Files.exists(parent, LinkOption.NOFOLLOW_LINKS) && !Files.isDirectory(parent, LinkOption.NOFOLLOW_LINKS)) {
            throw invalidPath();
        }
        return new Resolved(candidate, relative);
    }

    private static String validateInput(String requested, boolean allowRoot) {
        if (requested == null) {
            throw invalidPath();
        }
        if (requested.isEmpty() && allowRoot) {
            return "";
        }
        if (requested.isEmpty() || requested.length() > 1024 || requested.indexOf('\0') >= 0 || requested.contains("\\")
                || requested.startsWith("/") || requested.startsWith("//") || requested.matches("^[A-Za-z]:.*")) {
            throw invalidPath();
        }
        String[] parts = requested.split("/", -1);
        for (String part : parts) {
            if (part.isEmpty() || part.equals(".") || part.equals("..")) {
                throw invalidPath();
            }
        }
        Path parsed;
        try {
            parsed = Path.of(requested);
        } catch (InvalidPathException e) {
            throw invalidPath();
        }
        if (parsed.isAbsolute() || parsed.getNameCount() == 0) {
            throw invalidPath();
        }
        String normalized = parsed.normalize().toString().replace(parsed.getFileSystem().getSeparator(), "/");
        if (normalized.equals("..") || normalized.startsWith("../") || normalized.contains("/../")) {
            throw invalidPath();
        }
        return normalized;
    }

    private boolean isProtected(String relative) {
        if (relative.isEmpty()) {
            return false;
        }
        String lower = relative.toLowerCase(Locale.ROOT);
        String[] parts = lower.split("/");
        String leaf = parts[parts.length - 1];
        if (DENIED_FILES.contains(leaf) || DENIED_DIRECTORIES.contains(parts[0])) {
            return true;
        }
        if (!protectedStatePath.isEmpty() && (lower.equals(protectedStatePath) || lower.startsWith(protectedStatePath + "/"))) {
            return true;
        }
        for (String extension : WORLD_BINARY_EXTENSIONS) {
            if (leaf.endsWith(extension)) {
                return true;
            }
        }
        return false;
    }

    private String protectedStatePath() {
        if (!stateDirectory.startsWith(root) || stateDirectory.equals(root)) {
            return "";
        }
        return root.relativize(stateDirectory).toString().replace(root.getFileSystem().getSeparator(), "/").toLowerCase(Locale.ROOT);
    }

    private void verifyRevision(Path path, String expectedRevision, boolean exists) {
        if (!exists) {
            if (expectedRevision != null && !expectedRevision.isEmpty()) {
                throw ApiException.conflict("revision_conflict", "The file no longer exists.");
            }
            return;
        }
        if (Files.isDirectory(path, LinkOption.NOFOLLOW_LINKS)) {
            return;
        }
        if (expectedRevision == null || expectedRevision.isEmpty()) {
            throw ApiException.conflict("revision_conflict", "expectedRevision is required when modifying an existing file.");
        }
        String actual = revision(path);
        if (!SecurityUtils.fixedTimeEquals(expectedRevision.toLowerCase(Locale.ROOT), actual)) {
            throw ApiException.conflict("revision_conflict", "The file has changed since it was read.");
        }
    }

    private String revision(Path path) {
        try {
            if (Files.size(path) > Protocol.MAX_TEXT_BYTES) {
                return null;
            }
            return SecurityUtils.sha256Hex(Files.readAllBytes(path));
        } catch (IOException e) {
            throw ApiException.internal();
        }
    }

    private static byte[] readLimited(Path path) throws IOException {
        long size = Files.size(path);
        if (size > Protocol.MAX_TEXT_BYTES) {
            throw new ApiException(413, "payload_too_large", "Text files are limited to 2 MiB.");
        }
        byte[] bytes = Files.readAllBytes(path);
        if (bytes.length > Protocol.MAX_TEXT_BYTES) {
            throw new ApiException(413, "payload_too_large", "Text files are limited to 2 MiB.");
        }
        return bytes;
    }

    private static String decodeUtf8(byte[] bytes) {
        try {
            String content = StandardCharsets.UTF_8.newDecoder()
                    .onMalformedInput(CodingErrorAction.REPORT).onUnmappableCharacter(CodingErrorAction.REPORT)
                    .decode(ByteBuffer.wrap(bytes)).toString();
            if (content.indexOf('\0') >= 0) {
                throw new ApiException(400, "invalid_request", "Binary files are not available through this API.");
            }
            return content;
        } catch (CharacterCodingException e) {
            throw new ApiException(400, "invalid_request", "Only valid UTF-8 text files are supported.");
        }
    }

    private Map<String, Object> mutation(String relative, boolean directory) {
        Resolved current = resolve(relative, true, false);
        Map<String, Object> response = Json.message();
        response.put("path", current.relative());
        response.put("isDirectory", directory);
        response.put("revision", directory ? directoryRevision(current.relative()) : revision(current.path()));
        response.put("modifiedAt", lastModified(current.path()));
        return response;
    }

    private String directoryRevision(String relative) {
        Map<String, Object> listing = list(relative);
        return "d_" + SecurityUtils.sha256Hex(Json.write(listing.get("entries")));
    }

    private static Instant lastModified(Path path) {
        try {
            return Files.getLastModifiedTime(path, LinkOption.NOFOLLOW_LINKS).toInstant();
        } catch (IOException e) {
            throw ApiException.internal();
        }
    }

    private String relative(Path path) {
        return root.relativize(path.toAbsolutePath().normalize()).toString().replace(root.getFileSystem().getSeparator(), "/");
    }

    private static String parentRelative(String relative) {
        int slash = relative.lastIndexOf('/');
        return slash < 0 ? "" : relative.substring(0, slash);
    }

    private static ApiException invalidPath() {
        return new ApiException(400, "invalid_path", "Path must be a normalized relative path inside the server root without symbolic links.");
    }

    private record Resolved(Path path, String relative) {
    }
}
