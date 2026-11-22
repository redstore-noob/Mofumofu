package mofumofu.nekoprobe.backup;

import mofumofu.nekoprobe.api.ApiException;

import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.file.Files;
import java.nio.file.LinkOption;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.util.HashSet;
import java.util.Locale;
import java.util.Set;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;

public final class ZipSecurity {
    public static final int MAX_ENTRIES = 50_000;
    public static final long MAX_ENTRY_BYTES = 2L * 1024 * 1024 * 1024;
    public static final long MAX_TOTAL_BYTES = 8L * 1024 * 1024 * 1024;
    private static final long RATIO_CHECK_THRESHOLD = 64L * 1024 * 1024;
    private static final long MAX_COMPRESSION_RATIO = 500;

    private ZipSecurity() {
    }

    public static void inspect(Path archive, Set<String> protectedPaths) {
        try (ZipFile zip = new ZipFile(archive.toFile())) {
            long total = 0;
            int count = 0;
            Set<String> names = new HashSet<>();
            var entries = zip.entries();
            while (entries.hasMoreElements()) {
                ZipEntry entry = entries.nextElement();
                String name = validateName(entry.getName(), protectedPaths);
                if (!names.add(name.toLowerCase(Locale.ROOT))) {
                    throw invalidArchive();
                }
                if (entry.isDirectory()) {
                    continue;
                }
                if (++count > MAX_ENTRIES || entry.getSize() > MAX_ENTRY_BYTES) {
                    throw invalidArchive();
                }
                if (entry.getSize() >= 0) {
                    total = addLimited(total, entry.getSize());
                    checkRatio(total, entry.getCompressedSize(), entry.getSize());
                }
            }
        } catch (ApiException e) {
            throw e;
        } catch (IOException | RuntimeException e) {
            throw invalidArchive();
        }
    }

    public static void extract(Path archive, Path staging, Set<String> protectedPaths) {
        try {
            Files.createDirectories(staging);
            Path realStaging = staging.toRealPath();
            try (ZipFile zip = new ZipFile(archive.toFile())) {
                long total = 0;
                int count = 0;
                Set<String> names = new HashSet<>();
                var entries = zip.entries();
                while (entries.hasMoreElements()) {
                    ZipEntry entry = entries.nextElement();
                    String name = validateName(entry.getName(), protectedPaths);
                    if (!names.add(name.toLowerCase(Locale.ROOT))) {
                        throw invalidArchive();
                    }
                    Path target = staging.resolve(name).normalize();
                    if (!target.startsWith(staging)) {
                        throw invalidArchive();
                    }
                    if (entry.isDirectory()) {
                        Files.createDirectories(target);
                        continue;
                    }
                    if (++count > MAX_ENTRIES || entry.getSize() > MAX_ENTRY_BYTES) {
                        throw invalidArchive();
                    }
                    Files.createDirectories(target.getParent());
                    rejectSymbolicSegments(staging, target.getParent());
                    long written = 0;
                    try (InputStream input = zip.getInputStream(entry);
                         OutputStream output = Files.newOutputStream(target, StandardOpenOption.CREATE_NEW, StandardOpenOption.WRITE)) {
                        byte[] buffer = new byte[64 * 1024];
                        int read;
                        while ((read = input.read(buffer)) != -1) {
                            written = addLimited(written, read);
                            if (written > MAX_ENTRY_BYTES) {
                                throw invalidArchive();
                            }
                            total = addLimited(total, read);
                            output.write(buffer, 0, read);
                        }
                    }
                    if (entry.getSize() >= 0 && entry.getSize() != written) {
                        throw invalidArchive();
                    }
                    checkRatio(entry.getCompressedSize(), written);
                    if (Files.isSymbolicLink(target) || !target.toRealPath().startsWith(realStaging)) {
                        throw invalidArchive();
                    }
                }
            }
        } catch (ApiException e) {
            throw e;
        } catch (IOException | RuntimeException e) {
            throw invalidArchive();
        }
    }

    public static String validateName(String name, Set<String> protectedPaths) {
        if (name == null || name.isEmpty() || name.length() > 2048 || name.indexOf('\0') >= 0 || name.contains("\\")
                || name.startsWith("/") || name.matches("^[A-Za-z]:.*")) {
            throw invalidArchive();
        }
        String[] segments = name.split("/", -1);
        for (int i = 0; i < segments.length; i++) {
            String segment = segments[i];
            if (segment.isEmpty() && i == segments.length - 1 && name.endsWith("/")) {
                continue;
            }
            if (segment.isEmpty() || segment.equals(".") || segment.equals("..")) {
                throw invalidArchive();
            }
        }
        String normalized = name.endsWith("/") ? name.substring(0, name.length() - 1) : name;
        String lower = normalized.toLowerCase(Locale.ROOT);
        for (String protectedPath : protectedPaths) {
            String protectedLower = protectedPath.toLowerCase(Locale.ROOT);
            if (!protectedLower.isEmpty() && (lower.equals(protectedLower) || lower.startsWith(protectedLower + "/"))) {
                throw invalidArchive();
            }
        }
        return normalized;
    }

    private static void rejectSymbolicSegments(Path staging, Path parent) throws IOException {
        Path current = staging;
        Path relative = staging.relativize(parent);
        for (Path segment : relative) {
            current = current.resolve(segment);
            if (Files.isSymbolicLink(current) || !Files.isDirectory(current, LinkOption.NOFOLLOW_LINKS)) {
                throw invalidArchive();
            }
        }
    }

    private static long addLimited(long current, long added) {
        if (added < 0 || current > MAX_TOTAL_BYTES - added) {
            throw invalidArchive();
        }
        return current + added;
    }

    private static void checkRatio(long total, long compressed, long uncompressed) {
        if (uncompressed > RATIO_CHECK_THRESHOLD && compressed >= 0
                && uncompressed / Math.max(1, compressed) > MAX_COMPRESSION_RATIO) {
            throw invalidArchive();
        }
    }

    private static void checkRatio(long compressed, long written) {
        checkRatio(written, compressed, written);
    }

    private static ApiException invalidArchive() {
        return new ApiException(400, "invalid_backup", "The backup archive failed ZIP entry, path, link, or size validation.");
    }
}
