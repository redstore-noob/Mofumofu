// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package config

import (
	"runtime"
	"testing"
)

func TestValidateInstanceIDRejectsUnportableNames(t *testing.T) {
	for _, id := range []string{"CON", "lpt1", "a:b", "survival.", "survival ", "..", "a|b"} {
		if err := validateInstanceID(id); err == nil {
			t.Errorf("accepted %q", id)
		}
	}
	for _, id := range []string{"survival", "survival-2", "main.v2"} {
		if err := validateInstanceID(id); err != nil {
			t.Errorf("%q rejected: %v", id, err)
		}
	}
}

func TestSamePathFollowsFileSystemCaseRules(t *testing.T) {
	folds := runtime.GOOS == "windows" || runtime.GOOS == "darwin"
	if got := samePath("/state/Events.jsonl", "/state/events.jsonl"); got != folds {
		t.Fatalf("samePath=%v on %s, want %v", got, runtime.GOOS, folds)
	}
}
