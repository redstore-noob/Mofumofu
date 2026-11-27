// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

// Package audit implements the append-only JSONL audit sink used by both the
// HTTP API and the local TUI.
package audit

import (
	"context"
	"errors"
	"fmt"
	"os"
	"sync"
	"time"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

// Actor identifies the authenticated caller. The local TUI uses a dedicated
// non-network identity but still passes through this package.
type Actor struct {
	TokenID       string
	Authorization string
}

// Parameter is one ordered audit parameter. C#'s Dictionary preserves insertion
// order in the emitted JSON, so a slice is used instead of a Go map.
type Parameter struct {
	Key   string `json:"key"`
	Value string `json:"value"`
}

// Parameters is an insertion-ordered JSON object.
type Parameters []Parameter

func (p Parameters) MarshalJSON() ([]byte, error) {
	if p == nil {
		return []byte("{}"), nil
	}
	var out []byte
	out = append(out, '{')
	for i, item := range p {
		if i > 0 {
			out = append(out, ',')
		}
		key, err := protocol.Marshal(item.Key)
		if err != nil {
			return nil, err
		}
		value, err := protocol.Marshal(item.Value)
		if err != nil {
			return nil, err
		}
		out = append(out, key...)
		out = append(out, ':')
		out = append(out, value...)
	}
	out = append(out, '}')
	return out, nil
}

type entry struct {
	Timestamp  string     `json:"timestamp"`
	TokenID    string     `json:"tokenId"`
	Action     string     `json:"action"`
	Parameters Parameters `json:"parameters"`
	Result     string     `json:"result"`
}

// ErrUnavailable means the audit side effect could not be persisted. Callers
// must not execute a mutation when the pending record cannot be written.
var ErrUnavailable = errors.New("audit unavailable")

// Sink serializes writes and flushes each record to disk.
type Sink struct {
	mu   sync.Mutex
	file *os.File
	now  func() time.Time
}

func Open(path string) (*Sink, error) {
	file, err := os.OpenFile(path, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o600)
	if err != nil {
		return nil, fmt.Errorf("open audit journal: %w", err)
	}
	return &Sink{file: file, now: time.Now}, nil
}

func (s *Sink) Close() error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.file == nil {
		return nil
	}
	err := s.file.Close()
	s.file = nil
	return err
}

func (s *Sink) Write(actor Actor, action string, parameters Parameters, result string) error {
	if s == nil {
		return ErrUnavailable
	}
	now := s.now
	if now == nil {
		now = time.Now
	}
	record := entry{
		Timestamp:  formatTimestamp(now().UTC()),
		TokenID:    actor.TokenID,
		Action:     action,
		Parameters: parameters,
		Result:     result,
	}
	payload, err := protocol.Marshal(record)
	if err != nil {
		return fmt.Errorf("encode audit entry: %w", err)
	}
	payload = append(payload, '\n')

	s.mu.Lock()
	defer s.mu.Unlock()
	if s.file == nil {
		return ErrUnavailable
	}
	if _, err := s.file.Write(payload); err != nil {
		return fmt.Errorf("append audit entry: %w", err)
	}
	if err := s.file.Sync(); err != nil {
		return fmt.Errorf("sync audit journal: %w", err)
	}
	return nil
}

// Execute mirrors AuditExecutor: pending is durable before the mutation, then
// succeeded or failed:<code> is appended. A failed terminal audit replaces the
// mutation error, matching the C# behavior (including its side-effect caveat).
func (s *Sink) Execute(ctx context.Context, actor Actor, action string, parameters Parameters, work func() error) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	if err := s.Write(actor, action, parameters, "pending"); err != nil {
		return ErrUnavailable
	}
	if err := work(); err != nil {
		result := "failed"
		var coded interface{ ErrorCode() string }
		if errors.As(err, &coded) && coded.ErrorCode() != "" {
			result += ":" + coded.ErrorCode()
		}
		if auditErr := s.Write(actor, action, parameters, result); auditErr != nil {
			return ErrUnavailable
		}
		return err
	}
	if err := s.Write(actor, action, parameters, "succeeded"); err != nil {
		return ErrUnavailable
	}
	return nil
}

func formatTimestamp(t time.Time) string {
	return t.Format("2006-01-02T15:04:05.9999999+00:00")
}
