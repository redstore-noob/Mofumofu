// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package tui

import (
	"fmt"
	"strings"
	"time"
	"unicode"

	"github.com/charmbracelet/lipgloss"
	"github.com/charmbracelet/x/ansi"
)

const (
	colorBackground = "#0b0e11"
	colorForeground = "#aab4bc"
	colorMuted      = "#66727b"
	colorBorder     = "#29343b"
	colorAccent     = "#67d4cf"
	colorRunning    = "#7fc98b"
	colorStarting   = "#d5bb69"
	colorStopped    = "#7d878f"
	colorCrashed    = "#dc777d"
	colorWarning    = "#d5bb69"
	colorError      = "#dc777d"
	colorChat       = "#7fc98b"
)

var (
	baseStyle       = lipgloss.NewStyle().Foreground(lipgloss.Color(colorForeground)).Background(lipgloss.Color(colorBackground))
	mutedStyle      = lipgloss.NewStyle().Foreground(lipgloss.Color(colorMuted))
	accentStyle     = lipgloss.NewStyle().Foreground(lipgloss.Color(colorAccent))
	panelStyle      = baseStyle.Border(lipgloss.NormalBorder()).BorderForeground(lipgloss.Color(colorBorder))
	modalStyle      = baseStyle.Border(lipgloss.NormalBorder()).BorderForeground(lipgloss.Color(colorAccent))
	dangerStyle     = lipgloss.NewStyle().Foreground(lipgloss.Color(colorError))
	warningStyle    = lipgloss.NewStyle().Foreground(lipgloss.Color(colorWarning))
	successStyle    = lipgloss.NewStyle().Foreground(lipgloss.Color(colorRunning))
	runningStyle    = lipgloss.NewStyle().Foreground(lipgloss.Color(colorRunning))
	startingStyle   = lipgloss.NewStyle().Foreground(lipgloss.Color(colorStarting))
	stoppedStyle    = lipgloss.NewStyle().Foreground(lipgloss.Color(colorStopped))
	crashedStyle    = lipgloss.NewStyle().Foreground(lipgloss.Color(colorCrashed))
	logInfoStyle    = lipgloss.NewStyle().Foreground(lipgloss.Color(colorStopped))
	logWarningStyle = lipgloss.NewStyle().Foreground(lipgloss.Color(colorWarning))
	logErrorStyle   = lipgloss.NewStyle().Foreground(lipgloss.Color(colorError))
	logChatStyle    = lipgloss.NewStyle().Foreground(lipgloss.Color(colorChat))
)

// cleanText removes terminal escapes, Minecraft formatting codes, and control
// characters before untrusted server text reaches the terminal.
func cleanText(value string) string {
	value = ansi.Strip(value)
	runes := []rune(value)
	var builder strings.Builder
	builder.Grow(len(value))
	for index := 0; index < len(runes); index++ {
		current := runes[index]
		if current == '§' {
			if index+1 < len(runes) {
				index++
			}
			continue
		}
		switch current {
		case '\t':
			builder.WriteString("    ")
		case '\r', '\n':
			builder.WriteByte(' ')
		default:
			if !unicode.IsControl(current) {
				builder.WriteRune(current)
			}
		}
	}
	return strings.Join(strings.Fields(builder.String()), " ")
}

func truncateText(value string, width int) string {
	if width <= 0 {
		return ""
	}
	value = cleanText(value)
	return ansi.Truncate(value, width, "…")
}

func padRight(value string, width int) string {
	if width <= 0 {
		return ""
	}
	value = ansi.Truncate(value, width, "…")
	if padding := width - ansi.StringWidth(value); padding > 0 {
		value += strings.Repeat(" ", padding)
	}
	return value
}

// fitBlock bounds a rendered block without allowing resize-induced negative
// dimensions or terminal overflow.
func fitBlock(value string, width, height int) string {
	if width <= 0 || height <= 0 {
		return ""
	}
	lines := strings.Split(value, "\n")
	if len(lines) > height {
		lines = lines[:height]
	}
	for index := range lines {
		lines[index] = padRight(lines[index], width)
	}
	for len(lines) < height {
		lines = append(lines, strings.Repeat(" ", width))
	}
	return strings.Join(lines, "\n")
}

func wrapPlain(value string, width int) []string {
	value = cleanText(value)
	if width <= 0 {
		return nil
	}
	if value == "" {
		return []string{""}
	}
	soft := strings.Split(ansi.Wordwrap(value, width, ""), "\n")
	lines := make([]string, 0, len(soft))
	for _, line := range soft {
		if ansi.StringWidth(line) <= width {
			lines = append(lines, line)
			continue
		}
		lines = append(lines, strings.Split(ansi.Hardwrap(line, width, false), "\n")...)
	}
	return lines
}

func formatBytes(value int64) string {
	if value < 1024 {
		return fmt.Sprintf("%d B", value)
	}
	units := []string{"KiB", "MiB", "GiB", "TiB"}
	amount := float64(value) / 1024
	index := 0
	for amount >= 1024 && index < len(units)-1 {
		amount /= 1024
		index++
	}
	return fmt.Sprintf("%.1f %s", amount, units[index])
}

func formatUptime(seconds *int64) string {
	if seconds == nil || *seconds < 0 {
		return "—"
	}
	value := *seconds
	hours := value / 3600
	minutes := (value % 3600) / 60
	remaining := value % 60
	if hours > 0 {
		return fmt.Sprintf("%02d:%02d:%02d", hours, minutes, remaining)
	}
	return fmt.Sprintf("%02d:%02d", minutes, remaining)
}

func formatClock(value time.Time) string {
	if value.IsZero() {
		return "--:--:--"
	}
	return value.Format("15:04:05")
}

func stateStyle(state string) lipgloss.Style {
	switch state {
	case protocolStateRunning:
		return runningStyle
	case protocolStateStarting, protocolStateStopping:
		return startingStyle
	case protocolStateCrashed:
		return crashedStyle
	default:
		return stoppedStyle
	}
}

const (
	protocolStateRunning  = "running"
	protocolStateStarting = "starting"
	protocolStateStopping = "stopping"
	protocolStateCrashed  = "crashed"
	protocolStateStopped  = "stopped"
)

func shortID(value string) string {
	value = cleanText(value)
	if len(value) <= 16 {
		return value
	}
	return value[:13] + "…"
}

func joinColumns(left, right string, width int) string {
	if width <= 0 {
		return ""
	}
	leftWidth := ansi.StringWidth(left)
	if leftWidth >= width {
		return truncateText(left, width)
	}
	rightWidth := max(0, width-leftWidth-1)
	if rightWidth == 0 {
		return padRight(left, width)
	}
	return padRight(left, leftWidth) + " " + padRight(right, rightWidth)
}
