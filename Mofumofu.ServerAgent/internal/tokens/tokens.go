// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

// Package tokens provides Bearer-token authentication and per-token rate limiting.
package tokens

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"errors"
)

const (
	TokenRandomBytes = 32
	TokenHexLength   = TokenRandomBytes * 2

	ScopeRead      = "read"
	ScopePlayers   = "players"
	ScopeFiles     = "files"
	ScopeBackup    = "backup"
	ScopeControl   = "control"
	ScopeCommand   = "command"
	MaxTokenScopes = 6
)

var (
	ErrUnauthorized = errors.New("unauthorized")
	ErrRateLimited  = errors.New("rate_limited")
)

var validScopes = map[string]struct{}{
	ScopeRead:    {},
	ScopePlayers: {},
	ScopeFiles:   {},
	ScopeBackup:  {},
	ScopeControl: {},
	ScopeCommand: {},
}

// GenerateToken returns a cryptographically random 32-byte token as lowercase hex.
func GenerateToken() (string, error) {
	var random [TokenRandomBytes]byte
	if _, err := rand.Read(random[:]); err != nil {
		return "", err
	}
	return hex.EncodeToString(random[:]), nil
}

// TokenHash returns the lowercase hex SHA-256 of the UTF-8 token.
func TokenHash(token string) string {
	hash := sha256.Sum256([]byte(token))
	return hex.EncodeToString(hash[:])
}

// IsValidToken reports whether token is exactly 64 lowercase hexadecimal characters.
func IsValidToken(token string) bool {
	return isLowerHex(token, TokenHexLength)
}

func isLowerHex(value string, length int) bool {
	if len(value) != length {
		return false
	}
	for i := 0; i < len(value); i++ {
		if !((value[i] >= '0' && value[i] <= '9') || (value[i] >= 'a' && value[i] <= 'f')) {
			return false
		}
	}
	return true
}

func isAnyHex(value string, length int) bool {
	if len(value) != length {
		return false
	}
	for i := 0; i < len(value); i++ {
		if !((value[i] >= '0' && value[i] <= '9') ||
			(value[i] >= 'a' && value[i] <= 'f') ||
			(value[i] >= 'A' && value[i] <= 'F')) {
			return false
		}
	}
	return true
}
