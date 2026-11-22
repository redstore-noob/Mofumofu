package mofumofu.nekoprobe.security;

import com.fasterxml.jackson.databind.JsonNode;
import com.sun.net.httpserver.HttpServer;
import mofumofu.nekoprobe.api.Json;
import org.junit.jupiter.api.Test;

import java.io.InputStream;
import java.net.InetSocketAddress;
import java.net.URI;
import java.nio.charset.StandardCharsets;
import java.time.Clock;
import java.time.Instant;
import java.time.ZoneOffset;
import java.util.HexFormat;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

public final class AgentEventClientTest {
    @Test
    public void sendsFlatSignedEventMatchingSharedCrossEndVector() throws Exception {
        JsonNode fixture = fixture();
        String linkSecret = fixture.get("linkSecret").asText();
        String instanceId = fixture.get("instanceId").asText();
        String path = fixture.get("path").asText();
        String timestamp = fixture.get("timestamp").asText();
        String expectedBody = fixture.get("body").asText();
        String expectedCanonical = fixture.get("canonical").asText();
        String expectedSignature = fixture.get("signature").asText();

        byte[] key = HexFormat.of().parseHex(linkSecret);
        assertEquals(expectedSignature, AgentEventClient.sign(expectedCanonical, key));

        AtomicReference<Map<String, String>> capturedHeaders = new AtomicReference<>();
        AtomicReference<byte[]> capturedBody = new AtomicReference<>();
        CountDownLatch received = new CountDownLatch(1);
        HttpServer server = HttpServer.create(new InetSocketAddress("127.0.0.1", 0), 0);
        server.createContext(path, exchange -> {
            Map<String, String> headers = new LinkedHashMap<>();
            for (String name : new String[]{
                    AgentEventClient.INSTANCE_HEADER, AgentEventClient.TIMESTAMP_HEADER,
                    AgentEventClient.NONCE_HEADER, AgentEventClient.SIGNATURE_HEADER}) {
                headers.put(name, exchange.getRequestHeaders().getFirst(name));
            }
            capturedHeaders.set(headers);
            capturedBody.set(exchange.getRequestBody().readAllBytes());
            exchange.sendResponseHeaders(202, -1);
            exchange.close();
            received.countDown();
        });
        ExecutorService executor = Executors.newSingleThreadExecutor();
        server.setExecutor(executor);
        server.start();
        try {
            URI callback = URI.create("http://127.0.0.1:" + server.getAddress().getPort() + path);
            AgentEventClient client = new AgentEventClient(callback, instanceId, linkSecret,
                    Clock.fixed(Instant.parse(timestamp), ZoneOffset.UTC), executor);
            Map<String, Object> event = new LinkedHashMap<>();
            event.put("eventId", "evt-contract-1");
            event.put("type", "server.state_changed");
            event.put("occurredAt", Instant.parse(timestamp));
            event.put("message", "NekoProbe is online");
            event.put("details", Map.of("state", "running"));
            client.send(event);

            assertTrue(received.await(5, TimeUnit.SECONDS), "the agent callback was not received");
            assertEquals(expectedBody, new String(capturedBody.get(), StandardCharsets.UTF_8));
            assertEquals(instanceId, capturedHeaders.get().get(AgentEventClient.INSTANCE_HEADER));
            assertEquals(timestamp, capturedHeaders.get().get(AgentEventClient.TIMESTAMP_HEADER));
            String nonce = capturedHeaders.get().get(AgentEventClient.NONCE_HEADER);
            assertTrue(SecurityUtils.isLowerHex(nonce, 32));
            String canonical = AgentEventClient.canonical("POST", path, instanceId, timestamp, nonce,
                    capturedBody.get());
            assertEquals(capturedHeaders.get().get(AgentEventClient.SIGNATURE_HEADER),
                    AgentEventClient.sign(canonical, key));
        } finally {
            server.stop(0);
            executor.shutdownNow();
        }
    }

    private static JsonNode fixture() throws Exception {
        try (InputStream input = AgentEventClientTest.class.getResourceAsStream("/probe-event-golden.json")) {
            if (input == null) {
                throw new IllegalStateException("shared probe event fixture is missing");
            }
            return Json.MAPPER.readTree(input);
        }
    }
}
