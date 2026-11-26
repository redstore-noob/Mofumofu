// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package httpapi

import (
	"encoding/json"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/audit"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/config"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/files"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/server"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/service"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/tokens"
)

func TestHandshakeStatusAndExplicitCapabilityPlaceholders(t *testing.T) {
	now := time.Unix(1_800_000_000, 0).UTC()
	const token = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
	const fileToken = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
	registryPath := writeRegistry(t, "survival",
		tokens.TokenRegistryToken{
			TokenID: "daily", Hash: tokens.TokenHash(token), CreatedAt: now,
			Scopes: []string{tokens.ScopeRead, tokens.ScopeCommand, tokens.ScopeBackup, tokens.ScopeFiles},
		},
		tokens.TokenRegistryToken{
			TokenID: "files", Hash: tokens.TokenHash(fileToken), CreatedAt: now,
			Scopes: []string{tokens.ScopeRead, tokens.ScopeFiles},
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
	auditSink, err := audit.Open(filepath.Join(t.TempDir(), "audit.jsonl"))
	if err != nil {
		t.Fatal(err)
	}
	defer auditSink.Close()
	application.SetAudit(auditSink)
	fileService, err := files.New(metadata.Directory)
	if err != nil {
		t.Fatal(err)
	}
	application.SetFiles(fileService, files.NewPropertyService(fileService, func() time.Time { return now }))
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
		strings.Join(handshake.Capabilities, ",") != "read,files,properties" || strings.Join(handshake.Scopes, ",") != "backup,command,files,read" {
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
	var statusResponse protocol.StatusResponse
	if err := protocol.Unmarshal(payload, &statusResponse); err != nil {
		t.Fatal(err)
	}
	if statusResponse.State == nil || *statusResponse.State != "offline" || statusResponse.MaxPlayers != 20 || statusResponse.MOTD == nil || *statusResponse.MOTD != "Hello" {
		t.Fatalf("status=%+v json=%s", statusResponse, payload)
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

	do := func(method, path, body string) (int, []byte) {
		request, _ := http.NewRequest(method, httpServer.URL+path, strings.NewReader(body))
		request.Header.Set("Authorization", "Bearer "+fileToken)
		response, err := http.DefaultClient.Do(request)
		if err != nil {
			t.Fatal(err)
		}
		payload, _ := io.ReadAll(response.Body)
		response.Body.Close()
		return response.StatusCode, payload
	}
	base := "/v1/servers/survival"
	if status, payload := do(http.MethodPost, base+"/files/create", `{"path":"notes","isDirectory":true}`); status != http.StatusOK {
		t.Fatalf("create directory: %d %s", status, payload)
	}
	if status, payload := do(http.MethodPost, base+"/files/create", `{"path":"notes/a.txt","isDirectory":false}`); status != http.StatusOK {
		t.Fatalf("create file: %d %s", status, payload)
	}
	readStatus, readPayload := do(http.MethodGet, base+"/files/content?path=notes%2Fa.txt&apiVersion=1.0", "")
	if readStatus != http.StatusOK {
		t.Fatalf("read empty file: %d %s", readStatus, readPayload)
	}
	var current protocol.FileContentResponse
	if err := protocol.Unmarshal(readPayload, &current); err != nil {
		t.Fatal(err)
	}
	writeBody := fmt.Sprintf(`{"path":"notes/a.txt","content":"hello","expectedRevision":%q}`, current.Revision)
	status, writePayload := do(http.MethodPut, base+"/files/content", writeBody)
	if status != http.StatusOK {
		t.Fatalf("write file: %d %s", status, writePayload)
	}
	var written protocol.FileWriteResponse
	if err := protocol.Unmarshal(writePayload, &written); err != nil {
		t.Fatal(err)
	}
	renameBody := fmt.Sprintf(`{"path":"notes/a.txt","newName":"b.txt","expectedRevision":%q}`, written.Revision)
	if status, payload := do(http.MethodPost, base+"/files/rename", renameBody); status != http.StatusOK {
		t.Fatalf("rename file: %d %s", status, payload)
	}
	if status, payload := do(http.MethodGet, base+"/files?path=notes&apiVersion=1.0", ""); status != http.StatusOK || !strings.Contains(string(payload), `"path":"notes/b.txt"`) {
		t.Fatalf("list files: %d %s", status, payload)
	}
	if status, payload := do(http.MethodGet, base+"/properties?apiVersion=1.0", ""); status != http.StatusOK || !strings.Contains(string(payload), `"motd":"Hello"`) {
		t.Fatalf("properties: %d %s", status, payload)
	}
	if status, payload := do(http.MethodGet, base+"/files/content?path=server.properties&apiVersion=1.0", ""); status != http.StatusBadRequest || !strings.Contains(string(payload), `"code":"invalid_path"`) {
		t.Fatalf("protected file: %d %s", status, payload)
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
