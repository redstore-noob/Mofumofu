// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Mofumofu

package protocol_test

import (
	"encoding/json"
	"strings"
	"testing"
	"time"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

func TestTimestampMarshalUsesDateTimeOffsetFormat(t *testing.T) {
	tests := []struct {
		name     string
		value    time.Time
		expected string
	}{
		{"zero", time.Time{}, "0001-01-01T00:00:00+00:00"},
		{"UTC", time.Date(2026, 11, 22, 8, 0, 0, 0, time.UTC), "2026-11-22T08:00:00+00:00"},
		{"fraction trimmed", time.Date(2026, 11, 22, 8, 0, 0, 120000000, time.UTC), "2026-11-22T08:00:00.12+00:00"},
		{"fraction rounded up", time.Date(2026, 11, 22, 8, 0, 0, 123456789, time.UTC), "2026-11-22T08:00:00.1234568+00:00"},
		{"fraction rounded down", time.Date(2026, 11, 22, 8, 0, 0, 123456449, time.UTC), "2026-11-22T08:00:00.1234564+00:00"},
		{"half tick rounds down", time.Date(2026, 11, 22, 8, 0, 0, 123456450, time.UTC), "2026-11-22T08:00:00.1234564+00:00"},
		{"rounding carries second", time.Date(2026, 11, 22, 8, 0, 0, 999999999, time.UTC), "2026-11-22T08:00:01+00:00"},
		{"positive offset", time.Date(2026, 11, 22, 8, 0, 0, 0, time.FixedZone("plus", 5*3600+30*60)), "2026-11-22T08:00:00+05:30"},
		{"negative offset", time.Date(2026, 11, 22, 8, 0, 0, 0, time.FixedZone("minus", -(7*3600+15*60))), "2026-11-22T08:00:00-07:15"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			encoded, err := json.Marshal(protocol.Timestamp{Time: test.value})
			if err != nil {
				t.Fatalf("json.Marshal() error = %v", err)
			}
			if got, want := string(encoded), `"`+test.expected+`"`; got != want {
				t.Fatalf("json.Marshal() = %s, want %s", got, want)
			}
			if got := protocol.NewTimestamp(test.value).String(); got != test.expected {
				t.Fatalf("String() = %s, want %s", got, test.expected)
			}
		})
	}
}

func TestTimestampUnmarshalParsesRFC3339AndNormalizesUTC(t *testing.T) {
	tests := []struct {
		input    string
		expected string
	}{
		{"0001-01-01T00:00:00Z", "0001-01-01T00:00:00+00:00"},
		{"2026-11-22T08:00:00Z", "2026-11-22T08:00:00+00:00"},
		{"2026-11-22T08:00:00+00:00", "2026-11-22T08:00:00+00:00"},
		{"2026-11-22T08:00:00.1Z", "2026-11-22T08:00:00.1+00:00"},
		{"2026-11-22T08:00:00.123456789Z", "2026-11-22T08:00:00.1234568+00:00"},
		{"2026-11-22T08:00:00.123456450Z", "2026-11-22T08:00:00.1234564+00:00"},
		{"2026-11-22T08:00:00.1234567501Z", "2026-11-22T08:00:00.1234568+00:00"},
		{"2026-11-22T08:00:00.9999999999Z", "2026-11-22T08:00:01+00:00"},
		{"2026-11-22T08:00:00-07:15", "2026-11-22T08:00:00-07:15"},
	}
	for _, test := range tests {
		t.Run(test.input, func(t *testing.T) {
			var timestamp protocol.Timestamp
			if err := protocol.Unmarshal([]byte(`"`+test.input+`"`), &timestamp); err != nil {
				t.Fatalf("Unmarshal() error = %v", err)
			}
			if got := timestamp.String(); got != test.expected {
				t.Fatalf("String() = %s, want %s", got, test.expected)
			}
			encoded, err := protocol.Marshal(timestamp)
			if err != nil {
				t.Fatalf("Marshal() error = %v", err)
			}
			if strings.Contains(string(encoded), "Z") {
				t.Fatalf("Marshal() used Z: %s", encoded)
			}
		})
	}
}

func TestTimestampUnmarshalRejectsNullAndInvalidRFC3339(t *testing.T) {
	for _, input := range []string{
		`null`,
		`3`,
		`"2026-11-22"`,
		`"2026-11-22T08:00:00"`,
		`"2026-11-22T08:00:00,1Z"`,
		`"2026-11-22T08:00:00+24:00"`,
		`"2026-13-22T08:00:00Z"`,
	} {
		var timestamp protocol.Timestamp
		if err := protocol.Unmarshal([]byte(input), &timestamp); err == nil {
			t.Fatalf("Unmarshal(%s) error = nil, want error", input)
		}
	}
}
