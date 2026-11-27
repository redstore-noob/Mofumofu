// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package tokens

import (
	"errors"
	"sync"
	"time"
)

// RateCategory identifies the rate-limit policy for a request.
type RateCategory uint8

const (
	CategoryRead RateCategory = iota
	CategoryWrite
	CategorySensitive

	readLimit      = 20
	writeLimit     = 5
	sensitiveLimit = 1
	maxWindows     = 4096
)

var ErrInvalidRateCategory = errors.New("invalid rate category")

type rateKey struct {
	tokenID  string
	category RateCategory
}

type rateWindow struct {
	second int64
	count  int
}

// RateLimiter applies independent per-token, per-category Unix-second windows.
type RateLimiter struct {
	mu      sync.Mutex
	now     func() time.Time
	windows map[rateKey]rateWindow
}

// NewRateLimiter creates a limiter. A nil clock uses time.Now.
func NewRateLimiter(now func() time.Time) *RateLimiter {
	if now == nil {
		now = time.Now
	}
	return &RateLimiter{now: now, windows: make(map[rateKey]rateWindow)}
}

// Check consumes one request slot. A rejected request does not increment its window.
func (limiter *RateLimiter) Check(tokenID string, category RateCategory) error {
	limit, valid := rateLimit(category)
	if !valid {
		return ErrInvalidRateCategory
	}

	second := limiter.now().Unix()
	key := rateKey{tokenID: tokenID, category: category}

	limiter.mu.Lock()
	defer limiter.mu.Unlock()

	window, exists := limiter.windows[key]
	if !exists || window.second != second {
		window = rateWindow{second: second}
	}
	if window.count >= limit {
		limiter.pruneIfNeeded(second)
		return ErrRateLimited
	}

	window.count++
	limiter.windows[key] = window
	limiter.pruneIfNeeded(second)
	return nil
}

func rateLimit(category RateCategory) (int, bool) {
	switch category {
	case CategoryRead:
		return readLimit, true
	case CategoryWrite:
		return writeLimit, true
	case CategorySensitive:
		return sensitiveLimit, true
	default:
		return 0, false
	}
}

// pruneIfNeeded removes windows strictly older than current-2. The caller holds mu.
func (limiter *RateLimiter) pruneIfNeeded(current int64) {
	if len(limiter.windows) <= maxWindows {
		return
	}
	cutoff := current - 2
	for key, window := range limiter.windows {
		if window.second < cutoff {
			delete(limiter.windows, key)
		}
	}
}
