// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package confirmation

import (
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"net/http"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

type testClock struct {
	mu  sync.Mutex
	now time.Time
}

func newTestClock() *testClock {
	return &testClock{now: time.Date(2026, 11, 27, 8, 0, 0, 0, time.UTC)}
}

func (c *testClock) Now() time.Time {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.now
}

func (c *testClock) Advance(duration time.Duration) {
	c.mu.Lock()
	c.now = c.now.Add(duration)
	c.mu.Unlock()
}

func TestPrepareBindsRequestAndHashesParameters(t *testing.T) {
	clock := newTestClock()
	service := New(clock.Now)
	parameters := map[string]string{"b": "2", "a": "1"}
	var called atomic.Bool
	execute := func() (protocol.OperationInfo, error) {
		called.Store(true)
		return testOperation(), nil
	}
	response, err := service.Prepare("survival", "token-a", protocol.ScopeFiles, protocol.ConfirmationActionFileDelete,
		"Delete config/settings.txt", "r_revision", parameters, execute)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.HasPrefix(response.ConfirmationID, "cfm_") || len(response.ConfirmationID) != 68 ||
		strings.ToLower(response.ConfirmationID) != response.ConfirmationID {
		t.Fatalf("invalid confirmation ID %q", response.ConfirmationID)
	}
	if decoded, err := hex.DecodeString(strings.TrimPrefix(response.ConfirmationID, "cfm_")); err != nil || len(decoded) != 32 {
		t.Fatalf("invalid confirmation ID encoding: %v", err)
	}
	if response.GetAPIVersion() != protocol.APIVersion || response.Action != protocol.ConfirmationActionFileDelete ||
		response.Summary != "Delete config/settings.txt" || response.ExpiresInSeconds == nil || *response.ExpiresInSeconds != 60 ||
		!response.CreatedAt.Equal(clock.Now()) || !response.ExpiresAt.Equal(clock.Now().Add(time.Minute)) {
		t.Fatalf("prepare response=%+v", response)
	}
	service.mu.Lock()
	pending := service.pending[response.ConfirmationID]
	if pending == nil {
		service.mu.Unlock()
		t.Fatal("prepared confirmation was not retained")
	}
	if pending.instanceID != "survival" || pending.tokenID != "token-a" || pending.requiredScope != protocol.ScopeFiles ||
		pending.action != protocol.ConfirmationActionFileDelete || pending.revision != "r_revision" || pending.execute == nil || called.Load() {
		service.mu.Unlock()
		t.Fatalf("pending binding=%+v", pending)
	}
	hash := pending.parametersHash
	service.mu.Unlock()
	if want := hashJSON(`{"a":"1","b":"2"}`); hash != want {
		t.Fatalf("parameters hash=%s want=%s", hash, want)
	}
}

func TestComputeParametersHashMatchesOrdinalSystemTextJsonEncoding(t *testing.T) {
	parameters := map[string]string{"a": "&<>", "b": "é"}
	want := hashJSON(`{"a":"\u0026\u003C\u003E","b":"\u00E9"}`)
	if got := ComputeParametersHash(parameters); got != want {
		t.Fatalf("escaped hash=%s want=%s", got, want)
	}
	parameters = map[string]string{"\ue000": "bmp", "\U00010000": "supplementary"}
	want = hashJSON(`{"\uD800\uDC00":"supplementary","\uE000":"bmp"}`)
	if got := ComputeParametersHash(parameters); got != want {
		t.Fatalf("ordinal hash=%s want=%s", got, want)
	}
	if ComputeParametersHash(nil) != ComputeParametersHash(map[string]string{}) {
		t.Fatal("nil and empty parameter dictionaries produced different hashes")
	}
}

func TestExecuteSucceedsOnceAndReplayIsGone(t *testing.T) {
	service := New(nil)
	prepared := prepare(t, service, "token-a", func() (protocol.OperationInfo, error) {
		return testOperation(), nil
	})
	operation, err := service.Execute("survival", "token-a", func(scope string) bool {
		return scope == protocol.ScopeFiles
	}, prepared.ConfirmationID)
	if err != nil {
		t.Fatal(err)
	}
	if operation.ID != "op_test" {
		t.Fatalf("operation=%+v", operation)
	}
	_, err = service.Execute("survival", "token-a", func(string) bool { return true }, prepared.ConfirmationID)
	assertConfirmationError(t, err, http.StatusGone, protocol.CodeConfirmationAlreadyUsed)
}

func TestExecuteAtExpiryBoundaryIsExpiredAndNotExecuted(t *testing.T) {
	clock := newTestClock()
	service := New(clock.Now)
	var calls atomic.Int32
	prepared := prepare(t, service, "token-a", func() (protocol.OperationInfo, error) {
		calls.Add(1)
		return testOperation(), nil
	})
	clock.Advance(confirmationLifetime)
	_, err := service.Execute("survival", "token-b", func(string) bool { return true }, prepared.ConfirmationID)
	assertConfirmationError(t, err, http.StatusGone, protocol.CodeConfirmationExpired)
	_, err = service.Execute("survival", "token-a", func(string) bool { return true }, prepared.ConfirmationID)
	assertConfirmationError(t, err, http.StatusGone, protocol.CodeConfirmationExpired)
	if calls.Load() != 0 {
		t.Fatalf("expired callback invoked %d times", calls.Load())
	}

	prepared = prepare(t, service, "token-a", func() (protocol.OperationInfo, error) {
		return testOperation(), nil
	})
	clock.Advance(confirmationLifetime - time.Nanosecond)
	if _, err := service.Execute("survival", "token-a", func(string) bool { return true }, prepared.ConfirmationID); err != nil {
		t.Fatalf("confirmation expired before its boundary: %v", err)
	}
}

func TestPreparePrunesExpiredTicketsIntoExpiredTombstones(t *testing.T) {
	clock := newTestClock()
	service := New(clock.Now)
	expired := prepare(t, service, "token-a", func() (protocol.OperationInfo, error) {
		return testOperation(), nil
	})
	clock.Advance(confirmationLifetime)
	current := prepare(t, service, "token-a", func() (protocol.OperationInfo, error) {
		return testOperation(), nil
	})
	service.mu.Lock()
	_, pending := service.pending[expired.ConfirmationID]
	state, tombstoned := service.tombstones[expired.ConfirmationID]
	service.mu.Unlock()
	if pending || !tombstoned || state.state != tombstoneExpired {
		t.Fatalf("expired ticket was not pruned: pending=%v tombstone=%+v", pending, state)
	}
	_, err := service.Execute("survival", "token-a", func(string) bool { return true }, expired.ConfirmationID)
	assertConfirmationError(t, err, http.StatusGone, protocol.CodeConfirmationExpired)
	if _, err := service.Execute("survival", "token-a", func(string) bool { return true }, current.ConfirmationID); err != nil {
		t.Fatal(err)
	}
}

func TestDifferentTokenOrInstanceIsForbiddenWithoutConsuming(t *testing.T) {
	service := New(nil)
	prepared := prepare(t, service, "token-a", func() (protocol.OperationInfo, error) {
		return testOperation(), nil
	})
	_, err := service.Execute("survival", "token-b", func(string) bool { return true }, prepared.ConfirmationID)
	assertConfirmationError(t, err, http.StatusForbidden, protocol.CodeForbidden)
	_, err = service.Execute("other", "token-a", func(string) bool { return true }, prepared.ConfirmationID)
	assertConfirmationError(t, err, http.StatusForbidden, protocol.CodeForbidden)
	if _, err := service.Execute("survival", "token-a", func(string) bool { return true }, prepared.ConfirmationID); err != nil {
		t.Fatalf("rejected requests consumed the ticket: %v", err)
	}
	_, err = service.Execute("survival", "token-b", func(string) bool { return true }, prepared.ConfirmationID)
	assertConfirmationError(t, err, http.StatusGone, protocol.CodeConfirmationAlreadyUsed)
}

func TestScopeRevocationIsRejectedWithoutConsuming(t *testing.T) {
	service := New(nil)
	prepared := prepare(t, service, "token-a", func() (protocol.OperationInfo, error) {
		return testOperation(), nil
	})
	_, err := service.Execute("survival", "token-a", func(string) bool { return false }, prepared.ConfirmationID)
	assertConfirmationError(t, err, http.StatusForbidden, protocol.CodeInsufficientScope)
	_, err = service.Execute("survival", "token-a", nil, prepared.ConfirmationID)
	assertConfirmationError(t, err, http.StatusForbidden, protocol.CodeInsufficientScope)
	if _, err := service.Execute("survival", "token-a", func(scope string) bool { return scope == protocol.ScopeFiles }, prepared.ConfirmationID); err != nil {
		t.Fatalf("scope rejection consumed the ticket: %v", err)
	}
}

func TestConcurrentExecuteConsumesExactlyOnce(t *testing.T) {
	service := New(nil)
	var calls atomic.Int32
	prepared := prepare(t, service, "token-a", func() (protocol.OperationInfo, error) {
		calls.Add(1)
		time.Sleep(10 * time.Millisecond)
		return testOperation(), nil
	})
	const attempts = 32
	start := make(chan struct{})
	results := make(chan error, attempts)
	var workers sync.WaitGroup
	for index := 0; index < attempts; index++ {
		workers.Add(1)
		go func() {
			defer workers.Done()
			<-start
			_, err := service.Execute("survival", "token-a", func(string) bool { return true }, prepared.ConfirmationID)
			results <- err
		}()
	}
	close(start)
	workers.Wait()
	close(results)
	successes, replays := 0, 0
	for err := range results {
		if err == nil {
			successes++
			continue
		}
		assertConfirmationError(t, err, http.StatusGone, protocol.CodeConfirmationAlreadyUsed)
		replays++
	}
	if successes != 1 || replays != attempts-1 || calls.Load() != 1 {
		t.Fatalf("successes=%d replays=%d calls=%d", successes, replays, calls.Load())
	}
}

func TestExecuteInvokesClosureOutsideLock(t *testing.T) {
	service := New(nil)
	prepared := prepare(t, service, "token-a", func() (protocol.OperationInfo, error) {
		_, err := service.Execute("survival", "token-a", func(string) bool { return true }, "cfm_missing")
		if err == nil {
			return protocol.OperationInfo{}, errors.New("missing confirmation unexpectedly existed")
		}
		return testOperation(), nil
	})
	result := make(chan error, 1)
	go func() {
		_, err := service.Execute("survival", "token-a", func(string) bool { return true }, prepared.ConfirmationID)
		result <- err
	}()
	select {
	case err := <-result:
		if err != nil {
			t.Fatal(err)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("execute closure ran while the service lock was held")
	}
}

func TestExecuteFailureRemainsConsumed(t *testing.T) {
	service := New(nil)
	failure := errors.New("operation start failed")
	prepared := prepare(t, service, "token-a", func() (protocol.OperationInfo, error) {
		return protocol.OperationInfo{}, failure
	})
	_, err := service.Execute("survival", "token-a", func(string) bool { return true }, prepared.ConfirmationID)
	if !errors.Is(err, failure) {
		t.Fatalf("Execute error=%v", err)
	}
	_, err = service.Execute("survival", "token-a", func(string) bool { return true }, prepared.ConfirmationID)
	assertConfirmationError(t, err, http.StatusGone, protocol.CodeConfirmationAlreadyUsed)
}

func TestMissingConfirmationAndNilExecute(t *testing.T) {
	service := New(nil)
	for _, id := range []string{"", "cfm_missing"} {
		_, err := service.Execute("survival", "token-a", func(string) bool { return true }, id)
		assertConfirmationError(t, err, http.StatusNotFound, protocol.CodeConfirmationNotFound)
	}
	_, err := service.Prepare("survival", "token-a", protocol.ScopeFiles, "file.delete", "summary", "", nil, nil)
	assertConfirmationError(t, err, http.StatusBadRequest, protocol.CodeInvalidRequest)
	service.mu.Lock()
	defer service.mu.Unlock()
	if len(service.pending) != 0 {
		t.Fatalf("invalid prepare retained %d tickets", len(service.pending))
	}
}

func TestConfirmationsAreNotSharedAcrossServiceInstances(t *testing.T) {
	first := New(nil)
	prepared := prepare(t, first, "token-a", func() (protocol.OperationInfo, error) { return testOperation(), nil })
	second := New(nil)
	_, err := second.Execute("survival", "token-a", func(string) bool { return true }, prepared.ConfirmationID)
	assertConfirmationError(t, err, http.StatusNotFound, protocol.CodeConfirmationNotFound)
}

func TestTombstonesTrimTo2048AndEvictOldest(t *testing.T) {
	clock := newTestClock()
	service := New(clock.Now)
	ids := make([]string, maxTombstones+1)
	for index := range ids {
		ids[index] = prepare(t, service, "token-a", func() (protocol.OperationInfo, error) {
			return testOperation(), nil
		}).ConfirmationID
		clock.Advance(time.Second)
		if _, err := service.Execute("survival", "token-a", func(string) bool { return true }, ids[index]); err != nil {
			t.Fatal(err)
		}
		if index == maxTombstones-1 {
			service.mu.Lock()
			count := len(service.tombstones)
			service.mu.Unlock()
			if count != maxTombstones {
				t.Fatalf("tombstone count at limit=%d", count)
			}
		}
	}
	service.mu.Lock()
	count := len(service.tombstones)
	service.mu.Unlock()
	if count != retainedTombstones {
		t.Fatalf("tombstone count after trim=%d", count)
	}
	for _, index := range []int{0, retainedTombstones} {
		_, err := service.Execute("survival", "token-a", func(string) bool { return true }, ids[index])
		assertConfirmationError(t, err, http.StatusNotFound, protocol.CodeConfirmationNotFound)
	}
	for _, index := range []int{retainedTombstones + 1, maxTombstones} {
		_, err := service.Execute("survival", "token-a", func(string) bool { return true }, ids[index])
		assertConfirmationError(t, err, http.StatusGone, protocol.CodeConfirmationAlreadyUsed)
	}
}

func prepare(t *testing.T, service *Service, tokenID string, execute Execute) protocol.ConfirmationPrepareResponse {
	t.Helper()
	response, err := service.Prepare("survival", tokenID, protocol.ScopeFiles, protocol.ConfirmationActionFileDelete,
		"Delete config/settings.txt", "r_revision", map[string]string{"path": "config/settings.txt"}, execute)
	if err != nil {
		t.Fatal(err)
	}
	return response
}

func testOperation() protocol.OperationInfo {
	operation := protocol.NewOperationInfo()
	operation.ID = "op_test"
	operation.Kind = "file.delete"
	operation.State = protocol.Ptr(protocol.OperationStateSucceeded)
	return operation
}

func assertConfirmationError(t *testing.T, err error, status int, code string) {
	t.Helper()
	var apiError *Error
	if !errors.As(err, &apiError) {
		t.Fatalf("error=%v is not *confirmation.Error", err)
	}
	if apiError.ErrorCode() != code || apiError.HTTPStatus() != status || apiError.ErrorDetails() != nil || apiError.OperationIdentifier() != "" {
		t.Fatalf("mapped error=%+v", apiError)
	}
	var mapped interface {
		ErrorCode() string
		HTTPStatus() int
		ErrorDetails() map[string]string
		OperationIdentifier() string
	}
	if !errors.As(err, &mapped) || mapped.ErrorCode() != code || mapped.HTTPStatus() != status {
		t.Fatalf("error does not implement the Agent mapping contract: %v", err)
	}
}

func hashJSON(value string) string {
	digest := sha256.Sum256([]byte(value))
	return hex.EncodeToString(digest[:])
}
