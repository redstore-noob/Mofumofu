// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

//go:build windows

package files

import (
	"context"
	"os"
	"os/exec"
	"path/filepath"
	"testing"
)

func TestJunctionsAreNotExposed(t *testing.T) {
	root := t.TempDir()
	outside := t.TempDir()
	if err := os.WriteFile(filepath.Join(outside, "secret.txt"), []byte("secret"), 0o600); err != nil {
		t.Fatal(err)
	}
	link := filepath.Join(root, "link")
	if output, err := exec.Command("cmd", "/c", "mklink", "/J", link, outside).CombinedOutput(); err != nil {
		t.Skipf("junction unavailable: %v: %s", err, output)
	}
	service, err := New(root)
	if err != nil {
		t.Fatal(err)
	}
	ctx := context.Background()
	entries, err := service.List(ctx, "")
	if err != nil {
		t.Fatal(err)
	}
	for _, entry := range entries {
		if entry.Path == "link" {
			t.Fatal("junction listed")
		}
	}
	if _, err := service.Read(ctx, "link/secret.txt"); err == nil {
		t.Fatal("content read through junction")
	}
}
