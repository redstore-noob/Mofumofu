// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package server

import (
	"os"
	"path/filepath"
	"testing"
)

func TestLoadMetadataAndMotd(t *testing.T) {
	root := t.TempDir()
	instance := filepath.Join(root, "survival")
	if err := os.Mkdir(instance, 0o750); err != nil {
		t.Fatal(err)
	}
	payload := `{"id":"survival","name":"Survival","core":"paper","mcVersion":"1.21.4","port":25565,"maxPlayers":20,"javaPath":"/usr/bin/java","memoryMB":2048,"memoryMinMB":1024,"extraJavaArgs":["-Dfoo=bar"],"autoRestart":true}`
	if err := os.WriteFile(filepath.Join(instance, "server.json"), []byte(payload), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(instance, "server.properties"), []byte("# comment\nmotd = Hello world\nmax-players=20\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	metadata, err := LoadMetadata(root, "survival")
	if err != nil {
		t.Fatal(err)
	}
	if metadata.Config.MemoryMB != 2048 || metadata.Config.MemoryMinMB != 1024 || !metadata.Config.AutoRestart {
		t.Fatalf("unexpected config: %+v", metadata.Config)
	}
	if got, ok := metadata.Motd(); !ok || got != " Hello world" {
		t.Fatalf("motd=%q, %v", got, ok)
	}
}

func TestLoadMetadataRejectsMissingAndNull(t *testing.T) {
	root := t.TempDir()
	if _, err := LoadMetadata(root, "missing"); err == nil {
		t.Fatal("missing config was accepted")
	}
	instance := filepath.Join(root, "survival")
	if err := os.Mkdir(instance, 0o750); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(instance, "server.json"), []byte(" \n null \n"), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, err := LoadMetadata(root, "survival"); err == nil {
		t.Fatal("null config was accepted")
	}
}
