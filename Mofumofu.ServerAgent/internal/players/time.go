// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package players

import (
	"strings"
	"time"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

func formatMinecraftTime(value time.Time) string {
	return value.UTC().Format("2006-01-02 15:04:05 -0700")
}

func parseMinecraftTime(value string) *protocol.Timestamp {
	text := strings.TrimSpace(value)
	if text == "" || text == "forever" {
		return nil
	}
	layouts := [...]string{
		"2006-01-02 15:04:05 -0700",
		"2006-01-02 15:04:05 -07:00",
		"2006-01-02 15:04:05Z07:00",
		time.RFC3339Nano,
		"2006-01-02T15:04:05",
		"2006-01-02 15:04:05",
		"2006-01-02",
	}
	for _, layout := range layouts {
		parsed, err := time.Parse(layout, text)
		if err != nil {
			continue
		}
		timestamp := protocol.NewTimestamp(parsed.UTC())
		return &timestamp
	}
	return nil
}
