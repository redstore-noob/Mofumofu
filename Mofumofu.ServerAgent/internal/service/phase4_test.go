// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package service

import (
	"context"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/audit"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/backups"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/config"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/confirmation"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/events"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/files"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/operations"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/server"
)

func TestFileDeleteConfirmationAndReplay(t *testing.T) {
	application, _, root := newPhase4Service(t)
	ctx := context.Background()
	if _, err := application.WriteFile(ctx, audit.Actor{TokenID: "daily"}, "delete.txt", "content", "0"); err != nil {
		t.Fatal(err)
	}
	item, err := application.GetFile(ctx, "delete.txt")
	if err != nil {
		t.Fatal(err)
	}
	prepared, err := application.PrepareFileDelete(ctx, audit.Actor{TokenID: "daily"}, item.Path, item.Revision)
	if err != nil {
		t.Fatal(err)
	}
	operation, err := application.ExecuteConfirmation(ctx, audit.Actor{TokenID: "daily"}, prepared.ConfirmationID, func(string) bool { return true })
	if err != nil {
		t.Fatal(err)
	}
	waitForOperation(t, application, operation.ID, protocol.OperationStateSucceeded)
	if _, err := os.Stat(filepath.Join(root, "survival", "delete.txt")); !os.IsNotExist(err) {
		t.Fatalf("file remains: %v", err)
	}
	_, err = application.ExecuteConfirmation(ctx, audit.Actor{TokenID: "daily"}, prepared.ConfirmationID, func(string) bool { return true })
	if apiErr, ok := err.(*operations.Error); !ok || apiErr.Code != protocol.CodeConfirmationAlreadyUsed {
		t.Fatalf("replay error=%v", err)
	}
}

func TestPropertiesPrepareDefersValidationToOperation(t *testing.T) {
	application, _, root := newPhase4Service(t)
	ctx := context.Background()
	properties, err := application.Properties(ctx)
	if err != nil {
		t.Fatal(err)
	}
	prepared, err := application.PreparePropertiesPatch(ctx, audit.Actor{TokenID: "daily"}, properties.Revision, map[string]*string{"max-players": protocol.Ptr("invalid")})
	if err != nil {
		t.Fatalf("prepare should not validate values: %v", err)
	}
	operation, err := application.ExecuteConfirmation(ctx, audit.Actor{TokenID: "daily"}, prepared.ConfirmationID, func(string) bool { return true })
	if err != nil {
		t.Fatal(err)
	}
	waitForOperation(t, application, operation.ID, protocol.OperationStateFailed)
	payload, _ := os.ReadFile(filepath.Join(root, "survival", "server.properties"))
	if !strings.Contains(string(payload), "max-players=20") {
		t.Fatalf("properties changed: %s", payload)
	}
}

func TestBackupCreateRestoreAndControlConfirmation(t *testing.T) {
	application, fake, root := newPhase4Service(t)
	ctx := context.Background()
	path := filepath.Join(root, "survival", "world.txt")
	if err := os.WriteFile(path, []byte("original"), 0o600); err != nil {
		t.Fatal(err)
	}
	created, err := application.CreateBackup(ctx, audit.Actor{TokenID: "daily"}, "before-change", protocol.Ptr("manual"))
	if err != nil {
		t.Fatal(err)
	}
	if created.Operation.State == nil || *created.Operation.State != protocol.OperationStateSucceeded || created.Backup.SHA256 == "" {
		t.Fatalf("created=%+v", created)
	}
	if err := os.WriteFile(path, []byte("modified"), 0o600); err != nil {
		t.Fatal(err)
	}
	fake.SetState(server.StateRunning)
	if _, err := application.PrepareBackup(ctx, audit.Actor{TokenID: "daily"}, created.Backup.ID, true); err == nil {
		t.Fatal("restore prepared while running")
	}
	fake.SetState(server.StateStopped)
	prepared, err := application.PrepareBackup(ctx, audit.Actor{TokenID: "daily"}, created.Backup.ID, true)
	if err != nil {
		t.Fatal(err)
	}
	operation, err := application.ExecuteConfirmation(ctx, audit.Actor{TokenID: "daily"}, prepared.ConfirmationID, func(string) bool { return true })
	if err != nil {
		t.Fatal(err)
	}
	waitForOperation(t, application, operation.ID, protocol.OperationStateSucceeded)
	payload, _ := os.ReadFile(path)
	if string(payload) != "original" {
		t.Fatalf("restored=%q", payload)
	}

	prepared, err = application.PrepareControl(ctx, audit.Actor{TokenID: "daily"}, protocol.ControlActionStart)
	if err != nil {
		t.Fatal(err)
	}
	operation, err = application.ExecuteConfirmation(ctx, audit.Actor{TokenID: "daily"}, prepared.ConfirmationID, func(string) bool { return true })
	if err != nil {
		t.Fatal(err)
	}
	waitForOperation(t, application, operation.ID, protocol.OperationStateSucceeded)
	if fake.State() != server.StateStarting {
		t.Fatalf("state=%s", fake.State())
	}
}

func newPhase4Service(t *testing.T) (*Service, *fakeSupervisor, string) {
	t.Helper()
	root := t.TempDir()
	instance := filepath.Join(root, "survival")
	if err := os.Mkdir(instance, 0o750); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(instance, "server.json"), []byte(`{"name":"Survival","mcVersion":"1.21.4","maxPlayers":20}`), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(instance, "server.properties"), []byte("max-players=20\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	metadata, err := server.LoadMetadata(root, "survival")
	if err != nil {
		t.Fatal(err)
	}
	now := func() time.Time { return time.Now() }
	auditSink, err := audit.Open(filepath.Join(root, "audit.jsonl"))
	if err != nil {
		t.Fatal(err)
	}
	journal, err := events.OpenJournal(filepath.Join(root, "events.jsonl"), 128, now, nil)
	if err != nil {
		t.Fatal(err)
	}
	operationService := operations.New(journal, now)
	t.Cleanup(func() {
		_ = operationService.Stop(context.Background())
		_ = journal.Close(context.Background())
		_ = auditSink.Close()
	})
	cfg := config.Default()
	cfg.InstanceId = "survival"
	cfg.ServerRootDirectory = root
	fake := &fakeSupervisor{state: server.StateStopped}
	application := New(cfg, metadata, "test", now)
	application.SetSupervisor(fake)
	application.SetAudit(auditSink)
	application.SetOperations(operationService, confirmation.New(now))
	application.SetJournal(journal)
	fileService, _ := files.New(instance)
	application.SetFiles(fileService, files.NewPropertyService(fileService, now))
	application.SetBackups(backups.New(instance, root, metadata.Config.MCVersion, fake, now, nil))
	return application, fake, root
}

func waitForOperation(t *testing.T, application *Service, id, state string) {
	t.Helper()
	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		operation, _, ok := application.Operation(id)
		if ok && operation.State != nil && *operation.State == state {
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	operation, _, _ := application.Operation(id)
	t.Fatalf("operation=%+v, want %s", operation, state)
}
