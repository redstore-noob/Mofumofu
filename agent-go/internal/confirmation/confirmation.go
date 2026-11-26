// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

// Package confirmation implements the Agent's in-memory, single-use
// confirmation tickets. Tickets bind a closure and its request context; they
// are intentionally not persisted.
package confirmation

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"sort"
	"strings"
	"sync"
	"time"
	"unicode/utf16"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

const (
	confirmationLifetime = time.Duration(protocol.ConfirmationLifetimeSeconds) * time.Second
	maxTombstones        = 4096
	retainedTombstones   = 2048
)

// Execute starts the already-authorized action after the ticket is consumed.
type Execute func() (protocol.OperationInfo, error)

type pendingConfirmation struct {
	id             string
	instanceID     string
	tokenID        string
	requiredScope  string
	action         string
	parametersHash string
	revision       string
	createdAt      time.Time
	expiresAt      time.Time
	execute        Execute
}

type tombstoneState uint8

const (
	tombstoneUsed tombstoneState = iota
	tombstoneExpired
)

type tombstone struct {
	state     tombstoneState
	createdAt time.Time
	sequence  uint64
}

type tombstoneEntry struct {
	id        string
	tombstone tombstone
}

// Service owns pending confirmations and replay-prevention tombstones.
type Service struct {
	now func() time.Time

	mu                sync.Mutex
	pending           map[string]*pendingConfirmation
	tombstones        map[string]tombstone
	tombstoneSequence uint64
}

// New creates a confirmation service. A nil clock uses time.Now.
func New(now func() time.Time) *Service {
	if now == nil {
		now = time.Now
	}
	return &Service{
		now:        now,
		pending:    make(map[string]*pendingConfirmation),
		tombstones: make(map[string]tombstone),
	}
}

// Prepare creates a 60-second ticket bound to the instance, token, scope,
// action, revision, parameter hash, and execute closure.
func (s *Service) Prepare(instanceID, tokenID, scope, action, summary, revision string, parameters map[string]string, execute Execute) (protocol.ConfirmationPrepareResponse, error) {
	if execute == nil {
		return protocol.ConfirmationPrepareResponse{}, newError(400, protocol.CodeInvalidRequest, "The confirmation execute callback is required.")
	}
	now := s.now()
	id, err := randomID()
	if err != nil {
		return protocol.ConfirmationPrepareResponse{}, err
	}
	pending := &pendingConfirmation{
		id:             id,
		instanceID:     instanceID,
		tokenID:        tokenID,
		requiredScope:  scope,
		action:         action,
		parametersHash: ComputeParametersHash(parameters),
		revision:       revision,
		createdAt:      now,
		expiresAt:      now.Add(confirmationLifetime),
		execute:        execute,
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	s.pruneExpiredLocked(now)
	if _, exists := s.pending[id]; exists {
		return protocol.ConfirmationPrepareResponse{}, errors.New("confirmation ID collision")
	}
	if _, exists := s.tombstones[id]; exists {
		return protocol.ConfirmationPrepareResponse{}, errors.New("confirmation ID collision")
	}
	s.pending[id] = pending

	response := protocol.NewConfirmationPrepareResponse()
	response.ConfirmationID = id
	response.Action = action
	response.Summary = summary
	response.CreatedAt = protocol.NewTimestamp(now)
	response.ExpiresAt = protocol.NewTimestamp(pending.expiresAt)
	response.ExpiresInSeconds = protocol.Ptr(protocol.ConfirmationLifetimeSeconds)
	return response, nil
}

// Execute validates and consumes a ticket while holding the service lock, then
// invokes its closure after unlocking. Failed closures remain consumed.
func (s *Service) Execute(instanceID, tokenID string, hasScope func(string) bool, confirmationID string) (protocol.OperationInfo, error) {
	s.mu.Lock()
	now := s.now()
	s.pruneExpiredLocked(now)
	pending, exists := s.pending[confirmationID]
	if !exists {
		if consumed, ok := s.tombstones[confirmationID]; ok {
			s.mu.Unlock()
			return protocol.OperationInfo{}, consumedError(consumed.state)
		}
		s.mu.Unlock()
		return protocol.OperationInfo{}, newError(404, protocol.CodeConfirmationNotFound, "The confirmation does not exist or was invalidated by an Agent restart.")
	}
	if pending.instanceID != instanceID || pending.tokenID != tokenID {
		s.mu.Unlock()
		return protocol.OperationInfo{}, newError(403, protocol.CodeForbidden, "The confirmation belongs to a different instance or token.")
	}
	if hasScope == nil || !hasScope(pending.requiredScope) {
		s.mu.Unlock()
		return protocol.OperationInfo{}, newError(403, protocol.CodeInsufficientScope, "The confirmation scope is no longer granted.")
	}
	if !now.Before(pending.expiresAt) {
		delete(s.pending, confirmationID)
		s.addTombstoneLocked(confirmationID, tombstoneExpired, now)
		s.trimTombstonesLocked()
		s.mu.Unlock()
		return protocol.OperationInfo{}, consumedError(tombstoneExpired)
	}

	delete(s.pending, confirmationID)
	s.addTombstoneLocked(confirmationID, tombstoneUsed, now)
	s.trimTombstonesLocked()
	execute := pending.execute
	s.mu.Unlock()
	return execute()
}

// ComputeParametersHash reproduces the C# hash: SHA-256 of compact JSON for a
// dictionary sorted by ordinal key, including System.Text.Json-style escaping.
func ComputeParametersHash(parameters map[string]string) string {
	type keyValue struct {
		key   string
		units []uint16
	}
	keys := make([]keyValue, 0, len(parameters))
	for key := range parameters {
		keys = append(keys, keyValue{key: key, units: utf16.Encode([]rune(key))})
	}
	sort.Slice(keys, func(i, j int) bool {
		left, right := keys[i].units, keys[j].units
		for index := 0; index < len(left) && index < len(right); index++ {
			if left[index] != right[index] {
				return left[index] < right[index]
			}
		}
		return len(left) < len(right)
	})

	var builder strings.Builder
	builder.WriteByte('{')
	for index, entry := range keys {
		if index != 0 {
			builder.WriteByte(',')
		}
		encodedKey, _ := protocol.Marshal(entry.key)
		encodedValue, _ := protocol.Marshal(parameters[entry.key])
		builder.Write(encodedKey)
		builder.WriteByte(':')
		builder.Write(encodedValue)
	}
	builder.WriteByte('}')
	digest := sha256.Sum256([]byte(builder.String()))
	return hex.EncodeToString(digest[:])
}

func (s *Service) pruneExpiredLocked(now time.Time) {
	for id, pending := range s.pending {
		if now.Before(pending.expiresAt) {
			continue
		}
		delete(s.pending, id)
		s.addTombstoneLocked(id, tombstoneExpired, now)
	}
	s.trimTombstonesLocked()
}

func (s *Service) addTombstoneLocked(id string, state tombstoneState, now time.Time) {
	s.tombstoneSequence++
	s.tombstones[id] = tombstone{state: state, createdAt: now, sequence: s.tombstoneSequence}
}

func (s *Service) trimTombstonesLocked() {
	if len(s.tombstones) <= maxTombstones {
		return
	}
	entries := make([]tombstoneEntry, 0, len(s.tombstones))
	for id, entry := range s.tombstones {
		entries = append(entries, tombstoneEntry{id: id, tombstone: entry})
	}
	sort.Slice(entries, func(i, j int) bool {
		if entries[i].tombstone.createdAt.Equal(entries[j].tombstone.createdAt) {
			return entries[i].tombstone.sequence < entries[j].tombstone.sequence
		}
		return entries[i].tombstone.createdAt.Before(entries[j].tombstone.createdAt)
	})
	for _, entry := range entries[:len(entries)-retainedTombstones] {
		delete(s.tombstones, entry.id)
	}
}

func consumedError(state tombstoneState) *Error {
	if state == tombstoneUsed {
		return newError(410, protocol.CodeConfirmationAlreadyUsed, "The confirmation has already been consumed and cannot be replayed.")
	}
	return newError(410, protocol.CodeConfirmationExpired, "The confirmation has expired.")
}

func randomID() (string, error) {
	buffer := make([]byte, 32)
	if _, err := rand.Read(buffer); err != nil {
		return "", err
	}
	return "cfm_" + hex.EncodeToString(buffer), nil
}
