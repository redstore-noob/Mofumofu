package mofumofu.nekoprobe.security;

import mofumofu.nekoprobe.api.ApiException;

import java.time.Clock;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;

public final class TokenRateLimiter {
    private final int requestsPerSecond;
    private final Clock clock;
    private final Map<String, Window> windows = new ConcurrentHashMap<>();

    public TokenRateLimiter(int requestsPerSecond, Clock clock) {
        if (requestsPerSecond < 1) {
            throw new IllegalArgumentException("requestsPerSecond");
        }
        this.requestsPerSecond = requestsPerSecond;
        this.clock = clock;
    }

    public void check(String tokenId) {
        long second = clock.millis() / 1000;
        Window window = windows.computeIfAbsent(tokenId, ignored -> new Window());
        synchronized (window) {
            if (window.second != second) {
                window.second = second;
                window.count = 0;
            }
            if (++window.count > requestsPerSecond) {
                throw new ApiException(429, "rate_limited", "Too many requests for this token.", Map.of("retryAfterSeconds", "1"));
            }
        }
    }

    private static final class Window {
        private long second = Long.MIN_VALUE;
        private int count;
    }
}
