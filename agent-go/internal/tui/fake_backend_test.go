// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package tui

import (
	"context"
	"fmt"
	"path"
	"sort"
	"sync"
	"time"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/audit"
	fileutil "github.com/redstore-noob/Mofumofu/agent-go/internal/files"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/service"
)

type fakeBackend struct {
	mu sync.Mutex

	status      protocol.StatusResponse
	console     []protocol.ConsoleEntry
	events      []protocol.ServerEvent
	online      protocol.OnlinePlayersResponse
	playerLists map[string]protocol.PlayerListResponse
	operations  []protocol.OperationInfo
	backups     []protocol.BackupMetadata
	files       map[string]protocol.FileListResponse
	properties  protocol.ServerPropertiesResponse
	agent       service.AgentInfo
	uptime      *int64

	statusErr  error
	prepareErr error
	executeErr error

	prepareCalls int
	executeCalls int
	createCalls  int
	playerCalls  int
	lastActor    audit.Actor
	lastAction   string
	lastTarget   string
	lastRevision string
	lastRestore  bool
	lastChanges  map[string]*string
	lastLabel    string
	lastPlayer   string
	lastKind     string
	lastReason   string
	scopeAllowed bool
}

func newFakeBackend() *fakeBackend {
	now := protocol.NewTimestamp(time.Date(2026, time.November, 26, 12, 30, 0, 0, time.UTC))
	uptime := int64(3661)
	cpu := 12.5
	memory := int64(1024 * 1024 * 1024)
	maximumMemory := int64(2 * 1024 * 1024 * 1024)
	version := "1.21.4"
	motd := "Mofumofu test server"
	state := protocol.ServerStateRunning
	status := protocol.NewStatusResponse()
	status.InstanceID = "survival"
	status.State = &state
	status.OnlinePlayers = 2
	status.MaxPlayers = 20
	status.CPUPercent = &cpu
	status.MemoryUsedBytes = &memory
	status.MemoryMaxBytes = &maximumMemory
	status.MinecraftVersion = &version
	status.MOTD = &motd
	status.ObservedAt = now

	aliceUUID, _ := protocol.ParseUUID("11111111-1111-3111-8111-111111111111")
	bobUUID, _ := protocol.ParseUUID("22222222-2222-3222-8222-222222222222")
	alice := protocol.PlayerInfo{Name: "Alice", UUID: &aliceUUID, GameMode: testPointer("creative"), PingMilliseconds: testPointer(32), OnlineSeconds: testPointer(int64(601))}
	bob := protocol.PlayerInfo{Name: "Bob", UUID: &bobUUID, GameMode: testPointer("survival"), PingMilliseconds: testPointer(48), OnlineSeconds: testPointer(int64(202))}
	level := 4
	bypass := false
	reason := "griefing"
	revision := "pl_fixed_revision"
	fileRevision := "r_fixed_file"
	directoryRevision := "d_fixed_directory"
	operationState := protocol.OperationStateRunning
	progress := 40.0
	operationMessage := "Creating archive"

	return &fakeBackend{
		status: status,
		console: []protocol.ConsoleEntry{
			{Sequence: 1, Timestamp: now, Level: "INFO", Source: "minecraft", Message: "Starting Mofumofu server"},
			{Sequence: 2, Timestamp: now, Level: "WARN", Source: "minecraft", Message: "\x1b[33m§eWatchdog§r: slow tick"},
			{Sequence: 3, Timestamp: now, Level: "ERROR", Source: "minecraft", Message: "Backup failed"},
			{Sequence: 4, Timestamp: now, Level: "CHAT", Source: "minecraft", Message: "<Alice> hello"},
			{Sequence: 5, Timestamp: now, Level: "INFO", Source: "minecraft", Message: "Players joined: Alice, Bob"},
		},
		events: []protocol.ServerEvent{{EventID: "evt_fixed", Type: protocol.EventTypeServerStateChanged, OccurredAt: now, Message: "Server state changed", Details: map[string]string{"previous": "starting", "current": "running"}}},
		online: protocol.OnlinePlayersResponse{OnlineCount: 2, MaxPlayers: 20, ObservedAt: now, Players: []protocol.PlayerInfo{alice, bob}},
		playerLists: map[string]protocol.PlayerListResponse{
			protocol.PlayerListWhitelist: {Kind: protocol.PlayerListWhitelist, Revision: revision, Entries: []protocol.PlayerListEntry{{Name: "Alice"}, {Name: "Charlie"}}},
			protocol.PlayerListOps:       {Kind: protocol.PlayerListOps, Revision: revision, Entries: []protocol.PlayerListEntry{{Name: "Alice", OpLevel: &level}}},
			protocol.PlayerListBans:      {Kind: protocol.PlayerListBans, Revision: revision, Entries: []protocol.PlayerListEntry{{Name: "Mallory", Reason: &reason, BypassesPlayerLimit: &bypass}}},
		},
		operations: []protocol.OperationInfo{{ID: "op_1234567890abcdef", Kind: "backup.create", State: &operationState, CreatedAt: now, ProgressPercent: &progress, Message: &operationMessage}},
		backups:    []protocol.BackupMetadata{{ID: "bk_1234567890abcdef", Label: "nightly", CreatedAt: now, SizeBytes: 1536 * 1024, SHA256: "abcdef0123456789"}},
		files: map[string]protocol.FileListResponse{
			"":       {Path: "", Entries: []protocol.FileEntry{{Path: "config", IsDirectory: true, Revision: &directoryRevision, Writable: true}, {Path: "readme.txt", SizeBytes: testPointer(int64(128)), Revision: &fileRevision, Writable: true}}},
			"config": {Path: "config", Entries: []protocol.FileEntry{{Path: "config/rules.txt", SizeBytes: testPointer(int64(256)), Revision: &fileRevision, Writable: true}}},
		},
		properties:   protocol.ServerPropertiesResponse{Revision: "props_fixed", RestartRequired: true, Properties: map[string]string{"max-players": "20", "motd": "Mofumofu test server"}},
		agent:        service.AgentInfo{InstanceID: "survival", Bind: "127.0.0.1:17870", Mode: "managed", Probe: true, TokenCount: 2},
		uptime:       &uptime,
		scopeAllowed: true,
	}
}

func testPointer[T any](value T) *T { return &value }

func (f *fakeBackend) Capabilities() []string {
	return []string{protocol.CapabilityRead, protocol.CapabilityPlayers, protocol.CapabilityFiles, protocol.CapabilityBackup, protocol.CapabilityControl, protocol.CapabilityCommand, protocol.CapabilityConsole, protocol.CapabilityEvents, protocol.CapabilityProperties, protocol.CapabilityTokens}
}

func (f *fakeBackend) Status(context.Context) (protocol.StatusResponse, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.status, f.statusErr
}

func (f *fakeBackend) ConsoleEntries(_ context.Context, after int64, maximum int) ([]protocol.ConsoleEntry, string, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	entries := make([]protocol.ConsoleEntry, 0, len(f.console))
	for _, entry := range f.console {
		if entry.Sequence > after {
			entries = append(entries, entry)
		}
	}
	if len(entries) > maximum {
		entries = entries[len(entries)-maximum:]
	}
	return append([]protocol.ConsoleEntry(nil), entries...), "42", nil
}

func (f *fakeBackend) RecentEvents() []protocol.ServerEvent {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([]protocol.ServerEvent(nil), f.events...)
}

func (f *fakeBackend) OnlinePlayers(context.Context, audit.Actor) (protocol.OnlinePlayersResponse, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.online, nil
}

func (f *fakeBackend) PlayerList(_ context.Context, kind string) (protocol.PlayerListResponse, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	response, exists := f.playerLists[kind]
	if !exists {
		return protocol.PlayerListResponse{}, fmt.Errorf("unknown player list %q", kind)
	}
	return response, nil
}

func (f *fakeBackend) ActiveOperations() []protocol.OperationInfo {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([]protocol.OperationInfo(nil), f.operations...)
}

func (f *fakeBackend) Backups(context.Context) (protocol.BackupListResponse, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	return protocol.BackupListResponse{Backups: append([]protocol.BackupMetadata(nil), f.backups...)}, nil
}

func (f *fakeBackend) Files(_ context.Context, path string) (protocol.FileListResponse, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	response, exists := f.files[normalizeServerPath(path)]
	if !exists {
		return protocol.FileListResponse{}, fmt.Errorf("file path not found: %s", path)
	}
	return response, nil
}

func (f *fakeBackend) GetFile(_ context.Context, filePath string) (fileutil.Mutation, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	filePath = normalizeServerPath(filePath)
	parent := path.Dir(filePath)
	if parent == "." {
		parent = ""
	}
	response, exists := f.files[parent]
	if !exists {
		return fileutil.Mutation{}, fmt.Errorf("file path not found: %s", filePath)
	}
	for _, entry := range response.Entries {
		if entry.Path == filePath {
			revision := "0"
			if entry.Revision != nil {
				revision = *entry.Revision
			}
			return fileutil.Mutation{Path: entry.Path, Revision: revision, IsDirectory: entry.IsDirectory}, nil
		}
	}
	return fileutil.Mutation{}, fmt.Errorf("file path not found: %s", filePath)
}

func (f *fakeBackend) Properties(context.Context) (protocol.ServerPropertiesResponse, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	properties := make(map[string]string, len(f.properties.Properties))
	for key, value := range f.properties.Properties {
		properties[key] = value
	}
	response := f.properties
	response.Properties = properties
	return response, nil
}

func (f *fakeBackend) PrepareControl(_ context.Context, actor audit.Actor, action string) (protocol.ConfirmationPrepareResponse, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.prepareCalls++
	f.lastActor = actor
	f.lastAction = action
	if f.prepareErr != nil {
		return protocol.ConfirmationPrepareResponse{}, f.prepareErr
	}
	return fixedConfirmation("control."+action, "Confirm server "+action), nil
}

func (f *fakeBackend) ExecuteConfirmation(_ context.Context, actor audit.Actor, confirmationID string, hasScope func(string) bool) (protocol.OperationInfo, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.executeCalls++
	f.lastActor = actor
	f.lastTarget = confirmationID
	f.scopeAllowed = hasScope("control")
	if f.executeErr != nil {
		return protocol.OperationInfo{}, f.executeErr
	}
	state := protocol.OperationStateQueued
	return protocol.OperationInfo{ID: "op_1234567890abcdef", Kind: "control.stop", State: &state}, nil
}

func (f *fakeBackend) CreateBackup(_ context.Context, actor audit.Actor, label string, _ *string) (protocol.BackupCreateResponse, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.createCalls++
	f.lastActor = actor
	f.lastLabel = label
	state := protocol.OperationStateSucceeded
	backup := f.backups[0]
	backup.Label = label
	return protocol.BackupCreateResponse{Backup: backup, Operation: protocol.OperationInfo{ID: "op_backup", Kind: "backup.create", State: &state}}, nil
}

func (f *fakeBackend) PrepareBackup(_ context.Context, actor audit.Actor, backupID string, restore bool) (protocol.ConfirmationPrepareResponse, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.prepareCalls++
	f.lastActor = actor
	f.lastAction = "backup"
	f.lastTarget = backupID
	f.lastRestore = restore
	if f.prepareErr != nil {
		return protocol.ConfirmationPrepareResponse{}, f.prepareErr
	}
	if restore {
		return fixedConfirmation("backup.restore", "Restore backup "+backupID+" while offline"), nil
	}
	return fixedConfirmation("backup.delete", "Delete backup "+backupID), nil
}

func (f *fakeBackend) PrepareFileDelete(_ context.Context, actor audit.Actor, path, revision string) (protocol.ConfirmationPrepareResponse, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.prepareCalls++
	f.lastActor = actor
	f.lastAction = "file.delete"
	f.lastTarget = path
	f.lastRevision = revision
	if f.prepareErr != nil {
		return protocol.ConfirmationPrepareResponse{}, f.prepareErr
	}
	return fixedConfirmation("file.delete", "Delete "+path), nil
}

func (f *fakeBackend) PreparePropertiesPatch(_ context.Context, actor audit.Actor, revision string, changes map[string]*string) (protocol.ConfirmationPrepareResponse, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.prepareCalls++
	f.lastActor = actor
	f.lastAction = "properties.patch"
	f.lastRevision = revision
	f.lastChanges = make(map[string]*string, len(changes))
	keys := make([]string, 0, len(changes))
	for key, value := range changes {
		keys = append(keys, key)
		if value == nil {
			f.lastChanges[key] = nil
		} else {
			f.lastChanges[key] = testPointer(*value)
		}
	}
	sort.Strings(keys)
	f.lastTarget = keys[0]
	if f.prepareErr != nil {
		return protocol.ConfirmationPrepareResponse{}, f.prepareErr
	}
	return fixedConfirmation("properties.patch", "Patch server.properties; restart required"), nil
}

func (f *fakeBackend) AddPlayer(_ context.Context, actor audit.Actor, kind string, request protocol.PlayerAddRequest) (protocol.PlayerMutationResponse, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.playerCalls++
	f.lastActor = actor
	f.lastKind = kind
	f.lastPlayer = request.PlayerName
	f.lastAction = "add"
	if request.Reason != nil {
		f.lastReason = *request.Reason
	}
	return protocol.PlayerMutationResponse{Kind: kind, Entry: protocol.PlayerListEntry{Name: request.PlayerName}}, nil
}

func (f *fakeBackend) RemovePlayer(_ context.Context, actor audit.Actor, kind string, request protocol.PlayerRemoveRequest) (protocol.PlayerMutationResponse, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.playerCalls++
	f.lastActor = actor
	f.lastKind = kind
	f.lastPlayer = request.PlayerName
	f.lastAction = "remove"
	return protocol.PlayerMutationResponse{Kind: kind, Entry: protocol.PlayerListEntry{Name: request.PlayerName}, Removed: true}, nil
}

func (f *fakeBackend) AgentInfo() service.AgentInfo {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.agent
}

func (f *fakeBackend) UptimeSeconds() *int64 {
	f.mu.Lock()
	defer f.mu.Unlock()
	return testPointer(*f.uptime)
}

func fixedConfirmation(action, summary string) protocol.ConfirmationPrepareResponse {
	now := protocol.NewTimestamp(time.Date(2026, time.November, 26, 12, 30, 0, 0, time.UTC))
	return protocol.ConfirmationPrepareResponse{ConfirmationID: "cfm_" + action, Action: action, Summary: summary, CreatedAt: now, ExpiresAt: protocol.NewTimestamp(now.Add(time.Minute)), ExpiresInSeconds: testPointer(60)}
}
