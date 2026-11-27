// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package tokens

import (
	"errors"
	"fmt"
	"testing"
	"time"
)

type manualClock struct {
	now time.Time
}

func (clock *manualClock) Now() time.Time {
	return clock.now
}

func TestRateLimiterBoundariesAndRejectedRequestsDoNotIncrement(t *testing.T) {
	tests := []struct {
		name     string
		category RateCategory
		limit    int
	}{
		{name: "read", category: CategoryRead, limit: 20},
		{name: "write", category: CategoryWrite, limit: 5},
		{name: "sensitive", category: CategorySensitive, limit: 1},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			clock := &manualClock{now: time.Unix(100, 0)}
			limiter := NewRateLimiter(clock.Now)
			key := rateKey{tokenID: "token", category: test.category}

			for i := 0; i < test.limit; i++ {
				if err := limiter.Check(key.tokenID, key.category); err != nil {
					t.Fatalf("request %d: %v", i+1, err)
				}
			}
			window := limiter.windows[key]
			if window.count != test.limit {
				t.Fatalf("window count = %d, want %d", window.count, test.limit)
			}

			for i := 0; i < 3; i++ {
				err := limiter.Check(key.tokenID, key.category)
				if !errors.Is(err, ErrRateLimited) {
					t.Fatalf("over-limit error = %v, want ErrRateLimited", err)
				}
			}
			if got := limiter.windows[key].count; got != test.limit {
				t.Fatalf("rejected requests changed count to %d, want %d", got, test.limit)
			}
		})
	}
}

func TestRateLimiterSeparatesTokensAndCategories(t *testing.T) {
	clock := &manualClock{now: time.Unix(100, 0)}
	limiter := NewRateLimiter(clock.Now)

	if err := limiter.Check("first", CategorySensitive); err != nil {
		t.Fatalf("first sensitive request: %v", err)
	}
	if err := limiter.Check("first", CategorySensitive); !errors.Is(err, ErrRateLimited) {
		t.Fatalf("second sensitive request = %v, want ErrRateLimited", err)
	}
	if err := limiter.Check("first", CategoryRead); err != nil {
		t.Fatalf("read category should be independent: %v", err)
	}
	if err := limiter.Check("second", CategorySensitive); err != nil {
		t.Fatalf("second token should be independent: %v", err)
	}
}

func TestRateLimiterUsesUnixSeconds(t *testing.T) {
	clock := &manualClock{now: time.Unix(100, 999_999_999)}
	limiter := NewRateLimiter(clock.Now)

	if err := limiter.Check("token", CategorySensitive); err != nil {
		t.Fatalf("first request: %v", err)
	}
	if err := limiter.Check("token", CategorySensitive); !errors.Is(err, ErrRateLimited) {
		t.Fatalf("same-second request = %v, want ErrRateLimited", err)
	}

	clock.now = time.Unix(101, 0)
	if err := limiter.Check("token", CategorySensitive); err != nil {
		t.Fatalf("next Unix second should reset the window: %v", err)
	}
}

func TestRateLimiterCleansWindowsOlderThanCurrentMinusTwo(t *testing.T) {
	clock := &manualClock{now: time.Unix(100, 0)}
	limiter := NewRateLimiter(clock.Now)
	for i := 0; i <= maxWindows; i++ {
		if err := limiter.Check(fmt.Sprintf("token-%d", i), CategoryRead); err != nil {
			t.Fatalf("populate window %d: %v", i, err)
		}
	}
	if got := len(limiter.windows); got != maxWindows+1 {
		t.Fatalf("recent windows were pruned: got %d, want %d", got, maxWindows+1)
	}

	clock.now = time.Unix(101, 0)
	if err := limiter.Check("at-101", CategoryRead); err != nil {
		t.Fatalf("request at 101: %v", err)
	}
	if got := len(limiter.windows); got != maxWindows+2 {
		t.Fatalf("windows at current-2 must remain: got %d", got)
	}

	clock.now = time.Unix(102, 0)
	if err := limiter.Check("at-102", CategoryRead); err != nil {
		t.Fatalf("request at 102: %v", err)
	}
	if got := len(limiter.windows); got != maxWindows+3 {
		t.Fatalf("windows at current-2 must remain: got %d", got)
	}

	clock.now = time.Unix(103, 0)
	if err := limiter.Check("at-103", CategoryRead); err != nil {
		t.Fatalf("request at 103: %v", err)
	}
	if got := len(limiter.windows); got != 3 {
		t.Fatalf("old windows were not pruned: got %d, want 3", got)
	}
	for _, tokenID := range []string{"at-101", "at-102", "at-103"} {
		if _, exists := limiter.windows[rateKey{tokenID: tokenID, category: CategoryRead}]; !exists {
			t.Fatalf("window for %q should have been retained", tokenID)
		}
	}
}

func TestRateLimiterRejectsUnknownCategory(t *testing.T) {
	limiter := NewRateLimiter(nil)
	if err := limiter.Check("token", RateCategory(255)); !errors.Is(err, ErrInvalidRateCategory) {
		t.Fatalf("error = %v, want ErrInvalidRateCategory", err)
	}
	if len(limiter.windows) != 0 {
		t.Fatal("invalid category created a rate window")
	}
}
