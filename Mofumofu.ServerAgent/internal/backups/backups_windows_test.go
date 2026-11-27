// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

//go:build windows

package backups

import (
	"context"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestArchiveSkipsJunctions(t *testing.T) {
	root := t.TempDir()
	outside := t.TempDir()
	instance := filepath.Join(root, "survival")
	mustMkdir(t, filepath.Join(instance, "world"))
	mustMkdir(t, filepath.Join(instance, "backups"))
	mustWrite(t, filepath.Join(instance, "world", "level.dat"), "original")
	mustWrite(t, filepath.Join(outside, "secret.txt"), "secret")
	link := filepath.Join(instance, "world", "link")
	if output, err := exec.Command("cmd", "/c", "mklink", "/J", link, outside).CombinedOutput(); err != nil {
		t.Skipf("junction unavailable: %v: %s", err, output)
	}
	manager := New(instance, root, "", &fakeCore{}, time.Now, nil)
	info, err := manager.Create(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	for _, name := range zipNames(t, info.Path) {
		if strings.Contains(name, "secret") || strings.Contains(name, "link") {
			t.Fatalf("junction content archived: %s", name)
		}
	}
}
