// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

// Package pathsafe holds the path rules shared by the file, backup, and
// configuration code. The rules are identical on every platform so that a
// name accepted on Linux can also be stored on Windows and macOS.
package pathsafe

import (
	"fmt"
	"os"
	"runtime"
	"strings"
	"unicode"
)

const invalidRunes = `<>:"|?*\/`

var reservedNames = func() map[string]struct{} {
	names := map[string]struct{}{"CON": {}, "PRN": {}, "AUX": {}, "NUL": {}}
	for index := 0; index <= 9; index++ {
		names[fmt.Sprintf("COM%d", index)] = struct{}{}
		names[fmt.Sprintf("LPT%d", index)] = struct{}{}
	}
	return names
}()

// ValidSegment reports whether name is usable as one path element on Windows,
// macOS, and Linux. It rejects Windows device names (with or without an
// extension), trailing dots or spaces, and characters Windows cannot store.
func ValidSegment(name string) bool {
	if name == "" || name == "." || name == ".." ||
		strings.HasSuffix(name, " ") || strings.HasSuffix(name, ".") {
		return false
	}
	for _, r := range name {
		if unicode.IsControl(r) || strings.ContainsRune(invalidRunes, r) {
			return false
		}
	}
	base := name
	if index := strings.IndexByte(base, '.'); index >= 0 {
		base = base[:index]
	}
	_, reserved := reservedNames[strings.ToUpper(strings.TrimRight(base, " "))]
	return !reserved
}

// IsLink reports whether mode describes a symbolic link. On Windows, Go reports
// junctions and other reparse points as irregular files, so they are included.
func IsLink(mode os.FileMode) bool {
	return mode&(os.ModeSymlink|os.ModeIrregular) != 0
}

// Key returns the form used to compare two paths for identity. Windows and
// macOS file systems are case-insensitive by default, so their keys fold case.
func Key(path string) string {
	switch runtime.GOOS {
	case "windows", "darwin":
		return strings.ToLower(path)
	default:
		return path
	}
}
