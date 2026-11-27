// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package tui

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"time"
	"unicode"

	tea "github.com/charmbracelet/bubbletea"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/service"
)

type paneKind int

const (
	paneConsole paneKind = iota
	panePlayers
	paneOperations
	paneBackups
	paneFiles
	paneProperties
	paneCount
)

var paneNames = [...]string{"Console", "Players", "Operations", "Backups", "Files", "Properties"}

type inputKind int

const (
	inputNone inputKind = iota
	inputFilter
	inputCommand
)

type modalKind int

const (
	modalNone modalKind = iota
	modalHelp
	modalConfirm
	modalQuit
	modalError
	modalNotice
)

type modalState struct {
	kind           modalKind
	title          string
	body           []string
	danger         bool
	pending        *actionRequest
	confirmationID string
}

type inputState struct {
	kind         inputKind
	value        []rune
	cursor       int
	filterBefore string
	historyIndex int
}

type toastKind int

const (
	toastInfo toastKind = iota
	toastSuccess
	toastError
)

// Model is the Bubble Tea model. All backend work runs in tea.Cmd goroutines;
// Update and View only mutate or read this model.
type Model struct {
	backend      Backend
	version      string
	width        int
	height       int
	pane         paneKind
	capabilities []string
	capability   map[string]bool

	status        protocol.StatusResponse
	state         string
	uptime        *int64
	agent         service.AgentInfo
	busy          bool
	console       []protocol.ConsoleEntry
	consoleAfter  int64
	consoleCursor string
	consoleFollow bool
	filter        string
	offsets       [paneCount]int

	online      protocol.OnlinePlayersResponse
	playerLists map[string]protocol.PlayerListResponse
	playerKind  string
	operations  []protocol.OperationInfo
	backups     []protocol.BackupMetadata
	files       protocol.FileListResponse
	properties  protocol.ServerPropertiesResponse

	modal          *modalState
	input          inputState
	commandHistory []string
	toast          string
	toastKind      toastKind
	toastTTL       int
}

type statusMsg struct {
	status protocol.StatusResponse
	events []protocol.ServerEvent
	uptime *int64
	agent  service.AgentInfo
	err    error
}

type consoleMsg struct {
	entries []protocol.ConsoleEntry
	cursor  string
	err     error
}

type operationsMsg struct {
	operations []protocol.OperationInfo
}

type onlinePlayersMsg struct {
	players protocol.OnlinePlayersResponse
	err     error
}

type playerListMsg struct {
	kind     string
	response protocol.PlayerListResponse
	err      error
}

type backupsMsg struct {
	backups []protocol.BackupMetadata
	err     error
}

type filesMsg struct {
	path     string
	response protocol.FileListResponse
	err      error
}

type propertiesMsg struct {
	response protocol.ServerPropertiesResponse
	err      error
}

type tickMsg struct{}

// New creates a local TUI backed by the shared service implementation.
func New(backend Backend, version string) *Model {
	capabilities := []string{}
	capabilitySet := make(map[string]bool)
	if backend != nil {
		capabilities = append(capabilities, backend.Capabilities()...)
		for _, capability := range capabilities {
			capabilitySet[capability] = true
		}
	}
	return &Model{
		backend:       backend,
		version:       cleanText(version),
		width:         80,
		height:        24,
		capabilities:  capabilities,
		capability:    capabilitySet,
		state:         protocolStateStopped,
		consoleAfter:  -1,
		consoleFollow: true,
		playerLists:   make(map[string]protocol.PlayerListResponse),
		playerKind:    protocol.PlayerListWhitelist,
	}
}

func (m *Model) Init() tea.Cmd {
	commands := []tea.Cmd{
		m.statusCmd(),
		m.consoleCmd(),
		m.operationsCmd(),
		tickCmd(),
	}
	if m.hasCapability(protocol.CapabilityPlayers) {
		commands = append(commands,
			m.onlinePlayersCmd(),
			m.playerListCmd(protocol.PlayerListWhitelist),
			m.playerListCmd(protocol.PlayerListOps),
			m.playerListCmd(protocol.PlayerListBans),
		)
	}
	if m.hasCapability(protocol.CapabilityBackup) {
		commands = append(commands, m.backupsCmd())
	}
	if m.hasCapability(protocol.CapabilityFiles) {
		commands = append(commands, m.filesCmd(""), m.propertiesCmd())
	}
	return tea.Batch(commands...)
}

func (m *Model) Update(message tea.Msg) (tea.Model, tea.Cmd) {
	switch typed := message.(type) {
	case tea.WindowSizeMsg:
		m.width = max(0, typed.Width)
		m.height = max(0, typed.Height)
		m.clampOffsets()
		return m, nil
	case tickMsg:
		if m.toastTTL > 0 {
			m.toastTTL--
			if m.toastTTL == 0 {
				m.toast = ""
			}
		}
		return m, tea.Batch(tickCmd(), m.refreshCmd())
	case statusMsg:
		if typed.err != nil {
			m.showFailure("Status refresh failed", typed.err, false)
			return m, nil
		}
		m.status = typed.status
		m.state = displayedState(typed.status, typed.events)
		m.uptime = typed.uptime
		m.agent = typed.agent
		return m, nil
	case consoleMsg:
		if typed.err != nil {
			if !isContextError(typed.err) {
				m.showFailure("Console refresh failed", typed.err, false)
			}
			return m, nil
		}
		m.appendConsole(typed.entries)
		m.consoleCursor = typed.cursor
		return m, nil
	case operationsMsg:
		m.operations = append([]protocol.OperationInfo(nil), typed.operations...)
		m.clampOffsets()
		return m, nil
	case onlinePlayersMsg:
		if typed.err != nil {
			if !isContextError(typed.err) {
				m.showFailure("Players refresh failed", typed.err, false)
			}
			return m, nil
		}
		m.online = typed.players
		return m, nil
	case playerListMsg:
		if typed.err != nil {
			if !isContextError(typed.err) {
				m.showFailure("Player list refresh failed", typed.err, false)
			}
			return m, nil
		}
		m.playerLists[typed.kind] = typed.response
		if m.playerKind == typed.kind {
			m.clampOffsets()
		}
		return m, nil
	case backupsMsg:
		if typed.err != nil {
			if !isContextError(typed.err) {
				m.showFailure("Backup refresh failed", typed.err, false)
			}
			return m, nil
		}
		m.backups = append([]protocol.BackupMetadata(nil), typed.backups...)
		m.clampOffsets()
		return m, nil
	case filesMsg:
		if typed.err != nil {
			if !isContextError(typed.err) {
				m.showFailure("File refresh failed", typed.err, false)
			}
			return m, nil
		}
		if normalizeServerPath(typed.path) == normalizeServerPath(m.files.Path) || m.files.Path == "" {
			m.files = typed.response
			m.offsets[paneFiles] = 0
			m.clampOffsets()
		}
		return m, nil
	case propertiesMsg:
		if typed.err != nil {
			if !isContextError(typed.err) {
				m.showFailure("Properties refresh failed", typed.err, false)
			}
			return m, nil
		}
		m.properties = typed.response
		m.clampOffsets()
		return m, nil
	case preparedMsg:
		return m.updatePrepared(typed)
	case executionMsg:
		return m.updateExecution(typed)
	case backupCreatedMsg:
		return m.updateBackupCreated(typed)
	case playerChangedMsg:
		return m.updatePlayerChanged(typed)
	case tea.KeyMsg:
		return m.handleKey(typed)
	}
	return m, nil
}

func (m *Model) View() string {
	width := max(0, m.width)
	height := max(0, m.height)
	if width == 0 || height == 0 {
		return ""
	}
	if width < 20 || height < 6 {
		return fitBlock(m.renderStatus(width)+"\n"+m.renderHint(width), width, height)
	}

	toastHeight := 0
	toast := ""
	if m.toast != "" {
		toastHeight = 1
		toast = m.renderToast(width)
	}
	contentHeight := height - 2 - toastHeight
	if contentHeight < 1 {
		contentHeight = 1
	}

	var content string
	if m.input.kind == inputCommand {
		content = m.renderCommandPalette(width, contentHeight)
	} else if m.modal != nil {
		content = m.renderModal(width, contentHeight)
	} else if width >= 120 {
		sidebarWidth := 30
		mainWidth := width - sidebarWidth - 1
		main := m.renderMainPane(mainWidth, contentHeight)
		sidebar := m.renderSidebar(sidebarWidth, contentHeight)
		content = lipglossJoinHorizontal(main, sidebar)
	} else {
		content = m.renderMainPane(width, contentHeight)
	}

	lines := []string{m.renderStatus(width), content}
	if toastHeight > 0 {
		lines = append(lines, toast)
	}
	lines = append(lines, m.renderHint(width))
	return fitBlock(strings.Join(lines, "\n"), width, height)
}

func (m *Model) handleKey(message tea.KeyMsg) (tea.Model, tea.Cmd) {
	key := message.String()
	if key == "ctrl+c" {
		return m, tea.Quit
	}
	if m.input.kind != inputNone {
		return m, m.handleInputKey(message)
	}
	if m.modal != nil {
		return m, m.handleModalKey(message)
	}
	return m, m.handleGlobalKey(message)
}

func (m *Model) handleGlobalKey(message tea.KeyMsg) tea.Cmd {
	key := message.String()
	switch key {
	case "q":
		m.openQuit()
		return nil
	case "?":
		m.openHelp()
		return nil
	case "tab":
		return m.switchPane((m.activePane() + 1) % paneCount)
	case "shift+tab":
		return m.switchPane((m.activePane() + paneCount - 1) % paneCount)
	case "/":
		if m.activePane() != paneConsole {
			m.setToast("Console filter is available in the Console pane.", toastInfo)
			return nil
		}
		m.openFilter()
		return nil
	case ":":
		m.openCommand()
		return nil
	case "s":
		return m.requestControl(protocol.ControlActionStart)
	case "x":
		return m.requestControl(protocol.ControlActionStop)
	case "r":
		return m.requestControl(protocol.ControlActionRestart)
	case "b":
		return m.requestBackupCreate("")
	case "p":
		return m.switchPane(panePlayers)
	case "f":
		return m.switchPane(paneFiles)
	case "c":
		return m.switchPane(paneProperties)
	case " ":
		if m.activePane() == paneConsole {
			m.consoleFollow = !m.consoleFollow
			if m.consoleFollow {
				m.offsets[paneConsole] = 0
			}
		}
		return nil
	case "j", "down":
		m.scrollActive(1, false)
	case "k", "up":
		m.scrollActive(-1, false)
	case "g", "home":
		m.scrollActive(-1, true)
	case "G", "end":
		m.scrollActive(1, true)
	case "pgdown":
		m.scrollActive(max(1, m.height-7), false)
	case "pgup":
		m.scrollActive(-max(1, m.height-7), false)
	case "left":
		if m.activePane() == panePlayers {
			m.cyclePlayerKind(-1)
			return m.playerListCmd(m.playerKind)
		}
		if m.activePane() == paneFiles {
			return m.navigateFiles(parentServerPath(m.files.Path))
		}
	case "right":
		if m.activePane() == panePlayers {
			m.cyclePlayerKind(1)
			return m.playerListCmd(m.playerKind)
		}
	case "enter":
		return m.activateSelection()
	case "d":
		if m.activePane() == paneFiles {
			return m.requestFileDelete(m.selectedFilePath())
		}
		if m.activePane() == paneBackups {
			return m.requestBackupAction(actionBackupDelete, m.selectedBackupID())
		}
	}
	if runes := message.Runes; len(runes) == 1 {
		switch strings.ToLower(string(runes)) {
		case "s":
			return m.requestControl(protocol.ControlActionStart)
		case "x":
			return m.requestControl(protocol.ControlActionStop)
		case "r":
			return m.requestControl(protocol.ControlActionRestart)
		case "b":
			return m.requestBackupCreate("")
		case "p":
			return m.switchPane(panePlayers)
		case "f":
			return m.switchPane(paneFiles)
		case "c":
			return m.switchPane(paneProperties)
		case "q":
			m.openQuit()
		case "d":
			if m.activePane() == paneFiles {
				return m.requestFileDelete(m.selectedFilePath())
			}
			if m.activePane() == paneBackups {
				return m.requestBackupAction(actionBackupDelete, m.selectedBackupID())
			}
		}
	}
	return nil
}

func (m *Model) handleModalKey(message tea.KeyMsg) tea.Cmd {
	if m.modal == nil {
		return nil
	}
	key := message.String()
	switch m.modal.kind {
	case modalConfirm:
		switch key {
		case "y", "Y", "enter":
			return m.executePending()
		case "n", "N", "esc":
			m.modal = nil
			m.busy = false
		}
	case modalQuit:
		switch key {
		case "y", "Y", "enter":
			return tea.Quit
		case "n", "N", "esc":
			m.modal = nil
		}
	default:
		if key == "esc" || key == "enter" || key == "?" || key == "q" {
			m.modal = nil
		}
	}
	return nil
}

func (m *Model) handleInputKey(message tea.KeyMsg) tea.Cmd {
	key := message.String()
	switch key {
	case "esc":
		if m.input.kind == inputFilter {
			m.filter = m.input.filterBefore
			m.offsets[paneConsole] = 0
		}
		m.input = inputState{}
		return nil
	case "enter":
		value := string(m.input.value)
		kind := m.input.kind
		m.input = inputState{}
		if kind == inputFilter {
			m.filter = cleanText(value)
			m.offsets[paneConsole] = 0
			return nil
		}
		if strings.TrimSpace(value) != "" && value != ":" {
			m.commandHistory = append(m.commandHistory, value)
			if len(m.commandHistory) > 50 {
				m.commandHistory = m.commandHistory[len(m.commandHistory)-50:]
			}
		}
		return m.executeCommand(value)
	case "left":
		m.input.cursor = max(0, m.input.cursor-1)
	case "right":
		m.input.cursor = min(len(m.input.value), m.input.cursor+1)
	case "home", "ctrl+a":
		m.input.cursor = 0
	case "end", "ctrl+e":
		m.input.cursor = len(m.input.value)
	case "backspace":
		if m.input.cursor > 0 {
			m.input.value = append(m.input.value[:m.input.cursor-1], m.input.value[m.input.cursor:]...)
			m.input.cursor--
		}
	case "delete":
		if m.input.cursor < len(m.input.value) {
			m.input.value = append(m.input.value[:m.input.cursor], m.input.value[m.input.cursor+1:]...)
		}
	case "ctrl+u":
		m.input.value = nil
		m.input.cursor = 0
	case "ctrl+w":
		m.deleteInputWord()
	case "up":
		if m.input.kind == inputCommand {
			m.recallCommand(-1)
		}
	case "down":
		if m.input.kind == inputCommand {
			m.recallCommand(1)
		}
	default:
		for _, current := range message.Runes {
			if !unicodeControl(current) {
				updated := make([]rune, 0, len(m.input.value)+1)
				updated = append(updated, m.input.value[:m.input.cursor]...)
				updated = append(updated, current)
				updated = append(updated, m.input.value[m.input.cursor:]...)
				m.input.value = updated
				m.input.cursor++
			}
		}
	}
	return nil
}

func unicodeControl(value rune) bool {
	return unicode.IsControl(value)
}

func (m *Model) deleteInputWord() {
	cursor := m.input.cursor
	for cursor > 0 && m.input.value[cursor-1] == ' ' {
		cursor--
	}
	for cursor > 0 && m.input.value[cursor-1] != ' ' {
		cursor--
	}
	m.input.value = append(m.input.value[:cursor], m.input.value[m.input.cursor:]...)
	m.input.cursor = cursor
}

func (m *Model) recallCommand(direction int) {
	history := m.commandHistory
	if len(history) == 0 {
		return
	}
	if m.input.historyIndex < 0 || m.input.historyIndex > len(history) {
		m.input.historyIndex = len(history)
	}
	m.input.historyIndex = max(0, min(len(history), m.input.historyIndex+direction))
	if m.input.historyIndex == len(history) {
		m.input.value = []rune(":")
	} else {
		m.input.value = []rune(history[m.input.historyIndex])
	}
	m.input.cursor = len(m.input.value)
}

func (m *Model) openFilter() {
	value := []rune(m.filter)
	m.input = inputState{kind: inputFilter, value: value, cursor: len(value), filterBefore: m.filter}
}

func (m *Model) openCommand() {
	m.input = inputState{
		kind: inputCommand, value: []rune(":"), cursor: 1,
		historyIndex: len(m.commandHistory),
	}
}

func (m *Model) openHelp() {
	m.modal = &modalState{kind: modalHelp, title: "Help", body: helpLines}
}

func (m *Model) openQuit() {
	m.modal = &modalState{
		kind: modalQuit, title: "Quit",
		body: []string{"Quit the local TUI?", "The Agent and Minecraft server keep running.", "", "Press y or Enter to quit. Esc cancels."},
	}
}

func (m *Model) showFailure(title string, err error, modal bool) {
	if err == nil || isContextError(err) {
		return
	}
	message := errorText(err)
	if modal {
		m.modal = &modalState{kind: modalError, title: title, body: []string{message, "", "Press Enter or Esc to close."}}
		return
	}
	m.setToast(title+": "+message, toastError)
}

func (m *Model) setToast(message string, kind toastKind) {
	m.toast = cleanText(message)
	m.toastKind = kind
	if m.toast == "" {
		m.toastTTL = 0
		return
	}
	m.toastTTL = 5
}

func errorText(err error) string {
	if coded, ok := err.(interface{ ErrorCode() string }); ok && coded.ErrorCode() != "" {
		return cleanText(coded.ErrorCode()) + ": " + cleanText(err.Error())
	}
	return cleanText(err.Error())
}

func isContextError(err error) bool {
	return err != nil && (errors.Is(err, context.Canceled) || errors.Is(err, context.DeadlineExceeded))
}

func (m *Model) activePane() paneKind {
	if m.input.kind == inputFilter {
		return paneConsole
	}
	// The active pane is kept separately from focus-like input state.
	return m.pane
}

func (m *Model) switchPane(target paneKind) tea.Cmd {
	if target < paneConsole || target >= paneCount {
		target = paneConsole
	}
	m.pane = target
	m.modal = nil
	m.clampOffsets()
	switch target {
	case panePlayers:
		if m.hasCapability(protocol.CapabilityPlayers) {
			commands := []tea.Cmd{m.onlinePlayersCmd()}
			if m.playerKind != "online" {
				commands = append(commands, m.playerListCmd(m.playerKind))
			}
			return tea.Batch(commands...)
		}
	case paneOperations:
		return m.operationsCmd()
	case paneBackups:
		if m.hasCapability(protocol.CapabilityBackup) {
			return m.backupsCmd()
		}
	case paneFiles:
		if m.hasCapability(protocol.CapabilityFiles) {
			return m.filesCmd(m.files.Path)
		}
	case paneProperties:
		if m.hasCapability(protocol.CapabilityFiles) || m.hasCapability(protocol.CapabilityProperties) {
			return m.propertiesCmd()
		}
	}
	return nil
}

func (m *Model) cyclePlayerKind(direction int) {
	kinds := []string{protocol.PlayerListWhitelist, protocol.PlayerListOps, protocol.PlayerListBans, "online"}
	index := 0
	for current, kind := range kinds {
		if kind == m.playerKind {
			index = current
			break
		}
	}
	index = (index + direction + len(kinds)) % len(kinds)
	m.playerKind = kinds[index]
	m.offsets[panePlayers] = 0
}

func (m *Model) scrollActive(delta int, edge bool) {
	pane := m.activePane()
	if pane == paneConsole {
		if edge {
			if delta < 0 {
				m.offsets[pane] = int(^uint(0) >> 1)
			} else {
				m.offsets[pane] = 0
				m.consoleFollow = true
			}
			return
		}
		m.consoleFollow = false
		m.offsets[pane] = max(0, m.offsets[pane]+delta)
		return
	}
	count := m.activeItemCount()
	maximum := max(0, count-1)
	if edge {
		if delta < 0 {
			m.offsets[pane] = 0
		} else {
			m.offsets[pane] = maximum
		}
		return
	}
	m.offsets[pane] = max(0, min(maximum, m.offsets[pane]+delta))
}

func (m *Model) activeItemCount() int {
	return m.itemCountForPane(m.activePane())
}

func (m *Model) itemCountForPane(pane paneKind) int {
	switch pane {
	case panePlayers:
		return m.playerItemCount()
	case paneOperations:
		return len(m.operations)
	case paneBackups:
		return len(m.backups)
	case paneFiles:
		return len(m.files.Entries)
	case paneProperties:
		return len(m.properties.Properties)
	default:
		return len(m.console)
	}
}

func (m *Model) clampOffsets() {
	for pane := paneConsole + 1; pane < paneCount; pane++ {
		maximum := max(0, m.itemCountForPane(pane)-1)
		m.offsets[pane] = max(0, min(maximum, m.offsets[pane]))
	}
}

func (m *Model) appendConsole(entries []protocol.ConsoleEntry) {
	seen := make(map[int64]struct{}, len(entries))
	for _, entry := range entries {
		if entry.Sequence <= m.consoleAfter {
			continue
		}
		if _, exists := seen[entry.Sequence]; exists {
			continue
		}
		seen[entry.Sequence] = struct{}{}
		m.console = append(m.console, entry)
		if entry.Sequence > m.consoleAfter {
			m.consoleAfter = entry.Sequence
		}
	}
	if len(m.console) > 2000 {
		m.console = append([]protocol.ConsoleEntry(nil), m.console[len(m.console)-2000:]...)
	}
	if !m.consoleFollow {
		m.offsets[paneConsole] += len(entries)
	}
	if m.consoleFollow {
		m.offsets[paneConsole] = 0
	}
}

func (m *Model) playerItemCount() int {
	if m.playerKind == "online" {
		return len(m.online.Players)
	}
	return len(m.playerLists[m.playerKind].Entries)
}

func (m *Model) selectedFilePath() string {
	index := m.offsets[paneFiles]
	if index < 0 || index >= len(m.files.Entries) {
		return ""
	}
	return m.files.Entries[index].Path
}

func (m *Model) selectedBackupID() string {
	index := m.offsets[paneBackups]
	if index < 0 || index >= len(m.backups) {
		return ""
	}
	return m.backups[index].ID
}

func (m *Model) activateSelection() tea.Cmd {
	switch m.activePane() {
	case paneFiles:
		entryPath := m.selectedFilePath()
		if entryPath == "" {
			return nil
		}
		index := m.offsets[paneFiles]
		if index >= 0 && index < len(m.files.Entries) && m.files.Entries[index].IsDirectory {
			return m.navigateFiles(entryPath)
		}
		m.modal = &modalState{kind: modalNotice, title: "File", body: []string{entryPath, "This TUI browses and deletes files; content editing is available through the Agent API."}}
	case paneBackups:
		return m.requestBackupAction(actionBackupRestore, m.selectedBackupID())
	}
	return nil
}

func (m *Model) navigateFiles(target string) tea.Cmd {
	if !m.hasCapability(protocol.CapabilityFiles) {
		m.setToast("File capability is not available.", toastError)
		return nil
	}
	m.offsets[paneFiles] = 0
	m.files.Path = target
	return m.filesCmd(target)
}

func (m *Model) hasCapability(capability string) bool {
	return m.capability[capability]
}

func (m *Model) refreshCmd() tea.Cmd {
	commands := []tea.Cmd{m.statusCmd(), m.consoleCmd(), m.operationsCmd()}
	if m.hasCapability(protocol.CapabilityPlayers) {
		commands = append(commands, m.onlinePlayersCmd())
		if m.playerKind != "online" {
			commands = append(commands, m.playerListCmd(m.playerKind))
		}
	}
	if m.hasCapability(protocol.CapabilityBackup) {
		commands = append(commands, m.backupsCmd())
	}
	if m.hasCapability(protocol.CapabilityFiles) {
		commands = append(commands, m.filesCmd(m.files.Path))
	}
	if m.hasCapability(protocol.CapabilityFiles) || m.hasCapability(protocol.CapabilityProperties) {
		commands = append(commands, m.propertiesCmd())
	}
	return tea.Batch(commands...)
}

func (m *Model) statusCmd() tea.Cmd {
	backend := m.backend
	return func() tea.Msg {
		if backend == nil {
			return statusMsg{err: fmt.Errorf("backend is not configured")}
		}
		status, err := backend.Status(context.Background())
		return statusMsg{status: status, events: backend.RecentEvents(), uptime: backend.UptimeSeconds(), agent: backend.AgentInfo(), err: err}
	}
}

func (m *Model) consoleCmd() tea.Cmd {
	backend := m.backend
	after := m.consoleAfter
	return func() tea.Msg {
		if backend == nil {
			return consoleMsg{err: fmt.Errorf("backend is not configured")}
		}
		entries, cursor, err := backend.ConsoleEntries(context.Background(), after, 500)
		return consoleMsg{entries: entries, cursor: cursor, err: err}
	}
}

func (m *Model) operationsCmd() tea.Cmd {
	backend := m.backend
	return func() tea.Msg {
		if backend == nil {
			return operationsMsg{}
		}
		return operationsMsg{operations: backend.ActiveOperations()}
	}
}

func (m *Model) onlinePlayersCmd() tea.Cmd {
	backend := m.backend
	return func() tea.Msg {
		if backend == nil {
			return onlinePlayersMsg{err: fmt.Errorf("backend is not configured")}
		}
		players, err := backend.OnlinePlayers(context.Background(), localActor())
		return onlinePlayersMsg{players: players, err: err}
	}
}

func (m *Model) playerListCmd(kind string) tea.Cmd {
	backend := m.backend
	return func() tea.Msg {
		if backend == nil {
			return playerListMsg{kind: kind, err: fmt.Errorf("backend is not configured")}
		}
		response, err := backend.PlayerList(context.Background(), kind)
		return playerListMsg{kind: kind, response: response, err: err}
	}
}

func (m *Model) backupsCmd() tea.Cmd {
	backend := m.backend
	return func() tea.Msg {
		if backend == nil {
			return backupsMsg{err: fmt.Errorf("backend is not configured")}
		}
		response, err := backend.Backups(context.Background())
		return backupsMsg{backups: response.Backups, err: err}
	}
}

func (m *Model) filesCmd(target string) tea.Cmd {
	backend := m.backend
	return func() tea.Msg {
		if backend == nil {
			return filesMsg{path: target, err: fmt.Errorf("backend is not configured")}
		}
		response, err := backend.Files(context.Background(), target)
		return filesMsg{path: target, response: response, err: err}
	}
}

func (m *Model) propertiesCmd() tea.Cmd {
	backend := m.backend
	return func() tea.Msg {
		if backend == nil {
			return propertiesMsg{err: fmt.Errorf("backend is not configured")}
		}
		response, err := backend.Properties(context.Background())
		return propertiesMsg{response: response, err: err}
	}
}

func tickCmd() tea.Cmd {
	return tea.Tick(2*time.Second, func(time.Time) tea.Msg { return tickMsg{} })
}

func displayedState(status protocol.StatusResponse, events []protocol.ServerEvent) string {
	state := protocolStateStopped
	if status.State != nil {
		state = cleanText(*status.State)
	}
	if state == "offline" {
		state = protocolStateStopped
	}
	if state != protocolStateStopped {
		return state
	}
	crash := false
	for index := len(events) - 1; index >= 0; index-- {
		event := events[index]
		switch event.Type {
		case protocol.EventTypeServerCrashed:
			crash = true
		case protocol.EventTypeServerAutoRestarted:
			return state
		case protocol.EventTypeServerStateChanged:
			current := cleanText(event.Details["current"])
			if current == protocolStateRunning || current == protocolStateStarting || current == protocolStateStopping {
				return state
			}
			if current == protocolStateStopped || current == "offline" {
				continue
			}
		}
	}
	if crash {
		return protocolStateCrashed
	}
	return state
}

func normalizeServerPath(value string) string {
	value = strings.TrimSpace(value)
	value = strings.Trim(value, "/")
	if value == "." {
		return ""
	}
	return value
}

func parentServerPath(value string) string {
	value = normalizeServerPath(value)
	if value == "" {
		return ""
	}
	index := strings.LastIndexByte(value, '/')
	if index < 0 {
		return ""
	}
	return value[:index]
}

var helpLines = []string{
	"Tab / Shift-Tab   switch pane",
	"j / k, g / G      scroll; PgUp / PgDn page",
	"Space             toggle Console follow / pause",
	"/                 filter Console text",
	":                 open command panel",
	"s / x / r / b     start, stop, restart, backup",
	"p / f / c         Players, Files, Properties",
	"Enter / d         open selection / delete selection",
	"y / Enter         confirm a prepared dangerous action",
	"Esc               close modal or cancel input",
	"q                 quit; the server keeps running",
	"",
	":start :stop :restart :kill :backup [label]",
	":restore <id> :delete-backup <id> :delete-file <path>",
	":set <key> <value> :op <name> :deop <name>",
	":whitelist add|remove <name> :ban <name> [reason]",
	":pardon <name> :help :quit",
}
