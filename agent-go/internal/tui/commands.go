// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package tui

import (
	"fmt"
	"strings"
	"unicode"

	tea "github.com/charmbracelet/bubbletea"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

type commandSpec struct {
	name        string
	usage       string
	description string
}

var commandSpecs = []commandSpec{
	{name: "start", usage: ":start", description: "prepare and start the server"},
	{name: "stop", usage: ":stop", description: "confirm and stop the server"},
	{name: "restart", usage: ":restart", description: "confirm and restart the server"},
	{name: "kill", usage: ":kill", description: "confirm and force-kill the server"},
	{name: "backup", usage: ":backup [label]", description: "create a labeled backup"},
	{name: "restore", usage: ":restore <id>", description: "confirm and restore a backup while offline"},
	{name: "delete-backup", usage: ":delete-backup <id>", description: "confirm and delete a backup"},
	{name: "delete-file", usage: ":delete-file <path>", description: "confirm and delete a file or directory"},
	{name: "set", usage: ":set <key> <value>", description: "confirm a server.properties patch"},
	{name: "op", usage: ":op <name>", description: "grant operator level 4"},
	{name: "deop", usage: ":deop <name>", description: "revoke operator status"},
	{name: "whitelist", usage: ":whitelist add|remove <name>", description: "change the whitelist"},
	{name: "ban", usage: ":ban <name> [reason]", description: "ban a player"},
	{name: "pardon", usage: ":pardon <name>", description: "pardon a player"},
	{name: "help", usage: ":help", description: "show key and command help"},
	{name: "quit", usage: ":quit", description: "confirm quitting the TUI"},
}

func (m *Model) executeCommand(line string) tea.Cmd {
	line = strings.TrimSpace(line)
	line = strings.TrimPrefix(line, ":")
	words, err := splitCommandWords(line)
	if err != nil {
		m.showFailure("Command failed", err, true)
		return nil
	}
	if len(words) == 0 {
		return nil
	}
	name := strings.ToLower(words[0])
	arguments := words[1:]
	switch name {
	case "start":
		if !m.requireNoArguments(name, arguments) {
			return nil
		}
		return m.requestControl(protocol.ControlActionStart)
	case "stop":
		if !m.requireNoArguments(name, arguments) {
			return nil
		}
		return m.requestControl(protocol.ControlActionStop)
	case "restart":
		if !m.requireNoArguments(name, arguments) {
			return nil
		}
		return m.requestControl(protocol.ControlActionRestart)
	case "kill":
		if !m.requireNoArguments(name, arguments) {
			return nil
		}
		return m.requestControl(protocol.ControlActionKill)
	case "backup":
		return m.requestBackupCreate(strings.Join(arguments, " "))
	case "restore":
		if len(arguments) != 1 {
			m.commandUsage(name)
			return nil
		}
		return m.requestBackupAction(actionBackupRestore, arguments[0])
	case "delete-backup":
		if len(arguments) != 1 {
			m.commandUsage(name)
			return nil
		}
		return m.requestBackupAction(actionBackupDelete, arguments[0])
	case "delete-file":
		if len(arguments) != 1 {
			m.commandUsage(name)
			return nil
		}
		return m.requestFileDelete(arguments[0])
	case "set":
		if len(arguments) < 2 {
			m.commandUsage(name)
			return nil
		}
		return m.requestPropertiesPatch(arguments[0], strings.Join(arguments[1:], " "))
	case "op", "deop":
		if len(arguments) != 1 {
			m.commandUsage(name)
			return nil
		}
		action := "add"
		if name == "deop" {
			action = "remove"
		}
		return m.requestPlayerMutation(protocol.PlayerListOps, action, arguments[0], "")
	case "whitelist":
		if len(arguments) != 2 || (arguments[0] != "add" && arguments[0] != "remove") {
			m.commandUsage(name)
			return nil
		}
		return m.requestPlayerMutation(protocol.PlayerListWhitelist, arguments[0], arguments[1], "")
	case "ban":
		if len(arguments) < 1 {
			m.commandUsage(name)
			return nil
		}
		return m.requestPlayerMutation(protocol.PlayerListBans, "add", arguments[0], strings.Join(arguments[1:], " "))
	case "pardon":
		if len(arguments) != 1 {
			m.commandUsage(name)
			return nil
		}
		return m.requestPlayerMutation(protocol.PlayerListBans, "remove", arguments[0], "")
	case "help":
		if !m.requireNoArguments(name, arguments) {
			return nil
		}
		m.openHelp()
		return nil
	case "quit":
		if !m.requireNoArguments(name, arguments) {
			return nil
		}
		m.openQuit()
		return nil
	default:
		m.showFailure("Unknown command", fmt.Errorf("unknown command :%s", cleanText(name)), true)
		return nil
	}
}

func (m *Model) requireNoArguments(name string, arguments []string) bool {
	if len(arguments) != 0 {
		m.commandUsage(name)
		return false
	}
	return true
}

func (m *Model) commandUsage(name string) {
	usage := ":" + name
	for _, spec := range commandSpecs {
		if spec.name == name {
			usage = spec.usage
			break
		}
	}
	m.showFailure("Command usage", fmt.Errorf("usage: %s", usage), true)
}

func splitCommandWords(value string) ([]string, error) {
	var words []string
	var current strings.Builder
	var quote rune
	escaped := false
	started := false
	for _, character := range value {
		if escaped {
			current.WriteRune(character)
			escaped = false
			started = true
			continue
		}
		if character == '\\' && quote != '\'' {
			escaped = true
			started = true
			continue
		}
		if quote != 0 {
			if character == quote {
				quote = 0
			} else {
				current.WriteRune(character)
			}
			started = true
			continue
		}
		if character == '\'' || character == '"' {
			quote = character
			started = true
			continue
		}
		if unicode.IsSpace(character) {
			if started {
				words = append(words, current.String())
				current.Reset()
				started = false
			}
			continue
		}
		current.WriteRune(character)
		started = true
	}
	if escaped {
		return nil, fmt.Errorf("command ends with an incomplete escape")
	}
	if quote != 0 {
		return nil, fmt.Errorf("command contains an unterminated quote")
	}
	if started {
		words = append(words, current.String())
	}
	return words, nil
}

func commandSuggestions(value string) []commandSpec {
	query := strings.TrimSpace(strings.TrimPrefix(cleanText(value), ":"))
	first := query
	if index := strings.IndexAny(first, " \t"); index >= 0 {
		first = first[:index]
	}
	matches := make([]commandSpec, 0, len(commandSpecs))
	for _, spec := range commandSpecs {
		if strings.HasPrefix(spec.name, strings.ToLower(first)) {
			matches = append(matches, spec)
		}
	}
	return matches
}
