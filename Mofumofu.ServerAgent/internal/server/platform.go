// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package server

import (
	"context"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/rcon"
)

var tpsPattern = regexp.MustCompile(`TPS(?:[^:\r\n]*):\s*\*?([0-9]+(?:\.[0-9]+)?)`)

func (s *Supervisor) Platform() string { return detectPlatform(s.metadata) }

func (s *AttachSupervisor) Platform() string { return detectPlatform(s.metadata) }

func (s *Supervisor) TPS(ctx context.Context) (*float64, error) {
	platform := s.Platform()
	if platform != "paper" && platform != "spigot" {
		return nil, nil
	}
	response, err := s.RCONCommand(ctx, "tps")
	if err != nil {
		return nil, err
	}
	return ParseTPS(response), nil
}

func (s *AttachSupervisor) TPS(ctx context.Context) (*float64, error) {
	platform := s.Platform()
	if platform != "paper" && platform != "spigot" {
		return nil, nil
	}
	configuration, err := loadRconConfiguration(s.metadata.Directory)
	if err != nil || !configuration.Enabled {
		return nil, nil
	}
	response, err := rcon.Execute(ctx, rcon.Config{Port: configuration.Port, Password: configuration.Password}, "tps")
	if err != nil {
		return nil, err
	}
	return ParseTPS(response), nil
}

func ParseTPS(response string) *float64 {
	match := tpsPattern.FindStringSubmatch(response)
	if len(match) < 2 {
		return nil
	}
	value, err := strconv.ParseFloat(match[1], 64)
	if err != nil {
		return nil
	}
	return &value
}

func detectPlatform(metadata *Metadata) string {
	entries, _ := os.ReadDir(metadata.Directory)
	for _, entry := range entries {
		name := strings.ToLower(entry.Name())
		switch {
		case strings.HasPrefix(name, "paper") && strings.HasSuffix(name, ".jar"):
			return "paper"
		case strings.HasPrefix(name, "spigot") && strings.HasSuffix(name, ".jar"):
			return "spigot"
		case strings.HasPrefix(name, "sponge") && strings.HasSuffix(name, ".jar"):
			return "sponge"
		case strings.HasPrefix(name, "fabric") && strings.HasSuffix(name, ".jar"):
			return "fabric"
		case strings.Contains(name, "neoforge") && strings.HasSuffix(name, ".jar"):
			return "neoforge"
		case strings.Contains(name, "forge") && strings.HasSuffix(name, ".jar"):
			return "forge"
		}
	}
	if payload, err := os.ReadFile(filepath.Join(metadata.Directory, "logs", "latest.log")); err == nil {
		text := strings.ToLower(string(payload))
		for _, candidate := range []string{"paper", "spigot", "sponge", "fabric", "neoforge", "forge"} {
			if strings.Contains(text, candidate) {
				return candidate
			}
		}
	}
	switch metadata.Config.Core {
	case CorePaper:
		return "paper"
	case CoreFabric:
		return "fabric"
	case CoreNeoForge:
		return "neoforge"
	case CoreVanilla:
		return "vanilla"
	default:
		return "vanilla"
	}
}
