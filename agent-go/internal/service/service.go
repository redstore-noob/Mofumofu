// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

// Package service is the shared application layer used by HTTP and, later, the
// local TUI. Transport code must not duplicate confirmation or audit behavior.
package service

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
	"unicode"
	"unicode/utf16"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/audit"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/backups"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/config"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/confirmation"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/events"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/files"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/operations"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/players"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/probe"
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
	OnlineNames(ctx context.Context) []string
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

	supervisor    Supervisor
	audit         *audit.Sink
	files         *files.Service
	properties    *files.PropertyService
	operations    *operations.Service
	confirmations *confirmation.Service
	backups       *backups.Manager
	players       *players.Service
	probe         *probe.Client
	journal       *events.Journal
	registry      *tokens.TokenRegistry

	probeMu       sync.Mutex
	probeNonces   map[string]time.Time
	probeEventIDs map[string]struct{}

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
func (s *Service) SetFiles(service *files.Service, properties *files.PropertyService) {
	s.files = service
	s.properties = properties
}

func (s *Service) SetOperations(operations *operations.Service, confirmations *confirmation.Service) {
	s.operations = operations
	s.confirmations = confirmations
}

func (s *Service) SetBackups(manager *backups.Manager) { s.backups = manager }

func (s *Service) SetPlayers(players *players.Service, probe *probe.Client) {
	s.players = players
	s.probe = probe
}

func (s *Service) SetJournal(journal *events.Journal) { s.journal = journal }

func (s *Service) SetRegistry(registry *tokens.TokenRegistry) { s.registry = registry }

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
	if s.config.PlayerDiffInterval > 0 {
		go s.runPlayerDiff(ctx)
	}
}

type diffPlayer struct {
	joinedAt time.Time
	uuid     string
}

func (s *Service) runPlayerDiff(ctx context.Context) {
	provider, ok := s.supervisor.(interface {
		OnlineNamesDetailed(context.Context) ([]string, error)
	})
	if !ok {
		return
	}
	ticker := time.NewTicker(s.config.PlayerDiffInterval)
	defer ticker.Stop()
	previous := make(map[string]diffPlayer)
	initialized := false
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			names, err := provider.OnlineNamesDetailed(ctx)
			if err != nil {
				continue
			}
			uuids := s.usercacheUUIDs()
			current := make(map[string]diffPlayer, len(names))
			for _, name := range names {
				uuid := uuids[name]
				joinedAt := s.now()
				if old, exists := previous[name]; exists {
					joinedAt = old.joinedAt
				}
				current[name] = diffPlayer{joinedAt: joinedAt, uuid: uuid}
			}
			if initialized {
				for name, old := range previous {
					if _, exists := current[name]; !exists {
						details := map[string]string{"playerName": name}
						if old.uuid != "" {
							details["uuid"] = old.uuid
						}
						s.publishEvent(protocol.ServerEvent{EventID: newEventID(), Type: protocol.EventTypePlayerLeft, OccurredAt: protocol.NewTimestamp(s.now()), Message: name + " left", Details: details})
					}
				}
				for name, player := range current {
					if _, existed := previous[name]; !existed {
						details := map[string]string{"playerName": name}
						if player.uuid != "" {
							details["uuid"] = player.uuid
						}
						s.publishEvent(protocol.ServerEvent{EventID: newEventID(), Type: protocol.EventTypePlayerJoined, OccurredAt: protocol.NewTimestamp(s.now()), Message: name + " joined", Details: details})
					}
				}
			}
			previous = current
			initialized = true
		}
	}
}

func (s *Service) usercacheUUIDs() map[string]string {
	result := make(map[string]string)
	payload, err := os.ReadFile(filepath.Join(s.metadata.Directory, "usercache.json"))
	if err != nil {
		return result
	}
	var entries []struct {
		UUID string `json:"uuid"`
		Name string `json:"name"`
	}
	if json.Unmarshal(payload, &entries) != nil {
		return result
	}
	for _, entry := range entries {
		if entry.Name != "" && entry.UUID != "" {
			result[entry.Name] = strings.ToLower(entry.UUID)
		}
	}
	return result
}

func newEventID() string {
	var random [16]byte
	_, _ = rand.Read(random[:])
	return "evt_" + hex.EncodeToString(random[:])
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

type WebSnapshot struct {
	Status           protocol.StatusResponse
	Players          []protocol.PlayerInfo
	RecentConsole    []protocol.ConsoleEntry
	ActiveOperations []protocol.OperationInfo
}

func (s *Service) WebSnapshot(ctx context.Context, actor audit.Actor) (WebSnapshot, error) {
	players, err := s.OnlinePlayers(ctx, actor)
	if err != nil {
		return WebSnapshot{}, err
	}
	recent := make([]protocol.ConsoleEntry, 0)
	if s.journal != nil {
		recent, err = s.journal.RecentConsole(ctx, 100)
		if err != nil {
			return WebSnapshot{}, err
		}
	} else if s.supervisor != nil {
		lines, _ := s.supervisor.RecentConsole(-1)
		if len(lines) > 100 {
			lines = lines[len(lines)-100:]
		}
		for _, line := range lines {
			recent = append(recent, protocol.ConsoleEntry{Sequence: line.Sequence, Timestamp: protocol.NewTimestamp(line.Timestamp), Level: "INFO", Source: "minecraft", Message: line.Message})
		}
	}
	active := make([]protocol.OperationInfo, 0)
	if s.operations != nil {
		active = s.operations.Active()
	}
	return WebSnapshot{Status: s.Status(ctx), Players: players.Players, RecentConsole: recent, ActiveOperations: active}, nil
}

func (s *Service) ConsoleEntries(ctx context.Context, after int64, maximum int) ([]protocol.ConsoleEntry, string, error) {
	if s.journal != nil {
		return s.journal.ConsoleAfter(ctx, after, maximum)
	}
	if s.supervisor == nil {
		return []protocol.ConsoleEntry{}, "0", nil
	}
	lines, cursor := s.supervisor.RecentConsole(after)
	entries := make([]protocol.ConsoleEntry, 0, len(lines))
	for _, line := range lines {
		entries = append(entries, protocol.ConsoleEntry{Sequence: line.Sequence, Timestamp: protocol.NewTimestamp(line.Timestamp), Level: "INFO", Source: "minecraft", Message: line.Message})
	}
	if len(entries) > maximum {
		entries = entries[len(entries)-maximum:]
	}
	return entries, strconv.FormatInt(cursor, 10), nil
}

func (s *Service) ActiveOperations() []protocol.OperationInfo {
	if s.operations == nil {
		return []protocol.OperationInfo{}
	}
	return s.operations.Active()
}

type AgentInfo struct {
	InstanceID string
	Bind       string
	Mode       string
	Probe      bool
	TokenCount int
}

func (s *Service) AgentInfo() AgentInfo {
	info := AgentInfo{InstanceID: s.config.InstanceId, Bind: s.config.BindUrl, Mode: string(s.config.Mode), Probe: s.probe != nil}
	if s.registry != nil {
		if snapshot, err := s.registry.LoadSnapshot(s.config.InstanceId); err == nil {
			info.TokenCount = len(snapshot.Tokens)
		}
	}
	return info
}

func (s *Service) UptimeSeconds() *int64 {
	if s.supervisor == nil {
		return nil
	}
	snapshot := s.supervisor.Snapshot()
	if snapshot.StartedAt.IsZero() || !activeServiceState(snapshot.State) {
		return nil
	}
	seconds := int64(s.now().Sub(snapshot.StartedAt).Seconds())
	if seconds < 0 {
		seconds = 0
	}
	return &seconds
}

func activeServiceState(state string) bool {
	return state == server.StateStarting || state == server.StateRunning || state == server.StateStopping
}

func (s *Service) OpenSubscription(ctx context.Context, cursor *string, snapshotFactory func(string) protocol.SnapshotFrame) (*events.Subscription, error) {
	if s.journal == nil {
		return nil, capabilityNotReady()
	}
	return s.journal.Open(ctx, cursor, snapshotFactory)
}

func (s *Service) Capabilities() []string {
	capabilities := []string{protocol.CapabilityRead}
	if s.players != nil {
		capabilities = append(capabilities, protocol.CapabilityPlayers)
	}
	if s.files != nil {
		capabilities = append(capabilities, protocol.CapabilityFiles)
	}
	if s.backups != nil {
		capabilities = append(capabilities, protocol.CapabilityBackup)
	}
	if s.supervisor != nil {
		if controller, ok := s.supervisor.(interface{ SupportsControl() bool }); !ok || controller.SupportsControl() {
			capabilities = append(capabilities, protocol.CapabilityControl)
		}
		if commander, ok := s.supervisor.(interface{ SupportsCommand() bool }); !ok || commander.SupportsCommand() {
			capabilities = append(capabilities, protocol.CapabilityCommand)
		}
	}
	s.eventMu.Lock()
	eventsReady := s.eventsReady
	s.eventMu.Unlock()
	if eventsReady {
		capabilities = append(capabilities, protocol.CapabilityConsole, protocol.CapabilityEvents)
	}
	if s.properties != nil {
		capabilities = append(capabilities, protocol.CapabilityProperties)
	}
	if s.probe != nil {
		capabilities = append(capabilities, protocol.CapabilityTokens)
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
	if provider, ok := s.supervisor.(interface {
		TPS(context.Context) (*float64, error)
	}); ok && snapshot.State == server.StateRunning {
		tpsCtx, cancel := context.WithTimeout(ctx, 2*time.Second)
		tps, _ := provider.TPS(tpsCtx)
		cancel()
		response.TPS = tps
	}
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

type ProbeEventHeaders struct {
	InstanceID string
	Timestamp  string
	Nonce      string
	Signature  string
}

func (s *Service) AcceptProbeEvent(ctx context.Context, headers ProbeEventHeaders, body []byte) (protocol.AgentAcknowledgementResponse, bool, error) {
	ack := protocol.NewAgentAcknowledgementResponse()
	ack.Accepted = true
	if headers.InstanceID != s.config.InstanceId {
		return ack, false, &operations.Error{Status: 401, Code: protocol.CodeUnauthorized, Message: "Invalid Probe callback signature."}
	}
	if s.registry == nil || s.journal == nil {
		return ack, false, &operations.Error{Status: 401, Code: protocol.CodeUnauthorized, Message: "Invalid Probe callback signature."}
	}
	snapshot, err := s.registry.LoadSnapshot(s.config.InstanceId)
	if err != nil {
		return ack, false, &operations.Error{Status: 401, Code: protocol.CodeUnauthorized, Message: "Invalid Probe callback signature."}
	}
	secret := snapshot.LinkSecretBytes()
	timestamp, err := time.Parse(time.RFC3339Nano, headers.Timestamp)
	now := s.now()
	if err != nil || timestamp.Before(now.Add(-5*time.Minute)) || timestamp.After(now.Add(5*time.Minute)) {
		return ack, false, &operations.Error{Status: 401, Code: protocol.CodeUnauthorized, Message: "Invalid Probe callback signature."}
	}
	if !validNonce(headers.Nonce) {
		return ack, false, &operations.Error{Status: 401, Code: protocol.CodeUnauthorized, Message: "Invalid Probe callback signature."}
	}
	canonical := probe.Canonical(http.MethodPost, "/internal/probe/events", s.config.InstanceId, headers.Timestamp, headers.Nonce, body)
	if !probe.VerifySignature(secret, canonical, headers.Signature) {
		return ack, false, &operations.Error{Status: 401, Code: protocol.CodeUnauthorized, Message: "Invalid Probe callback signature."}
	}
	var request protocol.ProbeEventRequest
	if err := protocol.Unmarshal(body, &request); err != nil {
		return ack, false, &operations.Error{Status: 400, Code: protocol.CodeInvalidRequest, Message: "The Probe event body is invalid JSON."}
	}
	if versionError := protocol.ValidateVersion(request.GetAPIVersion()); versionError != nil {
		return ack, false, &operations.Error{Status: 400, Code: versionError.Code, Message: versionError.Message, Details: versionError.Details}
	}
	if err := validateProbeEvent(request); err != nil {
		return ack, false, err
	}

	s.probeMu.Lock()
	s.pruneNoncesLocked(s.now())
	if _, exists := s.probeNonces[headers.Nonce]; exists {
		s.probeMu.Unlock()
		return ack, false, &operations.Error{Status: 401, Code: protocol.CodeUnauthorized, Message: "Invalid Probe callback signature."}
	}
	if _, exists := s.probeEventIDs[request.EventID]; exists {
		s.probeMu.Unlock()
		ack.Duplicate = true
		return ack, true, nil
	}
	s.probeMu.Unlock()
	contains, err := s.journal.ContainsEventID(ctx, request.EventID)
	if err != nil {
		return ack, false, err
	}
	if contains {
		ack.Duplicate = true
		return ack, true, nil
	}
	s.probeMu.Lock()
	if _, exists := s.probeNonces[headers.Nonce]; exists {
		s.probeMu.Unlock()
		return ack, false, &operations.Error{Status: 401, Code: protocol.CodeUnauthorized, Message: "Invalid Probe callback signature."}
	}
	if _, exists := s.probeEventIDs[request.EventID]; exists {
		s.probeMu.Unlock()
		ack.Duplicate = true
		return ack, true, nil
	}
	if s.probeNonces == nil {
		s.probeNonces = make(map[string]time.Time)
	}
	if s.probeEventIDs == nil {
		s.probeEventIDs = make(map[string]struct{})
	}
	s.probeNonces[headers.Nonce] = s.now().Add(5 * time.Minute)
	s.probeEventIDs[request.EventID] = struct{}{}
	s.probeMu.Unlock()

	event := protocol.ServerEvent{EventID: request.EventID, Type: request.Type, OccurredAt: request.OccurredAt, Message: request.Message, Details: request.Details}
	if err := s.journal.AppendEvent(ctx, event); err != nil {
		s.probeMu.Lock()
		delete(s.probeNonces, headers.Nonce)
		delete(s.probeEventIDs, request.EventID)
		s.probeMu.Unlock()
		return ack, false, err
	}
	s.rememberEvent(event)
	return ack, false, nil
}

func (s *Service) rememberEvent(event protocol.ServerEvent) {
	s.eventMu.Lock()
	s.recentEvents = append(s.recentEvents, event)
	if len(s.recentEvents) > 1000 {
		s.recentEvents = append([]protocol.ServerEvent(nil), s.recentEvents[len(s.recentEvents)-1000:]...)
	}
	s.eventMu.Unlock()
}

func (s *Service) pruneNoncesLocked(now time.Time) {
	for nonce, expires := range s.probeNonces {
		if !now.Before(expires) {
			delete(s.probeNonces, nonce)
		}
	}
}

func validNonce(value string) bool {
	if len(value) < 16 || len(value) > 128 {
		return false
	}
	for _, r := range value {
		if !(r >= 'a' && r <= 'z' || r >= 'A' && r <= 'Z' || r >= '0' && r <= '9' || r == '-' || r == '_') {
			return false
		}
	}
	return true
}

func validateProbeEvent(request protocol.ProbeEventRequest) error {
	if strings.TrimSpace(request.EventID) == "" || utf16Length(request.EventID) > 128 || hasControl(request.EventID) ||
		strings.TrimSpace(request.Type) == "" || utf16Length(request.Type) > 128 || hasControl(request.Type) ||
		utf16Length(request.Message) > 65536 || request.Details == nil || len(request.Details) > 128 {
		return &operations.Error{Status: 400, Code: protocol.CodeInvalidRequest, Message: "The Probe event is invalid."}
	}
	if _, offset := request.OccurredAt.Zone(); offset != 0 {
		return &operations.Error{Status: 400, Code: protocol.CodeInvalidRequest, Message: "The Probe event is invalid."}
	}
	for key, value := range request.Details {
		if utf16Length(key) > 256 || utf16Length(value) > 4096 {
			return &operations.Error{Status: 400, Code: protocol.CodeInvalidRequest, Message: "The Probe event is invalid."}
		}
	}
	return nil
}

func (s *Service) Tokens(ctx context.Context, actor audit.Actor) (protocol.TokenListResponse, error) {
	var response protocol.TokenListResponse
	err := s.forwardToken(ctx, actor, "tokens.list", http.MethodGet, "/tokens", nil, nil, &response)
	return response, err
}

func (s *Service) DeriveToken(ctx context.Context, actor audit.Actor, request protocol.TokenDeriveRequest) (protocol.TokenIssuedResponse, error) {
	var response protocol.TokenIssuedResponse
	parameters := audit.Parameters{{Key: "name", Value: request.Name}, {Key: "scopes", Value: strings.Join(request.Scopes, ",")}}
	err := s.forwardToken(ctx, actor, "tokens.derive", http.MethodPost, "/tokens/derive", request, parameters, &response)
	return response, err
}

func (s *Service) RevokeToken(ctx context.Context, actor audit.Actor, request protocol.TokenRevokeRequest) (protocol.TokenRevokeResponse, error) {
	var response protocol.TokenRevokeResponse
	parameters := audit.Parameters{{Key: "targetTokenId", Value: request.TokenID}}
	err := s.forwardToken(ctx, actor, "tokens.revoke", http.MethodPost, "/tokens/revoke", request, parameters, &response)
	return response, err
}

func (s *Service) RotateToken(ctx context.Context, actor audit.Actor, request protocol.TokenRotateRequest) (protocol.TokenRotateResponse, error) {
	var response protocol.TokenRotateResponse
	parameters := audit.Parameters{{Key: "targetTokenId", Value: request.TokenID}}
	err := s.forwardToken(ctx, actor, "tokens.rotate", http.MethodPost, "/tokens/rotate", request, parameters, &response)
	return response, err
}

func (s *Service) forwardToken(ctx context.Context, actor audit.Actor, action, method, suffix string, body any, parameters audit.Parameters, target any) error {
	if s.audit == nil {
		return audit.ErrUnavailable
	}
	return s.audit.Execute(ctx, actor, action, parameters, func() error {
		if s.probe == nil {
			return &operations.Error{Status: 409, Code: protocol.CodeServerOffline, Message: "Token management requires an online Probe."}
		}
		path := protocol.ServerCollectionPath + "/" + url.PathEscape(s.config.InstanceId) + suffix
		response, err := s.probe.Send(ctx, method, path, actor.Authorization, body, "apiVersion=1.0")
		if err != nil {
			if errors.Is(err, probe.ErrUnavailable) {
				return &operations.Error{Status: 409, Code: protocol.CodeServerOffline, Message: "Token management requires an online Probe."}
			}
			return err
		}
		if response.StatusCode != http.StatusOK {
			return remoteProbeError(response)
		}
		if err := response.Decode(target); err != nil {
			return &operations.Error{Status: 500, Code: protocol.CodeInternalError, Message: "The request could not be completed."}
		}
		if message, ok := target.(protocol.IProtocolMessage); ok {
			if versionError := protocol.ValidateVersion(message.GetAPIVersion()); versionError != nil {
				return &operations.Error{Status: 500, Code: protocol.CodeInternalError, Message: "The request could not be completed."}
			}
		}
		return nil
	})
}

func (s *Service) PlayerList(ctx context.Context, kind string) (protocol.PlayerListResponse, error) {
	if s.players == nil {
		return protocol.PlayerListResponse{}, capabilityNotReady()
	}
	return s.players.List(ctx, kind)
}

func (s *Service) AddPlayer(ctx context.Context, actor audit.Actor, kind string, request protocol.PlayerAddRequest) (protocol.PlayerMutationResponse, error) {
	if s.players == nil {
		return protocol.PlayerMutationResponse{}, capabilityNotReady()
	}
	if s.audit == nil {
		return protocol.PlayerMutationResponse{}, audit.ErrUnavailable
	}
	var response protocol.PlayerMutationResponse
	err := s.audit.Execute(ctx, actor, "players."+kind+".add", audit.Parameters{
		{Key: "playerName", Value: request.PlayerName}, {Key: "expectedRevision", Value: stringValue(request.ExpectedRevision)},
	}, func() error {
		var err error
		response, err = s.players.Add(ctx, kind, request)
		return err
	})
	return response, err
}

func (s *Service) RemovePlayer(ctx context.Context, actor audit.Actor, kind string, request protocol.PlayerRemoveRequest) (protocol.PlayerMutationResponse, error) {
	if s.players == nil {
		return protocol.PlayerMutationResponse{}, capabilityNotReady()
	}
	if s.audit == nil {
		return protocol.PlayerMutationResponse{}, audit.ErrUnavailable
	}
	var response protocol.PlayerMutationResponse
	err := s.audit.Execute(ctx, actor, "players."+kind+".remove", audit.Parameters{
		{Key: "playerName", Value: request.PlayerName}, {Key: "expectedRevision", Value: stringValue(request.ExpectedRevision)},
	}, func() error {
		var err error
		response, err = s.players.Remove(ctx, kind, request)
		return err
	})
	return response, err
}

func (s *Service) OnlinePlayers(ctx context.Context, actor audit.Actor) (protocol.OnlinePlayersResponse, error) {
	if s.players == nil {
		return protocol.OnlinePlayersResponse{}, capabilityNotReady()
	}
	if s.probe != nil && actor.Authorization != "" && s.supervisor != nil && s.supervisor.IsRunning() {
		path := protocol.ServerCollectionPath + "/" + url.PathEscape(s.config.InstanceId) + "/players/online"
		response, err := s.probe.Send(ctx, http.MethodGet, path, actor.Authorization, nil, "apiVersion=1.0")
		if err == nil {
			if response.StatusCode != http.StatusOK {
				return protocol.OnlinePlayersResponse{}, remoteProbeError(response)
			}
			var online protocol.OnlinePlayersResponse
			if err := response.Decode(&online); err != nil {
				return protocol.OnlinePlayersResponse{}, &operations.Error{Status: 500, Code: protocol.CodeInternalError, Message: "The request could not be completed."}
			}
			if versionError := protocol.ValidateVersion(online.GetAPIVersion()); versionError != nil {
				return protocol.OnlinePlayersResponse{}, &operations.Error{Status: 500, Code: protocol.CodeInternalError, Message: "The request could not be completed."}
			}
			return online, nil
		}
		if !errors.Is(err, probe.ErrUnavailable) {
			return protocol.OnlinePlayersResponse{}, err
		}
	}
	return s.players.Online(ctx, s.metadata.Config.MaxPlayers)
}

func remoteProbeError(response probe.Response) error {
	apiError, ok := probe.APIErrorFrom(response)
	if !ok {
		return &operations.Error{Status: 500, Code: protocol.CodeInternalError, Message: "The request could not be completed."}
	}
	return &operations.Error{Status: response.StatusCode, Code: apiError.Code, Message: apiError.Message, Details: apiError.Details}
}

func stringValue(value *string) string {
	if value == nil {
		return ""
	}
	return *value
}

func (s *Service) Files(ctx context.Context, path string) (protocol.FileListResponse, error) {
	if s.files == nil {
		return protocol.FileListResponse{}, filesNotReady()
	}
	normalized, err := files.NormalizeRelativePath(path, true)
	if err != nil {
		return protocol.FileListResponse{}, err
	}
	entries, err := s.files.List(ctx, normalized)
	if err != nil {
		return protocol.FileListResponse{}, err
	}
	response := protocol.NewFileListResponse()
	response.Path = normalized
	response.Entries = make([]protocol.FileEntry, 0, len(entries))
	for _, entry := range entries {
		response.Entries = append(response.Entries, protocol.FileEntry{
			Path: entry.Path, IsDirectory: entry.IsDirectory, SizeBytes: entry.SizeBytes,
			ModifiedAt: timestampPointer(entry.ModifiedAt), Revision: entry.Revision, Writable: entry.Writable,
		})
	}
	return response, nil
}

func (s *Service) ReadFile(ctx context.Context, path string) (protocol.FileContentResponse, error) {
	if s.files == nil {
		return protocol.FileContentResponse{}, filesNotReady()
	}
	content, err := s.files.Read(ctx, path)
	if err != nil {
		return protocol.FileContentResponse{}, err
	}
	response := protocol.NewFileContentResponse()
	response.Path = content.Path
	response.Content = content.Content
	response.Revision = content.Revision
	response.SizeBytes = content.SizeBytes
	response.ModifiedAt = timestampPointer(&content.ModifiedAt)
	return response, nil
}

func (s *Service) CreateFile(ctx context.Context, actor audit.Actor, path string, directory bool) (protocol.FileMutationResponse, error) {
	if s.files == nil {
		return protocol.FileMutationResponse{}, filesNotReady()
	}
	if s.audit == nil {
		return protocol.FileMutationResponse{}, audit.ErrUnavailable
	}
	var mutation files.Mutation
	err := s.audit.Execute(ctx, actor, "file.create", audit.Parameters{
		{Key: "path", Value: path}, {Key: "isDirectory", Value: strconv.FormatBool(directory)},
	}, func() error {
		var err error
		mutation, err = s.files.Create(ctx, path, directory)
		return err
	})
	if err != nil {
		return protocol.FileMutationResponse{}, err
	}
	return fileMutationResponse(mutation), nil
}

func (s *Service) RenameFile(ctx context.Context, actor audit.Actor, path, newName, expectedRevision string) (protocol.FileMutationResponse, error) {
	if s.files == nil {
		return protocol.FileMutationResponse{}, filesNotReady()
	}
	if s.audit == nil {
		return protocol.FileMutationResponse{}, audit.ErrUnavailable
	}
	var mutation files.Mutation
	err := s.audit.Execute(ctx, actor, "file.rename", audit.Parameters{
		{Key: "path", Value: path}, {Key: "newName", Value: newName}, {Key: "expectedRevision", Value: expectedRevision},
	}, func() error {
		var err error
		mutation, err = s.files.Rename(ctx, path, newName, expectedRevision)
		return err
	})
	if err != nil {
		return protocol.FileMutationResponse{}, err
	}
	return fileMutationResponse(mutation), nil
}

func (s *Service) WriteFile(ctx context.Context, actor audit.Actor, path, content, expectedRevision string) (protocol.FileWriteResponse, error) {
	if s.files == nil {
		return protocol.FileWriteResponse{}, filesNotReady()
	}
	if s.audit == nil {
		return protocol.FileWriteResponse{}, audit.ErrUnavailable
	}
	var written files.Content
	err := s.audit.Execute(ctx, actor, "file.write", audit.Parameters{
		{Key: "path", Value: path}, {Key: "expectedRevision", Value: expectedRevision},
		{Key: "contentBytes", Value: strconv.Itoa(len([]byte(content)))},
	}, func() error {
		var err error
		written, err = s.files.Write(ctx, path, content, expectedRevision)
		return err
	})
	if err != nil {
		return protocol.FileWriteResponse{}, err
	}
	response := protocol.NewFileWriteResponse()
	response.Path = written.Path
	response.Revision = written.Revision
	response.SizeBytes = written.SizeBytes
	response.ModifiedAt = protocol.NewTimestamp(written.ModifiedAt)
	return response, nil
}

func (s *Service) GetFile(ctx context.Context, path string) (files.Mutation, error) {
	if s.files == nil {
		return files.Mutation{}, filesNotReady()
	}
	return s.files.Get(ctx, path)
}

func (s *Service) DeleteFile(ctx context.Context, path, expectedRevision string) error {
	if s.files == nil {
		return filesNotReady()
	}
	return s.files.Delete(ctx, path, expectedRevision)
}

func (s *Service) PrepareFileDelete(ctx context.Context, actor audit.Actor, path, expectedRevision string) (protocol.ConfirmationPrepareResponse, error) {
	if s.files == nil || s.confirmations == nil {
		return protocol.ConfirmationPrepareResponse{}, filesNotReady()
	}
	current, err := s.files.Get(ctx, path)
	if err != nil {
		return protocol.ConfirmationPrepareResponse{}, err
	}
	wanted := expectedRevision
	if wanted == "" {
		wanted = files.MissingRevision
	}
	if current.Revision != wanted {
		return protocol.ConfirmationPrepareResponse{}, &files.Error{Status: 409, Code: protocol.CodeRevisionConflict, Message: "The resource revision does not match.", Details: map[string]string{"expectedRevision": wanted}}
	}
	if s.audit == nil {
		return protocol.ConfirmationPrepareResponse{}, audit.ErrUnavailable
	}
	parameters := map[string]string{"path": current.Path, "revision": current.Revision}
	var response protocol.ConfirmationPrepareResponse
	err = s.audit.Execute(ctx, actor, "file.delete.prepare", audit.Parameters{
		{Key: "path", Value: current.Path}, {Key: "revision", Value: current.Revision},
	}, func() error {
		var err error
		response, err = s.confirmations.Prepare(s.config.InstanceId, actor.TokenID, protocol.ScopeFiles,
			protocol.ConfirmationActionFileDelete, "Delete "+current.Path, current.Revision, parameters,
			func() (protocol.OperationInfo, error) {
				return s.operations.Start(context.Background(), "file.delete", protocol.ScopeFiles,
					func(context.Context) (operations.Completion, error) {
						if err := s.files.Delete(context.Background(), current.Path, current.Revision); err != nil {
							return operations.Completion{}, err
						}
						return operations.Completion{Message: "File deleted."}, nil
					})
			})
		return err
	})
	return response, err
}

func (s *Service) PreparePropertiesPatch(ctx context.Context, actor audit.Actor, expectedRevision string, changes map[string]*string) (protocol.ConfirmationPrepareResponse, error) {
	if s.properties == nil || s.confirmations == nil {
		return protocol.ConfirmationPrepareResponse{}, filesNotReady()
	}
	current, err := s.properties.Get(ctx)
	if err != nil {
		return protocol.ConfirmationPrepareResponse{}, err
	}
	if current.Revision != expectedRevision {
		return protocol.ConfirmationPrepareResponse{}, &files.Error{Status: 409, Code: protocol.CodeRevisionConflict, Message: "The resource revision does not match.", Details: map[string]string{"expectedRevision": expectedRevision}}
	}
	if s.audit == nil {
		return protocol.ConfirmationPrepareResponse{}, audit.ErrUnavailable
	}
	setKeys, removedKeys := files.AuditChanges(changes)
	parameters := map[string]string{
		"setKeys": setKeys, "removedKeys": removedKeys, "values": "redacted", "expectedRevision": expectedRevision,
	}
	auditParameters := audit.Parameters{
		{Key: "setKeys", Value: setKeys}, {Key: "removedKeys", Value: removedKeys},
		{Key: "values", Value: "redacted"}, {Key: "expectedRevision", Value: expectedRevision},
	}
	var response protocol.ConfirmationPrepareResponse
	err = s.audit.Execute(ctx, actor, "properties.patch.prepare", auditParameters, func() error {
		var err error
		response, err = s.confirmations.Prepare(s.config.InstanceId, actor.TokenID, protocol.ScopeFiles,
			protocol.ConfirmationActionPropertiesPatch, "Patch server.properties; restart required", current.Revision, parameters,
			func() (protocol.OperationInfo, error) {
				return s.operations.Start(context.Background(), "properties.patch", protocol.ScopeFiles,
					func(context.Context) (operations.Completion, error) {
						if _, err := s.properties.Patch(context.Background(), current.Revision, changes); err != nil {
							return operations.Completion{}, err
						}
						return operations.Completion{Message: "Properties patched; restart required."}, nil
					})
			})
		return err
	})
	return response, err
}

func (s *Service) ExecuteConfirmation(ctx context.Context, actor audit.Actor, confirmationID string, hasScope func(string) bool) (protocol.OperationInfo, error) {
	if s.confirmations == nil {
		return protocol.OperationInfo{}, filesNotReady()
	}
	if s.audit == nil {
		return protocol.OperationInfo{}, audit.ErrUnavailable
	}
	var operation protocol.OperationInfo
	err := s.audit.Execute(ctx, actor, "confirmation.execute", audit.Parameters{{Key: "confirmationId", Value: confirmationID}}, func() error {
		var err error
		operation, err = s.confirmations.Execute(s.config.InstanceId, actor.TokenID, hasScope, confirmationID)
		return err
	})
	return operation, err
}

func (s *Service) Operation(id string) (protocol.OperationInfo, string, bool) {
	if s.operations == nil {
		return protocol.OperationInfo{}, "", false
	}
	scope, ok := s.operations.RequiredScope(id)
	if !ok {
		return protocol.OperationInfo{}, "", false
	}
	operation, _ := s.operations.Get(id)
	return operation, scope, true
}

func (s *Service) Backups(ctx context.Context) (protocol.BackupListResponse, error) {
	if s.backups == nil {
		return protocol.BackupListResponse{}, backupsNotReady()
	}
	response := protocol.NewBackupListResponse()
	for _, info := range s.backups.List() {
		metadata, err := s.backups.Metadata(info.Name)
		if err != nil {
			if errors.Is(err, os.ErrNotExist) {
				continue
			}
			return protocol.BackupListResponse{}, err
		}
		response.Backups = append(response.Backups, backupMetadata(metadata))
	}
	return response, nil
}

func (s *Service) CreateBackup(ctx context.Context, actor audit.Actor, label string, description *string) (protocol.BackupCreateResponse, error) {
	if s.backups == nil || s.operations == nil {
		return protocol.BackupCreateResponse{}, backupsNotReady()
	}
	if s.audit == nil {
		return protocol.BackupCreateResponse{}, audit.ErrUnavailable
	}
	var metadata backups.Metadata
	var operation protocol.OperationInfo
	err := s.audit.Execute(ctx, actor, "backup.create", audit.Parameters{{Key: "labelLength", Value: strconv.Itoa(utf16Length(label))}}, func() error {
		var err error
		operation, err = s.operations.Run(ctx, "backup.create", protocol.ScopeBackup, func(operationCtx context.Context) (operations.Completion, error) {
			if err := validateBackupText(label, 128, true); err != nil {
				return operations.Completion{}, err
			}
			if description != nil {
				if err := validateBackupText(*description, 1024, false); err != nil {
					return operations.Completion{}, err
				}
			}
			info, err := s.backups.Create(operationCtx)
			if err != nil {
				return operations.Completion{}, err
			}
			if err := s.backups.SetDescription(info.Path, label, description); err != nil {
				return operations.Completion{}, err
			}
			metadata, err = s.backups.Metadata(info.Name)
			if err != nil {
				return operations.Completion{}, err
			}
			return operations.Completion{Message: "Backup created.", Metadata: map[string]string{
				"backupId": metadata.ID, "sizeBytes": strconv.FormatInt(metadata.SizeBytes, 10), "sha256": metadata.SHA256,
			}}, nil
		})
		return err
	})
	if err != nil {
		return protocol.BackupCreateResponse{}, err
	}
	if operation.State == nil || *operation.State != protocol.OperationStateSucceeded || metadata.ID == "" {
		return protocol.BackupCreateResponse{}, &operations.Error{Status: 500, Code: protocol.CodeOperationFailed, Message: "The backup operation failed.", OperationID: operation.ID}
	}
	response := protocol.NewBackupCreateResponse()
	response.Backup = backupMetadata(metadata)
	response.Operation = operation
	return response, nil
}

func (s *Service) PrepareBackup(ctx context.Context, actor audit.Actor, backupID string, restore bool) (protocol.ConfirmationPrepareResponse, error) {
	if s.backups == nil || s.confirmations == nil {
		return protocol.ConfirmationPrepareResponse{}, backupsNotReady()
	}
	metadata, err := s.backups.Metadata(backupID)
	if err != nil {
		return protocol.ConfirmationPrepareResponse{}, err
	}
	if restore && s.supervisor != nil && s.supervisor.IsRunning() {
		return protocol.ConfirmationPrepareResponse{}, &server.Error{Status: 409, Code: protocol.CodeServerRunning, Message: "The server must be stopped before restoring a backup."}
	}
	if s.audit == nil {
		return protocol.ConfirmationPrepareResponse{}, audit.ErrUnavailable
	}
	action := protocol.ConfirmationActionBackupDelete
	auditAction := "backup.delete.prepare"
	summary := "Delete backup " + metadata.ID
	if restore {
		action = protocol.ConfirmationActionBackupRestore
		auditAction = "backup.restore.prepare"
		summary = "Restore backup " + metadata.ID + " while offline"
	}
	parameters := map[string]string{"backupId": metadata.ID, "sha256": metadata.SHA256}
	var response protocol.ConfirmationPrepareResponse
	err = s.audit.Execute(ctx, actor, auditAction, audit.Parameters{{Key: "backupId", Value: metadata.ID}, {Key: "sha256", Value: metadata.SHA256}}, func() error {
		var err error
		response, err = s.confirmations.Prepare(s.config.InstanceId, actor.TokenID, protocol.ScopeBackup,
			action, summary, metadata.SHA256, parameters, func() (protocol.OperationInfo, error) {
				if restore {
					return s.operations.Start(context.Background(), "backup.restore", protocol.ScopeBackup,
						func(operationCtx context.Context) (operations.Completion, error) {
							if err := s.backups.Restore(operationCtx, metadata.ID, metadata.SHA256); err != nil {
								return operations.Completion{}, err
							}
							return operations.Completion{Message: "Backup restored; Core created a safety backup first."}, nil
						})
				}
				return s.operations.Start(context.Background(), "backup.delete", protocol.ScopeBackup,
					func(context.Context) (operations.Completion, error) {
						if err := s.backups.Delete(metadata.ID, metadata.SHA256); err != nil {
							return operations.Completion{}, err
						}
						return operations.Completion{Message: "Backup deleted."}, nil
					})
			})
		return err
	})
	return response, err
}

func (s *Service) PrepareControl(ctx context.Context, actor audit.Actor, action string) (protocol.ConfirmationPrepareResponse, error) {
	if s.supervisor == nil || s.confirmations == nil {
		return protocol.ConfirmationPrepareResponse{}, capabilityNotReady()
	}
	if controller, ok := s.supervisor.(interface{ SupportsControl() bool }); ok && !controller.SupportsControl() {
		return protocol.ConfirmationPrepareResponse{}, capabilityNotReady()
	}
	if action != protocol.ControlActionStart && action != protocol.ControlActionStop && action != protocol.ControlActionRestart && action != protocol.ControlActionKill {
		return protocol.ConfirmationPrepareResponse{}, &operations.Error{Status: 400, Code: protocol.CodeInvalidRequest, Message: "control/prepare accepts start, stop, restart, or kill."}
	}
	running := s.supervisor.IsRunning()
	if action == protocol.ControlActionStart && running {
		return protocol.ConfirmationPrepareResponse{}, server.ServerRunning()
	}
	if action != protocol.ControlActionStart && !running {
		return protocol.ConfirmationPrepareResponse{}, server.ServerOffline()
	}
	if s.audit == nil {
		return protocol.ConfirmationPrepareResponse{}, audit.ErrUnavailable
	}
	confirmationAction := protocol.ConfirmationActionControlStart
	switch action {
	case protocol.ControlActionStop:
		confirmationAction = protocol.ConfirmationActionControlStop
	case protocol.ControlActionRestart:
		confirmationAction = protocol.ConfirmationActionControlRestart
	case protocol.ControlActionKill:
		confirmationAction = protocol.ConfirmationActionControlKill
	}
	parameters := map[string]string{"action": action}
	revision := protocolState(s.supervisor.State())
	var response protocol.ConfirmationPrepareResponse
	err := s.audit.Execute(ctx, actor, confirmationAction+".prepare", audit.Parameters{{Key: "action", Value: action}}, func() error {
		var err error
		response, err = s.confirmations.Prepare(s.config.InstanceId, actor.TokenID, protocol.ScopeControl,
			confirmationAction, "Confirm server "+action, revision, parameters, func() (protocol.OperationInfo, error) {
				return s.operations.Start(context.Background(), "control."+action, protocol.ScopeControl,
					func(operationCtx context.Context) (operations.Completion, error) {
						isRunning := s.supervisor.IsRunning()
						if action == protocol.ControlActionStart {
							if isRunning {
								return operations.Completion{}, &server.Error{Status: 409, Code: protocol.CodeServerRunning, Message: "The server started before the confirmed operation executed."}
							}
							if err := s.supervisor.Start(operationCtx); err != nil {
								return operations.Completion{}, err
							}
							s.NotifyServerStarted()
							return operations.Completion{Message: "Server start completed."}, nil
						}
						if !isRunning {
							return operations.Completion{}, server.ServerOffline()
						}
						s.NoteIntentionalStop()
						if action == protocol.ControlActionRestart {
							if err := s.supervisor.Restart(operationCtx); err != nil {
								return operations.Completion{}, err
							}
							s.NotifyServerStarted()
						} else if err := s.supervisor.Stop(operationCtx, action == protocol.ControlActionKill); err != nil {
							return operations.Completion{}, err
						}
						return operations.Completion{Message: "Server " + action + " completed."}, nil
					})
			})
		return err
	})
	return response, err
}

func backupMetadata(metadata backups.Metadata) protocol.BackupMetadata {
	result := protocol.NewBackupMetadata()
	result.ID = metadata.ID
	result.Label = metadata.Label
	result.Description = metadata.Description
	result.CreatedAt = protocol.NewTimestamp(metadata.CreatedAt)
	result.SizeBytes = metadata.SizeBytes
	result.SHA256 = metadata.SHA256
	result.MinecraftVersion = protocol.Ptr(metadata.MinecraftVersion)
	return result
}

func validateBackupText(value string, maximum int, required bool) error {
	if required && strings.TrimSpace(value) == "" || utf16Length(value) > maximum || hasControl(value) {
		return &operations.Error{Status: 400, Code: protocol.CodeInvalidRequest, Message: "The backup label or description is invalid."}
	}
	return nil
}

func hasControl(value string) bool {
	for _, r := range value {
		if unicode.IsControl(r) {
			return true
		}
	}
	return false
}

func utf16Length(value string) int { return len(utf16.Encode([]rune(value))) }

func backupsNotReady() error {
	return &operations.Error{Status: 403, Code: protocol.CodeCapabilityNotSupported, Message: "This capability is not supported by the current Agent backend."}
}

func capabilityNotReady() error { return backupsNotReady() }

func (s *Service) Properties(ctx context.Context) (protocol.ServerPropertiesResponse, error) {
	if s.properties == nil {
		return protocol.ServerPropertiesResponse{}, filesNotReady()
	}
	snapshot, err := s.properties.Get(ctx)
	if err != nil {
		return protocol.ServerPropertiesResponse{}, err
	}
	response := protocol.NewServerPropertiesResponse()
	response.Revision = snapshot.Revision
	response.RestartRequired = snapshot.RestartRequired
	response.Properties = snapshot.Properties
	return response, nil
}

func (s *Service) PatchProperties(ctx context.Context, expectedRevision string, changes map[string]*string) (protocol.ServerPropertiesPatchResponse, error) {
	if s.properties == nil {
		return protocol.ServerPropertiesPatchResponse{}, filesNotReady()
	}
	result, err := s.properties.Patch(ctx, expectedRevision, changes)
	if err != nil {
		return protocol.ServerPropertiesPatchResponse{}, err
	}
	response := protocol.NewServerPropertiesPatchResponse()
	response.Revision = result.Revision
	response.RestartRequired = protocol.Ptr(true)
	response.AppliedAt = protocol.NewTimestamp(result.AppliedAt)
	response.Properties = result.Properties
	return response, nil
}

func (s *Service) NotifyServerStarted() {
	if s.properties != nil {
		s.properties.NotifyServerStarted()
	}
}

func fileMutationResponse(mutation files.Mutation) protocol.FileMutationResponse {
	response := protocol.NewFileMutationResponse()
	response.Path = mutation.Path
	response.Revision = mutation.Revision
	response.IsDirectory = mutation.IsDirectory
	response.ModifiedAt = protocol.NewTimestamp(mutation.ModifiedAt)
	return response
}

func timestampPointer(value *time.Time) *protocol.Timestamp {
	if value == nil {
		return nil
	}
	timestamp := protocol.NewTimestamp(*value)
	return &timestamp
}

func filesNotReady() error {
	return &files.Error{Status: 403, Code: protocol.CodeCapabilityNotSupported, Message: "This capability is not supported by the current Agent backend."}
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
