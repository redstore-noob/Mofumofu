// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package tui

import (
	"fmt"
	"regexp"
	"sort"
	"strings"

	"github.com/charmbracelet/lipgloss"
	"github.com/charmbracelet/x/ansi"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

func lipglossJoinHorizontal(left, right string) string {
	return lipgloss.JoinHorizontal(lipgloss.Top, left, right)
}

func (m *Model) renderStatus(width int) string {
	instance := m.status.InstanceID
	if instance == "" {
		instance = m.agent.InstanceID
	}
	if instance == "" {
		instance = "instance"
	}
	state := m.state
	if state == "" {
		state = protocolStateStopped
	}
	left := cleanText(instance) + " " + stateStyle(state).Render("●") + " " + state
	left += "  up " + formatUptime(m.uptime)
	if m.status.CPUPercent != nil {
		left += fmt.Sprintf("  cpu %.1f%%", *m.status.CPUPercent)
	}
	if m.status.MemoryUsedBytes != nil {
		left += "  mem " + formatBytes(*m.status.MemoryUsedBytes)
		if m.status.MemoryMaxBytes != nil {
			left += "/" + formatBytes(*m.status.MemoryMaxBytes)
		}
	}
	if m.busy {
		left += "  " + accentStyle.Render("working…")
	}
	right := cleanText(m.version)
	if right != "" {
		right = accentStyle.Render(right)
	}
	available := max(0, width-ansi.StringWidth(right)-1)
	left = ansi.Truncate(left, available, "…")
	value := left
	if right != "" && ansi.StringWidth(left) < available {
		value += strings.Repeat(" ", available-ansi.StringWidth(left)) + " " + right
	}
	return baseStyle.Width(width).Render(padRight(value, width))
}

func (m *Model) renderHint(width int) string {
	hint := "tab pane · j/k scroll · / filter · : cmd · ? help · q quit"
	if m.activePane() == paneConsole {
		mode := "follow"
		if !m.consoleFollow {
			mode = "paused"
		}
		hint = "console " + mode + " · space follow/pause · tab pane · / filter · : cmd · q quit"
	}
	return baseStyle.Width(width).Render(padRight(mutedStyle.Render(hint), width))
}

func (m *Model) renderToast(width int) string {
	style := mutedStyle
	switch m.toastKind {
	case toastSuccess:
		style = successStyle
	case toastError:
		style = logErrorStyle
	}
	return baseStyle.Width(width).Render(padRight(style.Render(truncateText(m.toast, width)), width))
}

func (m *Model) renderMainPane(width, height int) string {
	bodyHeight := max(1, height-4)
	var body []string
	status := ""
	switch m.activePane() {
	case panePlayers:
		body = m.playerLines(max(1, width-4), bodyHeight)
	case paneOperations:
		body = m.operationLines(max(1, width-4), bodyHeight)
	case paneBackups:
		body = m.backupLines(max(1, width-4), bodyHeight)
	case paneFiles:
		body = m.fileLines(max(1, width-4), bodyHeight)
	case paneProperties:
		body = m.propertyLines(max(1, width-4), bodyHeight)
	default:
		status = "follow"
		if !m.consoleFollow {
			status = "paused"
		}
		if m.filter != "" {
			status += " · filter: " + m.filter
		}
		if m.input.kind == inputFilter {
			status = "filter: " + m.inputDisplay(max(1, width-12))
		}
		body = m.consoleLines(max(1, width-4), bodyHeight)
	}
	title := accentStyle.Render(paneNames[m.activePane()])
	if status != "" {
		title += "  " + mutedStyle.Render(truncateText(status, max(1, width-14)))
	}
	content := []string{title, m.renderTabs(max(1, width-2))}
	content = append(content, body...)
	return renderPanel(content, width, height)
}

func (m *Model) renderTabs(width int) string {
	var tabs []string
	for index, name := range paneNames {
		label := name
		if paneKind(index) == m.activePane() {
			label = accentStyle.Render("[" + name + "]")
		} else {
			label = mutedStyle.Render(name)
		}
		tabs = append(tabs, label)
	}
	return ansi.Truncate(strings.Join(tabs, " "), width, "…")
}

func renderPanel(lines []string, width, height int) string {
	innerWidth := max(1, width-2)
	innerHeight := max(1, height-2)
	content := fitBlock(strings.Join(lines, "\n"), innerWidth, innerHeight)
	style := panelStyle.Width(innerWidth).Height(innerHeight)
	return fitBlock(style.Render(content), width, height)
}

type consoleVisualLine struct {
	text  string
	style lipgloss.Style
}

func (m *Model) consoleLines(width, height int) []string {
	entries := m.filteredConsole()
	visual := make([]consoleVisualLine, 0, len(entries))
	filterValue := m.consoleFilterValue()
	filter := strings.ToLower(cleanText(filterValue))
	for _, entry := range entries {
		level := logLevel(entry)
		prefix := formatClock(entry.Timestamp.Time) + " " + level + " "
		message := cleanText(entry.Message)
		if message == "" {
			message = "(empty message)"
		}
		messageWidth := max(1, width-ansi.StringWidth(prefix))
		wrapped := wrapPlain(message, messageWidth)
		style := logStyle(level)
		for index, line := range wrapped {
			if index == 0 {
				visual = append(visual, consoleVisualLine{text: truncateText(prefix+line, width), style: style})
			} else {
				visual = append(visual, consoleVisualLine{text: truncateText(strings.Repeat(" ", ansi.StringWidth(prefix))+line, width), style: style})
			}
		}
	}
	if len(visual) == 0 {
		message := "(no console entries)"
		if filter != "" {
			message = fmt.Sprintf("(no console entries match %q)", filterValue)
		}
		return padLines([]string{mutedStyle.Render(truncateText(message, width))}, height)
	}
	offset := m.offsets[paneConsole]
	if m.consoleFollow {
		offset = 0
	}
	maximum := max(0, len(visual)-height)
	offset = max(0, min(maximum, offset))
	start := len(visual) - height - offset
	if start < 0 {
		start = 0
	}
	end := min(len(visual), start+height)
	lines := make([]string, 0, height)
	for _, line := range visual[start:end] {
		lines = append(lines, line.style.Render(padRight(line.text, width)))
	}
	return padLines(lines, height)
}

func (m *Model) consoleFilterValue() string {
	if m.input.kind == inputFilter {
		return string(m.input.value)
	}
	return m.filter
}

func (m *Model) filteredConsole() []protocol.ConsoleEntry {
	filter := strings.ToLower(cleanText(m.consoleFilterValue()))
	if filter == "" {
		return m.console
	}
	result := make([]protocol.ConsoleEntry, 0, len(m.console))
	for _, entry := range m.console {
		haystack := strings.ToLower(cleanText(entry.Message + " " + entry.Source + " " + entry.Level))
		if strings.Contains(haystack, filter) {
			result = append(result, entry)
		}
	}
	return result
}

var (
	log4jSeverityPattern      = regexp.MustCompile(`(?i)\[[^\]\r\n]*/(WARN|WARNING|ERROR|FATAL)\]`)
	standaloneSeverityPattern = regexp.MustCompile(`(?i)(^|[^A-Za-z0-9_])(WARN|WARNING|ERROR|FATAL)([^A-Za-z0-9_]|$)`)
	chatMessagePattern        = regexp.MustCompile(`(?:^|[\]:][ \t]*)<[^<>\r\n]{1,64}>[ \t]+\S`)
)

func logLevel(entry protocol.ConsoleEntry) string {
	explicit := normalizeLogLevel(entry.Level)
	if explicit == "WARN" || explicit == "ERROR" || explicit == "CHAT" {
		return explicit
	}
	message := cleanText(entry.Message)
	if match := log4jSeverityPattern.FindStringSubmatch(message); len(match) > 1 {
		return normalizeLogLevel(match[1])
	}
	if chatMessagePattern.MatchString(message) {
		return "CHAT"
	}
	if match := standaloneSeverityPattern.FindStringSubmatch(message); len(match) > 2 {
		return normalizeLogLevel(match[2])
	}
	if explicit != "" {
		return explicit
	}
	return "INFO"
}

func normalizeLogLevel(level string) string {
	switch strings.ToUpper(cleanText(level)) {
	case "WARN", "WARNING":
		return "WARN"
	case "ERROR", "FATAL":
		return "ERROR"
	default:
		return strings.ToUpper(cleanText(level))
	}
}

func logStyle(level string) lipgloss.Style {
	switch level {
	case "WARN":
		return logWarningStyle
	case "ERROR":
		return logErrorStyle
	case "CHAT":
		return logChatStyle
	default:
		return logInfoStyle
	}
}

func (m *Model) playerLines(width, height int) []string {
	kind := m.playerKind
	if kind == "online" {
		lines := make([]string, 0, len(m.online.Players))
		for _, player := range m.online.Players {
			details := cleanText(player.Name)
			if player.GameMode != nil {
				details += "  " + cleanText(*player.GameMode)
			}
			if player.PingMilliseconds != nil {
				details += fmt.Sprintf("  %dms", *player.PingMilliseconds)
			}
			if player.OnlineSeconds != nil {
				details += "  " + formatUptime(player.OnlineSeconds)
			}
			lines = append(lines, truncateText(details, width))
		}
		if len(lines) == 0 {
			lines = append(lines, mutedStyle.Render("(no players online)"))
		}
		return selectionWindow(lines, m.offsets[panePlayers], height, width, false)
	}
	response := m.playerLists[kind]
	lines := make([]string, 0, len(response.Entries))
	for _, entry := range response.Entries {
		details := cleanText(entry.Name)
		if entry.OpLevel != nil {
			details += fmt.Sprintf("  op=%d", *entry.OpLevel)
		}
		if entry.Reason != nil && cleanText(*entry.Reason) != "" {
			details += "  " + cleanText(*entry.Reason)
		}
		if entry.ExpiresAt != nil {
			details += "  until " + entry.ExpiresAt.Time.Format("2006-01-02")
		}
		lines = append(lines, truncateText(details, width))
	}
	kindLabel := strings.ToUpper(kind)
	if len(lines) == 0 {
		lines = append(lines, mutedStyle.Render("(no "+kind+" entries)"))
	}
	lines = append([]string{mutedStyle.Render(kindLabel + " · left/right switches list")}, lines...)
	return selectionWindow(lines, m.offsets[panePlayers], height, width, false)
}

func (m *Model) operationLines(width, height int) []string {
	lines := make([]string, 0, len(m.operations))
	for _, operation := range m.operations {
		state := protocol.OperationStateQueued
		if operation.State != nil {
			state = cleanText(*operation.State)
		}
		line := shortID(operation.ID) + "  " + cleanText(operation.Kind) + "  " + state
		if operation.ProgressPercent != nil {
			line += fmt.Sprintf("  %.0f%%", *operation.ProgressPercent)
		}
		if operation.Message != nil && cleanText(*operation.Message) != "" {
			line += "  " + cleanText(*operation.Message)
		}
		lines = append(lines, truncateText(line, width))
	}
	if len(lines) == 0 {
		lines = append(lines, mutedStyle.Render("(no active operations)"))
	}
	return selectionWindow(lines, m.offsets[paneOperations], height, width, true)
}

func (m *Model) backupLines(width, height int) []string {
	lines := make([]string, 0, len(m.backups))
	for _, backup := range m.backups {
		line := cleanText(backup.Label) + "  " + shortID(backup.ID) + "  " + backup.CreatedAt.Time.Format("2006-01-02 15:04") + "  " + formatBytes(backup.SizeBytes)
		lines = append(lines, truncateText(line, width))
	}
	if len(lines) == 0 {
		lines = append(lines, mutedStyle.Render("(no backups)"))
	}
	return selectionWindow(lines, m.offsets[paneBackups], height, width, true)
}

func (m *Model) fileLines(width, height int) []string {
	current := normalizeServerPath(m.files.Path)
	header := "path /" + current
	if m.files.Path == "" {
		header = "path /"
	}
	lines := []string{mutedStyle.Render(truncateText(header, width))}
	for _, entry := range m.files.Entries {
		name := normalizeServerPath(entry.Path)
		if current != "" {
			name = strings.TrimPrefix(name, current+"/")
		}
		if entry.IsDirectory {
			name += "/"
		}
		details := name
		if !entry.IsDirectory && entry.SizeBytes != nil {
			details += "  " + formatBytes(*entry.SizeBytes)
		}
		if !entry.Writable {
			details += "  ro"
		}
		lines = append(lines, truncateText(details, width))
	}
	if len(lines) == 1 {
		lines = append(lines, mutedStyle.Render("(empty directory)"))
	}
	return selectionWindowAdjusted(lines, m.offsets[paneFiles], height, width, true, 1)
}

func (m *Model) propertyLines(width, height int) []string {
	keys := make([]string, 0, len(m.properties.Properties))
	for key := range m.properties.Properties {
		keys = append(keys, key)
	}
	sort.Strings(keys)
	restart := "no restart required"
	if m.properties.RestartRequired {
		restart = "restart required"
	}
	lines := []string{mutedStyle.Render(truncateText(restart, width))}
	for _, key := range keys {
		lines = append(lines, truncateText(key+"="+m.properties.Properties[key], width))
	}
	if len(lines) == 1 {
		lines = append(lines, mutedStyle.Render("(no properties)"))
	}
	return selectionWindowAdjusted(lines, m.offsets[paneProperties], height, width, true, 1)
}

func selectionWindow(lines []string, offset, height, width int, marker bool) []string {
	return selectionWindowAdjusted(lines, offset, height, width, marker, 0)
}

func selectionWindowAdjusted(lines []string, offset, height, width int, marker bool, markerAdjustment int) []string {
	if height <= 0 {
		return nil
	}
	maximum := max(0, len(lines)-height)
	offset = max(0, min(maximum, offset))
	start := offset
	end := min(len(lines), start+height)
	result := make([]string, 0, height)
	for index := start; index < end; index++ {
		line := padRight(lines[index], max(1, width-2))
		if marker {
			prefix := "  "
			if index == offset+markerAdjustment {
				prefix = "> "
			}
			line = prefix + line
		} else {
			line = "  " + line
		}
		result = append(result, ansi.Truncate(line, width, "…"))
	}
	return padLines(result, height)
}

func padLines(lines []string, height int) []string {
	if height <= 0 {
		return nil
	}
	if len(lines) > height {
		return lines[:height]
	}
	for len(lines) < height {
		lines = append(lines, "")
	}
	return lines
}

func (m *Model) renderSidebar(width, height int) string {
	bodyHeight := max(1, height-2)
	lines := m.sidebarLines(max(1, width-2), bodyHeight)
	return renderPanel(lines, width, height)
}

func (m *Model) sidebarLines(width, height int) []string {
	var lines []string
	lines = append(lines, accentStyle.Render("Players")+" "+mutedStyle.Render(fmt.Sprintf("(%d/%d)", m.online.OnlineCount, m.online.MaxPlayers)))
	for index, player := range m.online.Players {
		if index >= 4 {
			break
		}
		lines = append(lines, "  "+truncateText(player.Name, width-2))
	}
	if len(m.online.Players) == 0 {
		lines = append(lines, mutedStyle.Render("  (none online)"))
	}
	lines = append(lines, "")
	lines = append(lines, accentStyle.Render("Operations")+" "+mutedStyle.Render(fmt.Sprintf("(%d)", len(m.operations))))
	for index, operation := range m.operations {
		if index >= 4 {
			break
		}
		state := protocol.OperationStateQueued
		if operation.State != nil {
			state = cleanText(*operation.State)
		}
		lines = append(lines, "  "+truncateText(shortID(operation.ID)+" "+state, width-2))
	}
	if len(m.operations) == 0 {
		lines = append(lines, mutedStyle.Render("  (none)"))
	}
	lines = append(lines, "")
	lines = append(lines, accentStyle.Render("Backups")+" "+mutedStyle.Render(fmt.Sprintf("(%d)", len(m.backups))))
	for index, backup := range m.backups {
		if index >= 4 {
			break
		}
		lines = append(lines, "  "+truncateText(cleanText(backup.Label)+" "+formatBytes(backup.SizeBytes), width-2))
	}
	if len(m.backups) == 0 {
		lines = append(lines, mutedStyle.Render("  (none)"))
	}
	lines = append(lines, "")
	lines = append(lines, accentStyle.Render("Agent"))
	lines = append(lines, "  instance  "+truncateText(m.agent.InstanceID, max(1, width-12)))
	lines = append(lines, "  bind      "+truncateText(m.agent.Bind, max(1, width-12)))
	lines = append(lines, "  mode      "+truncateText(m.agent.Mode, max(1, width-12)))
	probe := "no"
	if m.agent.Probe {
		probe = "yes"
	}
	lines = append(lines, "  probe     "+probe)
	lines = append(lines, fmt.Sprintf("  tokens    %d", m.agent.TokenCount))
	capabilities := strings.Join(m.capabilities, ", ")
	if capabilities == "" {
		capabilities = "(none)"
	}
	lines = append(lines, "  caps")
	for _, wrapped := range wrapPlain(capabilities, max(1, width-4)) {
		lines = append(lines, "    "+wrapped)
	}
	if len(lines) > height {
		lines = lines[:height]
	}
	return padLines(lines, height)
}

func (m *Model) renderModal(width, height int) string {
	if m.modal == nil {
		return ""
	}
	return renderBox(m.modal.title, m.modal.body, width, height, m.modal.danger || m.modal.kind == modalError)
}

func (m *Model) renderCommandPalette(width, height int) string {
	innerWidth := max(1, min(64, width)-2)
	body := []string{accentStyle.Render("> ") + m.inputDisplay(innerWidth-2), ""}
	suggestions := commandSuggestions(string(m.input.value))
	maximum := max(1, height-7)
	if len(suggestions) > maximum {
		suggestions = suggestions[:maximum]
	}
	for _, spec := range suggestions {
		usage := truncateText(spec.usage, max(8, innerWidth/2))
		body = append(body, joinColumns(usage, spec.description, innerWidth))
	}
	if len(suggestions) == 0 {
		body = append(body, mutedStyle.Render("(no matching commands)"))
	}
	body = append(body, "", mutedStyle.Render("Enter run · Esc close · Up/Down history"))
	return renderBox("Command", body, width, height, false)
}

func renderBox(title string, body []string, width, height int, danger bool) string {
	if width <= 0 || height <= 0 {
		return ""
	}
	boxWidth := min(64, width)
	innerWidth := max(1, boxWidth-2)
	innerHeight := max(1, min(height-2, len(body)+4))
	lines := []string{title}
	for _, line := range body {
		for _, wrapped := range wrapPlain(line, innerWidth) {
			lines = append(lines, wrapped)
		}
	}
	if len(lines) > innerHeight {
		lines = lines[:innerHeight]
	}
	content := fitBlock(strings.Join(lines, "\n"), innerWidth, innerHeight)
	style := modalStyle.Width(innerWidth).Height(innerHeight)
	if danger {
		style = style.BorderForeground(lipgloss.Color(colorError))
	}
	box := style.Render(content)
	return fitBlock(lipgloss.Place(width, height, lipgloss.Center, lipgloss.Center, box), width, height)
}

func (m *Model) inputDisplay(width int) string {
	runes := m.input.value
	cursor := max(0, min(len(runes), m.input.cursor))
	value := string(runes[:cursor]) + "▏" + string(runes[cursor:])
	return ansi.Truncate(value, max(1, width), "…")
}
