// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

// Package files implements the safe text-file subset exposed by ServerAgent.
// It deliberately does not expose arbitrary binary files or protected server
// state through the generic file API.
package files

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/binary"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
	"time"
	"unicode"
	"unicode/utf8"
)

const (
	MaxTextBytes      = 2 * 1024 * 1024
	MissingRevision   = "0"
	maxDirectoryNodes = 10_000
)

// Error is a mapped API failure without an HTTP dependency.
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

func invalidPath() *Error {
	return &Error{Status: 400, Code: "invalid_path", Message: "The path is outside the safe text-file allowlist or contains an unsafe segment."}
}

func invalidRequest(message string) *Error {
	return &Error{Status: 400, Code: "invalid_request", Message: message}
}

func payloadTooLarge() *Error {
	return &Error{Status: 413, Code: "payload_too_large", Message: "The UTF-8 text file exceeds the 2 MiB limit."}
}

func notFound(message string) *Error {
	return &Error{Status: 404, Code: "not_found", Message: message}
}

func conflict(message string) *Error {
	return &Error{Status: 409, Code: "conflict", Message: message}
}

func revisionConflict(expected string) *Error {
	return &Error{Status: 409, Code: "revision_conflict", Message: "The resource revision does not match.", Details: map[string]string{"expectedRevision": expected}}
}

type Entry struct {
	Path        string
	IsDirectory bool
	SizeBytes   *int64
	ModifiedAt  *time.Time
	Revision    *string
	Writable    bool
}

type Mutation struct {
	Path        string
	Revision    string
	IsDirectory bool
	ModifiedAt  time.Time
}

type Content struct {
	Path       string
	Content    string
	Revision   string
	SizeBytes  int64
	ModifiedAt time.Time
}

// Service serializes operations exactly as SafeFileService's SemaphoreSlim.
type Service struct {
	root      string
	protected map[string]struct{}
	sem       chan struct{}
}

func New(root string, protectedPaths ...string) (*Service, error) {
	absolute, err := filepath.Abs(root)
	if err != nil {
		return nil, err
	}
	service := &Service{
		root:      filepath.Clean(absolute),
		protected: make(map[string]struct{}, len(protectedPaths)),
		sem:       make(chan struct{}, 1),
	}
	service.sem <- struct{}{}
	for _, path := range protectedPaths {
		absolute, err := filepath.Abs(path)
		if err != nil {
			return nil, err
		}
		service.protected[filepath.Clean(absolute)] = struct{}{}
	}
	return service, nil
}

func (s *Service) acquire(ctx context.Context) error {
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-s.sem:
		return nil
	}
}

func (s *Service) release() { s.sem <- struct{}{} }

var blockedDirectories = map[string]struct{}{
	"auth": {}, "audit": {}, "backups": {}, "logs": {}, ".mofumofu-agent": {}, "world": {},
}

var blockedFiles = map[string]struct{}{
	"server.json": {}, "server.properties": {}, "eula.txt": {}, "whitelist.json": {},
	"ops.json": {}, "banned-players.json": {}, "banned-ips.json": {}, "usercache.json": {},
	"usernamecache.json": {}, "level.dat_old": {},
}

var blockedExtensions = map[string]struct{}{
	".mca": {}, ".mcc": {}, ".dat": {}, ".dat_old": {}, ".db": {}, ".zip": {},
	".jar": {}, ".dll": {}, ".exe": {},
}

// NormalizeRelativePath preserves spelling/case and only validates syntax.
func NormalizeRelativePath(path string, allowEmpty bool) (string, error) {
	if path == "" {
		if allowEmpty {
			return "", nil
		}
		return "", invalidPath()
	}
	if strings.HasPrefix(path, "/") || strings.Contains(path, "\\") || strings.ContainsRune(path, ':') ||
		strings.Contains(path, "//") || !utf8.ValidString(path) {
		return "", invalidPath()
	}
	for _, r := range path {
		if unicode.IsControl(r) {
			return "", invalidPath()
		}
	}
	segments := strings.Split(path, "/")
	for _, segment := range segments {
		if segment == "" || segment == "." || segment == ".." ||
			strings.HasSuffix(segment, " ") || strings.HasSuffix(segment, ".") {
			return "", invalidPath()
		}
	}
	return strings.Join(segments, "/"), nil
}

func (s *Service) List(ctx context.Context, path string) ([]Entry, error) {
	if err := s.acquire(ctx); err != nil {
		return nil, err
	}
	defer s.release()
	relative, err := NormalizeRelativePath(path, true)
	if err != nil {
		return nil, err
	}
	full, err := s.resolve(relative, false)
	if err != nil {
		return nil, err
	}
	info, err := os.Stat(full)
	if err != nil || !info.IsDir() {
		return nil, notFound("The directory was not found.")
	}
	children, err := os.ReadDir(full)
	if err != nil {
		return nil, err
	}
	sort.Slice(children, func(i, j int) bool { return children[i].Name() < children[j].Name() })
	entries := make([]Entry, 0, len(children))
	for _, child := range children {
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		name := child.Name()
		childRelative := name
		if relative != "" {
			childRelative = relative + "/" + name
		}
		childFull := filepath.Join(full, name)
		info, err := os.Lstat(childFull)
		if err != nil || info.Mode()&os.ModeSymlink != 0 {
			continue
		}
		if _, err := s.resolve(childRelative, false); err != nil {
			continue
		}
		modified := info.ModTime().UTC()
		if info.IsDir() {
			entries = append(entries, Entry{Path: childRelative, IsDirectory: true, ModifiedAt: &modified})
		} else {
			if info.Size() > MaxTextBytes {
				continue
			}
			revision, err := computeFileRevision(childFull, info)
			if err != nil {
				return nil, err
			}
			size := info.Size()
			entries = append(entries, Entry{Path: childRelative, SizeBytes: &size, ModifiedAt: &modified, Revision: &revision, Writable: true})
		}
		if len(entries) >= 1000 {
			break
		}
	}
	return entries, nil
}

func (s *Service) Get(ctx context.Context, path string) (Mutation, error) {
	if err := s.acquire(ctx); err != nil {
		return Mutation{}, err
	}
	defer s.release()
	relative, err := NormalizeRelativePath(path, false)
	if err != nil {
		return Mutation{}, err
	}
	return s.get(ctx, relative)
}

func (s *Service) Create(ctx context.Context, path string, directory bool) (Mutation, error) {
	if err := s.acquire(ctx); err != nil {
		return Mutation{}, err
	}
	defer s.release()
	relative, err := NormalizeRelativePath(path, false)
	if err != nil {
		return Mutation{}, err
	}
	full, err := s.resolve(relative, false)
	if err != nil {
		return Mutation{}, err
	}
	if _, err := os.Lstat(full); err == nil {
		return Mutation{}, conflict("The file or directory already exists.")
	} else if !os.IsNotExist(err) {
		return Mutation{}, err
	}
	parent := filepath.Dir(full)
	info, err := os.Stat(parent)
	if err != nil || !info.IsDir() {
		return Mutation{}, notFound("The parent directory was not found.")
	}
	if err := s.recheck(relative, full); err != nil {
		return Mutation{}, err
	}
	if directory {
		if err := os.Mkdir(full, 0o750); err != nil {
			return Mutation{}, err
		}
	} else {
		file, err := os.OpenFile(full, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0o600)
		if err != nil {
			return Mutation{}, err
		}
		if err := file.Sync(); err != nil {
			file.Close()
			return Mutation{}, err
		}
		if err := file.Close(); err != nil {
			return Mutation{}, err
		}
	}
	return s.get(ctx, relative)
}

func (s *Service) Rename(ctx context.Context, path, newName, expectedRevision string) (Mutation, error) {
	if err := s.acquire(ctx); err != nil {
		return Mutation{}, err
	}
	defer s.release()
	sourceRelative, err := NormalizeRelativePath(path, false)
	if err != nil {
		return Mutation{}, err
	}
	if newName == "" || strings.ContainsAny(newName, `/\`) {
		return Mutation{}, invalidPath()
	}
	leaf, err := NormalizeRelativePath(newName, false)
	if err != nil {
		return Mutation{}, err
	}
	parent := filepath.ToSlash(filepath.Dir(sourceRelative))
	if parent == "." {
		parent = ""
	}
	destinationRelative := leaf
	if parent != "" {
		destinationRelative = parent + "/" + leaf
	}
	source, err := s.resolve(sourceRelative, false)
	if err != nil {
		return Mutation{}, err
	}
	destination, err := s.resolve(destinationRelative, false)
	if err != nil {
		return Mutation{}, err
	}
	current, err := s.get(ctx, sourceRelative)
	if err != nil {
		return Mutation{}, err
	}
	if err := ensureRevision(current.Revision, expectedRevision); err != nil {
		return Mutation{}, err
	}
	if _, err := os.Lstat(destination); err == nil {
		return Mutation{}, conflict("The rename destination already exists.")
	} else if !os.IsNotExist(err) {
		return Mutation{}, err
	}
	if err := s.recheck(sourceRelative, source); err != nil {
		return Mutation{}, err
	}
	if err := s.recheck(destinationRelative, destination); err != nil {
		return Mutation{}, err
	}
	if err := os.Rename(source, destination); err != nil {
		return Mutation{}, err
	}
	return s.get(ctx, destinationRelative)
}

func (s *Service) Read(ctx context.Context, path string) (Content, error) {
	if err := s.acquire(ctx); err != nil {
		return Content{}, err
	}
	defer s.release()
	relative, err := NormalizeRelativePath(path, false)
	if err != nil {
		return Content{}, err
	}
	return s.read(ctx, relative, false)
}

func (s *Service) Write(ctx context.Context, path, content, expectedRevision string) (Content, error) {
	if err := s.acquire(ctx); err != nil {
		return Content{}, err
	}
	defer s.release()
	relative, err := NormalizeRelativePath(path, false)
	if err != nil {
		return Content{}, err
	}
	return s.write(ctx, relative, content, expectedRevision, false)
}

func (s *Service) Delete(ctx context.Context, path, expectedRevision string) error {
	if err := s.acquire(ctx); err != nil {
		return err
	}
	defer s.release()
	relative, err := NormalizeRelativePath(path, false)
	if err != nil {
		return err
	}
	full, err := s.resolve(relative, false)
	if err != nil {
		return err
	}
	current, err := s.get(ctx, relative)
	if err != nil {
		return err
	}
	if err := ensureRevision(current.Revision, expectedRevision); err != nil {
		return err
	}
	if err := s.recheck(relative, full); err != nil {
		return err
	}
	if !current.IsDirectory {
		return os.Remove(full)
	}
	latest, err := s.directoryRevision(ctx, relative, full)
	if err != nil {
		return err
	}
	if err := ensureRevision(latest, expectedRevision); err != nil {
		return err
	}
	return s.deleteDirectory(ctx, relative, full)
}

func (s *Service) ReadProperties(ctx context.Context) (Content, error) {
	if err := s.acquire(ctx); err != nil {
		return Content{}, err
	}
	defer s.release()
	return s.read(ctx, "server.properties", true)
}

func (s *Service) WriteProperties(ctx context.Context, content, expectedRevision string) (Content, error) {
	if err := s.acquire(ctx); err != nil {
		return Content{}, err
	}
	defer s.release()
	return s.write(ctx, "server.properties", content, expectedRevision, true)
}

func (s *Service) get(ctx context.Context, relative string) (Mutation, error) {
	if err := ctx.Err(); err != nil {
		return Mutation{}, err
	}
	full, err := s.resolve(relative, false)
	if err != nil {
		return Mutation{}, err
	}
	info, err := os.Stat(full)
	if err != nil {
		return Mutation{}, notFound("The file or directory was not found.")
	}
	modified := info.ModTime().UTC()
	if info.IsDir() {
		if err := s.recheck(relative, full); err != nil {
			return Mutation{}, err
		}
		revision, err := s.directoryRevision(ctx, relative, full)
		if err != nil {
			return Mutation{}, err
		}
		return Mutation{Path: relative, Revision: revision, IsDirectory: true, ModifiedAt: modified}, nil
	}
	content, err := s.read(ctx, relative, false)
	if err != nil {
		return Mutation{}, err
	}
	return Mutation{Path: relative, Revision: content.Revision, ModifiedAt: content.ModifiedAt}, nil
}

func (s *Service) read(ctx context.Context, relative string, allowProtected bool) (Content, error) {
	if err := ctx.Err(); err != nil {
		return Content{}, err
	}
	full, err := s.resolve(relative, allowProtected)
	if err != nil {
		return Content{}, err
	}
	info, err := os.Stat(full)
	if err != nil || info.IsDir() {
		return Content{}, notFound("The file was not found.")
	}
	if err := s.recheck(relative, full); err != nil {
		return Content{}, err
	}
	if info.Size() > MaxTextBytes {
		return Content{}, payloadTooLarge()
	}
	file, err := os.Open(full)
	if err != nil {
		return Content{}, err
	}
	payload := make([]byte, info.Size())
	read, readErr := io.ReadFull(file, payload)
	closeErr := file.Close()
	if readErr != nil || closeErr != nil {
		return Content{}, fmt.Errorf("read text file: %w", errors.Join(readErr, closeErr))
	}
	if int64(read) > MaxTextBytes {
		return Content{}, payloadTooLarge()
	}
	if err := s.recheck(relative, full); err != nil {
		return Content{}, err
	}
	info, err = os.Stat(full)
	if err != nil {
		return Content{}, err
	}
	if !utf8.Valid(payload) {
		return Content{}, invalidRequest("The file is not valid UTF-8 text.")
	}
	modified := info.ModTime().UTC()
	return Content{
		Path: relative, Content: string(payload), Revision: computeRevision(payload, modified),
		SizeBytes: int64(len(payload)), ModifiedAt: modified,
	}, nil
}

func (s *Service) write(ctx context.Context, relative, content, expectedRevision string, allowProtected bool) (Content, error) {
	if err := ctx.Err(); err != nil {
		return Content{}, err
	}
	full, err := s.resolve(relative, allowProtected)
	if err != nil {
		return Content{}, err
	}
	if !utf8.ValidString(content) {
		return Content{}, invalidRequest("The content is not valid UTF-8 text.")
	}
	payload := []byte(content)
	if len(payload) > MaxTextBytes {
		return Content{}, payloadTooLarge()
	}
	currentRevision := MissingRevision
	if info, err := os.Stat(full); err == nil {
		if info.IsDir() {
			return Content{}, invalidPath()
		}
		current, err := s.read(ctx, relative, allowProtected)
		if err != nil {
			return Content{}, err
		}
		currentRevision = current.Revision
	} else if !os.IsNotExist(err) {
		return Content{}, err
	}
	if err := ensureRevision(currentRevision, expectedRevision); err != nil {
		return Content{}, err
	}
	parent := filepath.Dir(full)
	info, err := os.Stat(parent)
	if err != nil || !info.IsDir() {
		return Content{}, notFound("The parent directory was not found.")
	}
	temporary, err := temporaryPath(parent)
	if err != nil {
		return Content{}, err
	}
	defer os.Remove(temporary)
	file, err := os.OpenFile(temporary, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0o600)
	if err != nil {
		return Content{}, err
	}
	if _, err := file.Write(payload); err != nil {
		file.Close()
		return Content{}, err
	}
	if err := file.Sync(); err != nil {
		file.Close()
		return Content{}, err
	}
	if err := file.Close(); err != nil {
		return Content{}, err
	}
	if err := s.recheck(relative, full); err != nil {
		return Content{}, err
	}
	latestRevision := MissingRevision
	if _, err := os.Stat(full); err == nil {
		latest, err := s.read(ctx, relative, allowProtected)
		if err != nil {
			return Content{}, err
		}
		latestRevision = latest.Revision
	} else if !os.IsNotExist(err) {
		return Content{}, err
	}
	if err := ensureRevision(latestRevision, expectedRevision); err != nil {
		return Content{}, err
	}
	if err := ctx.Err(); err != nil {
		return Content{}, err
	}
	if err := replaceFile(temporary, full); err != nil {
		return Content{}, err
	}
	if err := s.recheck(relative, full); err != nil {
		return Content{}, err
	}
	return s.read(ctx, relative, allowProtected)
}

func (s *Service) resolve(relative string, allowProtected bool) (string, error) {
	rootInfo, err := os.Lstat(s.root)
	if err != nil {
		return "", notFound("The server instance no longer exists.")
	}
	if rootInfo.Mode()&os.ModeSymlink != 0 || !rootInfo.IsDir() {
		return "", invalidPath()
	}
	full := s.root
	if relative != "" {
		full = filepath.Clean(filepath.Join(s.root, filepath.FromSlash(relative)))
	}
	if !pathWithin(s.root, full) {
		return "", invalidPath()
	}
	if !allowProtected && s.isProtected(relative, full) {
		return "", invalidPath()
	}
	if err := s.recheck(relative, full); err != nil {
		return "", err
	}
	return full, nil
}

func (s *Service) recheck(relative, full string) error {
	rootInfo, err := os.Lstat(s.root)
	if err != nil || rootInfo.Mode()&os.ModeSymlink != 0 || !rootInfo.IsDir() {
		return invalidPath()
	}
	current := s.root
	for _, segment := range strings.Split(relative, "/") {
		if segment == "" {
			continue
		}
		current = filepath.Join(current, segment)
		info, err := os.Lstat(current)
		if err == nil && info.Mode()&os.ModeSymlink != 0 {
			return invalidPath()
		}
		if err != nil && !os.IsNotExist(err) {
			return invalidPath()
		}
		if os.IsNotExist(err) {
			break
		}
	}
	expected := s.root
	if relative != "" {
		expected = filepath.Clean(filepath.Join(s.root, filepath.FromSlash(relative)))
	}
	if !samePath(expected, full) {
		return invalidPath()
	}
	return nil
}

func (s *Service) isProtected(relative, full string) bool {
	if relative == "" {
		return false
	}
	segments := strings.Split(relative, "/")
	for _, segment := range segments {
		lower := strings.ToLower(segment)
		if _, ok := blockedDirectories[lower]; ok || lower == "world" || strings.HasPrefix(lower, "world_") {
			return true
		}
	}
	name := strings.ToLower(segments[len(segments)-1])
	if _, ok := blockedFiles[name]; ok {
		return true
	}
	if _, ok := blockedExtensions[strings.ToLower(filepath.Ext(name))]; ok {
		return true
	}
	_, ok := s.protected[filepath.Clean(full)]
	return ok
}

func (s *Service) directoryRevision(ctx context.Context, relative, full string) (string, error) {
	var builder strings.Builder
	count := 0
	var visit func(string, string, bool) error
	visit = func(currentRelative, currentFull string, root bool) error {
		if err := ctx.Err(); err != nil {
			return err
		}
		count++
		info, err := os.Lstat(currentFull)
		if err != nil {
			return err
		}
		if count > maxDirectoryNodes {
			return payloadTooLarge()
		}
		if info.Mode()&os.ModeSymlink != 0 {
			if root {
				return invalidPath()
			}
			return payloadTooLarge()
		}
		builder.WriteString(currentRelative)
		builder.WriteByte('|')
		builder.WriteString(int64String(dotnetTicks(info.ModTime().UTC())))
		builder.WriteByte('\n')
		children, err := os.ReadDir(currentFull)
		if err != nil {
			return err
		}
		sort.Slice(children, func(i, j int) bool {
			return filepath.Join(currentFull, children[i].Name()) < filepath.Join(currentFull, children[j].Name())
		})
		for _, child := range children {
			if err := ctx.Err(); err != nil {
				return err
			}
			count++
			if count > maxDirectoryNodes {
				return payloadTooLarge()
			}
			name := child.Name()
			childRelative := currentRelative + "/" + name
			childFull := filepath.Join(currentFull, name)
			info, err := os.Lstat(childFull)
			if err != nil {
				return err
			}
			if info.Mode()&os.ModeSymlink != 0 || s.isProtected(childRelative, childFull) {
				return invalidPath()
			}
			builder.WriteString(childRelative)
			builder.WriteByte('|')
			if info.IsDir() {
				builder.WriteString("d|")
				builder.WriteString(int64String(dotnetTicks(info.ModTime().UTC())))
				builder.WriteByte('\n')
				if err := visit(childRelative, childFull, false); err != nil {
					return err
				}
			} else {
				builder.WriteString("f|")
				builder.WriteString(int64String(info.Size()))
				builder.WriteByte('|')
				builder.WriteString(int64String(dotnetTicks(info.ModTime().UTC())))
				builder.WriteByte('\n')
			}
		}
		return nil
	}
	if err := visit(relative, full, true); err != nil {
		return "", err
	}
	sum := sha256.Sum256([]byte(builder.String()))
	return "d_" + hex.EncodeToString(sum[:]), nil
}

func (s *Service) deleteDirectory(ctx context.Context, relative, full string) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	if err := s.recheck(relative, full); err != nil {
		return err
	}
	children, err := os.ReadDir(full)
	if err != nil {
		return err
	}
	for _, child := range children {
		if err := ctx.Err(); err != nil {
			return err
		}
		name := child.Name()
		childRelative := relative + "/" + name
		childFull := filepath.Join(full, name)
		info, err := os.Lstat(childFull)
		if err != nil {
			return err
		}
		if info.Mode()&os.ModeSymlink != 0 || s.isProtected(childRelative, childFull) {
			return invalidPath()
		}
		if info.IsDir() {
			if err := s.deleteDirectory(ctx, childRelative, childFull); err != nil {
				return err
			}
		} else if err := os.Remove(childFull); err != nil {
			return err
		}
	}
	if err := s.recheck(relative, full); err != nil {
		return err
	}
	return os.Remove(full)
}

func computeFileRevision(path string, info os.FileInfo) (string, error) {
	if info.Size() > MaxTextBytes {
		return "", payloadTooLarge()
	}
	payload, err := os.ReadFile(path)
	if err != nil {
		return "", err
	}
	if int64(len(payload)) > MaxTextBytes {
		return "", payloadTooLarge()
	}
	return computeRevision(payload, info.ModTime().UTC()), nil
}

func computeRevision(payload []byte, modified time.Time) string {
	hash := sha256.New()
	_, _ = hash.Write(payload)
	var timestamp [8]byte
	binary.LittleEndian.PutUint64(timestamp[:], uint64(dotnetTicks(modified)))
	_, _ = hash.Write(timestamp[:])
	return "r_" + hex.EncodeToString(hash.Sum(nil))
}

const dotnetEpochOffset = 621355968000000000

func dotnetTicks(t time.Time) int64 {
	return t.Unix()*10_000_000 + int64(t.Nanosecond())/100 + dotnetEpochOffset
}

func int64String(value int64) string { return fmt.Sprintf("%d", value) }

func ensureRevision(actual, expected string) error {
	wanted := expected
	if wanted == "" {
		wanted = MissingRevision
	}
	if actual != wanted {
		return revisionConflict(wanted)
	}
	return nil
}

func temporaryPath(directory string) (string, error) {
	var random [16]byte
	if _, err := rand.Read(random[:]); err != nil {
		return "", err
	}
	return filepath.Join(directory, ".mofu-"+hex.EncodeToString(random[:])+".tmp"), nil
}

func replaceFile(source, destination string) error {
	if runtime.GOOS != "windows" {
		return os.Rename(source, destination)
	}
	// Windows is only a development/test host. Linux uses the atomic rename
	// above; this fallback keeps unit tests usable without changing Linux behavior.
	if err := os.Remove(destination); err != nil && !os.IsNotExist(err) {
		return err
	}
	return os.Rename(source, destination)
}

func pathWithin(root, path string) bool {
	relative, err := filepath.Rel(root, path)
	if err != nil || relative == ".." || strings.HasPrefix(relative, ".."+string(filepath.Separator)) {
		return false
	}
	return true
}

func samePath(left, right string) bool {
	if runtime.GOOS == "windows" {
		return strings.EqualFold(left, right)
	}
	return left == right
}
