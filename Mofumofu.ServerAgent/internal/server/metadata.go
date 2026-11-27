// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

// Package server contains instance metadata, Java resolution, the managed
// supervisor, and the read-only attach backend. Metadata mirrors ServerConfig.
package server

import (
	"bufio"
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
)

const (
	CoreVanilla   = "vanilla"
	CorePaper     = "paper"
	CoreNeoForge  = "neoforge"
	CoreFabric    = "fabric"
	StateStopped  = "stopped"
	StateStarting = "starting"
	StateRunning  = "running"
	StateStopping = "stopping"
	StateCrashed  = "crashed"
)

// ServerConfig is the on-disk server.json contract. memoryMB/memoryMinMB are
// intentionally capitalized that way; encoding/json still accepts other casing.
type ServerConfig struct {
	ID            string   `json:"id"`
	Name          string   `json:"name"`
	Core          string   `json:"core"`
	CoreVersion   string   `json:"coreVersion"`
	MCVersion     string   `json:"mcVersion"`
	Port          int      `json:"port"`
	MaxPlayers    int      `json:"maxPlayers"`
	JavaPath      string   `json:"javaPath"`
	MemoryMB      int      `json:"memoryMB"`
	MemoryMinMB   int      `json:"memoryMinMB"`
	ExtraJavaArgs []string `json:"extraJavaArgs"`
	AutoRestart   bool     `json:"autoRestart"`
}

// Metadata is the small read-only view needed by handshake/status before the
// supervisor is constructed.
type Metadata struct {
	Config     ServerConfig
	Directory  string
	ConfigPath string
}

func LoadMetadata(root, instanceID string) (*Metadata, error) {
	directory := filepath.Join(root, instanceID)
	path := filepath.Join(directory, "server.json")
	payload, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("read server config: %w", err)
	}
	var config ServerConfig
	if err := json.Unmarshal(payload, &config); err != nil {
		return nil, fmt.Errorf("decode server config: %w", err)
	}
	if bytes.Equal(bytes.TrimSpace(payload), []byte("null")) {
		return nil, errors.New("server config is null")
	}
	return &Metadata{Config: config, Directory: directory, ConfigPath: path}, nil
}

// Motd returns the exact, case-sensitive motd key from server.properties. A
// missing or unreadable properties file is ignored by StatusService.
func (m *Metadata) Motd() (string, bool) {
	file, err := os.Open(filepath.Join(m.Directory, "server.properties"))
	if err != nil {
		return "", false
	}
	defer file.Close()

	scanner := bufio.NewScanner(file)
	scanner.Buffer(make([]byte, 4096), 1024*1024)
	for scanner.Scan() {
		text := strings.TrimSpace(scanner.Text())
		if text == "" || strings.HasPrefix(text, "#") {
			continue
		}
		index := strings.IndexByte(text, '=')
		if index < 0 {
			continue
		}
		if strings.TrimSpace(text[:index]) == "motd" {
			return text[index+1:], true
		}
	}
	return "", false
}
