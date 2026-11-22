package mofumofu.nekoprobe.security;

import java.time.Instant;
import java.util.Set;

public record AuthContext(String tokenId, Set<String> scopes, boolean root, Instant authenticatedAt) {
    public void requireScope(String scope) {
        if (!scopes.contains(scope)) {
            throw mofumofu.nekoprobe.api.ApiException.forbidden();
        }
    }

    public void requireRoot() {
        if (!root) {
            throw new mofumofu.nekoprobe.api.ApiException(403, "forbidden", "A root token is required for token administration.");
        }
    }
}
