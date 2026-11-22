package mofumofu.nekoprobe.backup;

import mofumofu.nekoprobe.api.Json;
import mofumofu.nekoprobe.security.AtomicFiles;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.LinkOption;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.stream.Stream;

public final class RestoreService {
    private final Path root;
    private final Path work;
    private final Path staging;
    private final Path rollback;
    private final Path journalPath;
    private final BackupService backups;
    private final MoveHook moveHook;

    public RestoreService(Path root, Path stateDirectory, BackupService backups) {
        this(root, stateDirectory, backups, (source, target) -> {
        });
    }

    RestoreService(Path root, Path stateDirectory, BackupService backups, MoveHook moveHook) {
        this.root = root.toAbsolutePath().normalize();
        this.work = this.root.resolve(".nekoprobe-restore");
        this.staging = work.resolve("staging");
        this.rollback = work.resolve("rollback");
        this.journalPath = stateDirectory.resolve("restore-journal.json");
        this.backups = backups;
        this.moveHook = moveHook;
    }

    public boolean hasRestoreActivity() {
        return backups.pendingRestore() != null || Files.exists(journalPath, LinkOption.NOFOLLOW_LINKS)
                || Files.exists(work, LinkOption.NOFOLLOW_LINKS);
    }

    public synchronized boolean restorePending() throws RestoreException {
        if (Files.exists(journalPath, LinkOption.NOFOLLOW_LINKS)) {
            recoverInterrupted();
            throw new RestoreException("An interrupted restore was rolled back; the server must not start from this attempt.");
        }
        BackupService.PendingRestore pending = backups.pendingRestore();
        if (pending == null) {
            try {
                deleteTree(work);
            } catch (IOException e) {
                throw new RestoreException("Restore temporary state could not be cleaned.", e);
            }
            return false;
        }
        Journal journal = null;
        try {
            deleteTree(work);
            Files.createDirectories(staging);
            BackupService.BackupPaths paths = backups.verifiedPaths(pending.backupId());
            ZipSecurity.extract(paths.archive(), staging, backups.protectedPaths());
            backups.createCold("pre-restore-safety", null);
            List<Entry> plan = new ArrayList<>();
            buildPlan(root, staging, "", plan);
            journal = new Journal(plan);
            persistJournal(journal);
            for (Entry entry : plan) {
                Path target = root.resolve(entry.path()).normalize();
                Path saved = rollback.resolve(entry.path()).normalize();
                Path source = staging.resolve(entry.path()).normalize();
                if (entry.hadOriginal()) {
                    Files.createDirectories(saved.getParent());
                    move(target, saved);
                }
                entry.state = State.ORIGINAL_MOVED;
                persistJournal(journal);
                if (entry.staged()) {
                    Files.createDirectories(target.getParent());
                    move(source, target);
                }
                entry.state = State.COMPLETED;
                persistJournal(journal);
            }
            Files.deleteIfExists(journalPath);
            backups.clearPendingRestore();
            deleteTree(work);
            return true;
        } catch (Exception failure) {
            if (journal != null) {
                rollback(journal);
            }
            try {
                Files.deleteIfExists(journalPath);
                backups.clearPendingRestore();
                deleteTree(work);
            } catch (IOException cleanupFailure) {
                throw new RestoreException("Restore failed and rollback state could not be finalized; startup is aborted.", failure);
            }
            if (failure instanceof RestoreException restoreException) {
                throw restoreException;
            }
            throw new RestoreException("Restore failed; the previous server files were restored and startup is aborted.", failure);
        }
    }

    private void recoverInterrupted() throws RestoreException {
        try {
            Journal journal = Json.MAPPER.readValue(Files.readAllBytes(journalPath), Journal.class);
            validateJournal(journal);
            rollback(journal);
            Files.deleteIfExists(journalPath);
            backups.clearPendingRestore();
            deleteTree(work);
        } catch (RestoreException e) {
            throw e;
        } catch (Exception e) {
            throw new RestoreException("Interrupted restore rollback failed; startup is aborted.", e);
        }
    }

    private void buildPlan(Path currentDirectory, Path stagedDirectory, String relative, List<Entry> plan) throws IOException {
        Set<String> names = new LinkedHashSet<>();
        if (Files.exists(currentDirectory, LinkOption.NOFOLLOW_LINKS)) {
            try (Stream<Path> files = Files.list(currentDirectory)) {
                files.forEach(path -> names.add(path.getFileName().toString()));
            }
        }
        if (Files.exists(stagedDirectory, LinkOption.NOFOLLOW_LINKS)) {
            try (Stream<Path> files = Files.list(stagedDirectory)) {
                files.forEach(path -> names.add(path.getFileName().toString()));
            }
        }
        List<String> sorted = new ArrayList<>(names);
        sorted.sort(String::compareTo);
        for (String name : sorted) {
            String childRelative = relative.isEmpty() ? name : relative + "/" + name;
            if (isProtectedExact(childRelative)) {
                continue;
            }
            Path current = currentDirectory.resolve(name);
            Path staged = stagedDirectory.resolve(name);
            if (Files.isSymbolicLink(current) || Files.isSymbolicLink(staged)) {
                throw new IOException("Symbolic link in restore set");
            }
            boolean currentExists = Files.exists(current, LinkOption.NOFOLLOW_LINKS);
            boolean stagedExists = Files.exists(staged, LinkOption.NOFOLLOW_LINKS);
            boolean currentDirectoryEntry = currentExists && Files.isDirectory(current, LinkOption.NOFOLLOW_LINKS);
            boolean stagedDirectoryEntry = stagedExists && Files.isDirectory(staged, LinkOption.NOFOLLOW_LINKS);
            if (hasProtectedDescendant(childRelative)
                    && ((currentDirectoryEntry && stagedDirectoryEntry) || (currentDirectoryEntry && !stagedExists))) {
                buildPlan(current, staged, childRelative, plan);
                continue;
            }
            if (currentExists || stagedExists) {
                plan.add(new Entry(childRelative, currentExists, stagedExists, State.PLANNED));
            }
        }
    }

    private void rollback(Journal journal) throws RestoreException {
        StringBuilder failures = new StringBuilder();
        List<Entry> reversed = new ArrayList<>(journal.entries());
        reversed.sort(Comparator.comparingInt((Entry entry) -> depth(entry.path())).reversed());
        for (Entry entry : reversed) {
            try {
                Path target = root.resolve(entry.path()).normalize();
                Path saved = rollback.resolve(entry.path()).normalize();
                if (Files.exists(saved, LinkOption.NOFOLLOW_LINKS)) {
                    deleteTree(target);
                    Files.createDirectories(target.getParent());
                    move(saved, target);
                } else if (!entry.hadOriginal() && entry.state() != State.PLANNED) {
                    deleteTree(target);
                }
            } catch (Exception e) {
                failures.append(entry.path()).append(';');
            }
        }
        if (!failures.isEmpty()) {
            throw new RestoreException("Restore rollback was incomplete; startup is aborted.");
        }
    }

    private void move(Path source, Path target) throws IOException {
        moveHook.beforeMove(source, target);
        Files.move(source, target, StandardCopyOption.ATOMIC_MOVE);
    }

    private void persistJournal(Journal journal) throws IOException {
        Files.createDirectories(journalPath.getParent());
        AtomicFiles.write(journalPath, Json.bytes(journal), true);
    }

    private void validateJournal(Journal journal) throws IOException {
        if (journal == null || journal.entries() == null || journal.entries().size() > ZipSecurity.MAX_ENTRIES) {
            throw new IOException("Invalid restore journal");
        }
        Set<String> paths = new LinkedHashSet<>();
        for (Entry entry : journal.entries()) {
            String path = ZipSecurity.validateName(entry.path(), backups.protectedPaths());
            if (!paths.add(path.toLowerCase(Locale.ROOT)) || entry.state() == null) {
                throw new IOException("Invalid restore journal");
            }
        }
    }

    private boolean isProtectedExact(String relative) {
        String lower = relative.toLowerCase(Locale.ROOT);
        return backups.protectedPaths().stream().map(value -> value.toLowerCase(Locale.ROOT))
                .anyMatch(value -> !value.isEmpty() && lower.equals(value));
    }

    private boolean hasProtectedDescendant(String relative) {
        String lower = relative.toLowerCase(Locale.ROOT) + "/";
        return backups.protectedPaths().stream().map(value -> value.toLowerCase(Locale.ROOT))
                .anyMatch(value -> !value.isEmpty() && value.startsWith(lower));
    }

    private static int depth(String path) {
        return (int) path.chars().filter(c -> c == '/').count();
    }

    private static void deleteTree(Path path) throws IOException {
        if (!Files.exists(path, LinkOption.NOFOLLOW_LINKS)) {
            return;
        }
        try (Stream<Path> walk = Files.walk(path)) {
            for (Path item : walk.sorted(Comparator.reverseOrder()).toList()) {
                Files.delete(item);
            }
        }
    }

    enum State { PLANNED, ORIGINAL_MOVED, COMPLETED }

    static final class Journal {
        public List<Entry> entries;

        Journal() {
        }

        Journal(List<Entry> entries) {
            this.entries = entries;
        }

        public List<Entry> entries() {
            return entries;
        }
    }

    static final class Entry {
        public String path;
        public boolean hadOriginal;
        public boolean staged;
        public State state;

        Entry() {
        }

        Entry(String path, boolean hadOriginal, boolean staged, State state) {
            this.path = path;
            this.hadOriginal = hadOriginal;
            this.staged = staged;
            this.state = state;
        }

        public String path() {
            return path;
        }

        public boolean hadOriginal() {
            return hadOriginal;
        }

        public boolean staged() {
            return staged;
        }

        public State state() {
            return state;
        }
    }

    @FunctionalInterface
    interface MoveHook {
        void beforeMove(Path source, Path target) throws IOException;
    }

    public static final class RestoreException extends Exception {
        RestoreException(String message) {
            super(message);
        }

        RestoreException(String message, Throwable cause) {
            super(message, cause);
        }
    }
}
