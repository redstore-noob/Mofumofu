// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package server

import (
	"bufio"
	"context"
	"crypto/rand"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/rcon"
)

const (
	ConsoleCapacity = 800
	pidFileName     = ".mofu-pid"
)

var playerCountPattern = regexp.MustCompile(`There are (\d+) of a max of (\d+) players online`)

type ConsoleLine struct {
	Sequence  int64
	Timestamp time.Time
	Message   string
}

type Snapshot struct {
	State         string
	OnlinePlayers int
	CPUPercent    *float64
	MemoryBytes   *int64
	PID           int
	ExitCode      int
	StartedAt     time.Time
}

type RconConfiguration struct {
	Enabled  bool
	Port     int
	Password string
}

type Options struct {
	Logger              *slog.Logger
	OnConsole           func(string)
	Environ             []string
	ExtraJavaCandidates []string
	JavaPathOverride    string
	NoEnsureRcon        bool
	StopGrace           time.Duration
	ForceWait           time.Duration
	AutoRestartDelay    time.Duration
	AutoRestartReset    time.Duration
	MaxAutoRestarts     int
	Now                 func() time.Time
	Launch              func(javaPath string, args []string, directory string) (*exec.Cmd, error)
}

type processRun struct {
	command      *exec.Cmd
	stdin        io.WriteCloser
	done         chan struct{}
	pid          int
	startedAt    time.Time
	writeMu      sync.Mutex
	metricMu     sync.Mutex
	lastCPUTicks float64
	lastSample   time.Time
}

// Supervisor owns one managed Minecraft process. It intentionally mirrors the
// C# Core timing and fallback rules rather than imposing a new lifecycle policy.
type Supervisor struct {
	metadata *Metadata
	options  Options
	logger   *slog.Logger
	now      func() time.Time

	ctx    context.Context
	cancel context.CancelFunc
	opMu   sync.Mutex
	mu     sync.Mutex

	state         string
	onlinePlayers int
	current       *processRun
	rcon          RconConfiguration
	console       []ConsoleLine
	consoleCursor int64
	exitCode      int
	lastError     string
	stopRequested bool
	shuttingDown  bool
	restartCancel context.CancelFunc
	restartCount  int
	lastRestart   time.Time
	cpuPercent    *float64
	memoryBytes   *int64
}

func NewSupervisor(metadata *Metadata, options Options) *Supervisor {
	if options.Logger == nil {
		options.Logger = slog.New(slog.NewTextHandler(io.Discard, nil))
	}
	if options.StopGrace <= 0 {
		options.StopGrace = 15 * time.Second
	}
	if options.ForceWait <= 0 {
		options.ForceWait = 3 * time.Second
	}
	if options.AutoRestartDelay <= 0 {
		options.AutoRestartDelay = 5 * time.Second
	}
	if options.AutoRestartReset <= 0 {
		options.AutoRestartReset = 10 * time.Minute
	}
	if options.MaxAutoRestarts <= 0 {
		options.MaxAutoRestarts = 3
	}
	if options.Now == nil {
		options.Now = time.Now
	}
	if options.Environ == nil {
		options.Environ = os.Environ()
	}
	ctx, cancel := context.WithCancel(context.Background())
	return &Supervisor{
		metadata: metadata, options: options, logger: options.Logger, now: options.Now,
		ctx: ctx, cancel: cancel, state: StateStopped, exitCode: -1,
	}
}

func (s *Supervisor) SupportsControl() bool { return true }
func (s *Supervisor) SupportsCommand() bool { return true }

func (s *Supervisor) Close() {
	s.Shutdown(context.Background(), 20*time.Second)
	s.cancel()
}

func (s *Supervisor) Start(ctx context.Context) error {
	return s.start(ctx, false)
}

func (s *Supervisor) start(ctx context.Context, fromAutoRestart bool) error {
	s.opMu.Lock()
	defer s.opMu.Unlock()
	s.mu.Lock()
	shuttingDown := s.shuttingDown
	s.mu.Unlock()
	if shuttingDown {
		return errors.New("the Agent is shutting down")
	}
	if s.IsRunning() {
		return ServerRunning()
	}
	s.cancelAutoRestart()
	s.mu.Lock()
	s.state = StateStarting
	s.onlinePlayers = 0
	s.stopRequested = false
	s.lastError = ""
	s.console = nil
	s.cpuPercent = nil
	s.memoryBytes = nil
	s.mu.Unlock()

	if err := s.cleanOrphan(); err != nil {
		s.logger.Warn("could not clean orphan process", "error", err)
	}
	if !s.options.NoEnsureRcon {
		configuration, err := ensureRcon(s.metadata.Directory)
		if err != nil {
			s.logger.Warn("could not ensure RCON configuration; falling back to stdin", "error", err)
		} else {
			s.mu.Lock()
			s.rcon = configuration
			s.mu.Unlock()
		}
	}
	var err error
	javaPath := s.options.JavaPathOverride
	if javaPath == "" {
		javaPath, err = ResolveJava(ctx, s.metadata.Config.JavaPath, s.metadata.Config.MCVersion, s.options.Environ, s.options.ExtraJavaCandidates)
		if err != nil {
			return s.failStart("Java resolution failed", err, fromAutoRestart)
		}
	}
	args, err := BuildJavaArguments(s.metadata.Directory, s.metadata.Config)
	if err != nil {
		return s.failStart("Java arguments are invalid", err, fromAutoRestart)
	}
	var command *exec.Cmd
	if s.options.Launch != nil {
		command, err = s.options.Launch(javaPath, args, s.metadata.Directory)
	} else {
		command = exec.Command(javaPath, args...)
		command.Dir = s.metadata.Directory
	}
	if err != nil {
		return s.failStart("Java process could not be created", err, fromAutoRestart)
	}
	configureProcess(command)
	stdout, err := command.StdoutPipe()
	if err != nil {
		return s.failStart("Java stdout pipe failed", err, fromAutoRestart)
	}
	stderr, err := command.StderrPipe()
	if err != nil {
		return s.failStart("Java stderr pipe failed", err, fromAutoRestart)
	}
	stdin, err := command.StdinPipe()
	if err != nil {
		return s.failStart("Java stdin pipe failed", err, fromAutoRestart)
	}
	if err := command.Start(); err != nil {
		return s.failStart("Java process could not be started", err, fromAutoRestart)
	}
	run := &processRun{
		command: command, stdin: stdin, done: make(chan struct{}), pid: command.Process.Pid,
		startedAt: s.now(), lastSample: s.now(),
	}
	s.mu.Lock()
	s.current = run
	s.state = StateStarting
	s.mu.Unlock()
	if err := os.WriteFile(filepath.Join(s.metadata.Directory, pidFileName), []byte(strconv.Itoa(run.pid)), 0o600); err != nil {
		s.logger.Warn("could not write PID file", "error", err)
	}
	s.appendConsole("Using Java: "+javaPath, false)

	var readers sync.WaitGroup
	readers.Add(2)
	go func() {
		defer readers.Done()
		s.readProcessLines(stdout)
	}()
	go func() {
		defer readers.Done()
		s.readProcessLines(stderr)
	}()
	go func() {
		readers.Wait()
		waitErr := command.Wait()
		code := -1
		if waitErr == nil {
			code = 0
		} else {
			var exitError *exec.ExitError
			if errors.As(waitErr, &exitError) {
				code = exitError.ExitCode()
			}
		}
		s.finishProcess(run, code)
	}()
	return nil
}

func (s *Supervisor) failStart(message string, cause error, fromAutoRestart bool) error {
	s.mu.Lock()
	s.state = StateStopped
	s.lastError = cause.Error()
	s.current = nil
	s.mu.Unlock()
	s.appendConsole(fmt.Sprintf("%s: %v", message, cause), false)
	if fromAutoRestart {
		s.logger.Warn("automatic restart failed", "error", cause)
	}
	return fmt.Errorf("%s: %w", message, cause)
}

func (s *Supervisor) Stop(ctx context.Context, force bool) error {
	s.opMu.Lock()
	defer s.opMu.Unlock()
	s.cancelAutoRestart()
	s.mu.Lock()
	run := s.current
	if run == nil || !activeState(s.state) {
		s.mu.Unlock()
		return nil
	}
	s.stopRequested = true
	s.state = StateStopping
	s.mu.Unlock()

	if !force && run.stdin != nil {
		run.writeMu.Lock()
		_, err := io.WriteString(run.stdin, "stop\n")
		run.writeMu.Unlock()
		if err == nil {
			if s.waitForExit(ctx, run, s.options.StopGrace) {
				return nil
			}
		}
	}
	if err := killProcessTree(run.command); err != nil {
		s.logger.Warn("could not kill Java process tree", "error", err)
	}
	s.waitForExit(ctx, run, s.options.ForceWait)
	return nil
}

func (s *Supervisor) Restart(ctx context.Context) error {
	if !s.IsRunning() {
		return ServerOffline()
	}
	s.cancelAutoRestart()
	s.mu.Lock()
	s.stopRequested = true
	s.restartCount = 0
	s.lastRestart = time.Time{}
	s.mu.Unlock()
	if err := s.Stop(ctx, false); err != nil {
		return err
	}
	return s.Start(ctx)
}

func (s *Supervisor) Kill(ctx context.Context) error {
	return s.Stop(ctx, true)
}

func (s *Supervisor) NoteIntentionalStop() {
	s.cancelAutoRestart()
	s.mu.Lock()
	s.stopRequested = true
	s.mu.Unlock()
}

func (s *Supervisor) Shutdown(ctx context.Context, timeout time.Duration) {
	s.opMu.Lock()
	s.mu.Lock()
	s.shuttingDown = true
	s.stopRequested = true
	run := s.current
	active := run != nil && activeState(s.state)
	s.mu.Unlock()
	s.opMu.Unlock()
	s.cancelAutoRestart()
	if active {
		_ = s.Stop(ctx, false)
	}
	if run != nil {
		s.waitForExit(ctx, run, timeout)
		if s.currentProcess() != nil {
			_ = s.Stop(ctx, true)
		}
	}
	s.mu.Lock()
	s.state = StateStopped
	s.current = nil
	s.mu.Unlock()
}

func (s *Supervisor) Command(ctx context.Context, command string) error {
	if strings.TrimSpace(command) == "" {
		return InvalidCommand()
	}
	s.mu.Lock()
	state := s.state
	run := s.current
	configuration := s.rcon
	s.mu.Unlock()
	if state == StateRunning && configuration.Enabled {
		if _, err := s.rconCommand(ctx, configuration, command); err == nil {
			return nil
		}
	}
	if run == nil || (state != StateRunning && state != StateStarting) {
		return ServerOffline()
	}
	if run.stdin == nil {
		return ServerOffline()
	}
	command = strings.TrimRight(command, "\r\n")
	run.writeMu.Lock()
	_, err := io.WriteString(run.stdin, command+"\n")
	run.writeMu.Unlock()
	if err != nil {
		return ServerOffline()
	}
	return nil
}

func (s *Supervisor) RCONCommand(ctx context.Context, command string) (string, error) {
	s.mu.Lock()
	state := s.state
	configuration := s.rcon
	s.mu.Unlock()
	if state != StateRunning || !configuration.Enabled {
		return "", ServerOffline()
	}
	return s.rconCommand(ctx, configuration, command)
}

func (s *Supervisor) rconCommand(ctx context.Context, configuration RconConfiguration, command string) (string, error) {
	return rcon.Execute(ctx, rcon.Config{Port: configuration.Port, Password: configuration.Password}, command)
}

func (s *Supervisor) OnlineNames(ctx context.Context) []string {
	response, err := s.RCONCommand(ctx, "list")
	if err != nil {
		return []string{}
	}
	return ParsePlayerListNames(response)
}

func ParsePlayerListNames(response string) []string {
	if !playerCountPattern.MatchString(response) {
		return nil
	}
	index := strings.IndexByte(response, ':')
	if index < 0 {
		return nil
	}
	parts := strings.Split(response[index+1:], ",")
	result := make([]string, 0, len(parts))
	for _, part := range parts {
		name := strings.TrimSpace(part)
		if name != "" {
			result = append(result, name)
		}
	}
	return result
}

func (s *Supervisor) OnlineNamesDetailed(ctx context.Context) ([]string, error) {
	response, err := s.RCONCommand(ctx, "list")
	if err != nil {
		return nil, err
	}
	if !playerCountPattern.MatchString(response) {
		return nil, errors.New("invalid player list response")
	}
	return ParsePlayerListNames(response), nil
}

func (s *Supervisor) IsRunning() bool {
	return activeState(s.State())
}

func (s *Supervisor) State() string {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.state
}

func (s *Supervisor) Snapshot() Snapshot {
	s.mu.Lock()
	run := s.current
	state := s.state
	players := s.onlinePlayers
	exitCode := s.exitCode
	s.mu.Unlock()
	result := Snapshot{State: state, OnlinePlayers: players, ExitCode: exitCode}
	if run == nil || !activeState(state) {
		return result
	}
	result.PID = run.pid
	result.StartedAt = run.startedAt
	ticks, err := processCPUTicks(run.pid)
	if err == nil {
		run.metricMu.Lock()
		now := s.now()
		elapsed := now.Sub(run.lastSample).Seconds()
		cpu := 0.0
		if elapsed > 0 {
			cpu = (ticks - run.lastCPUTicks) / elapsed / float64(runtime.NumCPU()) * 100
			cpu = clamp(cpu, 0, 100)
		}
		run.lastCPUTicks = ticks
		run.lastSample = now
		run.metricMu.Unlock()
		result.CPUPercent = &cpu
	}
	if memory, err := processMemoryBytes(run.pid); err == nil {
		result.MemoryBytes = &memory
	}
	return result
}

func (s *Supervisor) RecentConsole(after int64) ([]ConsoleLine, int64) {
	s.mu.Lock()
	defer s.mu.Unlock()
	result := make([]ConsoleLine, 0, len(s.console))
	for _, line := range s.console {
		if line.Sequence > after {
			result = append(result, line)
		}
	}
	return result, s.consoleCursor
}

func (s *Supervisor) LastError() string {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.lastError
}

func (s *Supervisor) AutoRestartEnabled() bool {
	payload, err := os.ReadFile(s.metadata.ConfigPath)
	if err != nil {
		return false
	}
	var config ServerConfig
	if json.Unmarshal(payload, &config) != nil {
		return false
	}
	return config.AutoRestart
}

func (s *Supervisor) readProcessLines(reader io.Reader) {
	buffered := bufio.NewReader(reader)
	for {
		line, err := buffered.ReadString('\n')
		if len(line) > 0 {
			line = strings.TrimSuffix(line, "\n")
			line = strings.TrimSuffix(line, "\r")
			s.handleProcessLine(line)
		}
		if err != nil {
			return
		}
	}
}

func (s *Supervisor) handleProcessLine(line string) {
	s.appendConsole(line, true)
	s.mu.Lock()
	if s.state == StateStarting && strings.Contains(line, "Done (") {
		s.state = StateRunning
	}
	if match := playerCountPattern.FindStringSubmatch(line); match != nil {
		if count, err := strconv.Atoi(match[1]); err == nil {
			s.onlinePlayers = count
		}
	}
	s.mu.Unlock()
}

func (s *Supervisor) appendConsole(line string, real bool) {
	s.mu.Lock()
	s.consoleCursor++
	entry := ConsoleLine{Sequence: s.consoleCursor, Timestamp: s.now(), Message: line}
	s.console = append(s.console, entry)
	if len(s.console) > ConsoleCapacity {
		s.console = append([]ConsoleLine(nil), s.console[len(s.console)-ConsoleCapacity:]...)
	}
	callback := s.options.OnConsole
	s.mu.Unlock()
	if real && callback != nil {
		callback(line)
	}
}

func (s *Supervisor) finishProcess(run *processRun, exitCode int) {
	s.mu.Lock()
	if s.current != run {
		s.mu.Unlock()
		close(run.done)
		return
	}
	s.current = nil
	s.exitCode = exitCode
	s.onlinePlayers = 0
	userRequested := s.stopRequested
	shuttingDown := s.shuttingDown
	s.stopRequested = false
	if userRequested || shuttingDown {
		s.state = StateStopped
	} else {
		s.state = StateCrashed
	}
	s.mu.Unlock()
	_ = os.Remove(filepath.Join(s.metadata.Directory, pidFileName))
	detail := "服务器进程已退出"
	if exitCode != 0 {
		detail = fmt.Sprintf("服务器进程已退出：exit code %d", exitCode)
	}
	s.appendConsole(fmt.Sprintf("[%s] %s", s.now().Format("15:04:05"), detail), false)
	close(run.done)
	if !userRequested && !shuttingDown {
		s.maybeAutoRestart(exitCode)
	}
}

func (s *Supervisor) maybeAutoRestart(exitCode int) {
	if !s.AutoRestartEnabled() {
		return
	}
	s.mu.Lock()
	now := s.now()
	if s.lastRestart.Before(now.Add(-s.options.AutoRestartReset)) {
		s.restartCount = 0
	}
	var message string
	if s.restartCount >= s.options.MaxAutoRestarts {
		message = fmt.Sprintf("[%s] 已达自动重启上限（%d 次 / %d 分钟），不再重启：请检查崩溃原因", now.Format("15:04:05"), s.options.MaxAutoRestarts, int(s.options.AutoRestartReset.Minutes()))
		s.mu.Unlock()
		s.appendConsole(message, false)
		return
	}
	s.restartCount++
	s.lastRestart = now
	attempt := s.restartCount
	message = fmt.Sprintf("[%s] 检测到异常退出（exit code %d），%d 秒后自动重启（第 %d/%d 次）", now.Format("15:04:05"), exitCode, int(s.options.AutoRestartDelay.Seconds()), attempt, s.options.MaxAutoRestarts)
	delayCtx, cancel := context.WithCancel(s.ctx)
	s.restartCancel = cancel
	s.mu.Unlock()
	s.appendConsole(message, false)

	go func() {
		timer := time.NewTimer(s.options.AutoRestartDelay)
		defer timer.Stop()
		select {
		case <-s.ctx.Done():
			return
		case <-delayCtx.Done():
			return
		case <-timer.C:
		}
		s.mu.Lock()
		state := s.state
		s.mu.Unlock()
		if state != StateStopped && state != StateCrashed {
			s.appendConsole("检测到手动操作，取消本次自动重启。", false)
			return
		}
		startCtx, cancel := context.WithTimeout(s.ctx, 30*time.Second)
		defer cancel()
		_ = s.start(startCtx, true)
	}()
}

func (s *Supervisor) cancelAutoRestart() {
	s.mu.Lock()
	cancel := s.restartCancel
	s.restartCancel = nil
	s.mu.Unlock()
	if cancel != nil {
		cancel()
	}
}

func (s *Supervisor) waitForExit(ctx context.Context, run *processRun, timeout time.Duration) bool {
	timer := time.NewTimer(timeout)
	defer timer.Stop()
	select {
	case <-run.done:
		return true
	case <-ctx.Done():
		return false
	case <-timer.C:
		return false
	}
}

func (s *Supervisor) currentProcess() *processRun {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.current
}

func (s *Supervisor) cleanOrphan() error {
	path := filepath.Join(s.metadata.Directory, pidFileName)
	payload, err := os.ReadFile(path)
	if err != nil {
		if os.IsNotExist(err) {
			return nil
		}
		return err
	}
	pid, err := strconv.Atoi(strings.TrimSpace(string(payload)))
	if err != nil || pid <= 0 {
		_ = os.Remove(path)
		return nil
	}
	if processExists(pid) {
		if err := killPID(pid); err != nil {
			s.logger.Warn("could not kill orphan process", "pid", pid, "error", err)
		}
	}
	if err := os.Remove(path); err != nil && !os.IsNotExist(err) {
		return err
	}
	if latestLogBusy(filepath.Join(s.metadata.Directory, "logs", "latest.log")) {
		deadline := time.Now().Add(5 * time.Second)
		for time.Now().Before(deadline) && latestLogBusy(filepath.Join(s.metadata.Directory, "logs", "latest.log")) {
			time.Sleep(100 * time.Millisecond)
		}
	}
	return nil
}

func activeState(state string) bool {
	return state == StateStarting || state == StateRunning || state == StateStopping
}

func clamp(value, minimum, maximum float64) float64 {
	if value < minimum {
		return minimum
	}
	if value > maximum {
		return maximum
	}
	return value
}

func ensureRcon(directory string) (RconConfiguration, error) {
	path := filepath.Join(directory, "server.properties")
	payload, err := os.ReadFile(path)
	if err != nil {
		return RconConfiguration{}, err
	}
	lines, values := collectPropertyLines(string(payload))
	password := strings.TrimSpace(values["rcon.password"])
	if password == "" {
		password = generateRconPassword()
	}
	port := 25575
	if parsed, err := strconv.Atoi(strings.TrimSpace(values["rcon.port"])); err == nil && parsed > 0 && parsed <= 65535 {
		port = parsed
	}
	updates := []struct{ key, value string }{{"enable-rcon", "true"}, {"rcon.port", strconv.Itoa(port)}, {"rcon.password", password}}
	changed := false
	for _, update := range updates {
		if strings.TrimSpace(values[update.key]) != update.value {
			changed = true
		}
	}
	if changed {
		if err := os.WriteFile(path, []byte(applyPropertyUpdates(lines, updates)), 0o600); err != nil {
			return RconConfiguration{}, err
		}
	}
	return RconConfiguration{Enabled: true, Port: port, Password: password}, nil
}

func collectPropertyLines(content string) ([]string, map[string]string) {
	content = strings.ReplaceAll(content, "\r\n", "\n")
	content = strings.ReplaceAll(content, "\r", "\n")
	if content == "" {
		return []string{}, make(map[string]string)
	}
	lines := strings.Split(content, "\n")
	if lines[len(lines)-1] == "" {
		lines = lines[:len(lines)-1]
	}
	values := make(map[string]string)
	for _, raw := range lines {
		trimmed := strings.TrimSpace(raw)
		index := strings.IndexByte(trimmed, '=')
		if trimmed == "" || strings.HasPrefix(trimmed, "#") || index < 0 {
			continue
		}
		values[strings.TrimSpace(trimmed[:index])] = trimmed[index+1:]
	}
	return lines, values
}

func applyPropertyUpdates(lines []string, updates []struct{ key, value string }) string {
	values := make(map[string]string, len(updates))
	for _, update := range updates {
		values[update.key] = update.value
	}
	written := make(map[string]struct{}, len(updates))
	var output strings.Builder
	for _, raw := range lines {
		trimmed := strings.TrimSpace(raw)
		index := strings.IndexByte(trimmed, '=')
		if trimmed == "" || strings.HasPrefix(trimmed, "#") || index < 0 {
			output.WriteString(raw)
			output.WriteByte('\n')
			continue
		}
		key := strings.TrimSpace(trimmed[:index])
		if _, exists := written[key]; exists {
			continue
		}
		written[key] = struct{}{}
		value := trimmed[index+1:]
		if updated, ok := values[key]; ok {
			value = updated
		}
		value = strings.NewReplacer("\r", "", "\n", "", "\x00", "").Replace(value)
		output.WriteString(key)
		output.WriteByte('=')
		output.WriteString(value)
		output.WriteByte('\n')
	}
	for _, update := range updates {
		if _, exists := written[update.key]; exists {
			continue
		}
		written[update.key] = struct{}{}
		output.WriteString(update.key)
		output.WriteByte('=')
		output.WriteString(update.value)
		output.WriteByte('\n')
	}
	return output.String()
}

func generateRconPassword() string {
	const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"
	var random [24]byte
	_, _ = rand.Read(random[:])
	var result strings.Builder
	for _, value := range random {
		result.WriteByte(alphabet[int(value)%len(alphabet)])
	}
	return result.String()
}
