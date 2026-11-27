// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package tui

import (
	"bytes"
	"context"
	"errors"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
	"time"

	tea "github.com/charmbracelet/bubbletea"
	"github.com/charmbracelet/x/ansi"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

func loadedModel(t *testing.T, width, height int) (*Model, *fakeBackend) {
	t.Helper()
	backend := newFakeBackend()
	model := New(backend, "v-test")
	model.Update(tea.WindowSizeMsg{Width: width, Height: height})
	status, _ := backend.Status(context.Background())
	entries, cursor, _ := backend.ConsoleEntries(context.Background(), -1, 500)
	online, _ := backend.OnlinePlayers(context.Background(), localActor())
	backups, _ := backend.Backups(context.Background())
	files, _ := backend.Files(context.Background(), "")
	properties, _ := backend.Properties(context.Background())
	messages := []tea.Msg{
		statusMsg{status: status, events: backend.RecentEvents(), uptime: backend.UptimeSeconds(), agent: backend.AgentInfo()},
		consoleMsg{entries: entries, cursor: cursor},
		operationsMsg{operations: backend.ActiveOperations()},
		onlinePlayersMsg{players: online},
		backupsMsg{backups: backups.Backups},
		filesMsg{path: "", response: files},
		propertiesMsg{response: properties},
	}
	for _, kind := range []string{protocol.PlayerListWhitelist, protocol.PlayerListOps, protocol.PlayerListBans} {
		response, _ := backend.PlayerList(context.Background(), kind)
		messages = append(messages, playerListMsg{kind: kind, response: response})
	}
	for _, message := range messages {
		model.Update(message)
	}
	return model, backend
}

func TestStableSnapshots(t *testing.T) {
	t.Run("80x24", func(t *testing.T) {
		model, _ := loadedModel(t, 80, 24)
		view := model.View()
		if strings.Contains(view, "bind") || strings.Contains(view, "caps") {
			t.Fatal("sidebar must be hidden below 120 columns")
		}
		assertGolden(t, "console_80x24.golden", stableSnapshot(view))
	})
	t.Run("120x40", func(t *testing.T) {
		model, _ := loadedModel(t, 120, 40)
		view := model.View()
		for _, expected := range []string{"Agent", "bind", "probe", "tokens", "caps", "127.0.0.1:17870"} {
			if !strings.Contains(ansi.Strip(view), expected) {
				t.Fatalf("120-column snapshot missing %q", expected)
			}
		}
		assertGolden(t, "sidebar_120x40.golden", stableSnapshot(view))
	})
}

func assertGolden(t *testing.T, name, actual string) {
	t.Helper()
	path := filepath.Join("testdata", name)
	if os.Getenv("UPDATE_GOLDEN") == "1" {
		if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(path, []byte(actual+"\n"), 0o600); err != nil {
			t.Fatal(err)
		}
		return
	}
	expected, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	if actual != strings.TrimSuffix(string(expected), "\n") {
		t.Fatalf("golden mismatch for %s\nactual:\n%s\nexpected:\n%s", name, actual, expected)
	}
}

func stableSnapshot(view string) string {
	clean := ansi.Strip(view)
	lines := strings.Split(clean, "\n")
	for index := range lines {
		lines[index] = strings.TrimRight(lines[index], " ")
	}
	return strings.TrimRight(strings.Join(lines, "\n"), "\n")
}

func TestLogLevelInference(t *testing.T) {
	tests := []struct {
		name    string
		level   string
		message string
		want    string
	}{
		{name: "explicit warn", level: "WARN", message: "slow tick", want: "WARN"},
		{name: "explicit chat", level: "CHAT", message: "plain message", want: "CHAT"},
		{name: "log4j warn", level: "INFO", message: "[12:30:00] [Server thread/WARN]: Can't keep up", want: "WARN"},
		{name: "log4j error", level: "INFO", message: "[12:30:00] [Server thread/ERROR]: Backup failed", want: "ERROR"},
		{name: "log4j fatal", level: "INFO", message: "[12:30:00] [Server thread/FATAL]: Stopping", want: "ERROR"},
		{name: "standalone warn", level: "INFO", message: "WARN: disk space low", want: "WARN"},
		{name: "standalone error", level: "INFO", message: "ERROR while saving world", want: "ERROR"},
		{name: "server chat", level: "INFO", message: "[12:30:00] [Server thread/INFO]: <Alice> hello", want: "CHAT"},
		{name: "async chat", level: "INFO", message: "[Async Chat Thread - Pool-1/INFO]: <Alice> hello", want: "CHAT"},
		{name: "chat severity word", level: "INFO", message: "<Alice> that error was funny", want: "CHAT"},
		{name: "ordinary info", level: "INFO", message: "[Server thread/INFO]: Started normally", want: "INFO"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			entry := protocol.ConsoleEntry{Level: test.level, Message: test.message}
			if got := logLevel(entry); got != test.want {
				t.Fatalf("logLevel(%q, %q) = %q, want %q", test.level, test.message, got, test.want)
			}
		})
	}
}

func TestTabFilterScrollAndFollow(t *testing.T) {
	model, _ := loadedModel(t, 80, 24)
	model.Update(tea.KeyMsg{Type: tea.KeyTab})
	if model.activePane() != panePlayers {
		t.Fatal("Tab did not select Players")
	}
	model.Update(tea.KeyMsg{Type: tea.KeyShiftTab})
	if model.activePane() != paneConsole {
		t.Fatal("Shift-Tab did not select Console")
	}
	model.Update(tea.KeyMsg{Type: tea.KeyRunes, Runes: []rune("/")})
	for _, character := range "joined" {
		model.Update(tea.KeyMsg{Type: tea.KeyRunes, Runes: []rune{character}})
	}
	model.Update(tea.KeyMsg{Type: tea.KeyEnter})
	if model.filter != "joined" || len(model.filteredConsole()) != 1 {
		t.Fatalf("filter not applied: %q, %d entries", model.filter, len(model.filteredConsole()))
	}
	model.Update(tea.KeyMsg{Type: tea.KeyDown})
	if model.consoleFollow || model.offsets[paneConsole] == 0 {
		t.Fatal("scrolling did not pause Console follow")
	}
	model.Update(tea.KeyMsg{Type: tea.KeySpace})
	if !model.consoleFollow || model.offsets[paneConsole] != 0 {
		t.Fatal("Space did not resume Console follow")
	}
	model.Update(tea.KeyMsg{Type: tea.KeyPgUp})
	if model.consoleFollow {
		t.Fatal("Page Up did not pause Console follow")
	}
	model.Update(tea.KeyMsg{Type: tea.KeyRunes, Runes: []rune("G")})
	if !model.consoleFollow {
		t.Fatal("G did not jump to the followed Console tail")
	}
}

func TestDangerousActionsRequireConfirmation(t *testing.T) {
	tests := []struct {
		name    string
		request func(*Model) tea.Cmd
	}{
		{name: "stop", request: func(m *Model) tea.Cmd { return m.requestControl(protocol.ControlActionStop) }},
		{name: "restart", request: func(m *Model) tea.Cmd { return m.requestControl(protocol.ControlActionRestart) }},
		{name: "kill", request: func(m *Model) tea.Cmd { return m.requestControl(protocol.ControlActionKill) }},
		{name: "restore", request: func(m *Model) tea.Cmd { return m.requestBackupAction(actionBackupRestore, "bk_1234567890abcdef") }},
		{name: "delete backup", request: func(m *Model) tea.Cmd { return m.requestBackupAction(actionBackupDelete, "bk_1234567890abcdef") }},
		{name: "delete file", request: func(m *Model) tea.Cmd { return m.requestFileDelete("readme.txt") }},
		{name: "properties patch", request: func(m *Model) tea.Cmd { return m.requestPropertiesPatch("max-players", "30") }},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			model, backend := loadedModel(t, 80, 24)
			prepare := test.request(model)
			if prepare == nil {
				t.Fatal("action did not start preparation")
			}
			prepared, _ := model.Update(prepare())
			model = prepared.(*Model)
			if model.modal == nil || model.modal.kind != modalConfirm || !model.modal.danger {
				t.Fatal("dangerous action did not open a confirmation modal")
			}
			if backend.executeCalls != 0 {
				t.Fatal("action executed before user confirmation")
			}
			model.Update(tea.KeyMsg{Type: tea.KeyEsc})
			if model.modal != nil || model.busy {
				t.Fatal("canceling confirmation did not release the action")
			}
			if backend.executeCalls != 0 {
				t.Fatal("canceling confirmation executed the action")
			}

			prepare = test.request(model)
			model.Update(prepare())
			_, execute := model.Update(tea.KeyMsg{Type: tea.KeyEnter})
			if execute == nil {
				t.Fatal("Enter did not execute the prepared confirmation")
			}
			model.Update(execute())
			if backend.executeCalls != 1 {
				t.Fatalf("ExecuteConfirmation called %d times, want 1", backend.executeCalls)
			}
			if backend.lastActor.TokenID != localTokenID || !backend.scopeAllowed {
				t.Fatal("confirmation did not use the local actor with local scope")
			}
		})
	}
}

func TestStartUsesPrepareAndExecuteWithoutDangerModal(t *testing.T) {
	model, backend := loadedModel(t, 80, 24)
	prepare := model.requestControl(protocol.ControlActionStart)
	preparedModel, command := model.Update(prepare())
	execute := preparedModel.(*Model)
	if execute.modal != nil {
		t.Fatal("start unexpectedly required a danger modal")
	}
	if command == nil {
		t.Fatal("start did not execute its prepared confirmation")
	}
	model.Update(command())
	if backend.prepareCalls != 1 || backend.executeCalls != 1 {
		t.Fatalf("start calls: prepare=%d execute=%d", backend.prepareCalls, backend.executeCalls)
	}
}

func TestQuitRequiresConfirmation(t *testing.T) {
	model, _ := loadedModel(t, 80, 24)
	model.Update(tea.KeyMsg{Type: tea.KeyRunes, Runes: []rune("q")})
	if model.modal == nil || model.modal.kind != modalQuit {
		t.Fatal("q did not open quit confirmation")
	}
	_, quit := model.Update(tea.KeyMsg{Type: tea.KeyEnter})
	if quit == nil {
		t.Fatal("Enter did not confirm quit")
	}
	if _, ok := quit().(tea.QuitMsg); !ok {
		t.Fatal("confirmed quit did not return tea.QuitMsg")
	}
}

func TestResizeAndSanitization(t *testing.T) {
	model, _ := loadedModel(t, 80, 24)
	for _, size := range []tea.WindowSizeMsg{{Width: 0, Height: 0}, {Width: 1, Height: 1}, {Width: 10, Height: 4}, {Width: 20, Height: 6}, {Width: 119, Height: 39}, {Width: 120, Height: 40}, {Width: 80, Height: 24}, {Width: 40, Height: 10}} {
		model.Update(size)
		view := model.View()
		if size.Width == 0 || size.Height == 0 {
			if view != "" {
				t.Fatalf("zero-size view was not empty: %q", view)
			}
			continue
		}
		for lineNumber, line := range strings.Split(view, "\n") {
			if width := ansi.StringWidth(line); width > size.Width {
				t.Fatalf("resize %dx%d line %d width %d", size.Width, size.Height, lineNumber, width)
			}
		}
		if strings.Contains(view, "\x1b[33m") || strings.Contains(view, "§") {
			t.Fatal("server ANSI or section-sign formatting leaked into the view")
		}
	}
	if cleaned := cleanText("\x1b[31m§c§lRed§r\x1b[0m\nnext"); cleaned != "Red next" {
		t.Fatalf("cleanText returned %q", cleaned)
	}
}

func TestActionFailureStaysInTUI(t *testing.T) {
	model, backend := loadedModel(t, 80, 24)
	backend.prepareErr = errors.New("prepare failed")
	command := model.requestControl(protocol.ControlActionStop)
	model.Update(command())
	if model.modal == nil || model.modal.kind != modalError {
		t.Fatal("prepare failure was not shown in a modal")
	}
	if model.busy {
		t.Fatal("prepare failure left the model busy")
	}
	model.Update(tea.KeyMsg{Type: tea.KeyEsc})
	if model.modal != nil {
		t.Fatal("error modal did not close")
	}
}

func TestDefaultBackupLabels(t *testing.T) {
	tests := []struct {
		name string
		run  func(*Model) tea.Cmd
	}{
		{name: "b key", run: func(model *Model) tea.Cmd {
			_, command := model.Update(tea.KeyMsg{Type: tea.KeyRunes, Runes: []rune("b")})
			return command
		}},
		{name: ":backup", run: func(model *Model) tea.Cmd { return model.executeCommand(":backup") }},
		{name: ":backup empty label", run: func(model *Model) tea.Cmd { return model.executeCommand(`:backup ""`) }},
	}
	pattern := regexp.MustCompile(`^TUI-[0-9]{8}-[0-9]{6}$`)
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			model, backend := loadedModel(t, 80, 24)
			command := test.run(model)
			if command == nil {
				t.Fatal("backup did not start")
			}
			model.Update(command())
			if backend.createCalls != 1 || !pattern.MatchString(backend.lastLabel) {
				t.Fatalf("backup calls=%d label=%q", backend.createCalls, backend.lastLabel)
			}
		})
	}
}

func TestCommandPanelOperations(t *testing.T) {
	t.Run("backup label", func(t *testing.T) {
		model, backend := loadedModel(t, 80, 24)
		command := model.executeCommand(`:backup "label with spaces"`)
		model.Update(command())
		if backend.createCalls != 1 || backend.lastLabel != "label with spaces" {
			t.Fatalf("backup calls=%d label=%q", backend.createCalls, backend.lastLabel)
		}
	})
	t.Run("properties", func(t *testing.T) {
		model, backend := loadedModel(t, 80, 24)
		command := model.executeCommand(":set max-players 30")
		model.Update(command())
		if backend.prepareCalls != 1 || backend.lastChanges["max-players"] == nil || *backend.lastChanges["max-players"] != "30" {
			t.Fatal(":set did not prepare a properties patch")
		}
	})
	t.Run("player mutation", func(t *testing.T) {
		model, backend := loadedModel(t, 80, 24)
		command := model.executeCommand(":op Alice")
		model.Update(command())
		if backend.playerCalls != 1 || backend.lastKind != protocol.PlayerListOps || backend.lastPlayer != "Alice" || backend.lastAction != "add" {
			t.Fatal(":op did not use the player service mutation")
		}
	})
	t.Run("usage error", func(t *testing.T) {
		model, _ := loadedModel(t, 80, 24)
		model.executeCommand(":set max-players")
		if model.modal == nil || model.modal.kind != modalError {
			t.Fatal("invalid command did not show usage")
		}
	})
}

func TestPlayerCommandRouting(t *testing.T) {
	tests := []struct {
		command string
		kind    string
		action  string
		name    string
		reason  string
	}{
		{command: ":op Alice", kind: protocol.PlayerListOps, action: "add", name: "Alice"},
		{command: ":deop Alice", kind: protocol.PlayerListOps, action: "remove", name: "Alice"},
		{command: ":whitelist add Alice", kind: protocol.PlayerListWhitelist, action: "add", name: "Alice"},
		{command: ":whitelist remove Alice", kind: protocol.PlayerListWhitelist, action: "remove", name: "Alice"},
		{command: `:ban Mallory "too many diamonds"`, kind: protocol.PlayerListBans, action: "add", name: "Mallory", reason: "too many diamonds"},
		{command: ":pardon Mallory", kind: protocol.PlayerListBans, action: "remove", name: "Mallory"},
	}
	for _, test := range tests {
		t.Run(test.command, func(t *testing.T) {
			model, backend := loadedModel(t, 80, 24)
			command := model.executeCommand(test.command)
			if command == nil {
				t.Fatal("player command did not start")
			}
			model.Update(command())
			if backend.playerCalls != 1 || backend.lastKind != test.kind || backend.lastAction != test.action || backend.lastPlayer != test.name || backend.lastReason != test.reason {
				t.Fatalf("player route calls=%d kind=%q action=%q name=%q reason=%q", backend.playerCalls, backend.lastKind, backend.lastAction, backend.lastPlayer, backend.lastReason)
			}
		})
	}
}

func TestDeleteFileRejectsNonRelativeTarget(t *testing.T) {
	model, backend := loadedModel(t, 80, 24)
	command := model.requestFileDelete("/readme.txt")
	if command != nil {
		model.Update(command())
	}
	if backend.prepareCalls != 0 {
		t.Fatal("absolute file target reached PrepareFileDelete")
	}
	if model.modal == nil || model.modal.kind != modalError {
		t.Fatal("invalid file target did not show an error")
	}
}

func TestFileNavigationAndDeleteRevision(t *testing.T) {
	model, backend := loadedModel(t, 80, 24)
	model.pane = paneFiles
	command := model.activateSelection()
	model.Update(command())
	if normalizeServerPath(model.files.Path) != "config" {
		t.Fatalf("directory navigation reached %q", model.files.Path)
	}
	_, deleteCommand := model.Update(tea.KeyMsg{Type: tea.KeyRunes, Runes: []rune("d")})
	if deleteCommand == nil {
		t.Fatal("delete key did not start preparation")
	}
	model.Update(deleteCommand())
	if backend.prepareCalls != 1 || backend.lastRevision != "r_fixed_file" || backend.lastTarget != "config/rules.txt" {
		t.Fatalf("file delete prepare target=%q revision=%q", backend.lastTarget, backend.lastRevision)
	}
}

func TestDirectoryDeleteUsesExactRevision(t *testing.T) {
	model, backend := loadedModel(t, 80, 24)
	model.pane = paneFiles
	command := model.requestFileDelete("config")
	model.Update(command())
	if backend.prepareCalls != 1 || backend.lastRevision != "d_fixed_directory" || backend.lastTarget != "config" {
		t.Fatalf("directory delete prepare target=%q revision=%q", backend.lastTarget, backend.lastRevision)
	}
}

func TestCrashedStateAndTerminalHelpers(t *testing.T) {
	status := protocol.NewStatusResponse()
	status.State = testPointer(protocol.ServerStateOffline)
	now := protocol.NewTimestamp(time.Date(2026, time.November, 26, 12, 30, 0, 0, time.UTC))
	events := []protocol.ServerEvent{
		{Type: protocol.EventTypeServerCrashed, OccurredAt: now},
		{Type: protocol.EventTypeServerStateChanged, OccurredAt: now, Details: map[string]string{"current": "offline"}},
	}
	if state := displayedState(status, events); state != protocolStateCrashed {
		t.Fatalf("crash state displayed as %q", state)
	}
	if state := displayedState(status, events[1:]); state != protocolStateStopped {
		t.Fatalf("intentional stop displayed as %q", state)
	}

	var reset bytes.Buffer
	if err := writeTerminalReset(&reset); err != nil {
		t.Fatal(err)
	}
	for _, sequence := range []string{"\x1b[?25h", "\x1b[?1049l", "\x1b[0m"} {
		if !strings.Contains(reset.String(), sequence) {
			t.Fatalf("terminal reset missing %q", sequence)
		}
	}
	if IsTTY(nil, nil) {
		t.Fatal("nil streams reported as a TTY")
	}
	devNull, err := os.Open(os.DevNull)
	if err != nil {
		t.Fatal(err)
	}
	defer devNull.Close()
	if IsTTY(devNull, devNull) {
		t.Fatal("null device reported as a TTY")
	}
	if err := Run(context.Background(), newFakeBackend(), "v-test"); err == nil {
		t.Fatal("Run accepted non-terminal streams")
	}
}
