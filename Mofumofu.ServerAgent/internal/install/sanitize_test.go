// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package install

import (
	"errors"
	"net/http"
	"testing"
)

func TestValidateVersionIDMatchesCSharpRules(t *testing.T) {
	valid := []string{"1.21.4", "26.1", "0.16.9+build", "neoforge_21-4", "21.4.156"}
	for _, value := range valid {
		if err := ValidateVersionID(value, "version"); err != nil {
			t.Fatalf("ValidateVersionID(%q) = %v", value, err)
		}
	}
	invalid := []string{"", "   ", "1.21/../x", "1.21 4", "1.21\n", "a;b", "é", "1.21.4\x00"}
	for _, value := range invalid {
		if err := ValidateVersionID(value, "version"); err == nil {
			t.Fatalf("ValidateVersionID(%q) accepted an invalid value", value)
		}
	}
	long := make([]byte, MaxVersionIDLength+1)
	for index := range long {
		long[index] = '1'
	}
	if err := ValidateVersionID(string(long), "version"); err == nil {
		t.Fatal("ValidateVersionID accepted a value longer than 64 characters")
	}
}

func TestSanitizeFileNameRejectsTraversalAndScriptCharacters(t *testing.T) {
	cases := map[string]string{
		"paper-1.21.4-123.jar": "paper-1.21.4-123.jar",
		"../../evil.jar":       "evil.jar",
		"dir\\sub\\core.jar":   "core.jar",
		"":                     ServerJarName,
		".":                    ServerJarName,
		"..":                   ServerJarName,
		"bad\"name.jar":        ServerJarName,
		"x;rm.jar":             ServerJarName,
		"$HOME.jar":            ServerJarName,
		"a&b.jar":              ServerJarName,
	}
	for input, want := range cases {
		if got := SanitizeFileName(input); got != want {
			t.Fatalf("SanitizeFileName(%q) = %q, want %q", input, got, want)
		}
	}
}

func TestSanitizeScriptValueStripsLineBreaksAndRejectsMetacharacters(t *testing.T) {
	got, err := SanitizeScriptValue("-jar server.jar\r\nnogui\x00")
	if err != nil || got != "-jar server.jarnogui" {
		t.Fatalf("SanitizeScriptValue() = %q, %v", got, err)
	}
	for _, value := range []string{"a&b", "a|b", "a%PATH%", "a!b", "a$b", "a`b", "a;b", "a\"b", "a'b", "a^b", "a<b", "a>b"} {
		if _, err := SanitizeScriptValue(value); err == nil {
			t.Fatalf("SanitizeScriptValue(%q) accepted an unsafe character", value)
		}
	}
}

func TestSanitizePropertyValueRemovesLineBreaksOnly(t *testing.T) {
	if got := SanitizePropertyValue("Hello\r\nWorld\x00!=#"); got != "HelloWorld!=#" {
		t.Fatalf("SanitizePropertyValue() = %q", got)
	}
}

func TestNeoForgeVersionPrefix(t *testing.T) {
	cases := map[string]string{"1.21.4": "21.4.", "26.1": "26.1.", " 1.20.1 ": "20.1."}
	for input, want := range cases {
		got, err := NeoForgeVersionPrefix(input)
		if err != nil || got != want {
			t.Fatalf("NeoForgeVersionPrefix(%q) = %q, %v; want %q", input, got, err, want)
		}
	}
	if _, err := NeoForgeVersionPrefix("1."); err == nil {
		t.Fatal("NeoForgeVersionPrefix accepted an empty version")
	}
}

func TestAllowlistDeniesByDefaultAndMatchesPatterns(t *testing.T) {
	var empty Allowlist
	if err := empty.Check(CoreVanilla, "1.21.4"); !isForbidden(err) {
		t.Fatalf("empty allowlist returned %v", err)
	}

	allowlist := Allowlist{Cores: []string{"paper"}, MinecraftVersions: []string{"1.21.*", "1.20.1"}}
	for _, version := range []string{"1.21", "1.21.4", "1.20.1"} {
		if err := allowlist.Check(CorePaper, version); err != nil {
			t.Fatalf("Check(paper, %s) = %v", version, err)
		}
	}
	for _, version := range []string{"1.210", "1.2", "1.20.2", "1.20"} {
		if err := allowlist.Check(CorePaper, version); !isForbidden(err) {
			t.Fatalf("Check(paper, %s) = %v, want forbidden", version, err)
		}
	}
	if err := allowlist.Check(CoreVanilla, "1.21.4"); !isForbidden(err) {
		t.Fatalf("core outside the allowlist returned %v", err)
	}
}

func isForbidden(err error) bool {
	var installErr *Error
	return errors.As(err, &installErr) && installErr.HTTPStatus() == http.StatusForbidden
}
