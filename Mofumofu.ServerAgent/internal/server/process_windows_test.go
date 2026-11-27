// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

//go:build windows

package server

import (
	"os"
	"path/filepath"
	"testing"
)

func TestLatestLogBusyDetectsOpenHandle(t *testing.T) {
	path := filepath.Join(t.TempDir(), "latest.log")
	if err := os.WriteFile(path, []byte("line\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	if latestLogBusy(path) {
		t.Fatal("closed log reported busy")
	}
	holder, err := os.OpenFile(path, os.O_RDWR|os.O_APPEND, 0)
	if err != nil {
		t.Fatal(err)
	}
	if !latestLogBusy(path) {
		holder.Close()
		t.Fatal("open handle not detected")
	}
	holder.Close()
	if latestLogBusy(path) {
		t.Fatal("released log still reported busy")
	}
}
