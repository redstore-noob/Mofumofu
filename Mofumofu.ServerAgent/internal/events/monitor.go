// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

// Package events contains state monitoring and the durable cursor journal.
package events

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"sync"
	"time"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/server"
)

type Monitor struct {
	state           func() string
	autoRestart     func() bool
	publish         func(protocol.ServerEvent)
	now             func() time.Time
	mu              sync.Mutex
	previous        string
	hasPrevious     bool
	crashPending    bool
	intentionalStop bool
}

func NewMonitor(state func() string, autoRestart func() bool, publish func(protocol.ServerEvent), now func() time.Time) *Monitor {
	if now == nil {
		now = time.Now
	}
	return &Monitor{state: state, autoRestart: autoRestart, publish: publish, now: now}
}

func (m *Monitor) NoteIntentionalStop() {
	m.mu.Lock()
	m.intentionalStop = true
	m.mu.Unlock()
}

func (m *Monitor) Run(ctx context.Context) {
	m.Observe()
	ticker := time.NewTicker(time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			m.Observe()
		}
	}
}

// Observe performs one best-effort state comparison. Tests call it directly to
// avoid wall-clock sleeps.
func (m *Monitor) Observe() {
	current := protocolState(m.state())
	m.mu.Lock()
	previous := m.previous
	hadPrevious := m.hasPrevious
	m.previous = current
	m.hasPrevious = true
	if !hadPrevious {
		m.mu.Unlock()
		return
	}
	wasActive := active(previous)
	unexpectedStop := wasActive && current == protocol.ServerStateOffline && !m.intentionalStop
	var pending []protocol.ServerEvent
	if unexpectedStop {
		m.crashPending = true
		pending = append(pending, m.event(protocol.EventTypeServerCrashed, "The server process stopped unexpectedly.", nil))
	}
	if wasActive && current == protocol.ServerStateOffline {
		m.intentionalStop = false
	}
	if previous != current {
		pending = append(pending, m.event(protocol.EventTypeServerStateChanged,
			"Server state changed from "+previous+" to "+current+".", map[string]string{"previous": previous, "current": current}))
	}
	autoRestarted := m.crashPending && (current == protocol.ServerStateStarting || current == protocol.ServerStateRunning) && m.autoRestart()
	if autoRestarted {
		m.crashPending = false
		pending = append(pending, m.event(protocol.EventTypeServerAutoRestarted, "Core automatically restarted the server.", nil))
	}
	m.mu.Unlock()
	for _, event := range pending {
		m.publish(event)
	}
}

func (m *Monitor) event(eventType, message string, details map[string]string) protocol.ServerEvent {
	if details == nil {
		details = map[string]string{}
	}
	var random [16]byte
	_, _ = rand.Read(random[:])
	return protocol.ServerEvent{
		EventID: "evt_" + hex.EncodeToString(random[:]), Type: eventType,
		OccurredAt: protocol.NewTimestamp(m.now()), Message: message, Details: details,
	}
}

func protocolState(state string) string {
	switch state {
	case server.StateStarting:
		return protocol.ServerStateStarting
	case server.StateRunning:
		return protocol.ServerStateRunning
	case server.StateStopping:
		return protocol.ServerStateStopping
	default:
		return protocol.ServerStateOffline
	}
}

func active(state string) bool {
	return state == protocol.ServerStateStarting || state == protocol.ServerStateRunning || state == protocol.ServerStateStopping
}
