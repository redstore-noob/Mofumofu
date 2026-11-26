// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package audit

import (
	"context"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestExecuteWritesOrderedDurableRecords(t *testing.T) {
	path := filepath.Join(t.TempDir(), "audit.jsonl")
	sink, err := Open(path)
	if err != nil {
		t.Fatal(err)
	}
	defer sink.Close()

	called := false
	err = sink.Execute(context.Background(), Actor{TokenID: "token-1"}, "file.write",
		Parameters{{Key: "path", Value: "config/a.txt"}, {Key: "contentBytes", Value: "3"}},
		func() error { called = true; return nil })
	if err != nil {
		t.Fatal(err)
	}
	if !called {
		t.Fatal("mutation was not called")
	}
	payload, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	lines := strings.Split(strings.TrimSpace(string(payload)), "\n")
	if len(lines) != 2 {
		t.Fatalf("got %d records: %s", len(lines), payload)
	}
	if !strings.Contains(lines[0], `"result":"pending"`) ||
		!strings.Contains(lines[1], `"result":"succeeded"`) {
		t.Fatalf("unexpected records: %s", payload)
	}
	if !strings.Contains(lines[0], `"parameters":{"path":"config/a.txt","contentBytes":"3"}`) {
		t.Fatalf("parameter order changed: %s", lines[0])
	}
}

func TestPendingFailurePreventsMutation(t *testing.T) {
	sink := &Sink{}
	called := false
	err := sink.Execute(context.Background(), Actor{TokenID: "t"}, "x", nil, func() error {
		called = true
		return nil
	})
	if !errors.Is(err, ErrUnavailable) {
		t.Fatalf("got %v", err)
	}
	if called {
		t.Fatal("mutation ran without a pending audit record")
	}
}

func TestFailureRecordsCode(t *testing.T) {
	path := filepath.Join(t.TempDir(), "audit.jsonl")
	sink, err := Open(path)
	if err != nil {
		t.Fatal(err)
	}
	defer sink.Close()
	want := errors.New("revision")
	coded := testCodedError{want}
	err = sink.Execute(context.Background(), Actor{TokenID: "t"}, "file.write", nil, func() error { return coded })
	if !errors.Is(err, want) {
		t.Fatalf("got %v", err)
	}
	payload, _ := os.ReadFile(path)
	if !strings.Contains(string(payload), `"result":"failed:revision_conflict"`) {
		t.Fatalf("unexpected audit: %s", payload)
	}
}

func TestParametersMarshalEmptyObject(t *testing.T) {
	payload, err := json.Marshal(Parameters(nil))
	if err != nil {
		t.Fatal(err)
	}
	if string(payload) != "{}" {
		t.Fatalf("got %s", payload)
	}
}

type testCodedError struct{ error }

func (e testCodedError) ErrorCode() string { return "revision_conflict" }
func (e testCodedError) Unwrap() error     { return e.error }
