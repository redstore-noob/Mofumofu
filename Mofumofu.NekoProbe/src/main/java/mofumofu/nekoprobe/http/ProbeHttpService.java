package mofumofu.nekoprobe.http;

import com.fasterxml.jackson.databind.JsonNode;
import io.undertow.Handlers;
import io.undertow.Undertow;
import io.undertow.UndertowOptions;
import io.undertow.server.HttpHandler;
import io.undertow.server.HttpServerExchange;
import io.undertow.server.handlers.PathHandler;
import io.undertow.util.Headers;
import io.undertow.util.HttpString;
import mofumofu.nekoprobe.ProbeConfig;
import mofumofu.nekoprobe.api.ApiException;
import mofumofu.nekoprobe.api.Json;
import mofumofu.nekoprobe.api.Protocol;
import mofumofu.nekoprobe.bukkit.ConsoleRing;
import mofumofu.nekoprobe.security.AuthContext;
import mofumofu.nekoprobe.security.SecurityUtils;
import mofumofu.nekoprobe.security.TokenService;

import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.time.Clock;
import java.util.Deque;
import java.util.List;
import java.util.Map;
import java.util.concurrent.Executor;
import java.util.function.Consumer;

public final class ProbeHttpService {
    private static final HttpString X_REQUEST_ID = new HttpString("X-Request-Id");
    private final ProbeConfig config;
    private final ProbeRouter router;
    private final Executor executor;
    private final Consumer<String> errorLogger;
    private final Undertow undertow;
    private final WebSocketHub webSockets;

    public ProbeHttpService(ProbeConfig config, ProbeRouter router, TokenService tokens, ConsoleRing journal,
                            Clock clock, Executor executor, Consumer<String> errorLogger) {
        this.config = config;
        this.router = router;
        this.executor = executor;
        this.errorLogger = errorLogger;
        this.webSockets = new WebSocketHub(tokens, router, journal, clock, executor);
        String webSocketPath = basePath() + "/ws";
        HttpHandler webSocketHandler = Handlers.websocket(webSockets);
        HttpHandler authenticatedWebSocket = exchange -> {
            String requestId = SecurityUtils.randomHex(16);
            try {
                router.authenticate(exchange.getRequestHeaders().getFirst(Headers.AUTHORIZATION)).requireScope("read");
                webSocketHandler.handleRequest(exchange);
            } catch (Throwable failure) {
                handleFailure(exchange, failure, requestId);
            }
        };
        PathHandler paths = Handlers.path()
                .addExactPath(webSocketPath, authenticatedWebSocket)
                .addPrefixPath("/", this::handle);
        this.undertow = Undertow.builder()
                .addHttpListener(config.port(), "127.0.0.1")
                .setIoThreads(2)
                .setWorkerThreads(4)
                .setServerOption(UndertowOptions.MAX_ENTITY_SIZE, (long) Protocol.MAX_BODY_BYTES)
                .setHandler(paths)
                .build();
    }

    public void start() {
        undertow.start();
    }

    public void stop() {
        webSockets.close();
        undertow.stop();
    }

    private void handle(HttpServerExchange exchange) {
        if (exchange.isInIoThread()) {
            exchange.dispatch(executor, () -> handleBlocking(exchange));
        } else {
            handleBlocking(exchange);
        }
    }

    private void handleBlocking(HttpServerExchange exchange) {
        String requestId = SecurityUtils.randomHex(16);
        try {
            String requestPath = exchange.getRequestPath();
            String base = basePath();
            if (!requestPath.equals(base) && !requestPath.startsWith(base + "/")) {
                throw ApiException.notFound("Endpoint");
            }
            AuthContext auth = router.authenticate(exchange.getRequestHeaders().getFirst(Headers.AUTHORIZATION));
            JsonNode body = readBody(exchange);
            String path = requestPath.substring(base.length());
            Map<String, List<String>> query = new java.util.LinkedHashMap<>();
            exchange.getQueryParameters().forEach((key, values) -> query.put(key, List.copyOf(values)));
            Map<String, Object> response = router.route(exchange.getRequestMethod().toString(), path, query, body, auth);
            send(exchange, 200, response, requestId);
        } catch (Throwable failure) {
            handleFailure(exchange, failure, requestId);
        }
    }

    private JsonNode readBody(HttpServerExchange exchange) throws IOException {
        String method = exchange.getRequestMethod().toString();
        if (!List.of("POST", "PUT", "PATCH", "DELETE").contains(method)) {
            return null;
        }
        long declared = exchange.getRequestContentLength();
        if (declared > Protocol.MAX_BODY_BYTES) {
            throw new ApiException(413, "payload_too_large", "Request body exceeds the supported size.");
        }
        exchange.startBlocking();
        try (InputStream input = exchange.getInputStream()) {
            byte[] bytes = input.readNBytes(Protocol.MAX_BODY_BYTES + 1);
            if (bytes.length > Protocol.MAX_BODY_BYTES) {
                throw new ApiException(413, "payload_too_large", "Request body exceeds the supported size.");
            }
            if (bytes.length == 0) {
                return null;
            }
            return Json.read(new String(bytes, StandardCharsets.UTF_8));
        }
    }

    private void handleFailure(HttpServerExchange exchange, Throwable failure, String requestId) {
        if (exchange.isResponseComplete()) {
            return;
        }
        ApiException error;
        if (failure instanceof ApiException apiException) {
            error = apiException;
        } else if (failure instanceof IOException) {
            error = ApiException.badRequest("Request body could not be read.");
        } else {
            errorLogger.accept("NekoProbe request " + requestId + " failed with " + failure.getClass().getSimpleName());
            error = ApiException.internal();
        }
        send(exchange, error.status(), error.body(requestId), requestId);
    }

    private static void send(HttpServerExchange exchange, int status, Map<String, Object> body, String requestId) {
        if (exchange.isResponseComplete()) {
            return;
        }
        exchange.setStatusCode(status);
        exchange.getResponseHeaders().put(Headers.CONTENT_TYPE, "application/json; charset=utf-8");
        exchange.getResponseHeaders().put(Headers.CACHE_CONTROL, "no-store");
        exchange.getResponseHeaders().put(Headers.X_CONTENT_TYPE_OPTIONS, "nosniff");
        exchange.getResponseHeaders().put(X_REQUEST_ID, requestId);
        exchange.getResponseSender().send(Json.write(body), StandardCharsets.UTF_8);
    }

    private String basePath() {
        return "/v1/servers/" + config.instanceId();
    }
}
