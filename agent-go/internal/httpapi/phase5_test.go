// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package httpapi

import (
	"context"
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

	"github.com/coder/websocket"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/config"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/confirmation"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/events"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/operations"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/players"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/probe"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/server"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/service"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/tokens"
)

type phase5Fixture struct {
	handler    http.Handler
	server     *httptest.Server
	journal    *events.Journal
	operations *operations.Service
	token      string
	now        time.Time
	secret     []byte
}

func newPhase5Fixture(t *testing.T) *phase5Fixture {
	t.Helper()
	now := time.Date(2026, 11, 22, 12, 34, 56, 0, time.UTC)
	const token = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"
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
	registryPath := writeRegistry(t, "survival", tokens.TokenRegistryToken{
		TokenID: "daily", Hash: tokens.TokenHash(token), CreatedAt: now.Add(-time.Hour), Scopes: []string{tokens.ScopeRead},
	})
	registry := tokens.NewTokenRegistry(registryPath, func() time.Time { return now })
	journal, err := events.OpenJournal(filepath.Join(root, "events.jsonl"), 64, func() time.Time { return now }, nil)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = journal.Close(context.Background()) })
	operationService := operations.New(journal, func() time.Time { return now })
	t.Cleanup(func() { _ = operationService.Stop(context.Background()) })
	cfg := config.Default()
	cfg.InstanceId = "survival"
	application := service.New(cfg, metadata, "test", func() time.Time { return now })
	application.SetJournal(journal)
	application.SetRegistry(registry)
	application.SetOperations(operationService, confirmation.New(func() time.Time { return now }))
	application.SetPlayers(players.New(instance, nil, func() time.Time { return now }), nil)
	application.SetEventPublisher(nil, true)
	security := &Security{InstanceID: "survival", Registry: registry, Limiter: tokens.NewRateLimiter(func() time.Time { return now })}
	api := New(application, security, slog.New(slog.NewTextHandler(io.Discard, nil)))
	httpServer := httptest.NewServer(api.Handler())
	t.Cleanup(httpServer.Close)
	secret, _ := probe.LinkSecret(strings.Repeat("ab", 32))
	return &phase5Fixture{handler: api.Handler(), server: httpServer, journal: journal, operations: operationService, token: token, now: now, secret: secret}
}

func TestWebSocketSnapshotReplayResetAndOperationFrames(t *testing.T) {
	fixture := newPhase5Fixture(t)
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	header := http.Header{"Authorization": []string{"Bearer " + fixture.token}}
	url := "ws" + strings.TrimPrefix(fixture.server.URL, "http") + "/v1/servers/survival/ws?apiVersion=1.0"
	connection, _, err := websocket.Dial(ctx, url, &websocket.DialOptions{HTTPHeader: header})
	if err != nil {
		t.Fatal(err)
	}
	defer connection.Close(websocket.StatusNormalClosure, "")
	frame := readWSFrame(t, ctx, connection)
	snapshot, ok := frame.(*protocol.SnapshotFrame)
	if !ok || snapshot.Cursor != "0" || len(snapshot.RecentConsole) != 0 {
		t.Fatalf("snapshot=%#v", frame)
	}
	if err := fixture.journal.AppendConsole(ctx, "hello"); err != nil {
		t.Fatal(err)
	}
	event := protocol.NewServerEvent()
	event.EventID = "evt-ws"
	event.Type = protocol.EventTypeServerStateChanged
	if err := fixture.journal.AppendEvent(ctx, event); err != nil {
		t.Fatal(err)
	}
	operation, err := fixture.operations.Start(ctx, "control.start", protocol.ScopeControl, func(context.Context) (operations.Completion, error) {
		return operations.Completion{Message: "started"}, nil
	})
	if err != nil {
		t.Fatal(err)
	}
	if got := readWSFrame(t, ctx, connection); frameCursorForTest(got) != "1" {
		t.Fatalf("console cursor=%s", frameCursorForTest(got))
	}
	if got := readWSFrame(t, ctx, connection); frameCursorForTest(got) != "2" {
		t.Fatalf("event cursor=%s", frameCursorForTest(got))
	}
	gotOperation := readWSFrame(t, ctx, connection)
	operationFrame, ok := gotOperation.(*protocol.OperationFrame)
	if !ok || operationFrame.Operation.ID != operation.ID {
		t.Fatalf("operation frame=%#v", gotOperation)
	}
	_ = connection.Close(websocket.StatusNormalClosure, "")

	url += "&cursor=1"
	connection, _, err = websocket.Dial(ctx, url, &websocket.DialOptions{HTTPHeader: header})
	if err != nil {
		t.Fatal(err)
	}
	defer connection.Close(websocket.StatusNormalClosure, "")
	if got := readWSFrame(t, ctx, connection); frameCursorForTest(got) != "2" {
		t.Fatalf("replay cursor=%s", frameCursorForTest(got))
	}
	_ = connection.Close(websocket.StatusNormalClosure, "")

	url = "ws" + strings.TrimPrefix(fixture.server.URL, "http") + "/v1/servers/survival/ws?apiVersion=1.0&cursor="
	connection, _, err = websocket.Dial(ctx, url, &websocket.DialOptions{HTTPHeader: header})
	if err != nil {
		t.Fatal(err)
	}
	defer connection.Close(websocket.StatusNormalClosure, "")
	reset := readWSFrame(t, ctx, connection)
	if _, ok := reset.(*protocol.ResetFrame); !ok {
		t.Fatalf("reset=%#v", reset)
	}
	if got := readWSFrame(t, ctx, connection); frameCursorForTest(got) != fixture.journal.LatestCursor() {
		t.Fatalf("snapshot after reset=%s", frameCursorForTest(got))
	}
}

func TestProbeCallbackSignatureDuplicateAndReplay(t *testing.T) {
	fixture := newPhase5Fixture(t)
	body := []byte(`{"apiVersion":"1.0","eventId":"evt-callback","type":"server.state_changed","occurredAt":"2026-11-22T12:34:56Z","message":"online","details":{"state":"running"}}`)
	post := func(instance, nonce, eventID string, remote string) (int, protocol.AgentAcknowledgementResponse) {
		t.Helper()
		timestamp := fixture.now.Format(time.RFC3339)
		canonical := probe.Canonical(http.MethodPost, "/internal/probe/events", "survival", timestamp, nonce, body)
		signature := probe.Signature(fixture.secret, canonical)
		request, _ := http.NewRequest(http.MethodPost, fixture.server.URL+"/internal/probe/events", strings.NewReader(string(body)))
		request.RemoteAddr = remote
		request.Header.Set("X-Mofumofu-Instance-Id", instance)
		request.Header.Set("X-Mofumofu-Timestamp", timestamp)
		request.Header.Set("X-Mofumofu-Nonce", nonce)
		request.Header.Set("X-Mofumofu-Signature", signature)
		response, err := http.DefaultClient.Do(request)
		if err != nil {
			t.Fatal(err)
		}
		payload, _ := io.ReadAll(response.Body)
		response.Body.Close()
		var ack protocol.AgentAcknowledgementResponse
		if response.StatusCode == http.StatusOK || response.StatusCode == http.StatusAccepted {
			if err := protocol.Unmarshal(payload, &ack); err != nil {
				t.Fatal(err)
			}
		}
		return response.StatusCode, ack
	}
	nonce := "0123456789abcdef0123456789abcdef"
	if status, ack := post("survival", nonce, "evt-callback", "127.0.0.1:1234"); status != http.StatusAccepted || ack.Duplicate {
		t.Fatalf("new event: %d %+v", status, ack)
	}
	if status, ack := post("survival", "1123456789abcdef0123456789abcdef", "evt-callback", "127.0.0.1:1234"); status != http.StatusOK || !ack.Duplicate {
		t.Fatalf("duplicate event: %d %+v", status, ack)
	}
	if status, _ := post("survival", nonce, "evt-other", "127.0.0.1:1234"); status != http.StatusUnauthorized {
		t.Fatalf("nonce replay: %d", status)
	}
	if status, _ := post("other", "2123456789abcdef0123456789abcdef", "evt-other", "127.0.0.1:1234"); status != http.StatusUnauthorized {
		t.Fatalf("instance mismatch: %d", status)
	}
	recorder := httptest.NewRecorder()
	request := httptest.NewRequest(http.MethodPost, "/internal/probe/events", strings.NewReader(string(body)))
	request.RemoteAddr = "192.0.2.1:1234"
	fixture.handler.ServeHTTP(recorder, request)
	if recorder.Code != http.StatusForbidden {
		t.Fatalf("non-loopback: %d", recorder.Code)
	}
}

func readWSFrame(t *testing.T, ctx context.Context, connection *websocket.Conn) protocol.WebSocketFrame {
	t.Helper()
	_, payload, err := connection.Read(ctx)
	if err != nil {
		t.Fatal(err)
	}
	var frame protocol.WebSocketFrame
	if err := protocol.Unmarshal(payload, &frame); err != nil {
		t.Fatal(err)
	}
	return frame
}

func frameCursorForTest(frame protocol.WebSocketFrame) string {
	payload, _ := json.Marshal(frame)
	var fields struct {
		Cursor string `json:"cursor"`
	}
	_ = json.Unmarshal(payload, &fields)
	return fields.Cursor
}
