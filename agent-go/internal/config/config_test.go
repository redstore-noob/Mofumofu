// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package config

import (
	"encoding/json"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"
)

func TestLoadDefaults(t *testing.T) {
	original := validConfig(t)
	path := writeConfig(t, serverFields(original), nil)

	cfg, err := Load(path, nil, nil)
	if err != nil {
		t.Fatalf("Load() error = %v", err)
	}
	if cfg.ProbeBaseUrl != DefaultProbeBaseURL || cfg.BindUrl != DefaultBindURL {
		t.Fatalf("URL defaults = %q, %q", cfg.ProbeBaseUrl, cfg.BindUrl)
	}
	if cfg.EventJournalCapacity != DefaultEventJournalCapacity {
		t.Fatalf("EventJournalCapacity = %d", cfg.EventJournalCapacity)
	}
	if cfg.Mode != ModeManaged || !cfg.ProbeEnabled || cfg.PlayerDiffInterval != 0 {
		t.Fatalf("Go defaults = mode %q, probe %t, diff %v", cfg.Mode, cfg.ProbeEnabled, cfg.PlayerDiffInterval)
	}
	if cfg.ServerRootDirectory != filepath.Clean(original.ServerRootDirectory) {
		t.Fatalf("ServerRootDirectory = %q", cfg.ServerRootDirectory)
	}
	if cfg.InstanceId != "survival" {
		t.Fatalf("InstanceId = %q", cfg.InstanceId)
	}
	wantInstance := filepath.Join(cfg.ServerRootDirectory, cfg.InstanceId)
	if cfg.InstanceDirectory() != wantInstance {
		t.Fatalf("InstanceDirectory() = %q, want %q", cfg.InstanceDirectory(), wantInstance)
	}
	port, err := cfg.BindPort()
	if err != nil || port != 17870 {
		t.Fatalf("BindPort() = %d, %v", port, err)
	}
}

func TestLoadJSONGoExtensions(t *testing.T) {
	path := writeConfig(t, serverFields(validConfig(t)), map[string]any{
		"Mode":               "attach",
		"ProbeEnabled":       false,
		"PlayerDiffInterval": 2,
	})

	cfg, err := Load(path, nil, nil)
	if err != nil {
		t.Fatalf("Load() error = %v", err)
	}
	if cfg.Mode != ModeAttach || cfg.ProbeEnabled || cfg.PlayerDiffInterval != 2*time.Second {
		t.Fatalf("Go extensions = mode %q, probe %t, diff %v", cfg.Mode, cfg.ProbeEnabled, cfg.PlayerDiffInterval)
	}
}

func TestLoadPrecedenceAndEnvironmentOverrides(t *testing.T) {
	original := validConfig(t)
	server := serverFields(original)
	server["InstanceId"] = "json-instance"
	server["BindUrl"] = "http://127.0.0.1:19070"
	server["EventJournalCapacity"] = 32
	path := writeConfig(t, server, map[string]any{
		"Mode":               "attach",
		"ProbeEnabled":       false,
		"PlayerDiffInterval": 1,
	})
	environ := []string{
		"ServerAgent__InstanceId= env-instance ",
		"ServerAgent__BindUrl=http://127.0.0.1:19071",
		"ServerAgent__EventJournalCapacity=64",
		"GoAgent__Mode=managed",
		"GoAgent__ProbeEnabled=true",
		"GoAgent__PlayerDiffInterval=2",
		"UNRELATED=value",
	}

	cfg, err := Load(path, environ, []string{"--urls", "http://127.0.0.1:0"})
	if err != nil {
		t.Fatalf("Load() error = %v", err)
	}
	if cfg.InstanceId != "env-instance" {
		t.Fatalf("InstanceId = %q, want environment override", cfg.InstanceId)
	}
	if cfg.EventJournalCapacity != 64 {
		t.Fatalf("EventJournalCapacity = %d, want environment override", cfg.EventJournalCapacity)
	}
	if cfg.BindUrl != "http://127.0.0.1:0" {
		t.Fatalf("BindUrl = %q, want CLI override", cfg.BindUrl)
	}
	if cfg.Mode != ModeManaged || !cfg.ProbeEnabled || cfg.PlayerDiffInterval != 2*time.Second {
		t.Fatalf("Go environment overrides = mode %q, probe %t, diff %v", cfg.Mode, cfg.ProbeEnabled, cfg.PlayerDiffInterval)
	}
}

func TestLoadCLIOptionForms(t *testing.T) {
	path := writeConfig(t, serverFields(validConfig(t)), nil)
	tests := []struct {
		name    string
		args    []string
		wantURL string
	}{
		{name: "separate", args: []string{"--config", path, "--urls", "http://127.0.0.1:20070"}, wantURL: "http://127.0.0.1:20070"},
		{name: "inline", args: []string{"--config=" + path, "--urls=http://127.0.0.1:20071"}, wantURL: "http://127.0.0.1:20071"},
		{name: "mixed", args: []string{"--config=" + path, "--urls", "http://127.0.0.1:20072"}, wantURL: "http://127.0.0.1:20072"},
		{name: "case insensitive", args: []string{"--CONFIG", path, "--URLS=http://127.0.0.1:20073"}, wantURL: "http://127.0.0.1:20073"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			cfg, err := Load("", nil, test.args)
			if err != nil {
				t.Fatalf("Load() error = %v", err)
			}
			if cfg.BindUrl != test.wantURL {
				t.Fatalf("BindUrl = %q, want %q", cfg.BindUrl, test.wantURL)
			}
		})
	}

	cfg, err := Load(filepath.Join(t.TempDir(), "missing.json"), nil, []string{"--config", path})
	if err != nil {
		t.Fatalf("CLI config should override configPath: %v", err)
	}
	if cfg.InstanceId != "survival" {
		t.Fatalf("InstanceId = %q", cfg.InstanceId)
	}

	cfg, err = Load(path, nil, []string{"--urls=http://127.0.0.1:20074", "--urls", "http://127.0.0.1:20075"})
	if err != nil {
		t.Fatalf("Load() error = %v", err)
	}
	if cfg.BindUrl != "http://127.0.0.1:20075" {
		t.Fatalf("last --urls should win, got %q", cfg.BindUrl)
	}
}

func TestLoadRequiresConfigAndOptionValues(t *testing.T) {
	validPath := writeConfig(t, serverFields(validConfig(t)), nil)
	tests := []struct {
		name string
		path string
		args []string
	}{
		{name: "no config"},
		{name: "missing separate value", args: []string{"--config"}},
		{name: "empty inline config", args: []string{"--config="}},
		{name: "blank config", args: []string{"--config", "   "}},
		{name: "option instead of config", args: []string{"--config", "--urls", "http://127.0.0.1:1"}},
		{name: "urls without config", args: []string{"--urls=http://127.0.0.1:1"}},
		{name: "missing urls value", path: validPath, args: []string{"--urls"}},
		{name: "empty urls", path: validPath, args: []string{"--urls="}},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			if _, err := Load(test.path, nil, test.args); err == nil {
				t.Fatal("Load() unexpectedly succeeded")
			}
		})
	}
}

func TestLoadRejectsMalformedDocuments(t *testing.T) {
	for _, content := range []string{"", "null", "{", "[]", `{"ServerAgent":null}`, `{"GoAgent":null}`} {
		t.Run(content, func(t *testing.T) {
			path := writeRawConfig(t, content)
			if _, err := Load(path, nil, nil); err == nil {
				t.Fatal("Load() unexpectedly succeeded")
			}
		})
	}
}

func TestLoadRejectsEmptyAndNullValues(t *testing.T) {
	tests := []struct {
		name      string
		serverKey string
		goKey     string
		value     any
	}{
		{name: "root empty", serverKey: "ServerRootDirectory", value: ""},
		{name: "instance blank", serverKey: "InstanceId", value: "   "},
		{name: "registry empty", serverKey: "ProbeRegistryPath", value: ""},
		{name: "probe URL empty", serverKey: "ProbeBaseUrl", value: ""},
		{name: "bind URL empty", serverKey: "BindUrl", value: ""},
		{name: "journal empty", serverKey: "EventJournalPath", value: ""},
		{name: "audit empty", serverKey: "AuditPath", value: ""},
		{name: "capacity null", serverKey: "EventJournalCapacity", value: nil},
		{name: "mode empty", goKey: "Mode", value: ""},
		{name: "probe enabled null", goKey: "ProbeEnabled", value: nil},
		{name: "player diff null", goKey: "PlayerDiffInterval", value: nil},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			server := serverFields(validConfig(t))
			var goAgent map[string]any
			if test.serverKey != "" {
				server[test.serverKey] = test.value
			}
			if test.goKey != "" {
				goAgent = map[string]any{test.goKey: test.value}
			}
			path := writeConfig(t, server, goAgent)
			if _, err := Load(path, nil, nil); err == nil {
				t.Fatal("Load() unexpectedly succeeded")
			}
		})
	}
}

func TestLoadRejectsMissingRequiredFields(t *testing.T) {
	for _, field := range []string{
		"ServerRootDirectory", "InstanceId", "ProbeRegistryPath", "EventJournalPath", "AuditPath",
	} {
		t.Run(field, func(t *testing.T) {
			server := serverFields(validConfig(t))
			delete(server, field)
			path := writeConfig(t, server, nil)
			if _, err := Load(path, nil, nil); err == nil {
				t.Fatal("Load() unexpectedly succeeded")
			}
		})
	}
}

func TestEnvironmentCanSupplyRequiredFields(t *testing.T) {
	path := writeConfig(t, map[string]any{}, nil)
	original := validConfig(t)
	environ := []string{
		"ServerAgent__ServerRootDirectory=" + original.ServerRootDirectory,
		"ServerAgent__InstanceId=survival",
		"ServerAgent__ProbeRegistryPath=" + original.ProbeRegistryPath,
		"ServerAgent__EventJournalPath=" + original.EventJournalPath,
		"ServerAgent__AuditPath=" + original.AuditPath,
	}
	cfg, err := Load(path, environ, nil)
	if err != nil {
		t.Fatalf("Load() error = %v", err)
	}
	if cfg.InstanceId != "survival" {
		t.Fatalf("InstanceId = %q", cfg.InstanceId)
	}
}

func TestLoadRejectsInvalidEnvironmentValues(t *testing.T) {
	path := writeConfig(t, serverFields(validConfig(t)), nil)
	for _, entry := range []string{
		"ServerAgent__InstanceId=",
		"ServerAgent__EventJournalCapacity=",
		"ServerAgent__EventJournalCapacity=not-an-int",
		"GoAgent__Mode=unknown",
		"GoAgent__ProbeEnabled=",
		"GoAgent__PlayerDiffInterval=",
		"GoAgent__PlayerDiffInterval=1.5",
		"GoAgent__PlayerDiffInterval=-1",
		"GoAgent__PlayerDiffInterval=-9223372036854775808",
		"GoAgent__PlayerDiffInterval=9223372036854775807",
	} {
		t.Run(entry, func(t *testing.T) {
			if _, err := Load(path, []string{entry}, nil); err == nil {
				t.Fatal("Load() unexpectedly succeeded")
			}
		})
	}
}

func TestInstanceIDValidation(t *testing.T) {
	for _, instanceID := range []string{"", ".", "..", "server..backup", "a/b", `a\b`, "a\x00b", " \t\n "} {
		t.Run(instanceID, func(t *testing.T) {
			cfg := validConfig(t)
			cfg.InstanceId = instanceID
			if err := cfg.Validate(); err == nil {
				t.Fatal("Validate() unexpectedly succeeded")
			}
		})
	}
	cfg := validConfig(t)
	cfg.InstanceId = "  survival  "
	if err := cfg.NormalizeAndValidate(); err != nil {
		t.Fatalf("NormalizeAndValidate() error = %v", err)
	}
	if cfg.InstanceId != "survival" {
		t.Fatalf("InstanceId = %q, want trimmed value", cfg.InstanceId)
	}
}

func TestPathNormalizationAndInstanceDirectory(t *testing.T) {
	cfg := validConfig(t)
	base := filepath.Dir(cfg.ServerRootDirectory)
	separator := string(filepath.Separator)
	cfg.ServerRootDirectory = base + separator + "unused" + separator + ".." + separator + "servers"
	cfg.ProbeRegistryPath = filepath.Dir(cfg.ProbeRegistryPath) + separator + "unused" + separator + ".." + separator + filepath.Base(cfg.ProbeRegistryPath)
	cfg.EventJournalPath = filepath.Dir(cfg.EventJournalPath) + separator + "unused" + separator + ".." + separator + filepath.Base(cfg.EventJournalPath)
	cfg.AuditPath = filepath.Dir(cfg.AuditPath) + separator + "unused" + separator + ".." + separator + filepath.Base(cfg.AuditPath)
	cfg.InstanceId = "  survival  "

	if err := cfg.NormalizeAndValidate(); err != nil {
		t.Fatalf("NormalizeAndValidate() error = %v", err)
	}
	if cfg.ServerRootDirectory != filepath.Join(base, "servers") {
		t.Fatalf("ServerRootDirectory = %q", cfg.ServerRootDirectory)
	}
	if cfg.ProbeRegistryPath != filepath.Join(base, "state", "registry.json") ||
		cfg.EventJournalPath != filepath.Join(base, "state", "events.jsonl") ||
		cfg.AuditPath != filepath.Join(base, "state", "audit.jsonl") {
		t.Fatalf("normalized state paths = %q, %q, %q", cfg.ProbeRegistryPath, cfg.EventJournalPath, cfg.AuditPath)
	}
	if cfg.InstanceDirectory() != filepath.Join(base, "servers", "survival") {
		t.Fatalf("InstanceDirectory() = %q", cfg.InstanceDirectory())
	}
}

func TestFilePathsRejectDirectoryRoots(t *testing.T) {
	volumeRoot := filepath.VolumeName(t.TempDir()) + string(filepath.Separator)
	for _, field := range []string{"registry", "journal", "audit"} {
		t.Run(field, func(t *testing.T) {
			cfg := validConfig(t)
			switch field {
			case "registry":
				cfg.ProbeRegistryPath = volumeRoot
			case "journal":
				cfg.EventJournalPath = volumeRoot
			case "audit":
				cfg.AuditPath = volumeRoot
			}
			if err := cfg.Validate(); err == nil {
				t.Fatal("Validate() unexpectedly succeeded")
			}
		})
	}
}

func TestStatePathOverlap(t *testing.T) {
	tests := []struct {
		name   string
		mutate func(*Config)
	}{
		{name: "registry equals journal", mutate: func(c *Config) { c.ProbeRegistryPath = c.EventJournalPath }},
		{name: "registry equals audit", mutate: func(c *Config) { c.ProbeRegistryPath = c.AuditPath }},
		{name: "journal equals audit", mutate: func(c *Config) { c.EventJournalPath = c.AuditPath }},
		{name: "clean paths overlap", mutate: func(c *Config) {
			separator := string(filepath.Separator)
			directory := filepath.Dir(c.AuditPath)
			c.EventJournalPath = directory + separator + "unused" + separator + ".." + separator + filepath.Base(c.AuditPath)
		}},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			cfg := validConfig(t)
			test.mutate(&cfg)
			if err := cfg.Validate(); err == nil {
				t.Fatal("Validate() unexpectedly succeeded")
			}
		})
	}

	if runtime.GOOS == "windows" {
		cfg := validConfig(t)
		cfg.AuditPath = strings.ToUpper(cfg.EventJournalPath)
		if err := cfg.Validate(); err == nil {
			t.Fatal("case-insensitive overlapping paths should fail on Windows")
		}
	}
}

func TestLexicalRootPrefix(t *testing.T) {
	root := filepath.Join(t.TempDir(), "servers")
	separator := string(filepath.Separator)
	if !hasLexicalRootPrefix(root, root+separator+"survival") {
		t.Fatal("child path should be inside root")
	}
	for _, path := range []string{
		root,
		root + "-other" + separator + "survival",
		filepath.Dir(root) + separator + "servers2" + separator + "survival",
	} {
		if hasLexicalRootPrefix(root, path) {
			t.Fatalf("path %q should not be inside %q", path, root)
		}
	}
	volumeRoot := filepath.VolumeName(root) + separator
	if !hasLexicalRootPrefix(volumeRoot, volumeRoot+"survival") {
		t.Fatal("volume-root child should be inside root")
	}
}

func TestEventJournalCapacity(t *testing.T) {
	for _, capacity := range []int{MinEventJournalCapacity, DefaultEventJournalCapacity, MaxEventJournalCapacity} {
		cfg := validConfig(t)
		cfg.EventJournalCapacity = capacity
		if err := cfg.Validate(); err != nil {
			t.Fatalf("capacity %d rejected: %v", capacity, err)
		}
	}
	for _, capacity := range []int{-1, 0, MinEventJournalCapacity - 1, MaxEventJournalCapacity + 1} {
		cfg := validConfig(t)
		cfg.EventJournalCapacity = capacity
		if err := cfg.Validate(); err == nil {
			t.Fatalf("capacity %d unexpectedly accepted", capacity)
		}
	}
}

func TestBindURLValidation(t *testing.T) {
	valid := []string{
		"http://127.0.0.1:17870",
		"https://127.0.0.1",
		"http://127.0.0.1:0/",
		"HTTP://127.0.0.1:80",
	}
	for _, value := range valid {
		t.Run("valid "+value, func(t *testing.T) {
			cfg := validConfig(t)
			cfg.BindUrl = value
			if err := cfg.Validate(); err != nil {
				t.Fatalf("Validate() error = %v", err)
			}
		})
	}

	invalid := []string{
		"",
		"127.0.0.1:17870",
		"ftp://127.0.0.1:17870",
		"http://localhost:17870",
		"http://127.0.0.2:17870",
		"http://[::1]:17870",
		"http://[::ffff:127.0.0.1]:17870",
		"http://0.0.0.0:17870",
		"http://user@127.0.0.1:17870",
		"http://@127.0.0.1:17870",
		"http://127.0.0.1:17870?key=value",
		"http://127.0.0.1:17870?",
		"http://127.0.0.1:17870#fragment",
		"http://127.0.0.1:17870#",
		"http://127.0.0.1:17870/path",
		"http://127.0.0.1:17870//",
		"http://127.0.0.1:17870/%2F",
		"http://127.0.0.1:65536",
		"http://127.0.0.1:",
		"http://127.0.0.1:not-a-port",
		"http:127.0.0.1:17870",
		"http:///127.0.0.1:17870",
	}
	for _, value := range invalid {
		t.Run("invalid "+value, func(t *testing.T) {
			cfg := validConfig(t)
			cfg.BindUrl = value
			if err := cfg.Validate(); err == nil {
				t.Fatal("Validate() unexpectedly succeeded")
			}
		})
	}
}

func TestBindPort(t *testing.T) {
	for _, test := range []struct {
		url  string
		port int
	}{
		{url: "http://127.0.0.1", port: 80},
		{url: "https://127.0.0.1", port: 443},
		{url: "http://127.0.0.1:0", port: 0},
		{url: "http://127.0.0.1:28766", port: 28766},
	} {
		cfg := validConfig(t)
		cfg.BindUrl = test.url
		port, err := cfg.BindPort()
		if err != nil || port != test.port {
			t.Fatalf("BindPort(%q) = %d, %v; want %d", test.url, port, err, test.port)
		}
	}
}

func TestProbeBaseURLValidation(t *testing.T) {
	valid := []string{
		"http://localhost:17871",
		"http://LOCALHOST:17871/internal/probe",
		"http://127.0.0.2:17871",
		"http://127.255.255.255:17871/path",
		"http://[::1]:17871/path",
		"https://[::ffff:127.0.0.1]:17871/path",
	}
	for _, value := range valid {
		t.Run("valid "+value, func(t *testing.T) {
			cfg := validConfig(t)
			cfg.ProbeBaseUrl = value
			if err := cfg.Validate(); err != nil {
				t.Fatalf("Validate() error = %v", err)
			}
		})
	}

	invalid := []string{
		"",
		"localhost:17871",
		"ftp://localhost:17871",
		"http://example.com:17871",
		"http://localhost.example.com:17871",
		"http://0.0.0.0:17871",
		"http://[::]:17871",
		"http://[fe80::1]:17871",
		"http://user@localhost:17871",
		"http://localhost:17871?key=value",
		"http://localhost:17871#fragment",
		"http://localhost:65536",
	}
	for _, value := range invalid {
		t.Run("invalid "+value, func(t *testing.T) {
			cfg := validConfig(t)
			cfg.ProbeBaseUrl = value
			if err := cfg.Validate(); err == nil {
				t.Fatal("Validate() unexpectedly succeeded")
			}
		})
	}
}

func TestModeAndPlayerDiffInterval(t *testing.T) {
	for _, mode := range []Mode{ModeManaged, ModeAttach} {
		cfg := validConfig(t)
		cfg.Mode = mode
		if err := cfg.Validate(); err != nil {
			t.Fatalf("mode %q rejected: %v", mode, err)
		}
	}
	for _, mode := range []Mode{"", "unknown", "MANAGED"} {
		cfg := validConfig(t)
		cfg.Mode = mode
		if err := cfg.Validate(); err == nil {
			t.Fatalf("mode %q unexpectedly accepted", mode)
		}
	}

	for _, interval := range []time.Duration{0, time.Second, RecommendedMaxPlayerDiffInterval, 301 * time.Second} {
		cfg := validConfig(t)
		cfg.PlayerDiffInterval = interval
		if err := cfg.Validate(); err != nil {
			t.Fatalf("interval %v rejected: %v", interval, err)
		}
	}
	cfg := validConfig(t)
	cfg.PlayerDiffInterval = -time.Nanosecond
	if err := cfg.Validate(); err == nil {
		t.Fatal("negative player diff interval unexpectedly accepted")
	}
}

func validConfig(t *testing.T) Config {
	t.Helper()
	base := t.TempDir()
	cfg := Default()
	cfg.ServerRootDirectory = filepath.Join(base, "servers")
	cfg.InstanceId = "survival"
	cfg.ProbeRegistryPath = filepath.Join(base, "state", "registry.json")
	cfg.EventJournalPath = filepath.Join(base, "state", "events.jsonl")
	cfg.AuditPath = filepath.Join(base, "state", "audit.jsonl")
	return cfg
}

func serverFields(cfg Config) map[string]any {
	return map[string]any{
		"ServerRootDirectory":  cfg.ServerRootDirectory,
		"InstanceId":           cfg.InstanceId,
		"ProbeRegistryPath":    cfg.ProbeRegistryPath,
		"ProbeBaseUrl":         cfg.ProbeBaseUrl,
		"BindUrl":              cfg.BindUrl,
		"EventJournalPath":     cfg.EventJournalPath,
		"AuditPath":            cfg.AuditPath,
		"EventJournalCapacity": cfg.EventJournalCapacity,
	}
}

func writeConfig(t *testing.T, server map[string]any, goAgent map[string]any) string {
	t.Helper()
	document := map[string]any{"ServerAgent": server}
	if goAgent != nil {
		document["GoAgent"] = goAgent
	}
	data, err := json.Marshal(document)
	if err != nil {
		t.Fatalf("json.Marshal() error = %v", err)
	}
	return writeRawConfig(t, string(data))
}

func writeRawConfig(t *testing.T, content string) string {
	t.Helper()
	path := filepath.Join(t.TempDir(), "agent.json")
	if err := os.WriteFile(path, []byte(content), 0o600); err != nil {
		t.Fatalf("os.WriteFile() error = %v", err)
	}
	return path
}
