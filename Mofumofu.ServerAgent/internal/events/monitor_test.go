// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package events

import (
	"strings"
	"testing"
	"time"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/server"
)

func TestMonitorCrashStateAndAutoRestartOrder(t *testing.T) {
	state := server.StateRunning
	autoRestart := true
	var events []protocol.ServerEvent
	monitor := NewMonitor(func() string { return state }, func() bool { return autoRestart },
		func(event protocol.ServerEvent) { events = append(events, event) },
		func() time.Time { return time.Unix(1_800_000_000, 0).UTC() })
	monitor.Observe() // baseline
	state = server.StateCrashed
	monitor.Observe()
	if len(events) != 2 || events[0].Type != protocol.EventTypeServerCrashed || events[1].Type != protocol.EventTypeServerStateChanged {
		t.Fatalf("events=%+v", events)
	}
	if events[1].Details["previous"] != "running" || events[1].Details["current"] != "offline" {
		t.Fatalf("state details=%v", events[1].Details)
	}
	events = nil
	state = server.StateStarting
	monitor.Observe()
	if len(events) != 2 || events[0].Type != protocol.EventTypeServerStateChanged || events[1].Type != protocol.EventTypeServerAutoRestarted {
		t.Fatalf("restart events=%+v", events)
	}
}

func TestMonitorIntentionalStopAndMissedOffline(t *testing.T) {
	state := server.StateRunning
	var events []protocol.ServerEvent
	monitor := NewMonitor(func() string { return state }, func() bool { return true },
		func(event protocol.ServerEvent) { events = append(events, event) }, time.Now)
	monitor.Observe()
	monitor.NoteIntentionalStop()
	state = server.StateStopped
	monitor.Observe()
	if len(events) != 1 || events[0].Type != protocol.EventTypeServerStateChanged {
		t.Fatalf("intentional stop events=%+v", events)
	}
	events = nil
	state = server.StateRunning
	monitor.Observe()
	state = server.StateCrashed
	state = server.StateRunning // The one-second poll can miss a short offline state.
	monitor.Observe()
	for _, event := range events {
		if strings.Contains(event.Message, "unexpectedly") {
			t.Fatalf("short offline interval produced crash: %+v", events)
		}
	}
}
