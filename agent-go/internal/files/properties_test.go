// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package files

import (
	"context"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func stringPointer(value string) *string { return &value }

func TestPropertiesPatchPreservesDocumentShape(t *testing.T) {
	root := t.TempDir()
	files, _ := New(root)
	input := "# heading\r\nmax-players=10\r\nmax-players=20\r\nmotd = Hello\r\ncustom=old\r\n\r\nrcon.password=secret\r\n"
	if _, err := files.WriteProperties(context.Background(), input, "0"); err != nil {
		t.Fatal(err)
	}
	patcher := NewPropertyService(files, func() time.Time { return time.Unix(1_700_000_000, 0).UTC() })
	snapshot, err := patcher.Get(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if snapshot.Properties["max-players"] != "20" || snapshot.Properties["motd"] != " Hello" {
		t.Fatalf("properties=%v", snapshot.Properties)
	}
	if _, exists := snapshot.Properties["rcon.password"]; exists {
		t.Fatal("secret exposed")
	}
	result, err := patcher.Patch(context.Background(), snapshot.Revision, map[string]*string{
		"max-players": stringPointer("30"),
		"motd":        stringPointer("New"),
		"custom":      nil,
		"new-key":     stringPointer("new"),
	})
	if err != nil {
		t.Fatal(err)
	}
	if !result.RestartRequired || result.AppliedAt.Unix() != 1_700_000_000 {
		t.Fatalf("result=%+v", result)
	}
	payload, _ := os.ReadFile(filepath.Join(root, "server.properties"))
	want := "# heading\nmax-players=30\nmotd=New\nrcon.password=secret\nnew-key=new\n"
	if string(payload) != want {
		t.Fatalf("got:\n%s\nwant:\n%s", payload, want)
	}
	patcher.NotifyServerStarted()
	snapshot, err = patcher.Get(context.Background())
	if err != nil || snapshot.RestartRequired {
		t.Fatalf("snapshot=%+v err=%v", snapshot, err)
	}
}

func TestPropertiesValidation(t *testing.T) {
	root := t.TempDir()
	files, _ := New(root)
	if _, err := files.WriteProperties(context.Background(), "max-players=20\n", "0"); err != nil {
		t.Fatal(err)
	}
	patcher := NewPropertyService(files, nil)
	snapshot, err := patcher.Get(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	tests := []struct {
		changes map[string]*string
		status  int
		code    string
	}{
		{map[string]*string{}, 400, "invalid_request"},
		{map[string]*string{"max-players": nil}, 400, "invalid_request"},
		{map[string]*string{"max-players": stringPointer("yes")}, 400, "invalid_request"},
		{map[string]*string{"view-distance": stringPointer("2")}, 400, "invalid_request"},
		{map[string]*string{"difficulty": stringPointer("hardcore")}, 400, "invalid_request"},
		{map[string]*string{"bad key": stringPointer("x")}, 400, "invalid_request"},
		{map[string]*string{"RCON.PASSWORD": stringPointer("x")}, 403, "forbidden"},
	}
	for _, test := range tests {
		_, err := patcher.Patch(context.Background(), snapshot.Revision, test.changes)
		assertFileError(t, err, test.status, test.code)
	}
	if _, err := patcher.Patch(context.Background(), "stale", map[string]*string{"custom": stringPointer("x")}); err == nil {
		t.Fatal("stale revision accepted")
	} else {
		assertFileError(t, err, 409, "revision_conflict")
	}
}

func TestUnknownPropertiesAreMutableAndKnownKeysAreCaseSensitive(t *testing.T) {
	root := t.TempDir()
	files, _ := New(root)
	if _, err := files.WriteProperties(context.Background(), "Max-Players=10\n", "0"); err != nil {
		t.Fatal(err)
	}
	patcher := NewPropertyService(files, nil)
	snapshot, _ := patcher.Get(context.Background())
	if _, err := patcher.Patch(context.Background(), snapshot.Revision, map[string]*string{
		"Max-Players": stringPointer("not-an-integer"),
		"custom":      stringPointer("ok"),
	}); err != nil {
		t.Fatal(err)
	}
	payload, _ := os.ReadFile(filepath.Join(root, "server.properties"))
	if !strings.Contains(string(payload), "Max-Players=not-an-integer") {
		t.Fatalf("case-sensitive known key changed: %s", payload)
	}
}

func TestAuditChangesSortsAndRedacts(t *testing.T) {
	set, removed := AuditChanges(map[string]*string{
		"z": stringPointer("secret"), "a": nil, "m": stringPointer("value"),
	})
	if set != "m,z" || removed != "a" {
		t.Fatalf("set=%q removed=%q", set, removed)
	}
}
