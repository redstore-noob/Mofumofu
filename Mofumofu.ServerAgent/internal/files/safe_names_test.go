// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package files

import (
	"context"
	"os"
	"path/filepath"
	"testing"
)

func TestNormalizeRejectsWindowsReservedNames(t *testing.T) {
	for _, path := range []string{"CON", "config/nul.txt", "logs2/aux", "com1.log", "a/LPT9.cfg", "bad<name", "pipe|name", "quote\"name"} {
		if _, err := NormalizeRelativePath(path, false); err == nil {
			t.Errorf("accepted %q", path)
		}
	}
	if _, err := NormalizeRelativePath("config/console.txt", false); err != nil {
		t.Fatalf("console.txt rejected: %v", err)
	}
}

func TestRenameCaseOnlyChangeIsAllowed(t *testing.T) {
	root := t.TempDir()
	if err := os.WriteFile(filepath.Join(root, "notes.txt"), []byte("kept"), 0o600); err != nil {
		t.Fatal(err)
	}
	service, err := New(root)
	if err != nil {
		t.Fatal(err)
	}
	ctx := context.Background()
	current, err := service.Get(ctx, "notes.txt")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := service.Rename(ctx, "notes.txt", "Notes.txt", current.Revision); err != nil {
		t.Fatalf("case-only rename rejected: %v", err)
	}
	entries, err := service.List(ctx, "")
	if err != nil || len(entries) != 1 || entries[0].Path != "Notes.txt" {
		t.Fatalf("entries=%+v err=%v", entries, err)
	}
}

func TestRenameOntoDifferentExistingFileConflicts(t *testing.T) {
	root := t.TempDir()
	for _, name := range []string{"a.txt", "b.txt"} {
		if err := os.WriteFile(filepath.Join(root, name), []byte(name), 0o600); err != nil {
			t.Fatal(err)
		}
	}
	service, err := New(root)
	if err != nil {
		t.Fatal(err)
	}
	ctx := context.Background()
	current, err := service.Get(ctx, "a.txt")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := service.Rename(ctx, "a.txt", "b.txt", current.Revision); err == nil {
		t.Fatal("rename over a different file was accepted")
	}
}

func TestProtectedPathsMatchCaseInsensitivelyOnlyWhereFileSystemDoes(t *testing.T) {
	root := t.TempDir()
	protected := filepath.Join(root, "secret", "token.txt")
	service, err := New(root, protected)
	if err != nil {
		t.Fatal(err)
	}
	if !service.isProtected("secret/token.txt", filepath.Join(root, "secret", "token.txt")) {
		t.Fatal("exact protected path not matched")
	}
	variant := filepath.Join(root, "Secret", "Token.txt")
	if got := service.isProtected("Secret/Token.txt", variant); got != (samePath(variant, protected)) {
		t.Fatalf("case-variant protection=%v, samePath=%v", got, samePath(variant, protected))
	}
}
