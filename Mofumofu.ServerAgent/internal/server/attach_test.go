// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package server

import (
	"context"
	"os"
	"path/filepath"
	"testing"
	"time"
)

func TestAttachTailsLogWithoutLifecycleOrPropertiesWrites(t *testing.T) {
	root := t.TempDir()
	instance := filepath.Join(root, "survival")
	if err := os.MkdirAll(filepath.Join(instance, "logs"), 0o750); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(instance, "server.json"), []byte(`{"name":"Attach","core":"vanilla"}`), 0o600); err != nil {
		t.Fatal(err)
	}
	properties := []byte("enable-rcon=false\nrcon.password=keep\n")
	if err := os.WriteFile(filepath.Join(instance, "server.properties"), properties, 0o600); err != nil {
		t.Fatal(err)
	}
	metadata, _ := LoadMetadata(root, "survival")
	var console []string
	attach := NewAttachSupervisor(metadata, nil, time.Now, func(line string) { console = append(console, line) })
	if attach.SupportsControl() || attach.SupportsCommand() {
		t.Fatal("attach advertised unsupported capabilities")
	}
	if err := attach.Start(context.Background()); err == nil {
		t.Fatal("attach start succeeded")
	} else if apiErr, ok := err.(*Error); !ok || apiErr.Code != "capability_not_supported" {
		t.Fatalf("start error=%v", err)
	}
	logPath := filepath.Join(instance, "logs", "latest.log")
	if err := os.WriteFile(logPath, []byte("first\npartial"), 0o600); err != nil {
		t.Fatal(err)
	}
	lines, cursor := attach.RecentConsole(-1)
	if cursor != 1 || len(lines) != 1 || lines[0].Message != "first" {
		t.Fatalf("lines=%+v cursor=%d", lines, cursor)
	}
	file, err := os.OpenFile(logPath, os.O_APPEND|os.O_WRONLY, 0)
	if err != nil {
		t.Fatal(err)
	}
	_, _ = file.WriteString(" second\nthird\n")
	_ = file.Close()
	lines, cursor = attach.RecentConsole(1)
	if cursor != 3 || len(lines) != 2 || lines[0].Message != "partial second" || lines[1].Message != "third" {
		t.Fatalf("lines=%+v cursor=%d", lines, cursor)
	}
	if len(console) != 3 {
		t.Fatalf("callbacks=%v", console)
	}
	payload, _ := os.ReadFile(filepath.Join(instance, "server.properties"))
	if string(payload) != string(properties) {
		t.Fatal("attach modified server.properties")
	}
}

func TestAttachStateUsesRecentLogAndOldLogIsOffline(t *testing.T) {
	root := t.TempDir()
	instance := filepath.Join(root, "survival")
	if err := os.MkdirAll(filepath.Join(instance, "logs"), 0o750); err != nil {
		t.Fatal(err)
	}
	_ = os.WriteFile(filepath.Join(instance, "server.json"), []byte(`{}`), 0o600)
	metadata, _ := LoadMetadata(root, "survival")
	attach := NewAttachSupervisor(metadata, nil, time.Now, nil)
	logPath := filepath.Join(instance, "logs", "latest.log")
	_ = os.WriteFile(logPath, []byte("recent\n"), 0o600)
	if state := attach.State(); state != StateRunning {
		t.Fatalf("state=%s", state)
	}
	old := time.Now().Add(-5 * time.Minute)
	_ = os.Chtimes(logPath, old, old)
	if state := attach.State(); state != StateStopped {
		t.Fatalf("state=%s", state)
	}
}

func TestLoadRconConfigurationRejectsInvalidPort(t *testing.T) {
	directory := t.TempDir()
	payload := "enable-rcon=true\nrcon.port=0\nrcon.password=secret\n"
	_ = os.WriteFile(filepath.Join(directory, "server.properties"), []byte(payload), 0o600)
	configuration, err := loadRconConfiguration(directory)
	if err != nil || configuration.Enabled {
		t.Fatalf("configuration=%+v err=%v", configuration, err)
	}
}

func TestPlatformAndTPSParsing(t *testing.T) {
	if value := ParseTPS("TPS from last 1m, 5m, 15m: *20.0, 19.5, 18.0"); value == nil || *value != 20 {
		t.Fatalf("tps=%v", value)
	}
	if value := ParseTPS("no tps here"); value != nil {
		t.Fatalf("tps=%v", value)
	}
	root := t.TempDir()
	instance := filepath.Join(root, "survival")
	_ = os.Mkdir(instance, 0o750)
	_ = os.WriteFile(filepath.Join(instance, "server.json"), []byte(`{}`), 0o600)
	_ = os.WriteFile(filepath.Join(instance, "paper-test.jar"), []byte("x"), 0o600)
	metadata, _ := LoadMetadata(root, "survival")
	if platform := detectPlatform(metadata); platform != "paper" {
		t.Fatalf("platform=%s", platform)
	}
}
