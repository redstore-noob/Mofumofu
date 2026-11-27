// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package backups

import (
	"archive/zip"
	"context"
	"os"
	"path/filepath"
	"testing"
	"time"
)

func TestCleanZipPathRejectsUnportableSegments(t *testing.T) {
	for _, name := range []string{"world/CON.txt", "world/a:b", "world/name.", "world/q?.dat", "world/nul/x"} {
		if got, err := cleanZipPath(name); err == nil {
			t.Errorf("%q accepted as %q", name, got)
		}
	}
	if got, err := cleanZipPath("world/console.txt"); err != nil || got != "world/console.txt" {
		t.Fatalf("console.txt: got %q, %v", got, err)
	}
}

func TestRestoreRejectsUnsafeEntryBeforeWriting(t *testing.T) {
	root := t.TempDir()
	instance := filepath.Join(root, "survival")
	mustMkdir(t, filepath.Join(instance, "backups"))
	writeTestArchive(t, filepath.Join(instance, "backups", "cold-20260101-000000.zip"), map[string]string{
		"world/level.dat": "restored",
		"world/CON":       "device",
	})
	manager := New(instance, root, "", &fakeCore{}, time.Now, nil)
	metadata, err := manager.Metadata("cold-20260101-000000.zip")
	if err != nil {
		t.Fatal(err)
	}
	if err := manager.Restore(context.Background(), metadata.ID, metadata.SHA256); err == nil {
		t.Fatal("unsafe archive restored")
	}
	if _, err := os.Stat(filepath.Join(instance, "world", "level.dat")); !os.IsNotExist(err) {
		t.Fatalf("partial restore wrote a file: %v", err)
	}
}

func writeTestArchive(t *testing.T, path string, files map[string]string) {
	t.Helper()
	file, err := os.Create(path)
	if err != nil {
		t.Fatal(err)
	}
	defer file.Close()
	archive := zip.NewWriter(file)
	for name, content := range files {
		writer, err := archive.Create(name)
		if err != nil {
			t.Fatal(err)
		}
		if _, err := writer.Write([]byte(content)); err != nil {
			t.Fatal(err)
		}
	}
	if err := archive.Close(); err != nil {
		t.Fatal(err)
	}
}
