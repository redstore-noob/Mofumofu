// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package service

import (
	"context"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/audit"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/config"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/server"
)

type fakeSupervisor struct {
	mu      sync.Mutex
	state   string
	command string
	fail    bool
}

func (f *fakeSupervisor) SetState(state string) {
	f.mu.Lock()
	f.state = state
	f.mu.Unlock()
}
func (f *fakeSupervisor) LastCommand() string {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.command
}
func (f *fakeSupervisor) Snapshot() server.Snapshot {
	cpu := 3.2
	memory := int64(512 << 20)
	return server.Snapshot{State: f.State(), CPUPercent: &cpu, MemoryBytes: &memory}
}
func (f *fakeSupervisor) State() string {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.state
}
func (f *fakeSupervisor) RecentConsole(int64) ([]server.ConsoleLine, int64) { return nil, 0 }
func (f *fakeSupervisor) IsRunning() bool {
	state := f.State()
	return state == server.StateRunning || state == server.StateStarting || state == server.StateStopping
}
func (f *fakeSupervisor) AutoRestartEnabled() bool { return true }
func (f *fakeSupervisor) Command(_ context.Context, command string) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.command = command
	if f.fail {
		return server.ServerOffline()
	}
	return nil
}
func (f *fakeSupervisor) OnlineNames(context.Context) []string { return nil }
func (f *fakeSupervisor) Start(context.Context) error          { f.SetState(server.StateStarting); return nil }
func (f *fakeSupervisor) Stop(context.Context, bool) error {
	f.SetState(server.StateStopped)
	return nil
}
func (f *fakeSupervisor) Restart(context.Context) error           { return nil }
func (f *fakeSupervisor) Kill(context.Context) error              { return f.Stop(context.Background(), true) }
func (f *fakeSupervisor) NoteIntentionalStop()                    {}
func (f *fakeSupervisor) Shutdown(context.Context, time.Duration) {}

func TestServiceStatusCapabilitiesAndAuditedCommand(t *testing.T) {
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
	cfg := config.Default()
	cfg.InstanceId = "survival"
	fake := &fakeSupervisor{state: server.StateRunning}
	auditPath := filepath.Join(t.TempDir(), "audit.jsonl")
	sink, err := audit.Open(auditPath)
	if err != nil {
		t.Fatal(err)
	}
	defer sink.Close()
	application := New(cfg, metadata, "test", time.Now)
	application.SetSupervisor(fake)
	application.SetAudit(sink)
	status := application.Status(context.Background())
	if status.State == nil || *status.State != protocol.ServerStateRunning || status.CPUPercent == nil || *status.CPUPercent != 3.2 {
		t.Fatalf("status=%+v", status)
	}
	capabilities := strings.Join(application.Capabilities(), ",")
	if !strings.Contains(capabilities, protocol.CapabilityCommand) || strings.Contains(capabilities, protocol.CapabilityConsole) {
		t.Fatalf("capabilities=%s", capabilities)
	}
	if err := application.Command(context.Background(), audit.Actor{TokenID: "daily"}, "list"); err != nil {
		t.Fatal(err)
	}
	if fake.LastCommand() != "list" {
		t.Fatalf("command=%q", fake.LastCommand())
	}
	payload, _ := os.ReadFile(auditPath)
	if !strings.Contains(string(payload), `"action":"console.command"`) ||
		!strings.Contains(string(payload), `"result":"succeeded"`) ||
		strings.Contains(string(payload), `"command":"list"`) {
		t.Fatalf("audit=%s", payload)
	}
}

func TestCommandValidationUsesUTF16Length(t *testing.T) {
	if !validCommand(strings.Repeat("😀", 128)) {
		t.Fatal("256 UTF-16 code units were rejected")
	}
	if validCommand(strings.Repeat("😀", 129)) {
		t.Fatal("258 UTF-16 code units were accepted")
	}
	if validCommand("list\n") || validCommand("   ") {
		t.Fatal("blank/control command was accepted")
	}
}
