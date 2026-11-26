// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package tui

import (
	"context"
	"fmt"
	"io"
	"os"

	tea "github.com/charmbracelet/bubbletea"
	"github.com/charmbracelet/x/term"
)

// IsTTY reports whether both streams are attached to a terminal.
func IsTTY(stdin, stdout *os.File) bool {
	return stdin != nil && stdout != nil && term.IsTerminal(stdin.Fd()) && term.IsTerminal(stdout.Fd())
}

// Run starts the local alternate-screen TUI. It never opens a listener or
// constructs an HTTP client; all access goes through backend.
func Run(ctx context.Context, backend Backend, version string) (err error) {
	if ctx == nil {
		ctx = context.Background()
	}
	if backend == nil {
		return fmt.Errorf("TUI backend is required")
	}
	stdin, stdout := os.Stdin, os.Stdout
	if !IsTTY(stdin, stdout) {
		return fmt.Errorf("TUI requires an interactive terminal on stdin and stdout")
	}

	defer func() {
		_ = writeTerminalReset(stdout)
		if recovered := recover(); recovered != nil {
			err = fmt.Errorf("TUI panic: %v", recovered)
		}
	}()

	program := tea.NewProgram(New(backend, version), tea.WithContext(ctx), tea.WithAltScreen())
	_, err = program.Run()
	if err == nil && ctx.Err() != nil {
		return ctx.Err()
	}
	return err
}

func writeTerminalReset(writer io.Writer) error {
	if writer == nil {
		return nil
	}
	_, err := io.WriteString(writer, "\x1b[0m\x1b[?25h\x1b[?2004l\x1b[?1000l\x1b[?1002l\x1b[?1003l\x1b[?1006l\x1b[?1049l")
	return err
}
