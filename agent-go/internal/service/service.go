// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

// Package service is the shared application layer used by HTTP and, later, the
// local TUI. Transport code must not duplicate confirmation or audit behavior.
package service

import (
	"context"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
	"unicode"
	"unicode/utf16"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/audit"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/config"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/events"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/server"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/tokens"
)

// Supervisor is the process capability used by the service layer.
type Supervisor interface {
	Snapshot() server.Snapshot
	State() string
	RecentConsole(after int64) ([]server.ConsoleLine, int64)
	IsRunning() bool
	AutoRestartEnabled() bool
	Command(ctx context.Context, command string) error
	Start(ctx context.Context) error
	Stop(ctx context.Context, force bool) error
	Restart(ctx context.Context) error
	Kill(ctx context.Context) error
	NoteIntentionalStop()
	Shutdown(ctx context.Context, timeout time.Duration)
}

type Service struct {
	config   config.Config
	metadata *server.Metadata
	version  string
	now      func() time.Time

	supervisor Supervisor
	audit      *audit.Sink

	eventMu        sync.Mutex
	recentEvents   []protocol.ServerEvent
	eventPublisher func(protocol.ServerEvent)
	eventsReady    bool
}

func New(cfg config.Config, metadata *server.Metadata, version string, now func() time.Time) *Service {
	if now == nil {
		now = time.Now
	}
	return &Service{config: cfg, metadata: metadata, version: version, now: now}
}

func (s *Service) SetSupervisor(supervisor Supervisor) { s.supervisor = supervisor }
func (s *Service) SetAudit(sink *audit.Sink)           { s.audit = sink }

func (s *Service) SetEventPublisher(publisher func(protocol.ServerEvent), ready bool) {
	s.eventMu.Lock()
	s.eventPublisher = publisher
	s.eventsReady = ready
	s.eventMu.Unlock()
}

func (s *Service) StartMonitor(ctx context.Context) {
	if s.supervisor == nil {
		return
	}
	monitor := events.NewMonitor(s.supervisor.State,
		s.supervisor.AutoRestartEnabled, s.publishEvent, s.now)
	go monitor.Run(ctx)
}

func (s *Service) publishEvent(event protocol.ServerEvent) {
	s.eventMu.Lock()
	s.recentEvents = append(s.recentEvents, event)
	if len(s.recentEvents) > 1000 {
		s.recentEvents = append([]protocol.ServerEvent(nil), s.recentEvents[len(s.recentEvents)-1000:]...)
	}
	publisher := s.eventPublisher
	s.eventMu.Unlock()
	if publisher != nil {
		publisher(event)
	}
}

func (s *Service) RecentEvents() []protocol.ServerEvent {
	s.eventMu.Lock()
	defer s.eventMu.Unlock()
	return append([]protocol.ServerEvent(nil), s.recentEvents...)
}

func (s *Service) Capabilities() []string {
	capabilities := []string{protocol.CapabilityRead}
	if s.supervisor != nil {
		capabilities = append(capabilities, protocol.CapabilityCommand)
	}
	s.eventMu.Lock()
	eventsReady := s.eventsReady
	s.eventMu.Unlock()
	if eventsReady {
		capabilities = append(capabilities, protocol.CapabilityEvents)
	}
	return capabilities
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
	if s.supervisor == nil || ctx.Err() != nil {
		return response
	}
	snapshot := s.supervisor.Snapshot()
	response.State = protocol.Ptr(protocolState(snapshot.State))
	response.OnlinePlayers = snapshot.OnlinePlayers
	response.CPUPercent = snapshot.CPUPercent
	response.MemoryUsedBytes = snapshot.MemoryBytes
	return response
}

func (s *Service) Command(ctx context.Context, actor audit.Actor, command string) error {
	if !validCommand(command) {
		return server.InvalidCommand()
	}
	if s.supervisor == nil {
		return server.ServerOffline()
	}
	if s.audit == nil {
		return audit.ErrUnavailable
	}
	commandLength := len(utf16.Encode([]rune(command)))
	return s.audit.Execute(ctx, actor, "console.command",
		audit.Parameters{{Key: "commandLength", Value: strconv.Itoa(commandLength)}},
		func() error { return s.supervisor.Command(ctx, command) })
}

func (s *Service) NoteIntentionalStop() {
	if s.supervisor != nil {
		s.supervisor.NoteIntentionalStop()
	}
}

func (s *Service) Shutdown(ctx context.Context, timeout time.Duration) {
	if s.supervisor != nil {
		s.supervisor.Shutdown(ctx, timeout)
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
		// C# Core never exposes its internal crash inference as a StatusResponse.
		return protocol.ServerStateOffline
	}
}

func validCommand(command string) bool {
	if strings.TrimSpace(command) == "" || len(utf16.Encode([]rune(command))) > protocol.MaxCommandLength {
		return false
	}
	for _, r := range command {
		if unicode.IsControl(r) {
			return false
		}
	}
	return true
}
