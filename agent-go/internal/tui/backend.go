// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

// Package tui implements the local Bubble Tea interface for a Mofumofu Agent.
package tui

import (
	"context"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/audit"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/files"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/service"
)

// Backend is the shared application service used by the TUI. Implementations
// must preserve the service's confirmation, operation, and audit semantics.
type Backend interface {
	Capabilities() []string
	Status(context.Context) (protocol.StatusResponse, error)
	ConsoleEntries(context.Context, int64, int) ([]protocol.ConsoleEntry, string, error)
	RecentEvents() []protocol.ServerEvent
	OnlinePlayers(context.Context, audit.Actor) (protocol.OnlinePlayersResponse, error)
	PlayerList(context.Context, string) (protocol.PlayerListResponse, error)
	ActiveOperations() []protocol.OperationInfo
	Backups(context.Context) (protocol.BackupListResponse, error)
	Files(context.Context, string) (protocol.FileListResponse, error)
	GetFile(context.Context, string) (files.Mutation, error)
	Properties(context.Context) (protocol.ServerPropertiesResponse, error)
	PrepareControl(context.Context, audit.Actor, string) (protocol.ConfirmationPrepareResponse, error)
	ExecuteConfirmation(context.Context, audit.Actor, string, func(string) bool) (protocol.OperationInfo, error)
	CreateBackup(context.Context, audit.Actor, string, *string) (protocol.BackupCreateResponse, error)
	PrepareBackup(context.Context, audit.Actor, string, bool) (protocol.ConfirmationPrepareResponse, error)
	PrepareFileDelete(context.Context, audit.Actor, string, string) (protocol.ConfirmationPrepareResponse, error)
	PreparePropertiesPatch(context.Context, audit.Actor, string, map[string]*string) (protocol.ConfirmationPrepareResponse, error)
	AddPlayer(context.Context, audit.Actor, string, protocol.PlayerAddRequest) (protocol.PlayerMutationResponse, error)
	RemovePlayer(context.Context, audit.Actor, string, protocol.PlayerRemoveRequest) (protocol.PlayerMutationResponse, error)
	AgentInfo() service.AgentInfo
	UptimeSeconds() *int64
}

// serviceAdapter bridges the service's infallible Status snapshot to the
// transport-neutral Backend signature without copying any application logic.
type serviceAdapter struct {
	*service.Service
}

func (adapter serviceAdapter) Status(ctx context.Context) (protocol.StatusResponse, error) {
	return adapter.Service.Status(ctx), nil
}

// WrapService exposes a shared service.Service through Backend. The adapter
// only converts Status's return shape; every mutation still runs in Service.
func WrapService(backend *service.Service) Backend {
	if backend == nil {
		return nil
	}
	return serviceAdapter{Service: backend}
}

var _ Backend = serviceAdapter{}

const localTokenID = "local-tui"

func localActor() audit.Actor {
	return audit.Actor{TokenID: localTokenID}
}

func localHasScope(string) bool { return true }
