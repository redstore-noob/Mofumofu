// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package backups

import (
	"archive/zip"
	"context"
	"errors"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"
)

type fakeCore struct {
	running  bool
	commands []string
	fail     map[string]error
}

func (f *fakeCore) IsRunning() bool { return f.running }
func (f *fakeCore) Command(_ context.Context, command string) error {
	f.commands = append(f.commands, command)
	return f.fail[command]
}

func TestColdCreateMetadataAndRestore(t *testing.T) {
	root := t.TempDir()
	instance := filepath.Join(root, "survival")
	mustMkdir(t, filepath.Join(instance, "world"))
	mustMkdir(t, filepath.Join(instance, "logs"))
	mustMkdir(t, filepath.Join(instance, "backups"))
	mustWrite(t, filepath.Join(instance, "world", "level.dat"), "original")
	mustWrite(t, filepath.Join(instance, "server.properties"), "motd=test\n")
	mustWrite(t, filepath.Join(instance, "logs", "latest.log"), "ignored")
	mustWrite(t, filepath.Join(instance, "backups", "old.zip"), "ignored")
	mustWrite(t, filepath.Join(instance, "scratch.tmp"), "ignored")
	mustWrite(t, filepath.Join(instance, "partial.mofu-download"), "ignored")
	core := &fakeCore{}
	manager := New(instance, root, "1.21.4", core, func() time.Time {
		return time.Date(2026, 11, 26, 12, 34, 56, 0, time.Local)
	}, nil)
	info, err := manager.Create(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if info.Hot || info.Name != "cold-20261126-123456.zip" {
		t.Fatalf("info=%+v", info)
	}
	entries := zipNames(t, info.Path)
	want := []string{"server.properties", "world/level.dat"}
	if strings.Join(entries, ",") != strings.Join(want, ",") {
		t.Fatalf("entries=%v want=%v", entries, want)
	}
	if err := manager.SetDescription(info.Path, "Before update", "manual"); err != nil {
		t.Fatal(err)
	}
	metadata, err := manager.Metadata(info.Name)
	if err != nil {
		t.Fatal(err)
	}
	if metadata.Label != "Before update" || metadata.Description != "manual" || len(metadata.SHA256) != 64 || metadata.MinecraftVersion != "1.21.4" {
		t.Fatalf("metadata=%+v", metadata)
	}
	mustWrite(t, filepath.Join(instance, "world", "level.dat"), "modified")
	mustWrite(t, filepath.Join(instance, "extra.txt"), "keep")
	if err := manager.Restore(context.Background(), info.Name, metadata.SHA256); err != nil {
		t.Fatal(err)
	}
	payload, _ := os.ReadFile(filepath.Join(instance, "world", "level.dat"))
	if string(payload) != "original" {
		t.Fatalf("restored=%q", payload)
	}
	if _, err := os.Stat(filepath.Join(instance, "extra.txt")); err != nil {
		t.Fatalf("restore deleted an extra file: %v", err)
	}
	list := manager.List()
	foundSafety := false
	for _, info := range list {
		if info.Name == "cold-20261126-123456-1.zip" {
			foundSafety = true
		}
	}
	if len(list) != 3 || !foundSafety {
		t.Fatalf("safety backup missing: %+v", list)
	}
}

func TestHotBackupCommandSequenceAndFailure(t *testing.T) {
	root := t.TempDir()
	instance := filepath.Join(root, "survival")
	mustMkdir(t, instance)
	mustWrite(t, filepath.Join(instance, "file.txt"), "x")
	core := &fakeCore{running: true}
	manager := New(instance, root, "", core, time.Now, nil)
	manager.flushDelay = 0
	if _, err := manager.Create(context.Background()); err != nil {
		t.Fatal(err)
	}
	if got := strings.Join(core.commands, ","); got != "save-off,save-all flush,save-on" {
		t.Fatalf("commands=%s", got)
	}
	core.commands = nil
	core.fail = map[string]error{"save-all flush": errors.New("failed")}
	if _, err := manager.Create(context.Background()); err == nil {
		t.Fatal("flush failure was ignored")
	}
	if got := strings.Join(core.commands, ","); got != "save-off,save-all flush,save-on" {
		t.Fatalf("failure commands=%s", got)
	}
}

func TestRetentionAndSettingsDefaults(t *testing.T) {
	root := t.TempDir()
	instance := filepath.Join(root, "survival")
	mustMkdir(t, filepath.Join(instance, "backups"))
	manager := New(instance, root, "", &fakeCore{}, time.Now, nil)
	settings := manager.CurrentSettings()
	if settings.Enabled || settings.IntervalHours != 6 || settings.KeepCount != 5 || settings.KeepDays != 0 {
		t.Fatalf("defaults=%+v", settings)
	}
	// CurrentSettings is cached like Core; use a fresh manager for the policy.
	mustWrite(t, filepath.Join(root, "backup-settings.json"), `{"enabled":true,"keepCount":2}`)
	manager = New(instance, root, "", &fakeCore{}, time.Now, nil)
	settings = manager.CurrentSettings()
	if !settings.Enabled || settings.IntervalHours != 6 || settings.KeepCount != 2 {
		t.Fatalf("loaded=%+v", settings)
	}
	for _, name := range []string{"hot-20260101-000000.zip", "hot-20260102-000000.zip", "hot-20260103-000000.zip"} {
		mustWrite(t, filepath.Join(instance, "backups", name), name)
	}
	if err := manager.prune(); err != nil {
		t.Fatal(err)
	}
	entries, _ := os.ReadDir(filepath.Join(instance, "backups"))
	if len(entries) != 2 || entries[0].Name() != "hot-20260102-000000.zip" || entries[1].Name() != "hot-20260103-000000.zip" {
		t.Fatalf("retained=%v", entries)
	}
}

func TestDeleteHashAndIDValidation(t *testing.T) {
	root := t.TempDir()
	instance := filepath.Join(root, "survival")
	mustMkdir(t, filepath.Join(instance, "backups"))
	path := filepath.Join(instance, "backups", "cold-test.zip")
	mustWrite(t, path, "backup")
	manager := New(instance, root, "", &fakeCore{}, time.Now, nil)
	metadata, err := manager.Metadata("cold-test.zip")
	if err != nil {
		t.Fatal(err)
	}
	err = manager.Delete(metadata.ID, "wrong")
	var apiErr *Error
	if !errors.As(err, &apiErr) || apiErr.Code != "revision_conflict" || apiErr.Details["expectedRevision"] != "wrong" {
		t.Fatalf("got %v", err)
	}
	if err := manager.Delete(metadata.ID, metadata.SHA256); err != nil {
		t.Fatal(err)
	}
	for _, id := range []string{"", "../x.zip", "a/b.zip", `a\b.zip`, "x.txt", "x..zip"} {
		if err := ValidateID(id); err == nil {
			t.Errorf("accepted %q", id)
		}
	}
}

func TestCleanZipPathAndSymlinkTarget(t *testing.T) {
	for input, want := range map[string]string{
		"world/level.dat": "world/level.dat", `world\level.dat`: "world/level.dat",
		"a/../b": "b", "a/./b": "a/b", "/absolute": "",
	} {
		got, err := cleanZipPath(input)
		if input == "/absolute" {
			if err == nil {
				t.Fatal("absolute path accepted")
			}
			continue
		}
		if err != nil || got != want {
			t.Errorf("%q: got %q, %v", input, got, err)
		}
	}
	if runtime.GOOS == "windows" {
		return
	}
	root := t.TempDir()
	outside := t.TempDir()
	mustMkdir(t, filepath.Join(root, "link"))
	if err := os.Remove(filepath.Join(root, "link")); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(outside, filepath.Join(root, "link")); err != nil {
		t.Skipf("symlink unavailable: %v", err)
	}
	if err := rejectSymlinkPath(root, filepath.Join(root, "link", "file")); err == nil {
		t.Fatal("symlink target accepted")
	}
}

func zipNames(t *testing.T, path string) []string {
	t.Helper()
	archive, err := zip.OpenReader(path)
	if err != nil {
		t.Fatal(err)
	}
	defer archive.Close()
	names := make([]string, 0, len(archive.File))
	for _, file := range archive.File {
		names = append(names, file.Name)
	}
	return names
}

func mustMkdir(t *testing.T, path string) {
	t.Helper()
	if err := os.MkdirAll(path, 0o750); err != nil {
		t.Fatal(err)
	}
}

func mustWrite(t *testing.T, path, content string) {
	t.Helper()
	if err := os.WriteFile(path, []byte(content), 0o600); err != nil {
		t.Fatal(err)
	}
}
