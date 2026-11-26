// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package files

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"
)

func TestNormalizeRelativePath(t *testing.T) {
	for _, path := range []string{"", "/abs", `a\b`, "a//b", "a/../b", "a/./b", "a/", "a /b", "a./b", "C:/x", "a\x00b"} {
		if _, err := NormalizeRelativePath(path, false); err == nil {
			t.Errorf("accepted %q", path)
		}
	}
	if got, err := NormalizeRelativePath("Config/My File.txt", false); err != nil || got != "Config/My File.txt" {
		t.Fatalf("got %q, %v", got, err)
	}
	if got, err := NormalizeRelativePath("", true); err != nil || got != "" {
		t.Fatalf("root: %q, %v", got, err)
	}
}

func TestWriteReadRenameDeleteAndRevision(t *testing.T) {
	root := t.TempDir()
	service, err := New(root)
	if err != nil {
		t.Fatal(err)
	}
	ctx := context.Background()
	if err := os.Mkdir(filepath.Join(root, "config"), 0o750); err != nil {
		t.Fatal(err)
	}
	created, err := service.Write(ctx, "config/a.txt", "hello", "0")
	if err != nil {
		t.Fatal(err)
	}
	if created.SizeBytes != 5 || !strings.HasPrefix(created.Revision, "r_") {
		t.Fatalf("unexpected content: %+v", created)
	}
	read, err := service.Read(ctx, "config/a.txt")
	if err != nil || read.Content != "hello" || read.Revision != created.Revision {
		t.Fatalf("read=%+v err=%v", read, err)
	}
	_, err = service.Write(ctx, "config/a.txt", "changed", "stale")
	assertFileError(t, err, 409, "revision_conflict")
	updated, err := service.Write(ctx, "config/a.txt", "changed", created.Revision)
	if err != nil || updated.Revision == created.Revision {
		t.Fatalf("updated=%+v err=%v", updated, err)
	}
	renamed, err := service.Rename(ctx, "config/a.txt", "b.txt", updated.Revision)
	if err != nil || renamed.Path != "config/b.txt" {
		t.Fatalf("renamed=%+v err=%v", renamed, err)
	}
	item, err := service.Get(ctx, "config/b.txt")
	if err != nil {
		t.Fatal(err)
	}
	if err := service.Delete(ctx, "config/b.txt", item.Revision); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(filepath.Join(root, "config", "b.txt")); !os.IsNotExist(err) {
		t.Fatalf("file still exists: %v", err)
	}
	matches, _ := filepath.Glob(filepath.Join(root, "config", ".mofu-*.tmp"))
	if len(matches) != 0 {
		t.Fatalf("temporary files left: %v", matches)
	}
}

func TestCreateListAndProtectedPaths(t *testing.T) {
	root := t.TempDir()
	registry := filepath.Join(t.TempDir(), "registry.json")
	service, err := New(root, registry)
	if err != nil {
		t.Fatal(err)
	}
	ctx := context.Background()
	if _, err := service.Create(ctx, "file.txt", false); err != nil {
		t.Fatal(err)
	}
	if _, err := service.Create(ctx, "file.txt", false); err == nil {
		t.Fatal("duplicate create succeeded")
	}
	if _, err := service.Create(ctx, "missing/file.txt", false); err == nil {
		t.Fatal("create under missing parent succeeded")
	}
	for _, path := range []string{"server.properties", "world/level.dat", "backups/a.zip", "x/server.jar", "auth/data"} {
		_, err := service.Read(ctx, path)
		assertFileError(t, err, 400, "invalid_path")
	}
	entries, err := service.List(ctx, "")
	if err != nil {
		t.Fatal(err)
	}
	if len(entries) != 1 || entries[0].Path != "file.txt" || !entries[0].Writable {
		t.Fatalf("entries=%+v", entries)
	}
}

func TestDirectoryRevisionAndDeleteRejectProtectedChild(t *testing.T) {
	root := t.TempDir()
	service, _ := New(root)
	ctx := context.Background()
	if _, err := service.Create(ctx, "config", true); err != nil {
		t.Fatal(err)
	}
	if _, err := service.Write(ctx, "config/a.txt", "one", "0"); err != nil {
		t.Fatal(err)
	}
	first, err := service.Get(ctx, "config")
	if err != nil || !strings.HasPrefix(first.Revision, "d_") {
		t.Fatalf("first=%+v err=%v", first, err)
	}
	path := filepath.Join(root, "config", "a.txt")
	directory := filepath.Join(root, "config")
	fixed := time.Unix(1_700_000_000, 0).UTC()
	if err := os.Chtimes(path, fixed, fixed); err != nil {
		t.Fatal(err)
	}
	if err := os.Chtimes(directory, fixed, fixed); err != nil {
		t.Fatal(err)
	}
	first, err = service.Get(ctx, "config")
	if err != nil {
		t.Fatal(err)
	}
	fileItem, err := service.Get(ctx, "config/a.txt")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := service.Write(ctx, "config/a.txt", "two", fileItem.Revision); err != nil {
		t.Fatal(err)
	}
	if err := os.Chtimes(path, fixed, fixed); err != nil {
		t.Fatal(err)
	}
	if err := os.Chtimes(directory, fixed, fixed); err != nil {
		t.Fatal(err)
	}
	second, err := service.Get(ctx, "config")
	if err != nil {
		t.Fatal(err)
	}
	if second.Revision != first.Revision {
		t.Fatal("directory revision included file content")
	}
	if err := os.WriteFile(filepath.Join(root, "config", "server.properties"), []byte("x"), 0o600); err != nil {
		t.Fatal(err)
	}
	err = service.Delete(ctx, "config", second.Revision)
	assertFileError(t, err, 400, "invalid_path")
	if _, err := os.Stat(filepath.Join(root, "config", "a.txt")); err != nil {
		t.Fatalf("directory was partially deleted: %v", err)
	}
}

func TestPropertiesProtectedAccessAndInvalidUTF8(t *testing.T) {
	root := t.TempDir()
	service, _ := New(root)
	ctx := context.Background()
	if _, err := service.WriteProperties(ctx, "motd=hello\n", "0"); err != nil {
		t.Fatal(err)
	}
	if _, err := service.Read(ctx, "server.properties"); err == nil {
		t.Fatal("generic API exposed server.properties")
	}
	properties, err := service.ReadProperties(ctx)
	if err != nil || properties.Content != "motd=hello\n" {
		t.Fatalf("properties=%+v err=%v", properties, err)
	}
	if err := os.WriteFile(filepath.Join(root, "bad.txt"), []byte{0xff, 0xfe}, 0o600); err != nil {
		t.Fatal(err)
	}
	_, err = service.Read(ctx, "bad.txt")
	assertFileError(t, err, 400, "invalid_request")
}

func TestSymlinkSegmentsAreRejected(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("creating symlinks may require elevated privileges on Windows")
	}
	root := t.TempDir()
	outside := t.TempDir()
	if err := os.WriteFile(filepath.Join(outside, "secret.txt"), []byte("secret"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(outside, filepath.Join(root, "link")); err != nil {
		t.Skipf("symlink unavailable: %v", err)
	}
	service, _ := New(root)
	_, err := service.Read(context.Background(), "link/secret.txt")
	assertFileError(t, err, 400, "invalid_path")
	entries, err := service.List(context.Background(), "")
	if err != nil || len(entries) != 0 {
		t.Fatalf("entries=%+v err=%v", entries, err)
	}
}

func TestCanceledContextDoesNotMutate(t *testing.T) {
	root := t.TempDir()
	service, _ := New(root)
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := service.Write(ctx, "a.txt", "x", "0"); !errors.Is(err, context.Canceled) {
		t.Fatalf("got %v", err)
	}
	if _, err := os.Stat(filepath.Join(root, "a.txt")); !os.IsNotExist(err) {
		t.Fatalf("file created: %v", err)
	}
}

func assertFileError(t *testing.T, err error, status int, code string) {
	t.Helper()
	var apiErr *Error
	if !errors.As(err, &apiErr) || apiErr.Status != status || apiErr.Code != code {
		t.Fatalf("got %v, want status=%d code=%s", err, status, code)
	}
}
