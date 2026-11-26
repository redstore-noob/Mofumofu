// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package players

import (
	"strings"
	"testing"
)

func TestOfflineUUIDKnownVectors(t *testing.T) {
	vectors := map[string]string{
		"Notch": "b50ad385-829d-3141-a216-7e7d7539ba7f",
		"Steve": "5627dd98-e6be-3c21-b8a8-e92344183641",
		"Alex":  "36532b5e-c442-3dbb-a24c-c7e55d0f979a",
	}
	for name, expected := range vectors {
		t.Run(name, func(t *testing.T) {
			actual := offlineUUID(name)
			if actual.String() != expected {
				t.Fatalf("offlineUUID(%q) = %q, want %q", name, actual.String(), expected)
			}
			if actual[6]&0xf0 != 0x30 {
				t.Fatalf("UUID version bits = %#x, want version 3", actual[6]&0xf0)
			}
			if actual[8]&0xc0 != 0x80 {
				t.Fatalf("UUID variant bits = %#x, want RFC 4122 variant", actual[8]&0xc0)
			}
		})
	}
	if offlineUUID("Notch") == offlineUUID("notch") {
		t.Fatal("offline UUID names must remain case-sensitive")
	}
}

func TestResolveUUIDCanonicalizesOrFallsBack(t *testing.T) {
	name := "Notch"
	vectors := []struct {
		input string
		want  string
	}{
		{"B50AD385-829D-3141-A216-7E7D7539BA7F", "b50ad385-829d-3141-a216-7e7d7539ba7f"},
		{" b50ad385829d3141a2167e7d7539ba7f ", "b50ad385-829d-3141-a216-7e7d7539ba7f"},
		{"not-a-uuid", "b50ad385-829d-3141-a216-7e7d7539ba7f"},
		{"", "b50ad385-829d-3141-a216-7e7d7539ba7f"},
		{"b50ad385-829d-3141-a216-7e7d7539ba7z", "b50ad385-829d-3141-a216-7e7d7539ba7f"},
	}
	for _, vector := range vectors {
		actual := resolveUUID(vector.input, name).String()
		if actual != vector.want {
			t.Errorf("resolveUUID(%q, %q) = %q, want %q", vector.input, name, actual, vector.want)
		}
		if len(actual) != 36 || strings.Count(actual, "-") != 4 || actual != strings.ToLower(actual) {
			t.Errorf("resolveUUID returned non-canonical text %q", actual)
		}
	}
}
