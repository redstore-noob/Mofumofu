// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package server

import (
	"context"
	"errors"
	"io"
	"log/slog"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/rcon"
)

// AttachSupervisor observes a server managed by a panel. It never starts,
// stops, kills, or reconfigures the process.
type AttachSupervisor struct {
	metadata  *Metadata
	logger    *slog.Logger
	now       func() time.Time
	onConsole func(string)

	mu       sync.Mutex
	console  []ConsoleLine
	cursor   int64
	offset   int64
	pending  string
	identity os.FileInfo
}

func NewAttachSupervisor(metadata *Metadata, logger *slog.Logger, now func() time.Time, onConsole func(string)) *AttachSupervisor {
	if logger == nil {
		logger = slog.Default()
	}
	if now == nil {
		now = time.Now
	}
	return &AttachSupervisor{metadata: metadata, logger: logger, now: now, onConsole: onConsole}
}

func (s *AttachSupervisor) SupportsControl() bool { return false }
func (s *AttachSupervisor) SupportsCommand() bool {
	configuration, err := loadRconConfiguration(s.metadata.Directory)
	return err == nil && configuration.Enabled
}

func (s *AttachSupervisor) Start(context.Context) error             { return attachUnsupported() }
func (s *AttachSupervisor) Stop(context.Context, bool) error        { return attachUnsupported() }
func (s *AttachSupervisor) Restart(context.Context) error           { return attachUnsupported() }
func (s *AttachSupervisor) Kill(context.Context) error              { return attachUnsupported() }
func (s *AttachSupervisor) NoteIntentionalStop()                    {}
func (s *AttachSupervisor) Shutdown(context.Context, time.Duration) {}

func attachUnsupported() *Error {
	return &Error{Status: 403, Code: "capability_not_supported", Message: "This server is managed externally; lifecycle control is not supported."}
}

func (s *AttachSupervisor) Poll(ctx context.Context) {
	if err := ctx.Err(); err != nil {
		return
	}
	s.pollConsole()
	configuration, err := loadRconConfiguration(s.metadata.Directory)
	if err != nil || !configuration.Enabled {
		return
	}
	probeCtx, cancel := context.WithTimeout(ctx, 2*time.Second)
	defer cancel()
	_, _ = rcon.Execute(probeCtx, rcon.Config{Port: configuration.Port, Password: configuration.Password}, "list")
}

func (s *AttachSupervisor) Run(ctx context.Context) {
	ticker := time.NewTicker(time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			s.Poll(ctx)
		}
	}
}

func (s *AttachSupervisor) State() string {
	s.pollConsole()
	configuration, err := loadRconConfiguration(s.metadata.Directory)
	if err == nil && configuration.Enabled {
		ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
		_, commandErr := rcon.Execute(ctx, rcon.Config{Port: configuration.Port, Password: configuration.Password}, "list")
		cancel()
		if commandErr == nil {
			return StateRunning
		}
	}
	info, err := os.Stat(filepath.Join(s.metadata.Directory, "logs", "latest.log"))
	if err == nil && time.Since(info.ModTime()) < 2*time.Minute {
		return StateRunning
	}
	return StateStopped
}

func (s *AttachSupervisor) IsRunning() bool {
	state := s.State()
	return state == StateRunning || state == StateStarting || state == StateStopping
}

func (s *AttachSupervisor) Snapshot() Snapshot {
	return Snapshot{State: s.State(), ExitCode: -1}
}

func (s *AttachSupervisor) Command(ctx context.Context, command string) error {
	if strings.TrimSpace(command) == "" {
		return InvalidCommand()
	}
	configuration, err := loadRconConfiguration(s.metadata.Directory)
	if err != nil || !configuration.Enabled {
		return ServerOffline()
	}
	_, err = rcon.Execute(ctx, rcon.Config{Port: configuration.Port, Password: configuration.Password}, command)
	return err
}

func (s *AttachSupervisor) OnlineNames(ctx context.Context) []string {
	configuration, err := loadRconConfiguration(s.metadata.Directory)
	if err != nil || !configuration.Enabled {
		return []string{}
	}
	response, err := rcon.Execute(ctx, rcon.Config{Port: configuration.Port, Password: configuration.Password}, "list")
	if err != nil {
		return []string{}
	}
	return ParsePlayerListNames(response)
}

func (s *AttachSupervisor) OnlineNamesDetailed(ctx context.Context) ([]string, error) {
	configuration, err := loadRconConfiguration(s.metadata.Directory)
	if err != nil || !configuration.Enabled {
		return nil, ServerOffline()
	}
	response, err := rcon.Execute(ctx, rcon.Config{Port: configuration.Port, Password: configuration.Password}, "list")
	if err != nil {
		return nil, err
	}
	if !playerCountPattern.MatchString(response) {
		return nil, errors.New("invalid player list response")
	}
	return ParsePlayerListNames(response), nil
}

func (s *AttachSupervisor) RecentConsole(after int64) ([]ConsoleLine, int64) {
	s.pollConsole()
	s.mu.Lock()
	defer s.mu.Unlock()
	result := make([]ConsoleLine, 0, len(s.console))
	for _, line := range s.console {
		if line.Sequence > after {
			result = append(result, line)
		}
	}
	return result, s.cursor
}

func (s *AttachSupervisor) AutoRestartEnabled() bool { return false }

func (s *AttachSupervisor) pollConsole() {
	path := filepath.Join(s.metadata.Directory, "logs", "latest.log")
	file, err := os.Open(path)
	if err != nil {
		return
	}
	defer file.Close()
	info, err := file.Stat()
	if err != nil {
		return
	}
	s.mu.Lock()
	if s.identity != nil && !os.SameFile(s.identity, info) {
		s.offset = 0
		s.pending = ""
	}
	s.identity = info
	if info.Size() < s.offset {
		s.offset = 0
		s.pending = ""
	}
	offset := s.offset
	s.mu.Unlock()
	if info.Size() <= offset {
		return
	}
	if _, err := file.Seek(offset, io.SeekStart); err != nil {
		return
	}
	payload, err := io.ReadAll(io.LimitReader(file, info.Size()-offset))
	if err != nil {
		return
	}
	s.mu.Lock()
	s.offset += int64(len(payload))
	s.pending += string(payload)
	parts := strings.Split(s.pending, "\n")
	s.pending = parts[len(parts)-1]
	lines := parts[:len(parts)-1]
	s.mu.Unlock()
	for _, line := range lines {
		line = strings.TrimSuffix(line, "\r")
		s.appendConsole(line)
	}
}

func (s *AttachSupervisor) appendConsole(line string) {
	s.mu.Lock()
	s.cursor++
	entry := ConsoleLine{Sequence: s.cursor, Timestamp: s.now(), Message: line}
	s.console = append(s.console, entry)
	if len(s.console) > ConsoleCapacity {
		s.console = append([]ConsoleLine(nil), s.console[len(s.console)-ConsoleCapacity:]...)
	}
	callback := s.onConsole
	s.mu.Unlock()
	if callback != nil {
		callback(line)
	}
}

func loadRconConfiguration(directory string) (RconConfiguration, error) {
	payload, err := os.ReadFile(filepath.Join(directory, "server.properties"))
	if err != nil {
		return RconConfiguration{}, err
	}
	_, values := collectPropertyLines(string(payload))
	if !strings.EqualFold(strings.TrimSpace(values["enable-rcon"]), "true") {
		return RconConfiguration{}, nil
	}
	port := 25575
	if text, exists := values["rcon.port"]; exists {
		parsed, err := strconv.Atoi(strings.TrimSpace(text))
		if err != nil || parsed < 1 || parsed > 65535 {
			return RconConfiguration{}, nil
		}
		port = parsed
	}
	password := strings.TrimSpace(values["rcon.password"])
	if password == "" {
		return RconConfiguration{}, nil
	}
	return RconConfiguration{Enabled: true, Port: port, Password: password}, nil
}
