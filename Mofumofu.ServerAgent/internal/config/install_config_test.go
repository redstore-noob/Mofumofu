// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package config

import (
	"path/filepath"
	"reflect"
	"testing"
)

func TestInstallSettingsDefaultToDisabled(t *testing.T) {
	cfg := Default()
	if cfg.InstallRootDirectory != "" || len(cfg.InstallAllowedCores) != 0 ||
		len(cfg.InstallAllowedMinecraftVersions) != 0 || cfg.InstallJavaPath != "" {
		t.Fatalf("install defaults are not disabled: %+v", cfg)
	}
}

func TestInstallSettingsValidation(t *testing.T) {
	cfg := validConfig(t)
	cfg.InstallRootDirectory = t.TempDir()
	cfg.InstallAllowedCores = []string{"paper", "neoforge"}
	cfg.InstallAllowedMinecraftVersions = []string{"1.21.*", "1.20.1"}
	if err := cfg.Validate(); err != nil {
		t.Fatalf("valid install settings rejected: %v", err)
	}

	mutations := map[string]func(*Config){
		"unknown core":        func(c *Config) { c.InstallAllowedCores = []string{"forge"} },
		"core case":           func(c *Config) { c.InstallAllowedCores = []string{"Paper"} },
		"bare wildcard":       func(c *Config) { c.InstallAllowedMinecraftVersions = []string{"*"} },
		"empty prefix":        func(c *Config) { c.InstallAllowedMinecraftVersions = []string{".*"} },
		"path-like version":   func(c *Config) { c.InstallAllowedMinecraftVersions = []string{"1.21/../x"} },
		"NUL in java path":    func(c *Config) { c.InstallJavaPath = "java\x00" },
		"NUL in install root": func(c *Config) { c.InstallRootDirectory = "bad\x00root" },
	}
	for name, mutate := range mutations {
		candidate := validConfig(t)
		candidate.InstallRootDirectory = t.TempDir()
		mutate(&candidate)
		if err := candidate.Validate(); err == nil {
			t.Fatalf("%s was accepted", name)
		}
	}
}

func TestInstallSettingsFromJSONAndEnvironment(t *testing.T) {
	root := t.TempDir()
	path := writeConfig(t, serverFields(validConfig(t)), map[string]any{
		"InstallRootDirectory":            root,
		"InstallAllowedCores":             []string{"paper"},
		"InstallAllowedMinecraftVersions": []string{"1.21.*"},
		"InstallJavaPath":                 "java-json",
	})
	cfg, err := Load(path, []string{
		"GOAGENT__INSTALLALLOWEDCORES= vanilla, fabric,",
		"GOAGENT__INSTALLJAVAPATH=java-env",
	}, nil)
	if err != nil {
		t.Fatalf("Load() error = %v", err)
	}
	absoluteRoot, err := filepath.Abs(root)
	if err != nil {
		t.Fatal(err)
	}
	if cfg.InstallRootDirectory != absoluteRoot {
		t.Fatalf("InstallRootDirectory = %q, want %q", cfg.InstallRootDirectory, absoluteRoot)
	}
	if !reflect.DeepEqual(cfg.InstallAllowedCores, []string{"vanilla", "fabric"}) {
		t.Fatalf("InstallAllowedCores = %v", cfg.InstallAllowedCores)
	}
	if !reflect.DeepEqual(cfg.InstallAllowedMinecraftVersions, []string{"1.21.*"}) {
		t.Fatalf("InstallAllowedMinecraftVersions = %v", cfg.InstallAllowedMinecraftVersions)
	}
	if cfg.InstallJavaPath != "java-env" {
		t.Fatalf("InstallJavaPath = %q, want the environment override", cfg.InstallJavaPath)
	}
}

func TestValidateInstanceIDExportsExistingRule(t *testing.T) {
	if err := ValidateInstanceID("survival"); err != nil {
		t.Fatalf("ValidateInstanceID(survival) = %v", err)
	}
	for _, id := range []string{"", ".", "..", "a/b", `a\b`, "x..y"} {
		if err := ValidateInstanceID(id); err == nil {
			t.Fatalf("ValidateInstanceID(%q) accepted an invalid ID", id)
		}
	}
}
