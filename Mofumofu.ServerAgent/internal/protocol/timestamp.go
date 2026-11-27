// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Mofumofu

package protocol

import (
	"bytes"
	"encoding/json"
	"fmt"
	"regexp"
	"strconv"
	"strings"
	"time"
)

// Timestamp is the protocol's ISO-8601 DateTimeOffset representation. Its zero
// value is 0001-01-01T00:00:00+00:00. The embedded time.Time retains its offset.
type Timestamp struct {
	time.Time
}

var rfc3339Pattern = regexp.MustCompile(`^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-](?:[01]\d|2[0-3]):[0-5]\d)$`)

// NewTimestamp converts a time.Time to protocol tick precision.
func NewTimestamp(value time.Time) Timestamp {
	return Timestamp{Time: roundToTicks(value)}
}

// MarshalJSON emits an explicit numeric offset, including +00:00 for UTC, and
// no more than seven fractional-second digits.
func (t Timestamp) MarshalJSON() ([]byte, error) {
	return []byte(`"` + formatTimestamp(t.Time) + `"`), nil
}

// UnmarshalJSON parses an RFC3339 timestamp and rounds sub-tick precision in
// the same direction as System.Text.Json DateTimeOffset parsing.
func (t *Timestamp) UnmarshalJSON(data []byte) error {
	if bytes.Equal(bytes.TrimSpace(data), []byte("null")) {
		return fmt.Errorf("timestamp must not be null")
	}
	var value string
	if err := json.Unmarshal(data, &value); err != nil {
		return err
	}
	if !rfc3339Pattern.MatchString(value) {
		return fmt.Errorf("invalid RFC3339 timestamp %q", value)
	}
	parsed, err := parseRFC3339Timestamp(value)
	if err != nil {
		return err
	}
	t.Time = parsed
	return nil
}

func (t Timestamp) String() string {
	return formatTimestamp(t.Time)
}

func formatTimestamp(value time.Time) string {
	value = roundToTicks(value)
	year, month, day := value.Date()
	hour, minute, second := value.Clock()
	nanosecond := value.Nanosecond()

	var builder strings.Builder
	builder.Grow(35)
	fmt.Fprintf(&builder, "%04d-%02d-%02dT%02d:%02d:%02d", year, month, day, hour, minute, second)
	if nanosecond != 0 {
		ticks := nanosecond / 100
		fraction := fmt.Sprintf("%07d", ticks)
		fraction = strings.TrimRight(fraction, "0")
		builder.WriteByte('.')
		builder.WriteString(fraction)
	}

	_, offset := value.Zone()
	sign := '+'
	if offset < 0 {
		sign = '-'
		offset = -offset
	}
	fmt.Fprintf(&builder, "%c%02d:%02d", sign, offset/3600, (offset%3600)/60)
	return builder.String()
}

func roundToTicks(value time.Time) time.Time {
	nanosecond := value.Nanosecond()
	remainder := nanosecond % 100
	if remainder > 50 {
		value = value.Add(time.Duration(100-remainder) * time.Nanosecond)
	} else {
		value = value.Add(-time.Duration(remainder) * time.Nanosecond)
	}
	return value
}

func parseRFC3339Timestamp(value string) (time.Time, error) {
	parsed, err := time.Parse(time.RFC3339Nano, value)
	if err != nil {
		return time.Time{}, err
	}
	if len(value) <= 19 || value[19] != '.' {
		return parsed, nil
	}

	zoneStart := len(value) - 1
	if value[zoneStart] != 'Z' {
		zoneStart = len(value) - 6
	}
	digits := value[20:zoneStart]
	padded := digits + "0000000"
	ticks, err := strconv.ParseUint(padded[:7], 10, 64)
	if err != nil {
		return time.Time{}, err
	}
	if len(digits) > 7 && fractionGreaterThanHalf(digits[7:]) {
		ticks++
	}

	year, month, day := parsed.Date()
	hour, minute, second := parsed.Clock()
	location := parsed.Location()
	if ticks == 10_000_000 {
		return time.Date(year, month, day, hour, minute, second, 0, location).Add(time.Second), nil
	}
	return time.Date(year, month, day, hour, minute, second, int(ticks*100), location), nil
}

func fractionGreaterThanHalf(remainder string) bool {
	if remainder[0] > '5' {
		return true
	}
	if remainder[0] < '5' {
		return false
	}
	for i := 1; i < len(remainder); i++ {
		if remainder[i] != '0' {
			return true
		}
	}
	return false
}
