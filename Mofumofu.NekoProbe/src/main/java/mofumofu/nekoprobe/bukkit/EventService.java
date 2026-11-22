package mofumofu.nekoprobe.bukkit;

import mofumofu.nekoprobe.security.AgentEventClient;
import mofumofu.nekoprobe.security.SecurityUtils;

import java.time.Clock;
import java.time.Instant;
import java.util.LinkedHashMap;
import java.util.Map;

public final class EventService {
    private final Clock clock;
    private final ConsoleRing journal;
    private final AgentEventClient agent;

    public EventService(Clock clock, ConsoleRing journal, AgentEventClient agent) {
        this.clock = clock;
        this.journal = journal;
        this.agent = agent;
    }

    public void publish(String type, String message, Map<String, String> details) {
        Map<String, Object> event = new LinkedHashMap<>();
        event.put("eventId", SecurityUtils.randomHex(16));
        event.put("type", type);
        event.put("occurredAt", Instant.now(clock));
        event.put("message", message == null ? "" : message);
        event.put("details", details == null ? Map.of() : Map.copyOf(details));
        journal.appendEvent(event);
        if (agent != null) {
            agent.send(event);
        }
    }
}
