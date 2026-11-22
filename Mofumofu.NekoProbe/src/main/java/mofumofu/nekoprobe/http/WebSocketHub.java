package mofumofu.nekoprobe.http;

import io.undertow.websockets.core.AbstractReceiveListener;
import io.undertow.websockets.core.BufferedTextMessage;
import io.undertow.websockets.core.WebSocketChannel;
import io.undertow.websockets.core.WebSockets;
import io.undertow.websockets.spi.WebSocketHttpExchange;
import mofumofu.nekoprobe.api.Json;
import mofumofu.nekoprobe.bukkit.ConsoleRing;
import mofumofu.nekoprobe.security.AuthContext;
import mofumofu.nekoprobe.security.TokenService;

import java.io.IOException;
import java.net.URLDecoder;
import java.nio.charset.StandardCharsets;
import java.time.Clock;
import java.time.Instant;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.Executor;

public final class WebSocketHub implements io.undertow.websockets.WebSocketConnectionCallback {
    private final TokenService tokens;
    private final ProbeRouter router;
    private final ConsoleRing journal;
    private final Clock clock;
    private final Executor executor;
    private final Set<Connection> connections = ConcurrentHashMap.newKeySet();

    public WebSocketHub(TokenService tokens, ProbeRouter router, ConsoleRing journal, Clock clock, Executor executor) {
        this.tokens = tokens;
        this.router = router;
        this.journal = journal;
        this.clock = clock;
        this.executor = executor;
        journal.listen(this::publish);
    }

    @Override
    public void onConnect(WebSocketHttpExchange exchange, WebSocketChannel channel) {
        String authorization = exchange.getRequestHeader("Authorization");
        String cursor = query(exchange.getQueryString(), "cursor");
        Connection connection = new Connection(channel);
        connections.add(connection);
        channel.getCloseSetter().set(closed -> connections.remove(connection));
        channel.getReceiveSetter().set(new AbstractReceiveListener() {
            @Override
            protected void onFullTextMessage(WebSocketChannel channel, BufferedTextMessage message) {
                // The stream is server-to-client; commands always use the audited HTTP endpoint.
            }

            @Override
            protected void onError(WebSocketChannel channel, Throwable error) {
                connections.remove(connection);
                try {
                    channel.close();
                } catch (IOException ignored) {
                    // Already closed.
                }
            }
        });
        channel.resumeReceives();
        executor.execute(() -> initialize(connection, authorization, cursor));
    }

    public void close() {
        for (Connection connection : connections) {
            try {
                connection.channel.close();
            } catch (IOException ignored) {
                // Shutdown is best effort.
            }
        }
        connections.clear();
    }

    private void initialize(Connection connection, String authorization, String cursor) {
        try {
            AuthContext auth = tokens.authenticate(authorization);
            auth.requireScope("read");
            Map<String, Object> snapshot = router.snapshot();
            synchronized (connection) {
                if (!connection.channel.isOpen()) {
                    return;
                }
                if (cursor == null || cursor.isBlank()) {
                    long current = journal.latestSequence();
                    sendSnapshot(connection, snapshot, current, journal.recentConsole(100));
                    connection.lastSent = current;
                } else {
                    catchUp(connection, cursor, snapshot);
                }
                if (connection.forceReset) {
                    long current = journal.latestSequence();
                    sendReset(connection, current, "cursor_expired");
                    sendSnapshot(connection, snapshot, current, journal.recentConsole(100));
                    connection.lastSent = current;
                    connection.queue.clear();
                }
                List<ConsoleRing.JournalItem> queued = new ArrayList<>(connection.queue);
                queued.sort(Comparator.comparingLong(ConsoleRing.JournalItem::sequence));
                for (ConsoleRing.JournalItem item : queued) {
                    sendItem(connection, item);
                }
                connection.queue.clear();
                connection.ready = true;
            }
        } catch (Exception e) {
            connections.remove(connection);
            try {
                connection.channel.close();
            } catch (IOException ignored) {
                // Already closed.
            }
        }
    }

    private void catchUp(Connection connection, String cursor, Map<String, Object> snapshot) {
        String currentCursor = cursor;
        while (true) {
            ConsoleRing.ReadResult result = journal.readAfter(currentCursor, 256);
            if (result.reset()) {
                sendReset(connection, result.latestSequence(), "cursor_expired");
                sendSnapshot(connection, snapshot, result.latestSequence(), journal.recentConsole(100));
                connection.lastSent = result.latestSequence();
                return;
            }
            for (ConsoleRing.JournalItem item : result.items()) {
                sendItem(connection, item);
            }
            currentCursor = result.cursor();
            if (result.items().isEmpty() || Long.parseLong(currentCursor) >= result.latestSequence()) {
                break;
            }
        }
        sendSnapshot(connection, snapshot, connection.lastSent, List.of());
    }

    private void publish(ConsoleRing.JournalItem item) {
        for (Connection connection : connections) {
            synchronized (connection) {
                if (!connection.ready) {
                    if (connection.queue.size() >= 2048) {
                        connection.forceReset = true;
                    } else {
                        connection.queue.add(item);
                    }
                } else {
                    sendItem(connection, item);
                }
            }
        }
    }

    private void sendItem(Connection connection, ConsoleRing.JournalItem item) {
        if (item.sequence() <= connection.lastSent) {
            return;
        }
        Map<String, Object> frame = frame(item.type(), item.sequence());
        switch (item.type()) {
            case "console" -> frame.put("entries", List.of(item.console()));
            case "event" -> frame.put("event", item.event());
            case "operation" -> frame.put("operation", item.operation());
            default -> {
                return;
            }
        }
        send(connection.channel, frame);
        connection.lastSent = item.sequence();
    }

    private void sendSnapshot(Connection connection, Map<String, Object> snapshot, long cursor, List<ConsoleRing.ConsoleEntry> console) {
        Map<String, Object> frame = frame("snapshot", cursor);
        frame.put("status", snapshot.get("status"));
        frame.put("players", snapshot.get("players"));
        frame.put("recentConsole", console);
        frame.put("activeOperations", snapshot.get("activeOperations"));
        send(connection.channel, frame);
    }

    private void sendReset(Connection connection, long cursor, String reason) {
        Map<String, Object> frame = frame("reset", cursor);
        frame.put("reason", reason);
        frame.put("requiresSnapshot", true);
        send(connection.channel, frame);
    }

    private Map<String, Object> frame(String type, long cursor) {
        Map<String, Object> frame = Json.message();
        frame.put("type", type);
        frame.put("cursor", Long.toString(cursor));
        frame.put("sentAt", Instant.now(clock));
        return frame;
    }

    private static void send(WebSocketChannel channel, Map<String, Object> frame) {
        if (channel.isOpen()) {
            WebSockets.sendText(Json.write(frame), channel, null);
        }
    }

    private static String query(String query, String name) {
        if (query == null) {
            return null;
        }
        for (String pair : query.split("&")) {
            int equals = pair.indexOf('=');
            String key = equals < 0 ? pair : pair.substring(0, equals);
            if (URLDecoder.decode(key, StandardCharsets.UTF_8).equals(name)) {
                return equals < 0 ? "" : URLDecoder.decode(pair.substring(equals + 1), StandardCharsets.UTF_8);
            }
        }
        return null;
    }

    private static final class Connection {
        private final WebSocketChannel channel;
        private final ArrayDeque<ConsoleRing.JournalItem> queue = new ArrayDeque<>();
        private boolean ready;
        private boolean forceReset;
        private long lastSent;

        private Connection(WebSocketChannel channel) {
            this.channel = channel;
        }
    }
}
