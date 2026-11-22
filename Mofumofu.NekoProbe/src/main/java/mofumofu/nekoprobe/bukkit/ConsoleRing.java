package mofumofu.nekoprobe.bukkit;

import mofumofu.nekoprobe.api.ApiException;

import java.time.Clock;
import java.time.Instant;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Deque;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.function.Consumer;

public final class ConsoleRing {
    private final int capacity;
    private final Clock clock;
    private final Deque<JournalItem> items = new ArrayDeque<>();
    private final List<Consumer<JournalItem>> listeners = new CopyOnWriteArrayList<>();
    private long sequence;

    public ConsoleRing(int capacity, Clock clock) {
        if (capacity < 1) {
            throw new IllegalArgumentException("capacity");
        }
        this.capacity = capacity;
        this.clock = clock;
    }

    public ConsoleEntry appendConsole(String level, String source, String message) {
        String bounded = message == null ? "" : message.length() <= 16_384 ? message : message.substring(0, 16_384);
        ConsoleEntry entry = new ConsoleEntry(0, Instant.now(clock), level == null ? "INFO" : level,
                source == null ? "" : source, bounded);
        JournalItem item;
        synchronized (this) {
            entry = new ConsoleEntry(++sequence, entry.timestamp(), entry.level(), entry.source(), entry.message());
            item = new JournalItem(sequence, "console", entry, null, null);
            add(item);
        }
        notifyListeners(item);
        return entry;
    }

    public long appendEvent(Map<String, Object> event) {
        return append("event", null, Map.copyOf(event), null);
    }

    public long appendOperation(Map<String, Object> operation) {
        return append("operation", null, null, new java.util.LinkedHashMap<>(operation));
    }

    private long append(String type, ConsoleEntry console, Map<String, Object> event, Map<String, Object> operation) {
        JournalItem item;
        synchronized (this) {
            long id = ++sequence;
            item = new JournalItem(id, type, console, event, operation);
            add(item);
        }
        notifyListeners(item);
        return item.sequence();
    }

    private void add(JournalItem item) {
        items.addLast(item);
        while (items.size() > capacity) {
            items.removeFirst();
        }
    }

    private void notifyListeners(JournalItem item) {
        for (Consumer<JournalItem> listener : listeners) {
            listener.accept(item);
        }
    }

    public void listen(Consumer<JournalItem> listener) {
        listeners.add(listener);
    }

    public synchronized ReadResult readAfter(String cursor, int maximum) {
        long position = parseCursor(cursor);
        int limit = Math.max(1, Math.min(maximum, 1000));
        long oldest = items.isEmpty() ? 0 : items.getFirst().sequence();
        long latest = sequence;
        boolean reset = position > latest || (!items.isEmpty() && position < oldest - 1) || (items.isEmpty() && position != 0);
        if (reset) {
            List<JournalItem> recent = items.stream().skip(Math.max(0, items.size() - limit)).toList();
            return new ReadResult(recent, Long.toString(latest), true, latest);
        }
        List<JournalItem> result = new ArrayList<>();
        long next = position;
        for (JournalItem item : items) {
            if (item.sequence() > position) {
                result.add(item);
                next = item.sequence();
                if (result.size() >= limit) {
                    break;
                }
            }
        }
        return new ReadResult(List.copyOf(result), Long.toString(next), false, latest);
    }

    public synchronized List<ConsoleEntry> recentConsole(int maximum) {
        int limit = Math.max(0, Math.min(maximum, capacity));
        List<ConsoleEntry> result = new ArrayList<>();
        for (JournalItem item : items) {
            if (item.console() != null) {
                result.add(item.console());
            }
        }
        return result.stream().skip(Math.max(0, result.size() - limit)).toList();
    }

    public synchronized long latestSequence() {
        return sequence;
    }

    public synchronized int size() {
        return items.size();
    }

    private static long parseCursor(String cursor) {
        if (cursor == null || cursor.isBlank()) {
            return 0;
        }
        try {
            long value = Long.parseLong(cursor);
            if (value < 0) {
                throw new NumberFormatException();
            }
            return value;
        } catch (NumberFormatException e) {
            throw ApiException.badRequest("Console cursor is invalid.");
        }
    }

    public record ConsoleEntry(long sequence, Instant timestamp, String level, String source, String message) {
    }

    public record JournalItem(long sequence, String type, ConsoleEntry console, Map<String, Object> event,
                              Map<String, Object> operation) {
    }

    public record ReadResult(List<JournalItem> items, String cursor, boolean reset, long latestSequence) {
        public List<ConsoleEntry> consoleEntries() {
            return items.stream().map(JournalItem::console).filter(java.util.Objects::nonNull).toList();
        }
    }
}
