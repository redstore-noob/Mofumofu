// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package server

import (
	"context"
	"os"
	"path/filepath"
	"reflect"
	"runtime"
	"strings"
	"testing"
)

func TestRequiredJavaMajor(t *testing.T) {
	values := map[string]int{"1.8": 8, "1.17": 16, "1.18": 17, "1.20.4": 17, "1.20.5": 21, "1.21": 21, "26.1": 25}
	for version, want := range values {
		got := RequiredJavaMajor(version)
		if got == nil || *got != want {
			t.Errorf("%s: got %v want %d", version, got, want)
		}
	}
	for _, version := range []string{"2.0", "unknown"} {
		if got := RequiredJavaMajor(version); got != nil {
			t.Errorf("%s: got %d want nil", version, *got)
		}
	}
}

func TestBuildJavaArguments(t *testing.T) {
	directory := t.TempDir()
	config := ServerConfig{Core: CoreVanilla, MemoryMB: 2048, MemoryMinMB: 1024, ExtraJavaArgs: []string{"-Dfoo=bar"}}
	args, err := BuildJavaArguments(directory, config)
	if err != nil {
		t.Fatal(err)
	}
	want := []string{"-Xmx2048M", "-Xms1024M", "-Dfoo=bar", "-jar", "server.jar", "nogui"}
	if !reflect.DeepEqual(args, want) {
		t.Fatalf("args=%v want=%v", args, want)
	}
	config.Core = CoreFabric
	args, _ = BuildJavaArguments(directory, config)
	if got := strings.Join(args[len(args)-3:], ","); got != "-jar,fabric-server.jar,nogui" {
		t.Fatalf("fabric args=%v", args)
	}
	config.Core = CorePaper
	if err := os.WriteFile(filepath.Join(directory, "paper-small.jar"), []byte("1"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(directory, "paper-large.jar"), []byte("12345"), 0o600); err != nil {
		t.Fatal(err)
	}
	args, _ = BuildJavaArguments(directory, config)
	if args[len(args)-2] != "paper-large.jar" {
		t.Fatalf("paper args=%v", args)
	}
	if err := os.WriteFile(filepath.Join(directory, "paper.jar"), []byte("x"), 0o600); err != nil {
		t.Fatal(err)
	}
	args, _ = BuildJavaArguments(directory, config)
	if args[len(args)-2] != "paper.jar" {
		t.Fatalf("paper.jar did not win: %v", args)
	}
}

func TestNeoForgePrefersWindowsArgsLikeCore(t *testing.T) {
	directory := t.TempDir()
	base := filepath.Join(directory, "libraries", "net", "neoforged", "neoforge", "21.1")
	if err := os.MkdirAll(base, 0o750); err != nil {
		t.Fatal(err)
	}
	for _, name := range []string{"win_args.txt", "unix_args.txt"} {
		if err := os.WriteFile(filepath.Join(base, name), []byte("x"), 0o600); err != nil {
			t.Fatal(err)
		}
	}
	args, err := BuildJavaArguments(directory, ServerConfig{Core: CoreNeoForge, CoreVersion: "21.1"})
	if err != nil {
		t.Fatal(err)
	}
	if len(args) != 1 || !strings.HasSuffix(filepath.ToSlash(args[0]), "win_args.txt") {
		t.Fatalf("args=%v", args)
	}
}

func TestResolveJavaSelection(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("shell fixture is Linux-only")
	}
	directory := t.TempDir()
	java17 := writeJavaFixture(t, directory, "java17", `openjdk version "17.0.1"`)
	java21 := writeJavaFixture(t, directory, "java21", `openjdk version "21.0.1"`)
	configured := writeJavaFixture(t, directory, "configured", `openjdk version "21.0.1"`)
	got, err := ResolveJava(context.Background(), configured, "1.21", nil, []string{java17, java21})
	if err != nil || got != configured {
		t.Fatalf("got %q, %v", got, err)
	}
	got, err = ResolveJava(context.Background(), java17, "1.21", nil, []string{java21})
	if err != nil || got != java21 {
		t.Fatalf("minimum version selection got %q, %v", got, err)
	}
}

func writeJavaFixture(t *testing.T, directory, name, version string) string {
	t.Helper()
	path := filepath.Join(directory, name)
	script := "#!/bin/sh\nprintf '%s\\n' '" + version + "' >&2\n"
	if err := os.WriteFile(path, []byte(script), 0o700); err != nil {
		t.Fatal(err)
	}
	return path
}
