package mofumofu.nekoprobe.security;

import com.fasterxml.jackson.annotation.JsonInclude;

import java.time.Instant;
import java.util.ArrayList;
import java.util.List;

final class TokenRegistry {
    private TokenRegistry() {
    }

    record Data(int schemaVersion, String instanceId, int generation, String linkSecret, List<Entry> tokens) {
        Data copy() {
            return new Data(schemaVersion, instanceId, generation, linkSecret,
                    tokens.stream().map(Entry::copy).toList());
        }
    }

    record Entry(
            String tokenId,
            String hash,
            List<String> scopes,
            Instant createdAt,
            @JsonInclude(JsonInclude.Include.ALWAYS) Instant notAfter,
            boolean revoked,
            boolean root) {
        Entry copy() {
            return new Entry(tokenId, hash, List.copyOf(scopes), createdAt, notAfter, revoked, root);
        }

        Entry with(List<String> newScopes, Instant newNotAfter, boolean newRevoked, boolean newRoot) {
            return new Entry(tokenId, hash, List.copyOf(newScopes), createdAt, newNotAfter, newRevoked, newRoot);
        }
    }

    static Data newData(String instanceId, String linkSecret) {
        return new Data(1, instanceId, 0, linkSecret, new ArrayList<>());
    }
}
