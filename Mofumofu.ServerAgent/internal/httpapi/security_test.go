// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package httpapi

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/tokens"
)

func TestSecurityOrderingScopesAndRateLimit(t *testing.T) {
	now := time.Unix(1_800_000_000, 0).UTC()
	const token = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
	registryPath := writeRegistry(t, "survival", tokens.TokenRegistryToken{
		TokenID:   "daily",
		Hash:      tokens.TokenHash(token),
		Scopes:    []string{tokens.ScopeRead},
		CreatedAt: now,
	})
	security := &Security{
		InstanceID: "survival",
		Registry:   tokens.NewTokenRegistry(registryPath, func() time.Time { return now }),
		Limiter:    tokens.NewRateLimiter(func() time.Time { return now }),
	}
	mux := http.NewServeMux()
	mux.HandleFunc("GET /v1/servers/{instanceId}/status", security.Wrap(tokens.ScopeRead, tokens.CategoryRead, false,
		func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(http.StatusNoContent) }))

	request := func(path, authorization string) *httptest.ResponseRecorder {
		recorder := httptest.NewRecorder()
		req := httptest.NewRequest(http.MethodGet, path, nil)
		if authorization != "" {
			req.Header.Set("Authorization", authorization)
		}
		mux.ServeHTTP(recorder, req)
		return recorder
	}

	if got := request("/v1/servers/other/status?apiVersion=1.0", ""); got.Code != http.StatusNotFound {
		t.Fatalf("instance mismatch: %d", got.Code)
	}
	if got := request("/v1/servers/survival/status", ""); got.Code != http.StatusBadRequest {
		t.Fatalf("missing version: %d", got.Code)
	}
	if got := request("/v1/servers/survival/status?apiVersion=1.0", "Bearer bad"); got.Code != http.StatusUnauthorized {
		t.Fatalf("authentication: %d", got.Code)
	}
	authorization := "Bearer " + token
	for i := 0; i < 20; i++ {
		if got := request("/v1/servers/survival/status?apiVersion=1.0", authorization); got.Code != http.StatusNoContent {
			t.Fatalf("request %d: %d", i, got.Code)
		}
	}
	if got := request("/v1/servers/survival/status?apiVersion=1.0", authorization); got.Code != http.StatusTooManyRequests {
		t.Fatalf("rate limit: %d", got.Code)
	}
}

func TestRootDoesNotImplyScopeButRootEndpointWorks(t *testing.T) {
	now := time.Unix(1_800_000_000, 0).UTC()
	const token = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
	registryPath := writeRegistry(t, "survival", tokens.TokenRegistryToken{
		TokenID: "root", Hash: tokens.TokenHash(token), Root: true, CreatedAt: now, Scopes: []string{},
	})
	security := &Security{InstanceID: "survival", Registry: tokens.NewTokenRegistry(registryPath, func() time.Time { return now }), Limiter: tokens.NewRateLimiter(func() time.Time { return now })}
	mux := http.NewServeMux()
	mux.HandleFunc("GET /v1/servers/{instanceId}/status", security.Wrap(tokens.ScopeRead, tokens.CategoryRead, false,
		func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(http.StatusNoContent) }))
	mux.HandleFunc("GET /v1/servers/{instanceId}/tokens", security.Wrap("", tokens.CategoryRead, true,
		func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(http.StatusNoContent) }))
	authorization := "Bearer " + token
	recorder := httptest.NewRecorder()
	mux.ServeHTTP(recorder, httptest.NewRequest(http.MethodGet, "/v1/servers/survival/status?apiVersion=1.0", nil))
	// The helper request above intentionally has no Authorization; set it on a fresh request.
	req := httptest.NewRequest(http.MethodGet, "/v1/servers/survival/status?apiVersion=1.0", nil)
	req.Header.Set("Authorization", authorization)
	recorder = httptest.NewRecorder()
	mux.ServeHTTP(recorder, req)
	if recorder.Code != http.StatusForbidden || !strings.Contains(recorder.Body.String(), "insufficient_scope") {
		t.Fatalf("root scope bypass: %d %s", recorder.Code, recorder.Body)
	}
	req = httptest.NewRequest(http.MethodGet, "/v1/servers/survival/tokens?apiVersion=1.0", nil)
	req.Header.Set("Authorization", authorization)
	recorder = httptest.NewRecorder()
	mux.ServeHTTP(recorder, req)
	if recorder.Code != http.StatusNoContent {
		t.Fatalf("root endpoint: %d %s", recorder.Code, recorder.Body)
	}
}

func writeRegistry(t *testing.T, instanceID string, entries ...tokens.TokenRegistryToken) string {
	t.Helper()
	path := filepath.Join(t.TempDir(), "registry.json")
	payload, err := json.Marshal(tokens.RegistrySnapshot{
		SchemaVersion: 1,
		InstanceID:    instanceID,
		Generation:    1,
		LinkSecret:    strings.Repeat("ab", 32),
		Tokens:        entries,
	})
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, payload, 0o600); err != nil {
		t.Fatal(err)
	}
	return path
}
