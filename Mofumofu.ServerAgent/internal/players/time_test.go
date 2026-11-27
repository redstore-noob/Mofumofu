// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package players

import (
	"testing"
	"time"
)

func TestFormatMinecraftTimeUsesUTCAndSeconds(t *testing.T) {
	value := time.Date(2026, time.November, 27, 20, 5, 6, 987654321, time.FixedZone("UTC+8", 8*60*60))
	actual := formatMinecraftTime(value)
	if actual != "2026-11-27 12:05:06 +0000" {
		t.Fatalf("formatMinecraftTime = %q", actual)
	}
}

func TestParseMinecraftTime(t *testing.T) {
	vectors := []struct {
		input string
		want  string
	}{
		{"2026-11-27 12:34:56 +0000", "2026-11-27T12:34:56+00:00"},
		{"2026-11-27 12:34:56 +0800", "2026-11-27T04:34:56+00:00"},
		{"2026-11-27T12:34:56.1234567Z", "2026-11-27T12:34:56.1234567+00:00"},
		{"2026-11-27 12:34:56", "2026-11-27T12:34:56+00:00"},
		{"2026-11-27", "2026-11-27T00:00:00+00:00"},
	}
	for _, vector := range vectors {
		t.Run(vector.input, func(t *testing.T) {
			actual := parseMinecraftTime(vector.input)
			if actual == nil {
				t.Fatalf("parseMinecraftTime(%q) returned nil", vector.input)
			}
			if actual.String() != vector.want {
				t.Fatalf("parseMinecraftTime(%q) = %q, want %q", vector.input, actual.String(), vector.want)
			}
		})
	}
	for _, input := range []string{"", " \t\n", "forever", "Forever", "not-a-date", "2026-99-99 00:00:00 +0000"} {
		if actual := parseMinecraftTime(input); actual != nil {
			t.Errorf("parseMinecraftTime(%q) = %q, want nil", input, actual.String())
		}
	}
}
