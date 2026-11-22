package mofumofu.nekoprobe.security;

import mofumofu.nekoprobe.api.Json;
import mofumofu.nekoprobe.api.Protocol;

import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.time.Clock;
import java.time.format.DateTimeFormatter;
import java.util.HexFormat;
import java.util.LinkedHashMap;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.Executor;

public final class AgentEventClient {
    static final String INSTANCE_HEADER = "X-Mofumofu-Instance-Id";
    static final String TIMESTAMP_HEADER = "X-Mofumofu-Timestamp";
    static final String NONCE_HEADER = "X-Mofumofu-Nonce";
    static final String SIGNATURE_HEADER = "X-Mofumofu-Signature";

    private final URI callback;
    private final String path;
    private final String instanceId;
    private final byte[] linkSecret;
    private final Clock clock;
    private final HttpClient client;

    public AgentEventClient(URI callback, String instanceId, String linkSecret, Clock clock, Executor executor) {
        if (callback == null) {
            throw new IllegalArgumentException("agentCallback is required");
        }
        if (callback.getRawQuery() != null) {
            throw new IllegalArgumentException("agentCallback must not contain a query string");
        }
        this.callback = callback;
        this.path = callback.getRawPath() == null || callback.getRawPath().isEmpty() ? "/" : callback.getRawPath();
        this.instanceId = instanceId;
        this.linkSecret = HexFormat.of().parseHex(linkSecret);
        this.clock = clock;
        this.client = HttpClient.newBuilder().executor(executor).connectTimeout(java.time.Duration.ofSeconds(3)).build();
    }

    public void send(Map<String, Object> event) {
        Map<String, Object> body = new LinkedHashMap<>();
        body.put("apiVersion", Protocol.API_VERSION);
        body.putAll(event);
        byte[] payload = Json.bytes(body);
        String timestamp = DateTimeFormatter.ISO_INSTANT.format(clock.instant());
        String nonce = SecurityUtils.randomHex(16);
        String canonical = canonical("POST", path, instanceId, timestamp, nonce, payload);
        try {
            String signature = sign(canonical, linkSecret);
            HttpRequest request = HttpRequest.newBuilder(callback)
                    .timeout(java.time.Duration.ofSeconds(5))
                    .header("Content-Type", "application/json")
                    .header(INSTANCE_HEADER, instanceId)
                    .header(TIMESTAMP_HEADER, timestamp)
                    .header(NONCE_HEADER, nonce)
                    .header(SIGNATURE_HEADER, signature)
                    .POST(HttpRequest.BodyPublishers.ofByteArray(payload)).build();
            client.sendAsync(request, HttpResponse.BodyHandlers.discarding());
        } catch (GeneralSecurityException | IllegalArgumentException e) {
            throw new IllegalStateException("Could not sign local event", e);
        }
    }

    static String canonical(String method, String path, String instanceId, String timestamp, String nonce,
                            byte[] body) {
        return method.toUpperCase(Locale.ROOT) + "\n" + path + "\n" + instanceId + "\n" + timestamp + "\n"
                + nonce + "\n" + SecurityUtils.sha256Hex(body);
    }

    static String sign(String canonical, byte[] linkSecret) throws GeneralSecurityException {
        Mac mac = Mac.getInstance("HmacSHA256");
        mac.init(new SecretKeySpec(linkSecret, "HmacSHA256"));
        return HexFormat.of().formatHex(mac.doFinal(canonical.getBytes(StandardCharsets.UTF_8)));
    }
}
