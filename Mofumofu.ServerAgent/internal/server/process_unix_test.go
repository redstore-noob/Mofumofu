// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

//go:build linux || darwin

package server

import (
	"os"
	"path/filepath"
	"syscall"
	"testing"
)

func TestLatestLogBusyDetectsFlockHolder(t *testing.T) {
	path := filepath.Join(t.TempDir(), "latest.log")
	if err := os.WriteFile(path, []byte("line\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	if latestLogBusy(path) {
		t.Fatal("unlocked log reported busy")
	}
	holder, err := os.OpenFile(path, os.O_RDWR, 0)
	if err != nil {
		t.Fatal(err)
	}
	defer holder.Close()
	if err := syscall.Flock(int(holder.Fd()), syscall.LOCK_EX); err != nil {
		t.Fatal(err)
	}
	if !latestLogBusy(path) {
		t.Fatal("flock holder not detected")
	}
	if err := syscall.Flock(int(holder.Fd()), syscall.LOCK_UN); err != nil {
		t.Fatal(err)
	}
	if latestLogBusy(path) {
		t.Fatal("released log still reported busy")
	}
	if latestLogBusy(filepath.Join(t.TempDir(), "missing.log")) {
		t.Fatal("missing log reported busy")
	}
}
