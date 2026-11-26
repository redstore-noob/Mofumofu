// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package httpapi

import (
	"encoding/json"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/config"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/server"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/service"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/tokens"
)

func TestHandshakeStatusAndExplicitCapabilityPlaceholders(t *testing.T) {
	now := time.Unix(1_800_000_000, 0).UTC()
	const token = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
	registryPath := writeRegistry(t, "survival", tokens.TokenRegistryToken{
		TokenID: "daily", Hash: tokens.TokenHash(token), CreatedAt: now,
		Scopes: []string{tokens.ScopeRead, tokens.ScopeCommand, tokens.ScopeBackup},
	})
	root := t.TempDir()
	instance := filepath.Join(root, "survival")
	if err := os.Mkdir(instance, 0o750); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(instance, "server.json"), []byte(`{"id":"survival","name":"Survival","core":"paper","mcVersion":"1.21.4","maxPlayers":20}`), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(instance, "server.properties"), []byte("motd=Hello\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	metadata, err := server.LoadMetadata(root, "survival")
	if err != nil {
		t.Fatal(err)
	}
	cfg := config.Default()
	cfg.InstanceId = "survival"
	application := service.New(cfg, metadata, "test-version", func() time.Time { return now })
	security := &Security{InstanceID: "survival", Registry: tokens.NewTokenRegistry(registryPath, func() time.Time { return now }), Limiter: tokens.NewRateLimiter(func() time.Time { return now })}
	api := New(application, security, slog.New(slog.NewTextHandler(io.Discard, nil)))
	httpServer := httptest.NewServer(api.Handler())
	defer httpServer.Close()

	request, _ := http.NewRequest(http.MethodPost, httpServer.URL+"/v1/servers/survival/handshake", strings.NewReader(`{"clientName":"test","requestedCapabilities":["future.capability"]}`))
	request.Header.Set("Authorization", "Bearer "+token)
	response, err := http.DefaultClient.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	payload, _ := io.ReadAll(response.Body)
	response.Body.Close()
	if response.StatusCode != http.StatusOK {
		t.Fatalf("handshake: %d %s", response.StatusCode, payload)
	}
	var handshake protocol.HandshakeResponse
	if err := protocol.Unmarshal(payload, &handshake); err != nil {
		t.Fatal(err)
	}
	if handshake.ServerName != "Survival" || handshake.ServerImplementationVersion != "test-version" ||
		strings.Join(handshake.Capabilities, ",") != "read" || strings.Join(handshake.Scopes, ",") != "backup,command,read" {
		t.Fatalf("handshake=%+v", handshake)
	}
	if handshake.TokenID == nil || *handshake.TokenID != "daily" || handshake.ServerTime.String() == "" {
		t.Fatalf("handshake identity=%+v", handshake)
	}

	request, _ = http.NewRequest(http.MethodGet, httpServer.URL+"/v1/servers/survival/status?apiVersion=1.1", nil)
	request.Header.Set("Authorization", "Bearer "+token)
	response, err = http.DefaultClient.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	payload, _ = io.ReadAll(response.Body)
	response.Body.Close()
	var status protocol.StatusResponse
	if err := protocol.Unmarshal(payload, &status); err != nil {
		t.Fatal(err)
	}
	if status.State == nil || *status.State != "offline" || status.MaxPlayers != 20 || status.MOTD == nil || *status.MOTD != "Hello" {
		t.Fatalf("status=%+v json=%s", status, payload)
	}
	if strings.Contains(string(payload), `"cpuPercent"`) || strings.Contains(string(payload), `"tps"`) {
		t.Fatalf("offline optional metrics were not omitted: %s", payload)
	}

	request, _ = http.NewRequest(http.MethodPost, httpServer.URL+"/v1/servers/survival/console/commands", strings.NewReader(`{"command":"list"}`))
	request.Header.Set("Authorization", "Bearer "+token)
	response, err = http.DefaultClient.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	payload, _ = io.ReadAll(response.Body)
	response.Body.Close()
	if response.StatusCode != http.StatusConflict || !strings.Contains(string(payload), `"code":"server_offline"`) {
		t.Fatalf("offline command: %d %s", response.StatusCode, payload)
	}

	request, _ = http.NewRequest(http.MethodPost, httpServer.URL+"/v1/servers/survival/console/commands", strings.NewReader(`{`))
	request.Header.Set("Authorization", "Bearer "+token)
	response, err = http.DefaultClient.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	payload, _ = io.ReadAll(response.Body)
	response.Body.Close()
	if response.StatusCode != http.StatusBadRequest || !strings.Contains(string(payload), `"code":"invalid_request"`) {
		t.Fatalf("invalid placeholder body: %d %s", response.StatusCode, payload)
	}
}

func TestInstanceMismatchAndUnknownRoute(t *testing.T) {
	now := time.Unix(1_800_000_000, 0).UTC()
	registryPath := writeRegistry(t, "survival")
	root := t.TempDir()
	instance := filepath.Join(root, "survival")
	_ = os.Mkdir(instance, 0o750)
	_ = os.WriteFile(filepath.Join(instance, "server.json"), []byte(`{"name":"Survival"}`), 0o600)
	metadata, _ := server.LoadMetadata(root, "survival")
	cfg := config.Default()
	cfg.InstanceId = "survival"
	application := service.New(cfg, metadata, "test", func() time.Time { return now })
	security := &Security{InstanceID: "survival", Registry: tokens.NewTokenRegistry(registryPath, func() time.Time { return now }), Limiter: tokens.NewRateLimiter(func() time.Time { return now })}
	api := New(application, security, slog.Default())
	recorder := httptest.NewRecorder()
	api.Handler().ServeHTTP(recorder, httptest.NewRequest(http.MethodGet, "/v1/servers/other/status?apiVersion=1.0", nil))
	if recorder.Code != http.StatusNotFound || !strings.Contains(recorder.Body.String(), `"code":"not_found"`) {
		t.Fatalf("instance mismatch: %d %s", recorder.Code, recorder.Body)
	}
	recorder = httptest.NewRecorder()
	api.Handler().ServeHTTP(recorder, httptest.NewRequest(http.MethodGet, "/missing", nil))
	if recorder.Code != http.StatusNotFound {
		t.Fatalf("unknown route: %d", recorder.Code)
	}
	var apiError protocol.APIError
	if err := protocol.Unmarshal(recorder.Body.Bytes(), &apiError); err != nil || apiError.Code != "not_found" {
		t.Fatalf("error shape: %+v %v", apiError, err)
	}
	var raw map[string]any
	_ = json.Unmarshal(recorder.Body.Bytes(), &raw)
	if _, exists := raw["details"]; exists {
		t.Fatal("null details were serialized")
	}
}
