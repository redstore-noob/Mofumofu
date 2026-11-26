// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package tui

import (
	"context"
	"fmt"
	"strings"
	"time"

	tea "github.com/charmbracelet/bubbletea"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

type actionKind int

const (
	actionControl actionKind = iota
	actionBackupRestore
	actionBackupDelete
	actionFileDelete
	actionPropertiesPatch
)

type actionRequest struct {
	kind   actionKind
	target string
	detail string
	danger bool
}

type preparedMsg struct {
	request      actionRequest
	confirmation protocol.ConfirmationPrepareResponse
	err          error
}

type executionMsg struct {
	request   actionRequest
	operation protocol.OperationInfo
	err       error
}

type backupCreatedMsg struct {
	response protocol.BackupCreateResponse
	err      error
}

type playerChangedMsg struct {
	kind   string
	action string
	name   string
	err    error
}

func (m *Model) actionAllowed() bool {
	if m.busy {
		m.setToast("Another operation is being prepared. Please wait.", toastInfo)
		return false
	}
	if m.backend == nil {
		m.showFailure("Action failed", fmt.Errorf("backend is not configured"), true)
		return false
	}
	return true
}

func (m *Model) requestControl(action string) tea.Cmd {
	if !m.actionAllowed() {
		return nil
	}
	request := actionRequest{
		kind: actionControl, target: action,
		detail: "Server control: " + action,
		danger: action == protocol.ControlActionStop || action == protocol.ControlActionRestart || action == protocol.ControlActionKill,
	}
	m.busy = true
	return m.prepareActionCmd(request)
}

func (m *Model) requestBackupAction(kind actionKind, backupID string) tea.Cmd {
	backupID = cleanText(backupID)
	if backupID == "" {
		m.showFailure("Backup action failed", fmt.Errorf("select a backup first"), true)
		return nil
	}
	if !m.actionAllowed() {
		return nil
	}
	label := "Restore"
	if kind == actionBackupDelete {
		label = "Delete"
	}
	request := actionRequest{kind: kind, target: backupID, detail: label + " backup " + backupID, danger: true}
	m.busy = true
	return m.prepareActionCmd(request)
}

func (m *Model) requestFileDelete(filePath string) tea.Cmd {
	filePath = strings.TrimSpace(filePath)
	if filePath == "" {
		m.showFailure("File delete failed", fmt.Errorf("select a file or directory first"), true)
		return nil
	}
	if strings.HasPrefix(filePath, "/") || strings.Contains(filePath, `\`) || strings.HasPrefix(filePath, "../") || filePath == ".." {
		m.showFailure("File delete failed", fmt.Errorf("file path must be relative and stay inside the instance"), true)
		return nil
	}
	if !m.actionAllowed() {
		return nil
	}
	request := actionRequest{kind: actionFileDelete, target: filePath, detail: "Delete " + filePath, danger: true}
	m.busy = true
	return m.prepareActionCmd(request)
}

func (m *Model) requestPropertiesPatch(key, value string) tea.Cmd {
	key = cleanText(key)
	if key == "" {
		m.showFailure("Properties patch failed", fmt.Errorf("a property key is required"), true)
		return nil
	}
	if !m.actionAllowed() {
		return nil
	}
	request := actionRequest{kind: actionPropertiesPatch, target: key, detail: "Patch server.properties key " + key + "; restart required", danger: true}
	m.busy = true
	return m.preparePropertiesCmd(request, value)
}

func (m *Model) requestBackupCreate(label string) tea.Cmd {
	if !m.actionAllowed() {
		return nil
	}
	label = strings.TrimSpace(label)
	if label == "" {
		label = defaultBackupLabel()
	}
	backend := m.backend
	m.busy = true
	return func() tea.Msg {
		response, err := backend.CreateBackup(context.Background(), localActor(), label, nil)
		return backupCreatedMsg{response: response, err: err}
	}
}

func defaultBackupLabel() string {
	return "TUI-" + time.Now().Format("20060102-150405")
}

func (m *Model) requestPlayerMutation(kind, action, name, reason string) tea.Cmd {
	if !m.actionAllowed() {
		return nil
	}
	name = cleanText(name)
	if name == "" {
		m.showFailure("Player mutation failed", fmt.Errorf("a player name is required"), true)
		return nil
	}
	backend := m.backend
	m.busy = true
	return func() tea.Msg {
		list, err := backend.PlayerList(context.Background(), kind)
		if err != nil {
			return playerChangedMsg{kind: kind, action: action, name: name, err: err}
		}
		revision := list.Revision
		if action == "add" {
			request := protocol.PlayerAddRequest{PlayerName: name, ExpectedRevision: &revision}
			if kind == protocol.PlayerListOps {
				level := 4
				request.OpLevel = &level
			}
			if kind == protocol.PlayerListBans && reason != "" {
				request.Reason = &reason
			}
			_, err = backend.AddPlayer(context.Background(), localActor(), kind, request)
		} else {
			request := protocol.PlayerRemoveRequest{PlayerName: name, ExpectedRevision: &revision}
			_, err = backend.RemovePlayer(context.Background(), localActor(), kind, request)
		}
		return playerChangedMsg{kind: kind, action: action, name: name, err: err}
	}
}

func (m *Model) prepareActionCmd(request actionRequest) tea.Cmd {
	backend := m.backend
	return func() tea.Msg {
		var response protocol.ConfirmationPrepareResponse
		var err error
		switch request.kind {
		case actionControl:
			response, err = backend.PrepareControl(context.Background(), localActor(), request.target)
		case actionBackupRestore, actionBackupDelete:
			response, err = backend.PrepareBackup(context.Background(), localActor(), request.target, request.kind == actionBackupRestore)
		case actionFileDelete:
			response, err = prepareFileDelete(context.Background(), backend, request.target)
		default:
			err = fmt.Errorf("unsupported confirmation action")
		}
		return preparedMsg{request: request, confirmation: response, err: err}
	}
}

func prepareFileDelete(ctx context.Context, backend Backend, filePath string) (protocol.ConfirmationPrepareResponse, error) {
	item, err := backend.GetFile(ctx, filePath)
	if err != nil {
		return protocol.ConfirmationPrepareResponse{}, err
	}
	return backend.PrepareFileDelete(ctx, localActor(), item.Path, item.Revision)
}

func (m *Model) preparePropertiesCmd(request actionRequest, value string) tea.Cmd {
	backend := m.backend
	return func() tea.Msg {
		properties, err := backend.Properties(context.Background())
		if err != nil {
			return preparedMsg{request: request, err: err}
		}
		response, err := backend.PreparePropertiesPatch(context.Background(), localActor(), properties.Revision, map[string]*string{request.target: &value})
		return preparedMsg{request: request, confirmation: response, err: err}
	}
}

func (m *Model) updatePrepared(message preparedMsg) (tea.Model, tea.Cmd) {
	if message.err != nil {
		m.busy = false
		m.showFailure("Preparation failed", message.err, true)
		return m, nil
	}
	if cleanText(message.confirmation.ConfirmationID) == "" {
		m.busy = false
		m.showFailure("Preparation failed", fmt.Errorf("backend returned an empty confirmation ID"), true)
		return m, nil
	}
	// Start is prepared and executed through the same confirmation service, but
	// is not destructive and therefore does not require a second user gesture.
	if message.request.kind == actionControl && message.request.target == protocol.ControlActionStart {
		return m.executePrepared(message.request, message.confirmation)
	}
	request := message.request
	m.modal = &modalState{
		kind: modalConfirm, title: "Confirm dangerous action", danger: true, pending: &request,
		confirmationID: message.confirmation.ConfirmationID,
		body:           confirmationLines(message.confirmation, request),
	}
	return m, nil
}

func confirmationLines(confirmation protocol.ConfirmationPrepareResponse, request actionRequest) []string {
	summary := cleanText(confirmation.Summary)
	if summary == "" {
		summary = cleanText(request.detail)
	}
	lines := []string{summary}
	if request.target != "" && !strings.Contains(summary, request.target) {
		lines = append(lines, "Target: "+cleanText(request.target))
	}
	if request.danger {
		lines = append(lines, "", "This action may be irreversible. Review it carefully.")
	}
	lines = append(lines, "", "Press y or Enter to execute. Esc cancels.")
	return lines
}

func (m *Model) executePending() tea.Cmd {
	if m.modal == nil || m.modal.pending == nil {
		return nil
	}
	request := *m.modal.pending
	confirmationID := m.modal.confirmationID
	m.modal = nil
	m.busy = true
	backend := m.backend
	return func() tea.Msg {
		operation, err := backend.ExecuteConfirmation(context.Background(), localActor(), confirmationID, localHasScope)
		return executionMsg{request: request, operation: operation, err: err}
	}
}

func (m *Model) executePrepared(request actionRequest, confirmation protocol.ConfirmationPrepareResponse) (tea.Model, tea.Cmd) {
	m.modal = nil
	m.busy = true
	backend := m.backend
	confirmationID := confirmation.ConfirmationID
	return m, func() tea.Msg {
		operation, err := backend.ExecuteConfirmation(context.Background(), localActor(), confirmationID, localHasScope)
		return executionMsg{request: request, operation: operation, err: err}
	}
}

func (m *Model) updateExecution(message executionMsg) (tea.Model, tea.Cmd) {
	m.busy = false
	if message.err != nil {
		m.showFailure("Confirmation failed", message.err, true)
		return m, nil
	}
	state := protocol.OperationStateQueued
	if message.operation.State != nil {
		state = cleanText(*message.operation.State)
	}
	m.setToast("Operation "+shortID(message.operation.ID)+" "+state+".", toastSuccess)
	return m, m.afterActionCmd(message.request)
}

func (m *Model) updateBackupCreated(message backupCreatedMsg) (tea.Model, tea.Cmd) {
	m.busy = false
	if message.err != nil {
		m.showFailure("Backup failed", message.err, true)
		return m, nil
	}
	m.pane = paneBackups
	m.setToast("Backup created: "+shortID(message.response.Backup.ID)+" "+cleanText(message.response.Backup.Label), toastSuccess)
	return m, tea.Batch(m.backupsCmd(), m.operationsCmd(), m.statusCmd())
}

func (m *Model) updatePlayerChanged(message playerChangedMsg) (tea.Model, tea.Cmd) {
	m.busy = false
	if message.err != nil {
		m.showFailure("Player mutation failed", message.err, true)
		return m, nil
	}
	verb := "updated"
	if message.action == "add" {
		verb = "added to"
	} else if message.action == "remove" {
		verb = "removed from"
	}
	m.setToast(cleanText(message.name)+" "+verb+" "+cleanText(message.kind)+".", toastSuccess)
	return m, tea.Batch(m.playerListCmd(message.kind), m.onlinePlayersCmd())
}

func (m *Model) afterActionCmd(request actionRequest) tea.Cmd {
	commands := []tea.Cmd{m.statusCmd(), m.consoleCmd(), m.operationsCmd()}
	if m.hasCapability(protocol.CapabilityBackup) && (request.kind == actionBackupRestore || request.kind == actionBackupDelete) {
		commands = append(commands, m.backupsCmd())
	}
	if m.hasCapability(protocol.CapabilityFiles) && (request.kind == actionFileDelete || request.kind == actionBackupRestore) {
		commands = append(commands, m.filesCmd(m.files.Path))
	}
	if m.hasCapability(protocol.CapabilityFiles) || m.hasCapability(protocol.CapabilityProperties) {
		if request.kind == actionControl || request.kind == actionPropertiesPatch || request.kind == actionBackupRestore {
			commands = append(commands, m.propertiesCmd())
		}
	}
	return tea.Batch(commands...)
}
