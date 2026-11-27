// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package pathsafe

import (
	"os"
	"runtime"
	"testing"
)

func TestValidSegment(t *testing.T) {
	valid := []string{"world", "server.properties", "a b", "CONFIG", "console.txt", "com10.txt", "nul-data", "é.txt"}
	for _, name := range valid {
		if !ValidSegment(name) {
			t.Errorf("%q should be valid", name)
		}
	}
	invalid := []string{
		"", ".", "..", "name.", "name ", "a:b", "a|b", "a*b", "a?b", "a<b", "a>b", "a\"b",
		"a\x00b", "a\tb", "CON", "con.txt", "Nul", "aux.log", "PRN.", "COM1", "com9.dat", "LPT0", "lpt5.txt",
	}
	for _, name := range invalid {
		if ValidSegment(name) {
			t.Errorf("%q should be invalid", name)
		}
	}
}

func TestIsLinkRejectsSymlinkAndIrregular(t *testing.T) {
	if !IsLink(os.ModeSymlink) || !IsLink(os.ModeIrregular) {
		t.Fatal("symlink and irregular modes must be treated as links")
	}
	if IsLink(0) || IsLink(os.ModeDir) || IsLink(0o644) {
		t.Fatal("regular files and directories must not be treated as links")
	}
}

func TestKeyFoldsCaseOnlyOnCaseInsensitiveSystems(t *testing.T) {
	left, right := "/srv/Server/Level.DAT", "/srv/server/level.dat"
	same := Key(left) == Key(right)
	switch runtime.GOOS {
	case "windows", "darwin":
		if !same {
			t.Fatalf("%s keys should fold case", runtime.GOOS)
		}
	default:
		if same {
			t.Fatalf("%s keys should be case-sensitive", runtime.GOOS)
		}
	}
}
