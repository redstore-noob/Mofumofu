// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package main

import "testing"

func TestValidateArguments(t *testing.T) {
	valid := [][]string{
		{"--config", "agent.json", "--urls", "http://127.0.0.1:17870", "--no-tui"},
		{"--CONFIG=agent.json", "--URLS=http://127.0.0.1:17870", "--tui"},
		{"--version"},
		{"--help"},
	}
	for _, args := range valid {
		if err := validateArguments(args); err != nil {
			t.Errorf("%v: %v", args, err)
		}
	}
	invalid := [][]string{
		{"--unknown"},
		{"--config"},
		{"--urls", "--no-tui"},
	}
	for _, args := range invalid {
		if err := validateArguments(args); err == nil {
			t.Errorf("accepted %v", args)
		}
	}
}

func TestRunRejectsConflictingTUIFlags(t *testing.T) {
	if err := run([]string{"--tui", "--no-tui"}, nil); err == nil {
		t.Fatal("conflicting TUI flags were accepted")
	}
}
