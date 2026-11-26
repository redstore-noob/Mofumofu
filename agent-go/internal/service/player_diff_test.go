// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package service

import (
	"context"
	"os"
	"path/filepath"
	"sync"
	"testing"
	"time"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

type diffSupervisor struct {
	*fakeSupervisor
	mu    sync.Mutex
	names []string
	err   error
}

func (f *diffSupervisor) SetNames(names ...string) {
	f.mu.Lock()
	f.names = append([]string(nil), names...)
	f.mu.Unlock()
}
func (f *diffSupervisor) OnlineNamesDetailed(context.Context) ([]string, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([]string(nil), f.names...), f.err
}

func TestOptionalPlayerDiffEmitsJoinedAndLeft(t *testing.T) {
	application, fake, root := newPhase4Service(t)
	provider := &diffSupervisor{fakeSupervisor: fake}
	application.supervisor = provider
	application.config.PlayerDiffInterval = 10 * time.Millisecond
	if err := os.WriteFile(filepath.Join(root, "survival", "usercache.json"), []byte(`[{"name":"Alice","uuid":"AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA"}]`), 0o600); err != nil {
		t.Fatal(err)
	}
	events := make(chan protocol.ServerEvent, 16)
	application.SetEventPublisher(func(event protocol.ServerEvent) { events <- event }, true)
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	application.StartMonitor(ctx)
	time.Sleep(30 * time.Millisecond)
	provider.SetNames("Alice")
	waitForEventType(t, events, protocol.EventTypePlayerJoined, "Alice")
	provider.SetNames("Alice", "Bob")
	waitForEventType(t, events, protocol.EventTypePlayerJoined, "Bob")
	provider.SetNames("Bob")
	waitForEventType(t, events, protocol.EventTypePlayerLeft, "Alice")
	select {
	case event := <-events:
		t.Fatalf("unexpected repeated event: %+v", event)
	case <-time.After(50 * time.Millisecond):
	}
}

func waitForEventType(t *testing.T, events <-chan protocol.ServerEvent, eventType, name string) {
	t.Helper()
	deadline := time.After(3 * time.Second)
	for {
		select {
		case event := <-events:
			if event.Type == eventType && event.Details["playerName"] == name {
				if name == "Alice" && event.Details["uuid"] == "" {
					t.Fatal("usercache UUID was not attached")
				}
				return
			}
		case <-deadline:
			t.Fatalf("missing %s for %s", eventType, name)
		}
	}
}
