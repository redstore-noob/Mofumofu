// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package tui

import (
	"bytes"
	"testing"
	"time"

	tea "github.com/charmbracelet/bubbletea"
	"github.com/charmbracelet/x/exp/teatest"
)

func waitForText(t *testing.T, model *teatest.TestModel, text string) {
	t.Helper()
	teatest.WaitFor(t, model.Output(), func(output []byte) bool {
		return bytes.Contains(output, []byte(text))
	}, teatest.WithDuration(3*time.Second), teatest.WithCheckInterval(20*time.Millisecond))
}

func sendRunes(model *teatest.TestModel, value string) {
	model.Send(tea.KeyMsg{Type: tea.KeyRunes, Runes: []rune(value)})
}

func TestTeatestNavigationFilterAndQuit(t *testing.T) {
	model := teatest.NewTestModel(t, New(newFakeBackend(), "v-test"), teatest.WithInitialTermSize(80, 24))
	defer model.Quit()
	waitForText(t, model, "Starting Mofumofu server")

	model.Send(tea.KeyMsg{Type: tea.KeyTab})
	waitForText(t, model, "[Players]")
	model.Send(tea.KeyMsg{Type: tea.KeyShiftTab})
	waitForText(t, model, "[Console]")

	sendRunes(model, "/")
	model.Type("joined")
	model.Send(tea.KeyMsg{Type: tea.KeyEnter})
	waitForText(t, model, "filter: joined")

	sendRunes(model, "q")
	waitForText(t, model, "Quit the local TUI?")
	sendRunes(model, "n")
	waitForText(t, model, "Players joined: Alice, Bob")
	sendRunes(model, "q")
	waitForText(t, model, "Quit the local TUI?")
	sendRunes(model, "y")
	model.WaitFinished(t, teatest.WithFinalTimeout(3*time.Second))
}

func TestTeatestDangerConfirmation(t *testing.T) {
	backend := newFakeBackend()
	model := teatest.NewTestModel(t, New(backend, "v-test"), teatest.WithInitialTermSize(80, 24))
	defer model.Quit()
	waitForText(t, model, "survival")

	sendRunes(model, "x")
	waitForText(t, model, "Confirm server stop")
	backend.mu.Lock()
	executedEarly := backend.executeCalls
	backend.mu.Unlock()
	if executedEarly != 0 {
		t.Fatal("stop executed before modal confirmation")
	}
	model.Send(tea.KeyMsg{Type: tea.KeyEnter})
	waitForText(t, model, "queued.")
	backend.mu.Lock()
	executeCalls := backend.executeCalls
	actor := backend.lastActor
	backend.mu.Unlock()
	if executeCalls != 1 || actor.TokenID != localTokenID {
		t.Fatalf("confirmed stop calls=%d actor=%q", executeCalls, actor.TokenID)
	}

	sendRunes(model, "q")
	waitForText(t, model, "Quit the local TUI?")
	sendRunes(model, "y")
	model.WaitFinished(t, teatest.WithFinalTimeout(3*time.Second))
}

func TestTeatestResizeSidebar(t *testing.T) {
	model := teatest.NewTestModel(t, New(newFakeBackend(), "v-test"), teatest.WithInitialTermSize(80, 24))
	defer model.Quit()
	waitForText(t, model, "Starting Mofumofu server")
	model.Send(tea.WindowSizeMsg{Width: 120, Height: 40})
	waitForText(t, model, "127.0.0.1:17870")
	model.Send(tea.WindowSizeMsg{Width: 80, Height: 24})
	waitForText(t, model, "console follow")

	sendRunes(model, "q")
	waitForText(t, model, "Quit the local TUI?")
	sendRunes(model, "y")
	model.WaitFinished(t, teatest.WithFinalTimeout(3*time.Second))
	finalModel := model.FinalModel(t).(*Model)
	if finalModel.width != 80 || finalModel.height != 24 {
		t.Fatalf("final size = %dx%d", finalModel.width, finalModel.height)
	}
}
