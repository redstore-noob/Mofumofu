// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

// Package backups implements the ServerAgent/Core timestamp-zip format. It is
// intentionally separate from NekoProbe's UUID backup and pending-restore format.
package backups

import (
	"archive/zip"
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"os"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
	"sync"
	"time"
	"unicode/utf16"
)

const MaxTotalBytes int64 = 8 << 30

// Core is the process capability needed by hot backup and restore.
type Core interface {
	IsRunning() bool
	Command(ctx context.Context, command string) error
}

type Error struct {
	Status  int
	Code    string
	Message string
	Details map[string]string
}

func (e *Error) Error() string                   { return e.Message }
func (e *Error) ErrorCode() string               { return e.Code }
func (e *Error) HTTPStatus() int                 { return e.Status }
func (e *Error) ErrorDetails() map[string]string { return e.Details }
func (e *Error) OperationIdentifier() string     { return "" }

func invalidRequest(message string) *Error {
	return &Error{Status: 400, Code: "invalid_request", Message: message}
}

func notFound(message string) *Error {
	return &Error{Status: 404, Code: "not_found", Message: message}
}

func revisionConflict(expected string) *Error {
	return &Error{Status: 409, Code: "revision_conflict", Message: "The resource revision does not match.", Details: map[string]string{"expectedRevision": expected}}
}

func serverRunning() *Error {
	return &Error{Status: 409, Code: "server_running", Message: "The server must remain stopped throughout backup restoration."}
}

type Info struct {
	Name      string
	Path      string
	SizeBytes int64
	CreatedAt int64
	Hot       bool
}

type Metadata struct {
	ID               string
	Label            string
	Description      string
	CreatedAt        time.Time
	SizeBytes        int64
	SHA256           string
	MinecraftVersion string
}

type description struct {
	Label       string  `json:"label"`
	Description *string `json:"description,omitempty"`
}

type hashEntry struct {
	size      int64
	timestamp int64
	hash      string
}

// Manager owns one instance's backups directory.
type Manager struct {
	instanceDir string
	serverRoot  string
	backupsDir  string
	mcVersion   string
	core        Core
	now         func() time.Time
	logger      *slog.Logger
	flushDelay  time.Duration

	hashMu sync.Mutex
	hashes map[string]hashEntry

	settingsOnce sync.Once
	settings     Settings
}

type Settings struct {
	Enabled       bool `json:"enabled"`
	IntervalHours int  `json:"intervalHours"`
	KeepCount     int  `json:"keepCount"`
	KeepDays      int  `json:"keepDays"`
}

func New(instanceDir, serverRoot, mcVersion string, core Core, now func() time.Time, logger *slog.Logger) *Manager {
	if now == nil {
		now = time.Now
	}
	if logger == nil {
		logger = slog.New(slog.NewTextHandler(io.Discard, nil))
	}
	return &Manager{
		instanceDir: instanceDir,
		serverRoot:  serverRoot,
		backupsDir:  filepath.Join(instanceDir, "backups"),
		mcVersion:   mcVersion,
		core:        core,
		now:         now,
		logger:      logger,
		flushDelay:  1500 * time.Millisecond,
		hashes:      make(map[string]hashEntry),
	}
}

func (m *Manager) Create(ctx context.Context) (Info, error) {
	running := m.core.IsRunning()
	worldSavingDisabled := false
	if running {
		if err := m.core.Command(ctx, "save-off"); err != nil {
			m.logger.Warn("server could not pause world writes; hot backup may be inconsistent", "error", err)
		} else {
			worldSavingDisabled = true
		}
	}
	defer func() {
		if worldSavingDisabled {
			if err := m.core.Command(context.Background(), "save-on"); err != nil {
				m.logger.Warn("server could not resume world writes", "error", err)
			}
		}
	}()
	if running {
		if err := m.core.Command(ctx, "save-all flush"); err != nil {
			return Info{}, err
		}
		timer := time.NewTimer(m.flushDelay)
		select {
		case <-ctx.Done():
			timer.Stop()
			return Info{}, ctx.Err()
		case <-timer.C:
		}
	}
	if err := os.MkdirAll(m.backupsDir, 0o750); err != nil {
		return Info{}, err
	}
	prefix := "cold-"
	if running {
		prefix = "hot-"
	}
	name, err := m.uniqueName(prefix + m.now().Format("20060102-150405") + ".zip")
	if err != nil {
		return Info{}, err
	}
	target := filepath.Join(m.backupsDir, name)
	temporary := target + ".tmp"
	written, err := m.writeArchive(ctx, temporary)
	if err != nil {
		_ = os.Remove(temporary)
		return Info{}, err
	}
	if err := os.Rename(temporary, target); err != nil {
		_ = os.Remove(temporary)
		return Info{}, err
	}
	info, err := os.Stat(target)
	if err != nil {
		return Info{}, err
	}
	result := Info{Name: name, Path: target, SizeBytes: info.Size(), CreatedAt: m.now().Unix(), Hot: running}
	if result.SizeBytes == 0 {
		result.SizeBytes = written
	}
	if err := m.prune(); err != nil {
		m.logger.Warn("could not prune old backups", "error", err)
	}
	return result, nil
}

func (m *Manager) List() []Info {
	entries, err := os.ReadDir(m.backupsDir)
	if err != nil {
		return []Info{}
	}
	result := make([]Info, 0, len(entries))
	for _, entry := range entries {
		if entry.IsDir() || entry.Type()&os.ModeSymlink != 0 || !strings.EqualFold(filepath.Ext(entry.Name()), ".zip") {
			continue
		}
		info, err := entry.Info()
		if err != nil {
			continue
		}
		result = append(result, Info{
			Name: entry.Name(), Path: filepath.Join(m.backupsDir, entry.Name()), SizeBytes: info.Size(),
			CreatedAt: info.ModTime().Unix(), Hot: strings.HasPrefix(strings.ToLower(entry.Name()), "hot-"),
		})
	}
	sort.Slice(result, func(i, j int) bool { return result[i].Name > result[j].Name })
	return result
}

func (m *Manager) Metadata(id string) (Metadata, error) {
	if err := ValidateID(id); err != nil {
		return Metadata{}, err
	}
	var selected *Info
	for _, info := range m.List() {
		if info.Name == id {
			copy := info
			selected = &copy
			break
		}
	}
	if selected == nil {
		return Metadata{}, notFound("The backup was not found.")
	}
	file, err := os.Open(selected.Path)
	if err != nil {
		return Metadata{}, err
	}
	info, err := file.Stat()
	if err != nil {
		file.Close()
		return Metadata{}, err
	}
	cacheKey := hashEntry{size: info.Size(), timestamp: info.ModTime().UnixNano()}
	m.hashMu.Lock()
	cached, ok := m.hashes[id]
	m.hashMu.Unlock()
	hash := cached.hash
	if !ok || cached.size != cacheKey.size || cached.timestamp != cacheKey.timestamp {
		digest := sha256.New()
		if _, err := io.Copy(digest, file); err != nil {
			file.Close()
			return Metadata{}, err
		}
		hash = hex.EncodeToString(digest.Sum(nil))
		cacheKey.hash = hash
		m.hashMu.Lock()
		m.hashes[id] = cacheKey
		m.hashMu.Unlock()
	}
	if err := file.Close(); err != nil {
		return Metadata{}, err
	}
	label := strings.TrimSuffix(id, filepath.Ext(id))
	desc := m.readDescription(id)
	if desc.Label != "" {
		label = desc.Label
	}
	descriptionText := ""
	if desc.Description != nil {
		descriptionText = *desc.Description
	}
	return Metadata{
		ID: id, Label: label, Description: descriptionText,
		CreatedAt: time.Unix(selected.CreatedAt, 0).UTC(), SizeBytes: selected.SizeBytes,
		SHA256: hash, MinecraftVersion: m.mcVersion,
	}, nil
}

func (m *Manager) SetDescription(backupPath, label, descriptionText string) error {
	path := backupPath + ".agent.json"
	temporary := path + "." + randomID() + ".tmp"
	payload, err := json.Marshal(description{Label: label, Description: stringPointerIfNotEmpty(descriptionText)})
	if err != nil {
		return err
	}
	if err := os.WriteFile(temporary, payload, 0o600); err != nil {
		return err
	}
	defer os.Remove(temporary)
	return replaceFile(temporary, path)
}

func (m *Manager) Delete(id, expectedSHA string) error {
	metadata, err := m.Metadata(id)
	if err != nil {
		return err
	}
	if metadata.SHA256 != expectedSHA {
		return revisionConflict(expectedSHA)
	}
	if err := os.Remove(filepath.Join(m.backupsDir, id)); err != nil {
		return err
	}
	_ = os.Remove(filepath.Join(m.backupsDir, id+".agent.json"))
	m.hashMu.Lock()
	delete(m.hashes, id)
	m.hashMu.Unlock()
	return nil
}

func (m *Manager) Restore(ctx context.Context, id, expectedSHA string) error {
	metadata, err := m.Metadata(id)
	if err != nil {
		return err
	}
	if metadata.SHA256 != expectedSHA {
		return revisionConflict(expectedSHA)
	}
	if m.core.IsRunning() {
		return serverRunning()
	}
	if _, err := m.Create(ctx); err != nil {
		return fmt.Errorf("pre-restore safety backup failed: %w", err)
	}
	archive, err := zip.OpenReader(filepath.Join(m.backupsDir, id))
	if err != nil {
		return fmt.Errorf("open backup: %w", err)
	}
	defer archive.Close()
	for _, entry := range archive.File {
		if err := ctx.Err(); err != nil {
			return err
		}
		cleaned, err := cleanZipPath(entry.Name)
		if err != nil || cleaned == "" {
			return invalidRequest("backup contains an unsafe path")
		}
		target, err := safeTarget(m.instanceDir, cleaned)
		if err != nil {
			return err
		}
		if strings.HasSuffix(entry.Name, "/") || entry.FileInfo().IsDir() {
			if err := os.MkdirAll(target, 0o750); err != nil {
				return err
			}
			continue
		}
		if err := os.MkdirAll(filepath.Dir(target), 0o750); err != nil {
			return err
		}
		if err := rejectSymlinkPath(m.instanceDir, target); err != nil {
			return err
		}
		if err := extractFile(entry, target); err != nil {
			return err
		}
	}
	return nil
}

func (m *Manager) CurrentSettings() Settings {
	m.settingsOnce.Do(func() {
		m.settings = Settings{Enabled: false, IntervalHours: 6, KeepCount: 5, KeepDays: 0}
		if payload, err := os.ReadFile(filepath.Join(m.serverRoot, "backup-settings.json")); err == nil {
			settings := m.settings
			if json.Unmarshal(payload, &settings) == nil {
				m.settings = settings
			}
		}
		if m.settings.IntervalHours < 1 {
			m.settings.IntervalHours = 1
		}
		if m.settings.KeepCount < 0 {
			m.settings.KeepCount = 0
		}
		if m.settings.KeepDays < 0 {
			m.settings.KeepDays = 0
		}
	})
	return m.settings
}

func (m *Manager) prune() error {
	settings := m.CurrentSettings()
	if settings.KeepCount == 0 && settings.KeepDays == 0 {
		return nil
	}
	deadline := m.now().Add(-time.Duration(settings.KeepDays) * 24 * time.Hour)
	for index, info := range m.List() {
		overCount := settings.KeepCount > 0 && index >= settings.KeepCount
		overAge := settings.KeepDays > 0 && time.Unix(info.CreatedAt, 0).Before(deadline)
		if index == 0 || (!overCount && !overAge) {
			continue
		}
		if err := os.Remove(info.Path); err != nil {
			return err
		}
	}
	return nil
}

func (m *Manager) uniqueName(base string) (string, error) {
	name := base
	for index := 1; index <= 1000; index++ {
		if _, err := os.Lstat(filepath.Join(m.backupsDir, name)); os.IsNotExist(err) {
			return name, nil
		} else if err != nil {
			return "", err
		}
		name = strings.TrimSuffix(base, filepath.Ext(base)) + fmt.Sprintf("-%d.zip", index)
	}
	return "", errors.New("could not allocate a unique backup name")
}

func (m *Manager) writeArchive(ctx context.Context, target string) (int64, error) {
	file, err := os.Create(target)
	if err != nil {
		return 0, err
	}
	closed := false
	defer func() {
		if !closed {
			_ = file.Close()
		}
	}()
	archive := zip.NewWriter(file)
	var written int64
	err = filepath.WalkDir(m.instanceDir, func(path string, entry os.DirEntry, walkErr error) error {
		if walkErr != nil {
			return walkErr
		}
		if err := ctx.Err(); err != nil {
			return err
		}
		if entry.Type()&os.ModeSymlink != 0 {
			if entry.IsDir() {
				return filepath.SkipDir
			}
			return nil
		}
		if entry.IsDir() {
			return nil
		}
		relative, err := filepath.Rel(m.instanceDir, path)
		if err != nil {
			return err
		}
		top := strings.Split(relative, string(filepath.Separator))[0]
		if top == "backups" || top == "logs" || strings.HasSuffix(strings.ToLower(path), ".tmp") ||
			strings.HasSuffix(strings.ToLower(path), ".mofu-download") {
			return nil
		}
		info, err := entry.Info()
		if err != nil {
			return err
		}
		header, err := zip.FileInfoHeader(info)
		if err != nil {
			return err
		}
		header.Name = filepath.ToSlash(relative)
		header.Method = zip.Deflate
		writer, err := archive.CreateHeader(header)
		if err != nil {
			return err
		}
		source, err := os.Open(path)
		if err != nil {
			return err
		}
		_, copyErr := io.Copy(writer, source)
		closeErr := source.Close()
		if copyErr != nil {
			return copyErr
		}
		if closeErr != nil {
			return closeErr
		}
		written += info.Size()
		if written > MaxTotalBytes {
			return fmt.Errorf("backup source exceeds 8 GiB")
		}
		return nil
	})
	if err != nil {
		archive.Close()
		return 0, err
	}
	if err := archive.Close(); err != nil {
		return 0, err
	}
	if err := file.Sync(); err != nil {
		return 0, err
	}
	closeErr := file.Close()
	closed = true
	return written, closeErr
}

func (m *Manager) readDescription(id string) description {
	path := filepath.Join(m.backupsDir, id+".agent.json")
	info, err := os.Stat(path)
	if err != nil || info.Size() > 4096 {
		return description{}
	}
	payload, err := os.ReadFile(path)
	if err != nil {
		return description{}
	}
	var result description
	if json.Unmarshal(payload, &result) != nil {
		return description{}
	}
	return result
}

func ValidateID(id string) error {
	if strings.TrimSpace(id) == "" || utf16Length(id) > 255 || strings.Contains(id, "..") ||
		strings.ContainsAny(id, `/\`) || !strings.EqualFold(filepath.Ext(id), ".zip") || filepath.Base(id) != id {
		return invalidRequest("The backup ID is invalid.")
	}
	return nil
}

func cleanZipPath(name string) (string, error) {
	normalized := strings.TrimSpace(strings.ReplaceAll(name, `\`, "/"))
	if normalized == "" || strings.HasPrefix(normalized, "/") || (len(normalized) > 1 && normalized[1] == ':') {
		return "", errors.New("unsafe zip path")
	}
	segments := make([]string, 0)
	for _, segment := range strings.Split(normalized, "/") {
		if segment == "" || segment == "." {
			continue
		}
		if segment == ".." {
			if len(segments) == 0 {
				return "", errors.New("zip path escapes root")
			}
			segments = segments[:len(segments)-1]
			continue
		}
		segments = append(segments, segment)
	}
	return strings.Join(segments, "/"), nil
}

func safeTarget(root, relative string) (string, error) {
	target := filepath.Clean(filepath.Join(root, filepath.FromSlash(relative)))
	within, err := filepath.Rel(root, target)
	if err != nil || within == ".." || strings.HasPrefix(within, ".."+string(filepath.Separator)) {
		return "", invalidRequest("backup contains an unsafe path")
	}
	return target, nil
}

func rejectSymlinkPath(root, target string) error {
	rootInfo, err := os.Lstat(root)
	if err != nil {
		return err
	}
	if rootInfo.Mode()&os.ModeSymlink != 0 {
		return invalidRequest("backup root is a symbolic link")
	}
	current := root
	relative, err := filepath.Rel(root, target)
	if err != nil {
		return err
	}
	for _, segment := range strings.Split(relative, string(filepath.Separator)) {
		current = filepath.Join(current, segment)
		info, err := os.Lstat(current)
		if os.IsNotExist(err) {
			return nil
		}
		if err != nil {
			return err
		}
		if info.Mode()&os.ModeSymlink != 0 {
			return invalidRequest("backup target contains a symbolic link")
		}
	}
	return nil
}

func extractFile(entry *zip.File, target string) error {
	source, err := entry.Open()
	if err != nil {
		return err
	}
	defer source.Close()
	file, err := os.Create(target)
	if err != nil {
		return err
	}
	_, copyErr := io.Copy(file, source)
	syncErr := file.Sync()
	closeErr := file.Close()
	if copyErr != nil {
		return copyErr
	}
	if syncErr != nil {
		return syncErr
	}
	return closeErr
}

func randomID() string {
	var buffer [16]byte
	if _, err := rand.Read(buffer[:]); err != nil {
		return fmt.Sprintf("%d", time.Now().UnixNano())
	}
	return hex.EncodeToString(buffer[:])
}

func stringPointerIfNotEmpty(value string) *string {
	if value == "" {
		return nil
	}
	return &value
}

func utf16Length(value string) int { return len(utf16.Encode([]rune(value))) }

func replaceFile(source, destination string) error {
	if runtime.GOOS != "windows" {
		return os.Rename(source, destination)
	}
	if err := os.Remove(destination); err != nil && !os.IsNotExist(err) {
		return err
	}
	return os.Rename(source, destination)
}
