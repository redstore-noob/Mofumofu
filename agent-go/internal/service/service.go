// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

// Package service is the shared application layer used by HTTP and, later, the
// local TUI. Transport code must not duplicate confirmation or audit behavior.
package service

import (
	"context"
	"sort"
	"time"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/config"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/server"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/tokens"
)

// Service owns the read-only Phase 1 application surface. Process, file,
// player, backup and event providers are added by later phases.
type Service struct {
	config   config.Config
	metadata *server.Metadata
	version  string
	now      func() time.Time

	capabilities []string
}

func New(cfg config.Config, metadata *server.Metadata, version string, now func() time.Time) *Service {
	if now == nil {
		now = time.Now
	}
	return &Service{
		config: cfg, metadata: metadata, version: version, now: now,
		capabilities: []string{protocol.CapabilityRead},
	}
}

func (s *Service) Capabilities() []string {
	return append([]string(nil), s.capabilities...)
}

func (s *Service) Handshake(authentication *tokens.AuthContext) protocol.HandshakeResponse {
	scopes := make([]string, 0, len(authentication.Scopes))
	for scope := range authentication.Scopes {
		scopes = append(scopes, scope)
	}
	sort.Strings(scopes)
	response := protocol.NewHandshakeResponse()
	response.InstanceID = s.config.InstanceId
	response.ServerName = s.metadata.Config.Name
	response.DeploymentMode = protocol.DeploymentModeAgent
	response.ServerImplementationVersion = s.version
	response.Capabilities = s.Capabilities()
	response.Scopes = scopes
	response.ServerTime = protocol.NewTimestamp(s.now())
	response.WebSocketPath = protocol.Ptr(protocol.WebSocketPath)
	response.TokenID = protocol.Ptr(authentication.TokenID)
	return response
}

func (s *Service) Status(ctx context.Context) protocol.StatusResponse {
	if err := ctx.Err(); err != nil {
		return protocol.NewStatusResponse()
	}
	response := protocol.NewStatusResponse()
	response.InstanceID = s.config.InstanceId
	response.State = protocol.Ptr(protocol.ServerStateOffline)
	response.OnlinePlayers = 0
	response.MaxPlayers = s.metadata.Config.MaxPlayers
	response.MinecraftVersion = protocol.Ptr(s.metadata.Config.MCVersion)
	if motd, ok := s.metadata.Motd(); ok {
		response.MOTD = protocol.Ptr(motd)
	}
	response.ObservedAt = protocol.NewTimestamp(s.now())
	return response
}
