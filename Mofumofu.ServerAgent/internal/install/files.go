// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package install

import (
	"encoding/json"
	"errors"
	"io/fs"
	"os"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
)

// ServerConfig mirrors server.ServerConfig's on-disk server.json contract.
// It is duplicated here so the install package does not depend on the
// process-control package.
type ServerConfig struct {
	ID            string   `json:"id"`
	Name          string   `json:"name"`
	Core          string   `json:"core"`
	CoreVersion   string   `json:"coreVersion"`
	MCVersion     string   `json:"mcVersion"`
	Port          int      `json:"port"`
	MaxPlayers    int      `json:"maxPlayers"`
	JavaPath      string   `json:"javaPath"`
	MemoryMB      int      `json:"memoryMB"`
	MemoryMinMB   int      `json:"memoryMinMB"`
	ExtraJavaArgs []string `json:"extraJavaArgs"`
	AutoRestart   bool     `json:"autoRestart"`
}

// WriteEULA writes eula.txt with eula=true.
func WriteEULA(directory string) error {
	content := "#By changing the setting below to TRUE you are indicating your agreement to our EULA (" + EulaURL + ").\neula=true\n"
	return os.WriteFile(filepath.Join(directory, "eula.txt"), []byte(content), 0o644)
}

type propertyEntry struct {
	key   string
	value string
}

var propertyNewlines = strings.NewReplacer("\r\n", "\n", "\r", "\n")

// WriteInitialProperties keeps existing keys and adds only missing defaults.
// Keys and values are sanitized, and the output has a single LF line ending.
func WriteInitialProperties(directory string, port, maxPlayers int, motd string) error {
	path := filepath.Join(directory, "server.properties")
	var entries []propertyEntry
	index := make(map[string]int)
	existing, err := os.ReadFile(path)
	if err == nil {
		for _, line := range strings.Split(propertyNewlines.Replace(string(existing)), "\n") {
			if strings.HasPrefix(line, "#") || !strings.Contains(line, "=") {
				continue
			}
			separator := strings.Index(line, "=")
			key := strings.TrimSpace(line[:separator])
			value := line[separator+1:]
			if position, ok := index[key]; ok {
				entries[position].value = value
				continue
			}
			index[key] = len(entries)
			entries = append(entries, propertyEntry{key: key, value: value})
		}
	} else if !errors.Is(err, fs.ErrNotExist) {
		return failedf("could not read server.properties")
	}

	defaults := []propertyEntry{
		{key: "server-port", value: strconv.Itoa(port)},
		{key: "max-players", value: strconv.Itoa(maxPlayers)},
		{key: "motd", value: motd},
		{key: "online-mode", value: "true"},
	}
	for _, item := range defaults {
		if _, ok := index[item.key]; !ok {
			index[item.key] = len(entries)
			entries = append(entries, item)
		}
	}

	var builder strings.Builder
	builder.WriteString("#Minecraft server properties\n")
	for _, entry := range entries {
		builder.WriteString(SanitizePropertyValue(entry.key))
		builder.WriteByte('=')
		builder.WriteString(SanitizePropertyValue(entry.value))
		builder.WriteByte('\n')
	}
	return os.WriteFile(path, []byte(builder.String()), 0o644)
}

// WriteStartScript writes start.bat on Windows and start.sh elsewhere. Values
// are sanitized and then embedded in a quoted Java command.
func WriteStartScript(directory string, core Core, coreVersion, jarName, javaPath string) (string, error) {
	var launch string
	if core == CoreNeoForge {
		argsName := "unix_args.txt"
		if runtime.GOOS == "windows" {
			argsName = "win_args.txt"
		}
		launch = "@libraries/net/neoforged/neoforge/" + coreVersion + "/" + argsName + " nogui"
	} else {
		launch = "-jar " + SanitizeFileName(jarName) + " nogui"
	}
	launch, err := SanitizeScriptValue(launch)
	if err != nil {
		return "", err
	}
	java, err := SanitizeScriptValue(javaPath)
	if err != nil {
		return "", err
	}
	command := "\"" + java + "\" -Xms2G -Xmx4G " + launch

	if runtime.GOOS == "windows" {
		path := filepath.Join(directory, "start.bat")
		content := "@echo off\r\ncd /d \"%~dp0\"\r\n" + command + "\r\npause\r\n"
		if err := os.WriteFile(path, []byte(content), 0o644); err != nil {
			return "", failedf("could not write start.bat")
		}
		return path, nil
	}
	path := filepath.Join(directory, "start.sh")
	content := "#!/bin/sh\ncd \"$(dirname \"$0\")\"\n" + command + "\n"
	if err := os.WriteFile(path, []byte(content), 0o755); err != nil {
		return "", failedf("could not write start.sh")
	}
	_ = os.Chmod(path, 0o755)
	return path, nil
}

// WriteServerConfig writes server.json in the on-disk ServerConfig shape.
func WriteServerConfig(directory string, cfg ServerConfig) error {
	payload, err := json.MarshalIndent(cfg, "", "  ")
	if err != nil {
		return failedf("could not encode server.json")
	}
	if err := os.WriteFile(filepath.Join(directory, "server.json"), append(payload, '\n'), 0o644); err != nil {
		return failedf("could not write server.json")
	}
	return nil
}
