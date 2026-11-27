// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package httpapi

import (
	"bytes"
	"context"
	"crypto/sha1"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"log/slog"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/audit"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/config"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/confirmation"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/events"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/install"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/operations"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/server"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/service"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/tokens"
)

type steppedClock struct {
	mu  sync.Mutex
	now time.Time
}

func (c *steppedClock) Now() time.Time {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.now
}

func (c *steppedClock) Advance(duration time.Duration) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.now = c.now.Add(duration)
}

type installFixture struct {
	url         string
	token       string
	clock       *steppedClock
	installRoot string
	auditPath   string
}

// newInstallFixture builds an Agent whose install client talks only to an
// httptest TLS upstream. Each request advances the clock so Sensitive
// (1/s) rate windows reset between calls.
func newInstallFixture(t *testing.T, tune func(*config.Config)) *installFixture {
	t.Helper()
	clock := &steppedClock{now: time.Date(2026, 11, 27, 12, 0, 0, 0, time.UTC)}
	root := t.TempDir()
	instance := filepath.Join(root, "survival")
	if err := os.Mkdir(instance, 0o750); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(instance, "server.json"), []byte(`{"name":"Survival","mcVersion":"1.21.4","maxPlayers":20}`), 0o600); err != nil {
		t.Fatal(err)
	}
	metadata, err := server.LoadMetadata(root, "survival")
	if err != nil {
		t.Fatal(err)
	}
	const token = "ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12"
	registryPath := writeRegistry(t, "survival", tokens.TokenRegistryToken{
		TokenID: "ops", Hash: tokens.TokenHash(token), CreatedAt: clock.Now().Add(-time.Hour), Scopes: []string{tokens.ScopeControl},
	})
	registry := tokens.NewTokenRegistry(registryPath, clock.Now)
	journal, err := events.OpenJournal(filepath.Join(root, "events.jsonl"), 256, clock.Now, nil)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = journal.Close(context.Background()) })
	operationService := operations.New(journal, clock.Now)
	t.Cleanup(func() { _ = operationService.Stop(context.Background()) })
	auditPath := filepath.Join(root, "audit.jsonl")
	auditSink, err := audit.Open(auditPath)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = auditSink.Close() })

	installRoot := filepath.Join(t.TempDir(), "installs")
	cfg := config.Default()
	cfg.InstanceId = "survival"
	cfg.ServerRootDirectory = root
	cfg.InstallRootDirectory = installRoot
	cfg.InstallAllowedCores = []string{"vanilla"}
	cfg.InstallAllowedMinecraftVersions = []string{"1.21.*"}
	if tune != nil {
		tune(&cfg)
	}

	upstream := newInstallUpstream(t)
	installer := install.NewClient(install.Options{
		HTTPClient: upstream.client,
		UserAgent:  "mofumofu-agent/test",
		Endpoints: install.Endpoints{
			MojangManifest: upstream.base + "/mojang/version_manifest_v2.json",
		},
		URLPolicy: func(_ context.Context, rawURL string) error {
			if strings.HasPrefix(rawURL, upstream.base+"/") {
				return nil
			}
			return fmt.Errorf("outside the test upstream")
		},
		Lookup: func(context.Context, string) ([]net.IPAddr, error) {
			return nil, fmt.Errorf("DNS disabled in tests")
		},
	})

	application := service.New(cfg, metadata, "test", clock.Now)
	application.SetJournal(journal)
	application.SetRegistry(registry)
	application.SetOperations(operationService, confirmation.New(clock.Now))
	application.SetAudit(auditSink)
	application.SetEventPublisher(nil, true)
	application.SetInstaller(installer)
	security := &Security{InstanceID: "survival", Registry: registry, Limiter: tokens.NewRateLimiter(clock.Now)}
	api := New(application, security, slog.New(slog.NewTextHandler(io.Discard, nil)))
	httpServer := httptest.NewServer(api.Handler())
	t.Cleanup(httpServer.Close)
	return &installFixture{url: httpServer.URL, token: token, clock: clock, installRoot: installRoot, auditPath: auditPath}
}

type installUpstream struct {
	base   string
	client *http.Client
}

func newInstallUpstream(t *testing.T) installUpstream {
	t.Helper()
	jar := []byte("remote install server jar")
	mux := http.NewServeMux()
	upstream := httptest.NewTLSServer(mux)
	t.Cleanup(upstream.Close)
	base := upstream.URL
	sum := sha1Hex(jar)
	mux.HandleFunc("/mojang/version_manifest_v2.json", func(w http.ResponseWriter, _ *http.Request) {
		fmt.Fprintf(w, `{"latest":{"release":"1.21.4","snapshot":""},"versions":[{"id":"1.21.4","type":"release","url":%q}]}`,
			base+"/mojang/meta/1.21.4.json")
	})
	mux.HandleFunc("/mojang/meta/1.21.4.json", func(w http.ResponseWriter, _ *http.Request) {
		fmt.Fprintf(w, `{"downloads":{"server":{"url":%q,"sha1":%q,"size":%d}}}`, base+"/files/server.jar", sum, len(jar))
	})
	mux.HandleFunc("/files/server.jar", func(w http.ResponseWriter, _ *http.Request) {
		_, _ = w.Write(jar)
	})
	return installUpstream{base: base, client: upstream.Client()}
}

func sha1Hex(payload []byte) string {
	sum := sha1.Sum(payload)
	return hex.EncodeToString(sum[:])
}

// do advances the clock by one second so each call falls in a fresh rate window.
func (f *installFixture) do(t *testing.T, method, path string, body any) (int, []byte) {
	t.Helper()
	f.clock.Advance(time.Second)
	return f.send(t, method, path, body)
}

func (f *installFixture) send(t *testing.T, method, path string, body any) (int, []byte) {
	t.Helper()
	var reader io.Reader
	if body != nil {
		reader = bytes.NewReader(mustJSON(t, body))
	}
	request, err := http.NewRequest(method, f.url+path, reader)
	if err != nil {
		t.Fatal(err)
	}
	request.Header.Set("Authorization", "Bearer "+f.token)
	if body != nil {
		request.Header.Set("Content-Type", "application/json")
	}
	response, err := http.DefaultClient.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	payload, err := io.ReadAll(response.Body)
	if err != nil {
		t.Fatal(err)
	}
	return response.StatusCode, payload
}

func errorCode(t *testing.T, payload []byte) string {
	t.Helper()
	var body struct {
		Code string `json:"code"`
	}
	if err := json.Unmarshal(payload, &body); err != nil {
		t.Fatalf("error body %s: %v", payload, err)
	}
	return body.Code
}

func validInstallBody() map[string]any {
	return map[string]any{"core": "vanilla", "minecraftVersion": "1.21.4", "acceptEula": true, "port": 25570, "motd": "Remote"}
}

func TestInstallPrepareExecuteAndOperationEndToEnd(t *testing.T) {
	fixture := newInstallFixture(t, nil)

	status, payload := fixture.do(t, http.MethodPost, "/v1/servers/survival/install/prepare", validInstallBody())
	if status != http.StatusOK {
		t.Fatalf("prepare status = %d body = %s", status, payload)
	}
	var prepared struct {
		ConfirmationID string `json:"confirmationId"`
		Action         string `json:"action"`
	}
	if err := json.Unmarshal(payload, &prepared); err != nil {
		t.Fatal(err)
	}
	if prepared.Action != protocol.ConfirmationActionServerInstall || prepared.ConfirmationID == "" {
		t.Fatalf("prepare response = %s", payload)
	}

	status, payload = fixture.do(t, http.MethodPost, "/v1/servers/survival/confirm/execute",
		map[string]any{"confirmationId": prepared.ConfirmationID})
	if status != http.StatusOK {
		t.Fatalf("execute status = %d body = %s", status, payload)
	}
	var executed struct {
		Operation struct {
			ID   string `json:"id"`
			Kind string `json:"kind"`
		} `json:"operation"`
	}
	if err := json.Unmarshal(payload, &executed); err != nil {
		t.Fatal(err)
	}
	if executed.Operation.ID == "" || executed.Operation.Kind != "server.install" {
		t.Fatalf("execute response = %s", payload)
	}

	var final struct {
		Operation struct {
			State    *string           `json:"state"`
			Metadata map[string]string `json:"metadata"`
			Error    *struct {
				Code string `json:"code"`
			} `json:"error"`
		} `json:"operation"`
	}
	deadline := time.Now().Add(20 * time.Second)
	for {
		status, payload = fixture.do(t, http.MethodGet, "/v1/servers/survival/operations/"+executed.Operation.ID+"?apiVersion=1.0", nil)
		if status != http.StatusOK {
			t.Fatalf("operation status = %d body = %s", status, payload)
		}
		if err := json.Unmarshal(payload, &final); err != nil {
			t.Fatal(err)
		}
		if final.Operation.State != nil && (*final.Operation.State == protocol.OperationStateSucceeded || *final.Operation.State == protocol.OperationStateFailed) {
			break
		}
		if time.Now().After(deadline) {
			t.Fatalf("operation did not finish: %s", payload)
		}
		time.Sleep(10 * time.Millisecond)
	}
	if final.Operation.State == nil || *final.Operation.State != protocol.OperationStateSucceeded {
		t.Fatalf("operation finished in %s", payload)
	}
	if final.Operation.Metadata["instanceId"] != "survival" || final.Operation.Metadata["startScript"] == "" {
		t.Fatalf("operation metadata = %v", final.Operation.Metadata)
	}

	directory := filepath.Join(fixture.installRoot, "survival")
	for _, name := range []string{"server.jar", "server.json", "eula.txt", "server.properties"} {
		if _, err := os.Stat(filepath.Join(directory, name)); err != nil {
			t.Fatalf("install did not write %s: %v", name, err)
		}
	}
	properties, err := os.ReadFile(filepath.Join(directory, "server.properties"))
	if err != nil || !strings.Contains(string(properties), "server-port=25570\n") || !strings.Contains(string(properties), "motd=Remote\n") {
		t.Fatalf("server.properties = %q, %v", properties, err)
	}

	status, payload = fixture.do(t, http.MethodPost, "/v1/servers/survival/confirm/execute",
		map[string]any{"confirmationId": prepared.ConfirmationID})
	if status != http.StatusGone || errorCode(t, payload) != protocol.CodeConfirmationAlreadyUsed {
		t.Fatalf("replayed confirmation status = %d body = %s", status, payload)
	}

	auditLog, err := os.ReadFile(fixture.auditPath)
	if err != nil {
		t.Fatal(err)
	}
	for _, action := range []string{`"server.install.prepare"`, `"confirmation.execute"`, `"server.install"`} {
		if !strings.Contains(string(auditLog), action) {
			t.Fatalf("audit log is missing %s:\n%s", action, auditLog)
		}
	}
	if strings.Contains(string(auditLog), "Remote") {
		t.Fatalf("audit log recorded MOTD text:\n%s", auditLog)
	}
}

func TestInstallPrepareRejectsDisallowedRequests(t *testing.T) {
	cases := []struct {
		name       string
		tune       func(*config.Config)
		prepare    func(f *installFixture) map[string]any
		wantStatus int
		wantCode   string
	}{
		{
			name: "version outside allowlist",
			prepare: func(*installFixture) map[string]any {
				body := validInstallBody()
				body["minecraftVersion"] = "1.20.6"
				return body
			},
			wantStatus: http.StatusForbidden, wantCode: protocol.CodeForbidden,
		},
		{
			name: "core outside allowlist",
			prepare: func(*installFixture) map[string]any {
				body := validInstallBody()
				body["core"] = "paper"
				return body
			},
			wantStatus: http.StatusForbidden, wantCode: protocol.CodeForbidden,
		},
		{
			name: "EULA not accepted",
			prepare: func(*installFixture) map[string]any {
				body := validInstallBody()
				body["acceptEula"] = false
				return body
			},
			wantStatus: http.StatusBadRequest, wantCode: protocol.CodeInvalidRequest,
		},
		{
			name: "path-like version",
			prepare: func(*installFixture) map[string]any {
				body := validInstallBody()
				body["minecraftVersion"] = "1.21/../../x"
				return body
			},
			wantStatus: http.StatusBadRequest, wantCode: protocol.CodeInvalidRequest,
		},
		{
			name: "vanilla with coreVersion",
			prepare: func(*installFixture) map[string]any {
				body := validInstallBody()
				body["coreVersion"] = "1.21.4"
				return body
			},
			wantStatus: http.StatusBadRequest, wantCode: protocol.CodeInvalidRequest,
		},
		{
			name: "attach mode",
			tune: func(cfg *config.Config) { cfg.Mode = config.ModeAttach },
			prepare: func(*installFixture) map[string]any {
				return validInstallBody()
			},
			wantStatus: http.StatusForbidden, wantCode: protocol.CodeCapabilityNotSupported,
		},
		{
			name: "install root not configured",
			tune: func(cfg *config.Config) { cfg.InstallRootDirectory = "" },
			prepare: func(*installFixture) map[string]any {
				return validInstallBody()
			},
			wantStatus: http.StatusForbidden, wantCode: protocol.CodeCapabilityNotSupported,
		},
		{
			name: "target directory not empty",
			prepare: func(f *installFixture) map[string]any {
				target := filepath.Join(f.installRoot, "survival")
				if err := os.MkdirAll(target, 0o750); err != nil {
					panic(err)
				}
				if err := os.WriteFile(filepath.Join(target, "keep.txt"), []byte("x"), 0o600); err != nil {
					panic(err)
				}
				return validInstallBody()
			},
			wantStatus: http.StatusConflict, wantCode: protocol.CodeConflict,
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			fixture := newInstallFixture(t, tc.tune)
			body := tc.prepare(fixture)
			status, payload := fixture.do(t, http.MethodPost, "/v1/servers/survival/install/prepare", body)
			if status != tc.wantStatus || errorCode(t, payload) != tc.wantCode {
				t.Fatalf("status = %d body = %s; want %d %s", status, payload, tc.wantStatus, tc.wantCode)
			}
		})
	}
}

func TestInstallPrepareIsRateLimitedAsSensitive(t *testing.T) {
	fixture := newInstallFixture(t, nil)
	status, payload := fixture.send(t, http.MethodPost, "/v1/servers/survival/install/prepare", validInstallBody())
	if status != http.StatusOK {
		t.Fatalf("first prepare status = %d body = %s", status, payload)
	}
	status, payload = fixture.send(t, http.MethodPost, "/v1/servers/survival/install/prepare", validInstallBody())
	if status != http.StatusTooManyRequests || errorCode(t, payload) != "rate_limited" {
		t.Fatalf("second prepare status = %d body = %s", status, payload)
	}
}

func mustJSON(t *testing.T, value any) []byte {
	t.Helper()
	payload, err := json.Marshal(value)
	if err != nil {
		t.Fatal(err)
	}
	return payload
}
