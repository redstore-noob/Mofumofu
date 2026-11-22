package mofumofu.nekoprobe.api;

import java.util.List;
import java.util.Set;

public final class Protocol {
    public static final String API_VERSION = "1.0";
    public static final int MAX_TEXT_BYTES = 2 * 1024 * 1024;
    public static final int MAX_BODY_BYTES = MAX_TEXT_BYTES + 128 * 1024;
    public static final long CONFIRMATION_MILLIS = 60_000;
    public static final long ROTATION_GRACE_MILLIS = 5 * 60_000;
    public static final Set<String> SCOPES = Set.of("read", "players", "files", "backup", "control", "command");
    public static final List<String> CAPABILITIES = List.of(
            "read", "players", "files", "backup", "command", "console", "events", "properties", "tokens");

    private Protocol() {
    }
}
