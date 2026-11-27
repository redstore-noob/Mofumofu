// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package players

import (
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func writePlayerFixture(t *testing.T, directory, name, content string) string {
	t.Helper()
	path := name
	if !filepath.IsAbs(path) {
		path = filepath.Join(directory, path)
	}
	if err := os.WriteFile(path, []byte(content), 0o644); err != nil {
		t.Fatalf("write %s: %v", name, err)
	}
	return path
}

func readPlayerFixture(t *testing.T, path string) string {
	t.Helper()
	payload, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read %s: %v", path, err)
	}
	return string(payload)
}

func TestReadWhitelistTrimsNamesAndResolvesUUIDs(t *testing.T) {
	directory := t.TempDir()
	path := writePlayerFixture(t, directory, "whitelist.json", `[
  {"UUID":"B50AD385-829D-3141-A216-7E7D7539BA7F","NAME":" Notch ","ignored":true},
  {"uuid":"bad","name":"Steve"},
  {"name":null}
]`)
	entries := readWhitelist(path)
	if len(entries) != 3 {
		t.Fatalf("readWhitelist returned %d entries, want 3", len(entries))
	}
	if entries[0].Name != "Notch" || entries[0].UUID != "B50AD385-829D-3141-A216-7E7D7539BA7F" {
		t.Fatalf("first entry = %#v", entries[0])
	}
	if entries[1].Name != "Steve" || entries[2].Name != "" {
		t.Fatalf("names were not trimmed/null-normalized: %#v", entries)
	}
}

func TestReadOpsAndBansFields(t *testing.T) {
	directory := t.TempDir()
	ops := readOps(writePlayerFixture(t, directory, "ops.json", `[
  {"uuid":"00112233445566778899aabbccddeeff","name":" Builder ","level":2,"bypassesPlayerLimit":true},
  {"name":"Default"}
]`))
	if len(ops) != 2 || ops[0].Name != "Builder" || ops[0].Level != 2 || !ops[0].BypassesPlayerLimit {
		t.Fatalf("unexpected ops entries: %#v", ops)
	}
	if ops[1].Level != 0 || ops[1].BypassesPlayerLimit {
		t.Fatalf("missing ops fields did not retain CLR defaults: %#v", ops[1])
	}
	for name, content := range map[string]string{
		"null-level":    `[{"name":"Steve","level":null}]`,
		"null-bypasses": `[{"name":"Steve","bypassesPlayerLimit":null}]`,
	} {
		t.Run(name, func(t *testing.T) {
			path := writePlayerFixture(t, directory, name+".json", content)
			if entries := readOps(path); entries == nil || len(entries) != 0 {
				t.Fatalf("null non-nullable field returned %#v, want empty list", entries)
			}
		})
	}

	bans := readBans(writePlayerFixture(t, directory, "banned-players.json", `[
  {"uuid":"00112233445566778899aabbccddeeff","name":" Grief ","created":"2026-11-27 12:34:56 +0000","source":"Console","expires":"forever","reason":"Rule & <test> 玩家"},
  {"name":"Empty"}
]`))
	if len(bans) != 2 {
		t.Fatalf("readBans returned %d entries, want 2", len(bans))
	}
	if bans[0].Name != "Grief" || bans[0].Created != "2026-11-27 12:34:56 +0000" ||
		bans[0].Source != "Console" || bans[0].Expires != "forever" || bans[0].Reason != "Rule & <test> 玩家" {
		t.Fatalf("unexpected ban entry: %#v", bans[0])
	}
	if bans[1].Created != "" || bans[1].Source != "" || bans[1].Expires != "" || bans[1].Reason != "" {
		t.Fatalf("missing ban fields did not retain empty defaults: %#v", bans[1])
	}
}

func TestReadJSONEntriesTreatsMissingAndDamagedFilesAsEmpty(t *testing.T) {
	directory := t.TempDir()
	missing := filepath.Join(directory, "missing.json")
	if entries := readJSONEntries[whitelistEntry](missing); entries == nil || len(entries) != 0 {
		t.Fatalf("missing file returned %#v, want non-nil empty slice", entries)
	}
	cases := map[string]string{
		"empty":         "",
		"broken":        `[{"name":"Steve"`,
		"object":        `{"name":"Steve"}`,
		"wrong-type":    `[{"uuid":42,"name":"Steve"}]`,
		"null-entry":    `[null]`,
		"trailing-json": `[] []`,
	}
	for name, content := range cases {
		t.Run(name, func(t *testing.T) {
			path := writePlayerFixture(t, directory, name+".json", content)
			if entries := readJSONEntries[whitelistEntry](path); entries == nil || len(entries) != 0 {
				t.Fatalf("damaged file returned %#v, want non-nil empty slice", entries)
			}
		})
	}
	path := writePlayerFixture(t, directory, "null.json", "null")
	if entries := readJSONEntries[whitelistEntry](path); entries == nil || len(entries) != 0 {
		t.Fatalf("top-level null returned %#v, want non-nil empty slice", entries)
	}
	path = writePlayerFixture(t, directory, "bom.json", "\xef\xbb\xbf[]\n")
	if entries := readJSONEntries[whitelistEntry](path); entries == nil || len(entries) != 0 {
		t.Fatalf("UTF-8 BOM file returned %#v, want non-nil empty slice", entries)
	}
}

func TestSaveWhitelistNormalizesSkipsInvalidAndWritesJSON(t *testing.T) {
	directory := t.TempDir()
	path := filepath.Join(directory, "whitelist.json")
	err := saveWhitelist(path, []whitelistEntry{
		{UUID: "B50AD385829D3141A2167E7D7539BA7F", Name: " Notch "},
		{UUID: "bad", Name: "Steve"},
		{UUID: "00112233445566778899aabbccddeeff", Name: "bad-name"},
		{UUID: "00112233445566778899aabbccddeeff", Name: ""},
	})
	if err != nil {
		t.Fatalf("saveWhitelist: %v", err)
	}
	expected := `[
  {
    "uuid": "b50ad385-829d-3141-a216-7e7d7539ba7f",
    "name": "Notch"
  },
  {
    "uuid": "5627dd98-e6be-3c21-b8a8-e92344183641",
    "name": "Steve"
  }
]
`
	if actual := readPlayerFixture(t, path); actual != expected {
		t.Fatalf("whitelist JSON mismatch\nactual:\n%s\nexpected:\n%s", actual, expected)
	}
}

func TestSaveOpsNormalizesLevelsAndRejectsAnyInvalidName(t *testing.T) {
	directory := t.TempDir()
	path := writePlayerFixture(t, directory, "ops.json", "original\n")
	original := readPlayerFixture(t, path)
	err := saveOps(path, []opEntry{
		{UUID: "bad", Name: "Good", Level: 2, BypassesPlayerLimit: true},
		{UUID: "bad", Name: "bad-name", Level: 3},
	})
	var apiError *Error
	if !errors.As(err, &apiError) || apiError.HTTPStatus() != 500 || apiError.ErrorCode() != "internal_error" {
		t.Fatalf("saveOps invalid-name error = %#v, want mapped internal_error", err)
	}
	if actual := readPlayerFixture(t, path); actual != original {
		t.Fatalf("failed ops save modified the file: %q", actual)
	}

	err = saveOps(path, []opEntry{
		{Name: " Zero ", Level: 0},
		{Name: "High", Level: 9},
		{Name: "Valid", Level: 3, BypassesPlayerLimit: true},
	})
	if err != nil {
		t.Fatalf("saveOps: %v", err)
	}
	entries := readOps(path)
	if len(entries) != 3 || entries[0].Name != "Zero" || entries[0].Level != 4 ||
		entries[1].Level != 4 || entries[2].Level != 3 || !entries[2].BypassesPlayerLimit {
		t.Fatalf("unexpected normalized ops: %#v", entries)
	}
}

func TestSaveBansDefaultsFieldsAndRejectsInvalidNames(t *testing.T) {
	directory := t.TempDir()
	path := writePlayerFixture(t, directory, "banned-players.json", "original\n")
	original := readPlayerFixture(t, path)
	err := saveBans(path, []bannedPlayerEntry{
		{Name: "Good"},
		{Name: "玩家"},
	})
	var apiError *Error
	if !errors.As(err, &apiError) || apiError.HTTPStatus() != 500 || apiError.ErrorCode() != "internal_error" {
		t.Fatalf("saveBans invalid-name error = %#v, want mapped internal_error", err)
	}
	if actual := readPlayerFixture(t, path); actual != original {
		t.Fatalf("failed ban save modified the file: %q", actual)
	}

	err = saveBans(path, []bannedPlayerEntry{
		{Name: " Empty ", Created: " \t", Source: "\n", Expires: " ", Reason: ""},
		{
			Name:    "Kept",
			Created: "2026-11-27 12:34:56 +0000",
			Source:  "Console",
			Expires: "2026-12-01 00:00:00 +0000",
			Reason:  "Rule & <test> 玩家",
		},
	})
	if err != nil {
		t.Fatalf("saveBans: %v", err)
	}
	entries := readBans(path)
	if len(entries) != 2 {
		t.Fatalf("saveBans wrote %d entries, want 2", len(entries))
	}
	if entries[0].Name != "Empty" || entries[0].Created != "1970-01-01 00:00:00 +0000" ||
		entries[0].Source != "Mofumofu" || entries[0].Expires != "forever" ||
		entries[0].Reason != "Banned by an operator." {
		t.Fatalf("ban defaults were not applied: %#v", entries[0])
	}
	if entries[1].Reason != "Rule & <test> 玩家" {
		t.Fatalf("non-empty ban fields were changed: %#v", entries[1])
	}
	payload := readPlayerFixture(t, path)
	if strings.Contains(payload, `\u003c`) || strings.Contains(payload, `\u0026`) || !strings.Contains(payload, "玩家") {
		t.Fatalf("ban JSON did not use relaxed Unicode/HTML escaping: %s", payload)
	}
	if !strings.HasSuffix(payload, "\n") {
		t.Fatal("player JSON must end with a newline")
	}
}
