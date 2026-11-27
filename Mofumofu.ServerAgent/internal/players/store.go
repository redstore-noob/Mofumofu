// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package players

import (
	"bytes"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"strings"
)

const (
	kindWhitelist = "whitelist"
	kindOps       = "ops"
	kindBans      = "bans"
)

type whitelistEntry struct {
	UUID string `json:"uuid"`
	Name string `json:"name"`
}

type opEntry struct {
	UUID                string `json:"uuid"`
	Name                string `json:"name"`
	Level               int32  `json:"level"`
	BypassesPlayerLimit bool   `json:"bypassesPlayerLimit"`
}

type bannedPlayerEntry struct {
	UUID    string `json:"uuid"`
	Name    string `json:"name"`
	Created string `json:"created"`
	Source  string `json:"source"`
	Expires string `json:"expires"`
	Reason  string `json:"reason"`
}

func (e *opEntry) UnmarshalJSON(data []byte) error {
	var wire struct {
		UUID                string          `json:"uuid"`
		Name                string          `json:"name"`
		Level               json.RawMessage `json:"level"`
		BypassesPlayerLimit json.RawMessage `json:"bypassesPlayerLimit"`
	}
	if err := json.Unmarshal(data, &wire); err != nil {
		return err
	}
	var decoded opEntry
	decoded.UUID = wire.UUID
	decoded.Name = wire.Name
	if len(wire.Level) != 0 {
		if bytes.Equal(bytes.TrimSpace(wire.Level), []byte("null")) {
			return errors.New("ops level must not be null")
		}
		if err := json.Unmarshal(wire.Level, &decoded.Level); err != nil {
			return err
		}
	}
	if len(wire.BypassesPlayerLimit) != 0 {
		if bytes.Equal(bytes.TrimSpace(wire.BypassesPlayerLimit), []byte("null")) {
			return errors.New("ops bypassesPlayerLimit must not be null")
		}
		if err := json.Unmarshal(wire.BypassesPlayerLimit, &decoded.BypassesPlayerLimit); err != nil {
			return err
		}
	}
	*e = decoded
	return nil
}

func validateKind(kind string) error {
	switch kind {
	case kindWhitelist, kindOps, kindBans:
		return nil
	default:
		return notFound("The player list kind was not found.")
	}
}

func playerFilePath(directory, kind string) string {
	name := ""
	switch kind {
	case kindWhitelist:
		name = "whitelist.json"
	case kindOps:
		name = "ops.json"
	case kindBans:
		name = "banned-players.json"
	}
	return filepath.Join(directory, name)
}

func readWhitelist(path string) []whitelistEntry {
	entries := readJSONEntries[whitelistEntry](path)
	for index := range entries {
		entries[index].Name = strings.TrimSpace(entries[index].Name)
	}
	return entries
}

func readOps(path string) []opEntry {
	entries := readJSONEntries[opEntry](path)
	for index := range entries {
		entries[index].Name = strings.TrimSpace(entries[index].Name)
	}
	return entries
}

func readBans(path string) []bannedPlayerEntry {
	entries := readJSONEntries[bannedPlayerEntry](path)
	for index := range entries {
		entries[index].Name = strings.TrimSpace(entries[index].Name)
	}
	return entries
}

func readJSONEntries[T any](path string) []T {
	payload, err := os.ReadFile(path)
	if err != nil {
		return []T{}
	}
	payload = bytes.TrimPrefix(payload, []byte{0xef, 0xbb, 0xbf})
	var encoded []*T
	if err := json.Unmarshal(payload, &encoded); err != nil {
		return []T{}
	}
	entries := make([]T, 0, len(encoded))
	for _, entry := range encoded {
		if entry == nil {
			return []T{}
		}
		entries = append(entries, *entry)
	}
	return entries
}

func saveWhitelist(path string, entries []whitelistEntry) error {
	normalized := make([]whitelistEntry, 0, len(entries))
	for _, entry := range entries {
		name, valid := normalizeStoredName(entry.Name)
		if !valid {
			continue
		}
		normalized = append(normalized, whitelistEntry{
			UUID: resolveUUID(entry.UUID, name).String(),
			Name: name,
		})
	}
	return writeJSONEntries(path, normalized)
}

func saveOps(path string, entries []opEntry) error {
	normalized := make([]opEntry, 0, len(entries))
	for _, entry := range entries {
		name, valid := normalizeStoredName(entry.Name)
		if !valid {
			return internalError()
		}
		level := entry.Level
		if level < 1 || level > 4 {
			level = 4
		}
		normalized = append(normalized, opEntry{
			UUID:                resolveUUID(entry.UUID, name).String(),
			Name:                name,
			Level:               level,
			BypassesPlayerLimit: entry.BypassesPlayerLimit,
		})
	}
	return writeJSONEntries(path, normalized)
}

func saveBans(path string, entries []bannedPlayerEntry) error {
	normalized := make([]bannedPlayerEntry, 0, len(entries))
	for _, entry := range entries {
		name, valid := normalizeStoredName(entry.Name)
		if !valid {
			return internalError()
		}
		normalized = append(normalized, bannedPlayerEntry{
			UUID:    resolveUUID(entry.UUID, name).String(),
			Name:    name,
			Created: defaultBanField(entry.Created, "1970-01-01 00:00:00 +0000"),
			Source:  defaultBanField(entry.Source, "Mofumofu"),
			Expires: defaultBanField(entry.Expires, "forever"),
			Reason:  defaultBanField(entry.Reason, "Banned by an operator."),
		})
	}
	return writeJSONEntries(path, normalized)
}

func normalizeStoredName(name string) (string, bool) {
	trimmed := strings.TrimSpace(name)
	return trimmed, validPlayerName(trimmed)
}

func defaultBanField(value, fallback string) string {
	if strings.TrimSpace(value) == "" {
		return fallback
	}
	return value
}

func writeJSONEntries(path string, entries any) error {
	var buffer bytes.Buffer
	encoder := json.NewEncoder(&buffer)
	encoder.SetIndent("", "  ")
	encoder.SetEscapeHTML(false)
	if err := encoder.Encode(entries); err != nil {
		return err
	}
	// Match Core's File.WriteAllText behavior: truncate/write in place rather
	// than writing a temporary file and renaming it atomically.
	return os.WriteFile(path, buffer.Bytes(), 0o666)
}
