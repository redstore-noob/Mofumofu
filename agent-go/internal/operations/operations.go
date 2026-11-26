// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

// Package operations owns the Agent's in-memory operation lifecycle.
package operations

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"
	"maps"
	"sort"
	"sync"
	"time"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

// Publisher receives each operation state transition. The queued publication
// uses Start's context; later publications are detached from lifetime
// cancellation so a stopped operation can still publish its terminal state.
type Publisher interface {
	PublishOperation(context.Context, protocol.OperationInfo) error
}

// Completion is returned by successful work. An empty message is omitted; a
// non-nil metadata map is retained even when it is empty.
type Completion struct {
	Message  string
	Metadata map[string]string
}

// Work runs with the service lifetime context, not an individual request's
// context. Stop cancels that shared context.
type Work func(context.Context) (Completion, error)

type record struct {
	requiredScope string
	current       protocol.OperationInfo
	done          chan struct{}
}

// Service tracks operations until the process exits. It deliberately provides
// no persistence, idempotency, or concurrency limit.
type Service struct {
	publisher Publisher
	now       func() time.Time

	mu         sync.Mutex
	operations map[string]*record
	lifetime   context.Context
	cancel     context.CancelFunc
	stopped    bool
	workers    sync.WaitGroup
	stopOnce   sync.Once
	stopDone   chan struct{}
}

// New creates an operation service. A nil clock uses time.Now; a nil publisher
// discards frames.
func New(publisher Publisher, now func() time.Time) *Service {
	if now == nil {
		now = time.Now
	}
	lifetime, cancel := context.WithCancel(context.Background())
	return &Service{
		publisher:  publisher,
		now:        now,
		operations: make(map[string]*record),
		lifetime:   lifetime,
		cancel:     cancel,
		stopDone:   make(chan struct{}),
	}
}

// Start publishes queued and returns without waiting for work. If the queued
// publication fails, the operation is removed and work is never invoked.
func (s *Service) Start(ctx context.Context, kind, scope string, work Work) (protocol.OperationInfo, error) {
	operation, _, err := s.start(ctx, kind, scope, work)
	return operation, err
}

// Run starts an operation and waits for its terminal snapshot. Canceling ctx
// stops the wait but does not cancel the shared operation lifetime.
func (s *Service) Run(ctx context.Context, kind, scope string, work Work) (protocol.OperationInfo, error) {
	if ctx == nil {
		ctx = context.Background()
	}
	_, started, err := s.start(ctx, kind, scope, work)
	if err != nil {
		return protocol.OperationInfo{}, err
	}
	select {
	case <-started.done:
		return s.snapshot(started), nil
	case <-ctx.Done():
		return protocol.OperationInfo{}, ctx.Err()
	}
}

// Get returns an independent snapshot of an operation.
func (s *Service) Get(id string) (protocol.OperationInfo, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	operation, ok := s.operations[id]
	if !ok {
		return protocol.OperationInfo{}, false
	}
	return cloneOperation(operation.current), true
}

// RequiredScope returns the scope captured when an operation was created.
func (s *Service) RequiredScope(id string) (string, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	operation, ok := s.operations[id]
	if !ok {
		return "", false
	}
	return operation.requiredScope, true
}

// Active returns independent queued and running snapshots in creation order.
func (s *Service) Active() []protocol.OperationInfo {
	s.mu.Lock()
	active := make([]protocol.OperationInfo, 0)
	for _, operation := range s.operations {
		if *operation.current.State == protocol.OperationStateQueued || *operation.current.State == protocol.OperationStateRunning {
			active = append(active, cloneOperation(operation.current))
		}
	}
	s.mu.Unlock()
	sort.Slice(active, func(i, j int) bool {
		if active[i].CreatedAt.Equal(active[j].CreatedAt.Time) {
			return active[i].ID < active[j].ID
		}
		return active[i].CreatedAt.Before(active[j].CreatedAt.Time)
	})
	return active
}

// Stop prevents new operations, cancels the shared lifetime, and waits for all
// accepted operations to finish publishing their terminal snapshots. A ctx
// deadline bounds the wait; a later Stop call can wait again.
func (s *Service) Stop(ctx context.Context) error {
	if ctx == nil {
		ctx = context.Background()
	}
	s.mu.Lock()
	if !s.stopped {
		s.stopped = true
		s.cancel()
	}
	s.mu.Unlock()
	s.stopOnce.Do(func() {
		go func() {
			s.workers.Wait()
			close(s.stopDone)
		}()
	})
	select {
	case <-s.stopDone:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}

func (s *Service) start(ctx context.Context, kind, scope string, work Work) (protocol.OperationInfo, *record, error) {
	if ctx == nil {
		ctx = context.Background()
	}
	if err := ctx.Err(); err != nil {
		return protocol.OperationInfo{}, nil, err
	}
	id, err := randomID(16)
	if err != nil {
		return protocol.OperationInfo{}, nil, err
	}
	createdAt := protocol.NewTimestamp(s.now())
	queued := protocol.OperationInfo{
		ID:        id,
		Kind:      kind,
		State:     protocol.Ptr(protocol.OperationStateQueued),
		CreatedAt: createdAt,
	}
	operation := &record{
		requiredScope: scope,
		current:       queued,
		done:          make(chan struct{}),
	}

	s.mu.Lock()
	if s.stopped {
		s.mu.Unlock()
		return protocol.OperationInfo{}, nil, context.Canceled
	}
	if _, exists := s.operations[id]; exists {
		s.mu.Unlock()
		return protocol.OperationInfo{}, nil, errors.New("operation ID collision")
	}
	s.operations[id] = operation
	s.workers.Add(1)
	s.mu.Unlock()

	if err := s.publish(ctx, operation.current); err != nil {
		s.mu.Lock()
		delete(s.operations, id)
		s.mu.Unlock()
		s.workers.Done()
		return protocol.OperationInfo{}, nil, err
	}

	go s.execute(operation, work)
	return s.snapshot(operation), operation, nil
}

func (s *Service) execute(operation *record, work Work) {
	defer func() {
		s.mu.Lock()
		close(operation.done)
		s.mu.Unlock()
		s.workers.Done()
	}()
	defer func() {
		if recovered := recover(); recovered != nil {
			s.fail(operation, errors.New("the operation panicked"))
		}
	}()

	startedAt := protocol.NewTimestamp(s.now())
	s.mu.Lock()
	operation.current.State = protocol.Ptr(protocol.OperationStateRunning)
	started := &startedAt
	operation.current.StartedAt = started
	s.mu.Unlock()
	publishContext := context.WithoutCancel(s.lifetime)
	if err := s.publish(publishContext, operation.current); err != nil {
		s.fail(operation, err)
		return
	}

	completion, err := work(s.lifetime)
	if err != nil {
		s.fail(operation, err)
		return
	}
	s.succeed(operation, completion)
}

func (s *Service) succeed(operation *record, completion Completion) {
	completedAt := protocol.NewTimestamp(s.now())
	progress := float64(100)
	s.mu.Lock()
	operation.current.State = protocol.Ptr(protocol.OperationStateSucceeded)
	operation.current.CompletedAt = &completedAt
	operation.current.ProgressPercent = &progress
	if completion.Message != "" {
		operation.current.Message = protocol.Ptr(completion.Message)
	}
	operation.current.Metadata = maps.Clone(completion.Metadata)
	terminal := cloneOperation(operation.current)
	s.mu.Unlock()
	if err := s.publish(context.WithoutCancel(s.lifetime), terminal); err != nil {
		s.fail(operation, err)
	}
}

func (s *Service) fail(operation *record, cause error) {
	completedAt := protocol.NewTimestamp(s.now())
	s.mu.Lock()
	operation.current.State = protocol.Ptr(protocol.OperationStateFailed)
	operation.current.CompletedAt = &completedAt
	operation.current.Error = apiErrorFor(cause, operation.current.ID)
	terminal := cloneOperation(operation.current)
	s.mu.Unlock()
	_ = s.publish(context.WithoutCancel(s.lifetime), terminal)
}

func (s *Service) publish(ctx context.Context, operation protocol.OperationInfo) (err error) {
	defer func() {
		if recover() != nil {
			err = errors.New("the operation publisher panicked")
		}
	}()
	if s.publisher == nil {
		return nil
	}
	return s.publisher.PublishOperation(ctx, cloneOperation(operation))
}

func (s *Service) snapshot(operation *record) protocol.OperationInfo {
	s.mu.Lock()
	defer s.mu.Unlock()
	return cloneOperation(operation.current)
}

func apiErrorFor(cause error, operationID string) *protocol.APIError {
	result := protocol.NewAPIError()
	result.Code = protocol.CodeOperationFailed
	result.Message = "The operation failed."
	result.OperationID = protocol.Ptr(operationID)
	var coded interface{ ErrorCode() string }
	if errors.As(cause, &coded) {
		if code := coded.ErrorCode(); code != "" {
			result.Code = code
			result.Message = cause.Error()
		}
	}
	return &result
}

func cloneOperation(source protocol.OperationInfo) protocol.OperationInfo {
	result := source
	result.State = clonePointer(source.State)
	result.StartedAt = clonePointer(source.StartedAt)
	result.CompletedAt = clonePointer(source.CompletedAt)
	result.ProgressPercent = clonePointer(source.ProgressPercent)
	result.Message = clonePointer(source.Message)
	result.Metadata = maps.Clone(source.Metadata)
	if source.Error != nil {
		apiError := *source.Error
		apiError.APIVersion = clonePointer(source.Error.APIVersion)
		apiError.RequestID = clonePointer(source.Error.RequestID)
		apiError.OperationID = clonePointer(source.Error.OperationID)
		apiError.Details = maps.Clone(source.Error.Details)
		result.Error = &apiError
	}
	return result
}

func clonePointer[T any](source *T) *T {
	if source == nil {
		return nil
	}
	result := *source
	return &result
}

func randomID(size int) (string, error) {
	buffer := make([]byte, size)
	if _, err := rand.Read(buffer); err != nil {
		return "", fmt.Errorf("generate operation ID: %w", err)
	}
	return "op_" + hex.EncodeToString(buffer), nil
}
