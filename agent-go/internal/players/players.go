// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

// Package players implements the ServerAgent player-list and online-player APIs
// over an instance directory and the server process capability.
package players

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"sort"
	"strings"
	"time"
	"unicode"
	"unicode/utf16"
	"unicode/utf8"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

// Core is the process capability needed by the player service. IsRunning must
// include the starting and stopping states, matching MofuCore.ServerManage.
type Core interface {
	IsRunning() bool
	Command(ctx context.Context, command string) error
	OnlineNames(ctx context.Context) []string
}

// Service owns one instance's player lists and serializes its mutations.
type Service struct {
	directory string
	core      Core
	now       func() time.Time
	mutations chan struct{}
}

// New creates a player service for one instance directory. A nil clock uses time.Now.
func New(directory string, core Core, now func() time.Time) *Service {
	if now == nil {
		now = time.Now
	}
	service := &Service{
		directory: directory,
		core:      core,
		now:       now,
		mutations: make(chan struct{}, 1),
	}
	service.mutations <- struct{}{}
	return service
}

// List returns the sorted API entries and their content-derived revision.
func (s *Service) List(ctx context.Context, kind string) (protocol.PlayerListResponse, error) {
	if err := validateKind(kind); err != nil {
		return protocol.PlayerListResponse{}, err
	}
	if err := ctx.Err(); err != nil {
		return protocol.PlayerListResponse{}, err
	}
	return s.list(kind)
}

// Add adds a player through a server command while active, or directly to the
// normalized JSON list while stopped.
func (s *Service) Add(ctx context.Context, kind string, request protocol.PlayerAddRequest) (protocol.PlayerMutationResponse, error) {
	if err := validateKind(kind); err != nil {
		return protocol.PlayerMutationResponse{}, err
	}
	if !validPlayerName(request.PlayerName) {
		return protocol.PlayerMutationResponse{}, invalidPlayerName()
	}
	if err := validateReason(request.Reason); err != nil {
		return protocol.PlayerMutationResponse{}, err
	}
	if request.OpLevel != nil && (*request.OpLevel < 1 || *request.OpLevel > 4) {
		return protocol.PlayerMutationResponse{}, invalidRequest("opLevel must be between 1 and 4.")
	}
	if err := s.acquire(ctx); err != nil {
		return protocol.PlayerMutationResponse{}, err
	}
	defer s.release()

	current, err := s.list(kind)
	if err != nil {
		return protocol.PlayerMutationResponse{}, err
	}
	if err := ensureRevision(current.Revision, request.ExpectedRevision); err != nil {
		return protocol.PlayerMutationResponse{}, err
	}
	if containsPlayer(current.Entries, request.PlayerName) {
		return protocol.PlayerMutationResponse{}, conflict("The player is already in this list.")
	}

	added := s.addedEntry(kind, request)
	if s.isRunning() {
		if err := s.core.Command(ctx, addCommand(kind, request.PlayerName, added)); err != nil {
			return protocol.PlayerMutationResponse{}, err
		}
	} else if err := s.addOffline(kind, request, added); err != nil {
		return protocol.PlayerMutationResponse{}, err
	}

	updated, err := s.list(kind)
	if err != nil {
		return protocol.PlayerMutationResponse{}, err
	}
	entry, found := findPlayer(updated.Entries, request.PlayerName)
	if !found {
		entry = added
	}
	response := protocol.NewPlayerMutationResponse()
	response.Kind = kind
	response.Revision = updated.Revision
	response.Entry = entry
	return response, nil
}

// Remove removes a player and returns the entry as it existed before mutation.
func (s *Service) Remove(ctx context.Context, kind string, request protocol.PlayerRemoveRequest) (protocol.PlayerMutationResponse, error) {
	if err := validateKind(kind); err != nil {
		return protocol.PlayerMutationResponse{}, err
	}
	if !validPlayerName(request.PlayerName) {
		return protocol.PlayerMutationResponse{}, invalidPlayerName()
	}
	if err := s.acquire(ctx); err != nil {
		return protocol.PlayerMutationResponse{}, err
	}
	defer s.release()

	current, err := s.list(kind)
	if err != nil {
		return protocol.PlayerMutationResponse{}, err
	}
	if err := ensureRevision(current.Revision, request.ExpectedRevision); err != nil {
		return protocol.PlayerMutationResponse{}, err
	}
	removed, found := findPlayer(current.Entries, request.PlayerName)
	if !found {
		return protocol.PlayerMutationResponse{}, notFound("The player is not in this list.")
	}

	if s.isRunning() {
		if err := s.core.Command(ctx, removeCommand(kind, request.PlayerName)); err != nil {
			return protocol.PlayerMutationResponse{}, err
		}
	} else if err := s.removeOffline(kind, request.PlayerName); err != nil {
		return protocol.PlayerMutationResponse{}, err
	}

	updated, err := s.list(kind)
	if err != nil {
		return protocol.PlayerMutationResponse{}, err
	}
	response := protocol.NewPlayerMutationResponse()
	response.Kind = kind
	response.Revision = updated.Revision
	response.Entry = removed
	response.Removed = true
	return response, nil
}

// Online projects Core.OnlineNames without inventing Probe-only player fields.
func (s *Service) Online(ctx context.Context, maxPlayers int) (protocol.OnlinePlayersResponse, error) {
	if err := ctx.Err(); err != nil {
		return protocol.OnlinePlayersResponse{}, err
	}
	var names []string
	if s.core != nil {
		names = s.core.OnlineNames(ctx)
	}
	response := protocol.NewOnlinePlayersResponse()
	response.Players = make([]protocol.PlayerInfo, 0, len(names))
	for _, name := range names {
		response.Players = append(response.Players, protocol.PlayerInfo{Name: name})
	}
	response.OnlineCount = len(response.Players)
	response.MaxPlayers = maxPlayers
	response.ObservedAt = protocol.NewTimestamp(s.now())
	return response, nil
}

func (s *Service) list(kind string) (protocol.PlayerListResponse, error) {
	entries := make([]protocol.PlayerListEntry, 0)
	path := playerFilePath(s.directory, kind)
	switch kind {
	case kindWhitelist:
		for _, entry := range readWhitelist(path) {
			entries = append(entries, protocol.PlayerListEntry{
				Name: entry.Name,
				UUID: protocol.Ptr(resolveUUID(entry.UUID, entry.Name)),
			})
		}
	case kindOps:
		for _, entry := range readOps(path) {
			entries = append(entries, protocol.PlayerListEntry{
				Name:                entry.Name,
				UUID:                protocol.Ptr(resolveUUID(entry.UUID, entry.Name)),
				OpLevel:             protocol.Ptr(int(entry.Level)),
				BypassesPlayerLimit: protocol.Ptr(entry.BypassesPlayerLimit),
			})
		}
	case kindBans:
		for _, entry := range readBans(path) {
			entries = append(entries, protocol.PlayerListEntry{
				Name:      entry.Name,
				UUID:      protocol.Ptr(resolveUUID(entry.UUID, entry.Name)),
				Reason:    protocol.Ptr(entry.Reason),
				Source:    protocol.Ptr(entry.Source),
				CreatedAt: parseMinecraftTime(entry.Created),
				ExpiresAt: parseMinecraftTime(entry.Expires),
			})
		}
	}
	sort.SliceStable(entries, func(left, right int) bool {
		return ordinalLess(entries[left].Name, entries[right].Name)
	})
	revision, err := listRevision(entries)
	if err != nil {
		return protocol.PlayerListResponse{}, err
	}
	response := protocol.NewPlayerListResponse()
	response.Kind = kind
	response.Revision = revision
	response.Entries = entries
	return response, nil
}

func (s *Service) addedEntry(kind string, request protocol.PlayerAddRequest) protocol.PlayerListEntry {
	entry := protocol.PlayerListEntry{
		Name: request.PlayerName,
		UUID: protocol.Ptr(offlineUUID(request.PlayerName)),
	}
	switch kind {
	case kindOps:
		level := 4
		if request.OpLevel != nil {
			level = *request.OpLevel
		}
		bypasses := false
		if request.BypassesPlayerLimit != nil {
			bypasses = *request.BypassesPlayerLimit
		}
		entry.OpLevel = &level
		entry.BypassesPlayerLimit = &bypasses
	case kindBans:
		reason := "Banned by an operator."
		if request.Reason != nil {
			reason = *request.Reason
		}
		source := "Mofumofu.ServerAgent"
		createdAt := protocol.NewTimestamp(s.now())
		entry.Reason = &reason
		entry.Source = &source
		entry.CreatedAt = &createdAt
		entry.ExpiresAt = request.ExpiresAt
	}
	return entry
}

func (s *Service) addOffline(kind string, request protocol.PlayerAddRequest, added protocol.PlayerListEntry) error {
	path := playerFilePath(s.directory, kind)
	switch kind {
	case kindWhitelist:
		entries := readWhitelist(path)
		entries = append(entries, whitelistEntry{Name: request.PlayerName})
		return saveWhitelist(path, entries)
	case kindOps:
		entries := readOps(path)
		level := int32(4)
		if request.OpLevel != nil {
			level = int32(*request.OpLevel)
		}
		bypasses := false
		if request.BypassesPlayerLimit != nil {
			bypasses = *request.BypassesPlayerLimit
		}
		entries = append(entries, opEntry{
			Name:                request.PlayerName,
			Level:               level,
			BypassesPlayerLimit: bypasses,
		})
		return saveOps(path, entries)
	case kindBans:
		entries := readBans(path)
		expires := "forever"
		if request.ExpiresAt != nil {
			expires = formatMinecraftTime(request.ExpiresAt.Time)
		}
		entries = append(entries, bannedPlayerEntry{
			Name:    request.PlayerName,
			Created: formatMinecraftTime(s.now()),
			Source:  *added.Source,
			Expires: expires,
			Reason:  *added.Reason,
		})
		return saveBans(path, entries)
	}
	return nil
}

func (s *Service) removeOffline(kind, playerName string) error {
	path := playerFilePath(s.directory, kind)
	switch kind {
	case kindWhitelist:
		entries := readWhitelist(path)
		filtered := make([]whitelistEntry, 0, len(entries))
		for _, entry := range entries {
			if !samePlayerName(entry.Name, playerName) {
				filtered = append(filtered, entry)
			}
		}
		return saveWhitelist(path, filtered)
	case kindOps:
		entries := readOps(path)
		filtered := make([]opEntry, 0, len(entries))
		for _, entry := range entries {
			if !samePlayerName(entry.Name, playerName) {
				filtered = append(filtered, entry)
			}
		}
		return saveOps(path, filtered)
	case kindBans:
		entries := readBans(path)
		filtered := make([]bannedPlayerEntry, 0, len(entries))
		for _, entry := range entries {
			if !samePlayerName(entry.Name, playerName) {
				filtered = append(filtered, entry)
			}
		}
		return saveBans(path, filtered)
	}
	return nil
}

func (s *Service) isRunning() bool {
	return s.core != nil && s.core.IsRunning()
}

func (s *Service) acquire(ctx context.Context) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-s.mutations:
		if err := ctx.Err(); err != nil {
			s.mutations <- struct{}{}
			return err
		}
		return nil
	}
}

func (s *Service) release() {
	s.mutations <- struct{}{}
}

func validPlayerName(name string) bool {
	if len(name) < 1 || len(name) > 16 {
		return false
	}
	for index := 0; index < len(name); index++ {
		character := name[index]
		if !((character >= '0' && character <= '9') ||
			(character >= 'A' && character <= 'Z') ||
			(character >= 'a' && character <= 'z') || character == '_') {
			return false
		}
	}
	return true
}

func validateReason(reason *string) error {
	if reason == nil {
		return nil
	}
	if !utf8.ValidString(*reason) || len(utf16.Encode([]rune(*reason))) > 256 {
		return invalidRequest("The ban reason is invalid.")
	}
	for _, character := range *reason {
		if unicode.IsControl(character) {
			return invalidRequest("The ban reason is invalid.")
		}
	}
	return nil
}

func ordinalLess(left, right string) bool {
	// StringComparer.Ordinal compares UTF-16 code units, whereas Go compares
	// UTF-8 bytes. Valid player names are ASCII, but preserving the file reader's
	// ordinal behavior also keeps malformed-name fixtures deterministic.
	leftUnits := utf16.Encode([]rune(left))
	rightUnits := utf16.Encode([]rune(right))
	limit := len(leftUnits)
	if len(rightUnits) < limit {
		limit = len(rightUnits)
	}
	for index := 0; index < limit; index++ {
		if leftUnits[index] != rightUnits[index] {
			return leftUnits[index] < rightUnits[index]
		}
	}
	return len(leftUnits) < len(rightUnits)
}

func samePlayerName(left, right string) bool {
	return strings.EqualFold(left, right)
}

func containsPlayer(entries []protocol.PlayerListEntry, name string) bool {
	_, found := findPlayer(entries, name)
	return found
}

func findPlayer(entries []protocol.PlayerListEntry, name string) (protocol.PlayerListEntry, bool) {
	for _, entry := range entries {
		if samePlayerName(entry.Name, name) {
			return entry, true
		}
	}
	return protocol.PlayerListEntry{}, false
}

func ensureRevision(actual string, expected *string) error {
	if expected == nil || *expected == "" || actual == *expected {
		return nil
	}
	return revisionConflict(*expected)
}

func listRevision(entries []protocol.PlayerListEntry) (string, error) {
	payload, err := protocol.Marshal(entries)
	if err != nil {
		return "", err
	}
	hash := sha256.Sum256(payload)
	return "pl_" + hex.EncodeToString(hash[:]), nil
}

func addCommand(kind, playerName string, added protocol.PlayerListEntry) string {
	switch kind {
	case kindWhitelist:
		return "whitelist add " + playerName
	case kindOps:
		return "op " + playerName
	case kindBans:
		return "ban " + playerName + " " + *added.Reason
	default:
		return ""
	}
}

func removeCommand(kind, playerName string) string {
	switch kind {
	case kindWhitelist:
		return "whitelist remove " + playerName
	case kindOps:
		return "deop " + playerName
	case kindBans:
		return "pardon " + playerName
	default:
		return ""
	}
}
