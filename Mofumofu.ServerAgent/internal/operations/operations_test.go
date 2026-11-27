// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package operations

import (
	"context"
	"encoding/hex"
	"errors"
	"fmt"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

type recordingPublisher struct {
	mu        sync.Mutex
	frames    []protocol.OperationInfo
	failState string
	failure   error
	hook      func(protocol.OperationInfo)
}

func (p *recordingPublisher) PublishOperation(_ context.Context, operation protocol.OperationInfo) error {
	p.mu.Lock()
	p.frames = append(p.frames, cloneOperation(operation))
	hook := p.hook
	fail := p.failState == *operation.State
	failure := p.failure
	p.mu.Unlock()
	if hook != nil {
		hook(cloneOperation(operation))
	}
	if fail {
		return failure
	}
	return nil
}

func (p *recordingPublisher) snapshot() []protocol.OperationInfo {
	p.mu.Lock()
	defer p.mu.Unlock()
	frames := make([]protocol.OperationInfo, len(p.frames))
	for index, frame := range p.frames {
		frames[index] = cloneOperation(frame)
	}
	return frames
}

type workGate struct {
	started  chan struct{}
	canceled chan struct{}
	release  chan struct{}
	once     sync.Once
}

func newWorkGate() *workGate {
	return &workGate{
		started:  make(chan struct{}),
		canceled: make(chan struct{}),
		release:  make(chan struct{}),
	}
}

func (g *workGate) work(ctx context.Context) (Completion, error) {
	g.once.Do(func() { close(g.started) })
	<-ctx.Done()
	close(g.canceled)
	<-g.release
	return Completion{}, ctx.Err()
}

func TestStartPublishesLifecycleAndReturnsIndependentSnapshots(t *testing.T) {
	now := time.Date(2026, 11, 27, 8, 9, 10, 0, time.FixedZone("test", 2*60*60))
	terminal := make(chan protocol.OperationInfo, 1)
	publisher := &recordingPublisher{hook: func(operation protocol.OperationInfo) {
		if *operation.State == protocol.OperationStateSucceeded {
			terminal <- operation
		}
	}}
	service := New(publisher, func() time.Time { return now })
	started := make(chan struct{})
	release := make(chan struct{})
	metadata := map[string]string{"backup": "cold-test.zip"}
	operation, err := service.Start(context.Background(), "backup.create", protocol.ScopeBackup,
		func(context.Context) (Completion, error) {
			close(started)
			<-release
			return Completion{Message: "backup complete", Metadata: metadata}, nil
		})
	if err != nil {
		t.Fatal(err)
	}
	assertOperationID(t, operation.ID, "op_", 32)
	if operation.Kind != "backup.create" || (*operation.State != protocol.OperationStateQueued && *operation.State != protocol.OperationStateRunning) || !operation.CreatedAt.Equal(now) {
		t.Fatalf("started operation=%+v", operation)
	}
	select {
	case <-started:
	case <-time.After(5 * time.Second):
		t.Fatal("work did not start")
	}
	running, ok := service.Get(operation.ID)
	if !ok || *running.State != protocol.OperationStateRunning || running.StartedAt == nil || running.CompletedAt != nil {
		t.Fatalf("running operation=%+v, found=%v", running, ok)
	}
	active := service.Active()
	if len(active) != 1 || active[0].ID != operation.ID || *active[0].State != protocol.OperationStateRunning {
		t.Fatalf("active=%+v", active)
	}
	states := publishedStates(publisher.snapshot())
	if strings.Join(states, ",") != "queued,running" {
		t.Fatalf("published states=%v", states)
	}

	close(release)
	var completed protocol.OperationInfo
	select {
	case completed = <-terminal:
	case <-time.After(5 * time.Second):
		t.Fatal("terminal operation was not published")
	}
	if *completed.State != protocol.OperationStateSucceeded || completed.ProgressPercent == nil || *completed.ProgressPercent != 100 ||
		completed.Message == nil || *completed.Message != "backup complete" || completed.Metadata["backup"] != "cold-test.zip" ||
		completed.CompletedAt == nil || completed.Error != nil {
		t.Fatalf("completed operation=%+v", completed)
	}
	if states := publishedStates(publisher.snapshot()); strings.Join(states, ",") != "queued,running,succeeded" {
		t.Fatalf("published states=%v", states)
	}
	metadata["backup"] = "caller-mutated.zip"
	if active := service.Active(); len(active) != 0 {
		t.Fatalf("terminal operation remained active: %+v", active)
	}

	completed.Metadata["backup"] = "publisher-mutated.zip"
	*completed.State = protocol.OperationStateFailed
	stored, ok := service.Get(operation.ID)
	if !ok || *stored.State != protocol.OperationStateSucceeded || stored.Metadata["backup"] != "cold-test.zip" {
		t.Fatalf("publisher mutated service snapshot: %+v", stored)
	}
	stored.Metadata["backup"] = "caller-mutated-again.zip"
	*stored.State = protocol.OperationStateFailed
	stored, _ = service.Get(operation.ID)
	if *stored.State != protocol.OperationStateSucceeded || stored.Metadata["backup"] != "cold-test.zip" {
		t.Fatalf("Get returned service-owned data: %+v", stored)
	}
}

func TestRunWaitsAndPreservesOptionalCompletionFields(t *testing.T) {
	service := New(nil, nil)
	started := make(chan struct{})
	release := make(chan struct{})
	result := make(chan error, 1)
	go func() {
		operation, err := service.Run(context.Background(), "test", "read", func(context.Context) (Completion, error) {
			close(started)
			<-release
			return Completion{Metadata: map[string]string{}}, nil
		})
		if err == nil && (operation.Message != nil || operation.Metadata == nil || len(operation.Metadata) != 0) {
			err = fmt.Errorf("unexpected optional fields: %+v", operation)
		}
		result <- err
	}()
	<-started
	select {
	case err := <-result:
		t.Fatalf("Run returned before work completed: %v", err)
	default:
	}
	close(release)
	if err := <-result; err != nil {
		t.Fatal(err)
	}

	operation, err := service.Run(context.Background(), "test", "read", func(context.Context) (Completion, error) {
		return Completion{}, nil
	})
	if err != nil {
		t.Fatal(err)
	}
	if operation.Message != nil || operation.Metadata != nil || operation.ProgressPercent == nil || *operation.ProgressPercent != 100 {
		t.Fatalf("empty completion=%+v", operation)
	}
}

func TestRunContextCancelsWaitButNotWork(t *testing.T) {
	service := New(nil, nil)
	ctx, cancel := context.WithCancel(context.Background())
	started := make(chan struct{})
	release := make(chan struct{})
	finished := make(chan struct{})
	result := make(chan error, 1)
	go func() {
		_, err := service.Run(ctx, "test", "read", func(workCtx context.Context) (Completion, error) {
			close(started)
			<-release
			if workCtx.Err() != nil {
				return Completion{}, errors.New("request cancellation leaked into work")
			}
			close(finished)
			return Completion{}, nil
		})
		result <- err
	}()
	<-started
	cancel()
	if err := <-result; !errors.Is(err, context.Canceled) {
		t.Fatalf("Run error=%v", err)
	}
	close(release)
	select {
	case <-finished:
	case <-time.After(5 * time.Second):
		t.Fatal("work was canceled with the request")
	}
}

func TestQueuedPublishFailureDoesNotStartWork(t *testing.T) {
	failure := errors.New("journal unavailable")
	publisher := &recordingPublisher{failState: protocol.OperationStateQueued, failure: failure}
	service := New(publisher, nil)
	var called atomic.Bool
	_, err := service.Start(context.Background(), "test", "read", func(context.Context) (Completion, error) {
		called.Store(true)
		return Completion{}, nil
	})
	if !errors.Is(err, failure) {
		t.Fatalf("Start error=%v", err)
	}
	frames := publisher.snapshot()
	if called.Load() || len(service.Active()) != 0 {
		t.Fatalf("work started or operation retained: called=%v active=%+v", called.Load(), service.Active())
	}
	if len(frames) != 1 {
		t.Fatalf("published frames=%+v", frames)
	}
	if _, retained := service.Get(frames[0].ID); retained {
		t.Fatalf("operation %q was retained after queued publication failed", frames[0].ID)
	}
	if states := publishedStates(frames); strings.Join(states, ",") != "queued" {
		t.Fatalf("published states=%v", states)
	}
}

func TestRunningPublishFailureFailsWithoutInvokingWork(t *testing.T) {
	failure := errors.New("running frame failed")
	publisher := &recordingPublisher{failState: protocol.OperationStateRunning, failure: failure}
	service := New(publisher, nil)
	var called atomic.Bool
	operation, err := service.Run(context.Background(), "test", "read", func(context.Context) (Completion, error) {
		called.Store(true)
		return Completion{}, nil
	})
	if err != nil {
		t.Fatal(err)
	}
	if called.Load() {
		t.Fatal("work ran after running publication failed")
	}
	assertFailedOperation(t, operation, protocol.CodeOperationFailed, "The operation failed.")
	if operation.StartedAt == nil || operation.CompletedAt == nil {
		t.Fatalf("missing running/terminal timestamps: %+v", operation)
	}
	if states := publishedStates(publisher.snapshot()); strings.Join(states, ",") != "queued,running,failed" {
		t.Fatalf("published states=%v", states)
	}
}

func TestSucceededPublishFailureIsConvertedToFailed(t *testing.T) {
	publisher := &recordingPublisher{failState: protocol.OperationStateSucceeded, failure: errors.New("success frame failed")}
	service := New(publisher, nil)
	operation, err := service.Run(context.Background(), "test", "read", func(context.Context) (Completion, error) {
		return Completion{Message: "not published", Metadata: map[string]string{"key": "value"}}, nil
	})
	if err != nil {
		t.Fatal(err)
	}
	assertFailedOperation(t, operation, protocol.CodeOperationFailed, "The operation failed.")
	if operation.Message == nil || *operation.Message != "not published" || operation.Metadata["key"] != "value" ||
		operation.ProgressPercent == nil || *operation.ProgressPercent != 100 {
		t.Fatalf("failed publication did not preserve C# success fields: %+v", operation)
	}
	if states := publishedStates(publisher.snapshot()); strings.Join(states, ",") != "queued,running,succeeded,failed" {
		t.Fatalf("published states=%v", states)
	}
}

func TestWorkErrorCodeAndTerminalPublishFailure(t *testing.T) {
	publisher := &recordingPublisher{failState: protocol.OperationStateFailed, failure: errors.New("terminal frame failed")}
	service := New(publisher, nil)
	operation, err := service.Run(context.Background(), "control.start", protocol.ScopeControl, func(context.Context) (Completion, error) {
		return Completion{}, &Error{Status: 409, Code: protocol.CodeServerRunning, Message: "The server is already running."}
	})
	if err != nil {
		t.Fatal(err)
	}
	assertFailedOperation(t, operation, protocol.CodeServerRunning, "The server is already running.")
	if states := publishedStates(publisher.snapshot()); strings.Join(states, ",") != "queued,running,failed" {
		t.Fatalf("published states=%v", states)
	}
}

func TestWrappedErrorCodeIsUsedInNestedAPIError(t *testing.T) {
	service := New(nil, nil)
	operation, err := service.Run(context.Background(), "test", "read", func(context.Context) (Completion, error) {
		return Completion{}, fmt.Errorf("backup operation: %w", &Error{Code: "backup_failed", Message: "backup failed"})
	})
	if err != nil {
		t.Fatal(err)
	}
	assertFailedOperation(t, operation, "backup_failed", "backup operation: backup failed")
}

func TestStopCancelsAllWorkAndWaitsForTerminalPublication(t *testing.T) {
	publisher := &recordingPublisher{}
	service := New(publisher, nil)
	first, second := newWorkGate(), newWorkGate()
	firstInfo, err := service.Start(context.Background(), "first", "read", first.work)
	if err != nil {
		t.Fatal(err)
	}
	secondInfo, err := service.Start(context.Background(), "second", "read", second.work)
	if err != nil {
		t.Fatal(err)
	}
	waitSignal(t, first.started, "first work start")
	waitSignal(t, second.started, "second work start")
	if active := service.Active(); len(active) != 2 {
		t.Fatalf("active=%+v", active)
	}

	stopped := make(chan error, 1)
	go func() { stopped <- service.Stop(context.Background()) }()
	waitSignal(t, first.canceled, "first work cancellation")
	waitSignal(t, second.canceled, "second work cancellation")
	select {
	case err := <-stopped:
		t.Fatalf("Stop returned before work finished: %v", err)
	default:
	}
	close(first.release)
	close(second.release)
	if err := <-stopped; err != nil {
		t.Fatal(err)
	}
	for _, info := range []protocol.OperationInfo{firstInfo, secondInfo} {
		operation, ok := service.Get(info.ID)
		if !ok {
			t.Fatalf("operation %s was lost", info.ID)
		}
		assertFailedOperation(t, operation, protocol.CodeOperationFailed, "The operation failed.")
	}
	stateCounts := make(map[string]int)
	for _, state := range publishedStates(publisher.snapshot()) {
		stateCounts[state]++
	}
	if stateCounts[protocol.OperationStateQueued] != 2 || stateCounts[protocol.OperationStateRunning] != 2 || stateCounts[protocol.OperationStateFailed] != 2 {
		t.Fatalf("published state counts=%v", stateCounts)
	}
	if err := service.Stop(context.Background()); err != nil {
		t.Fatal(err)
	}
	var called atomic.Bool
	if _, err := service.Start(context.Background(), "late", "read", func(context.Context) (Completion, error) {
		called.Store(true)
		return Completion{}, nil
	}); !errors.Is(err, context.Canceled) {
		t.Fatalf("Start after Stop error=%v", err)
	}
	if called.Load() {
		t.Fatal("work started after Stop")
	}
}

func TestStopContextBoundsWaitAndCanBeRetried(t *testing.T) {
	service := New(nil, nil)
	gate := newWorkGate()
	if _, err := service.Start(context.Background(), "test", "read", gate.work); err != nil {
		t.Fatal(err)
	}
	waitSignal(t, gate.started, "work start")
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if err := service.Stop(ctx); !errors.Is(err, context.Canceled) {
		t.Fatalf("Stop error=%v", err)
	}
	waitSignal(t, gate.canceled, "work cancellation")
	select {
	case <-service.stopDone:
		t.Fatal("Stop completed while work was still blocked")
	default:
	}
	close(gate.release)
	waitSignal(t, service.stopDone, "operation completion")
	if err := service.Stop(context.Background()); err != nil {
		t.Fatal(err)
	}
}

func TestWorkPanicBecomesFailedOperation(t *testing.T) {
	service := New(nil, nil)
	operation, err := service.Run(context.Background(), "test", "read", func(context.Context) (Completion, error) {
		panic("unexpected")
	})
	if err != nil {
		t.Fatal(err)
	}
	assertFailedOperation(t, operation, protocol.CodeOperationFailed, "The operation failed.")
}

func TestErrorMappingContract(t *testing.T) {
	failure := &Error{
		Status:      409,
		Code:        "server_running",
		Message:     "The server is already running.",
		Details:     map[string]string{"state": "running"},
		OperationID: "op_test",
	}
	var mapped interface {
		ErrorCode() string
		HTTPStatus() int
		ErrorDetails() map[string]string
		OperationIdentifier() string
	}
	if !errors.As(failure, &mapped) || mapped.ErrorCode() != failure.Code || mapped.HTTPStatus() != failure.Status ||
		mapped.ErrorDetails()["state"] != "running" || mapped.OperationIdentifier() != failure.OperationID {
		t.Fatalf("mapped error=%+v", mapped)
	}
}

func assertFailedOperation(t *testing.T, operation protocol.OperationInfo, code, message string) {
	t.Helper()
	if *operation.State != protocol.OperationStateFailed || operation.Error == nil {
		t.Fatalf("operation is not failed with an error: %+v", operation)
	}
	if operation.Error.Code != code || operation.Error.Message != message || operation.Error.OperationID == nil || *operation.Error.OperationID != operation.ID {
		t.Fatalf("nested error=%+v operation=%+v", operation.Error, operation)
	}
	if operation.Error.GetAPIVersion() != protocol.APIVersion {
		t.Fatalf("nested apiVersion=%q", operation.Error.GetAPIVersion())
	}
}

func assertOperationID(t *testing.T, id, prefix string, hexLength int) {
	t.Helper()
	if !strings.HasPrefix(id, prefix) || len(id) != len(prefix)+hexLength {
		t.Fatalf("invalid ID %q", id)
	}
	decoded, err := hex.DecodeString(strings.TrimPrefix(id, prefix))
	if err != nil || len(decoded)*2 != hexLength || strings.ToLower(id) != id {
		t.Fatalf("invalid ID encoding %q: %v", id, err)
	}
}

func publishedStates(frames []protocol.OperationInfo) []string {
	states := make([]string, len(frames))
	for index, frame := range frames {
		states[index] = *frame.State
	}
	return states
}

func waitSignal(t *testing.T, signal <-chan struct{}, description string) {
	t.Helper()
	select {
	case <-signal:
	case <-time.After(5 * time.Second):
		t.Fatalf("timed out waiting for %s", description)
	}
}
