// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package tokens

import (
	"bytes"
	"crypto/subtle"
	"encoding/hex"
	"encoding/json"
	"io"
	"os"
	"strings"
	"time"
	"unicode"
	"unicode/utf16"
)

const (
	SupportedSchemaVersion = 1
	MaxRegistryBytes       = 4 << 20
	MaxTokenIDLength       = 128
)

// RegistrySnapshot is the on-disk token registry envelope.
type RegistrySnapshot struct {
	SchemaVersion int                  `json:"schemaVersion"`
	InstanceID    string               `json:"instanceId"`
	Generation    int                  `json:"generation"`
	LinkSecret    string               `json:"linkSecret"`
	Tokens        []TokenRegistryToken `json:"tokens"`

	linkSecretBytes []byte
}

// TokenRegistryToken is one token entry in a RegistrySnapshot.
type TokenRegistryToken struct {
	TokenID   string     `json:"tokenId"`
	Hash      string     `json:"hash"`
	Scopes    []string   `json:"scopes"`
	CreatedAt time.Time  `json:"createdAt"`
	NotAfter  *time.Time `json:"notAfter"`
	Revoked   bool       `json:"revoked"`
	Root      bool       `json:"root"`
}

func (s *RegistrySnapshot) UnmarshalJSON(data []byte) error {
	type alias RegistrySnapshot
	decoded := alias{Tokens: []TokenRegistryToken{}}
	if err := json.Unmarshal(data, &decoded); err != nil {
		return err
	}
	*s = RegistrySnapshot(decoded)
	return nil
}

func (t *TokenRegistryToken) UnmarshalJSON(data []byte) error {
	type alias TokenRegistryToken
	decoded := alias{Scopes: []string{}}
	if err := json.Unmarshal(data, &decoded); err != nil {
		return err
	}
	*t = TokenRegistryToken(decoded)
	return nil
}

// AuthContext is the authenticated identity attached to a request.
type AuthContext struct {
	TokenID            string
	Scopes             map[string]struct{}
	IsRoot             bool
	Authorization      string
	RegistryGeneration int
}

// HasScope reports whether the token was granted scope. Root does not imply any scope.
func (context AuthContext) HasScope(scope string) bool {
	_, ok := context.Scopes[scope]
	return ok
}

// LinkSecretBytes returns a copy of the decoded registry link secret.
func (snapshot RegistrySnapshot) LinkSecretBytes() []byte {
	return bytes.Clone(snapshot.linkSecretBytes)
}

// LoadSnapshot reads and validates a registry for instanceID. Every registry or
// validation failure is reported as ErrUnauthorized.
func LoadSnapshot(path, instanceID string) (*RegistrySnapshot, error) {
	file, err := os.Open(path)
	if err != nil {
		return nil, ErrUnauthorized
	}
	defer file.Close()

	info, err := file.Stat()
	if err != nil || info.Size() > MaxRegistryBytes {
		return nil, ErrUnauthorized
	}

	data, err := io.ReadAll(io.LimitReader(file, MaxRegistryBytes+1))
	if err != nil || len(data) > MaxRegistryBytes {
		return nil, ErrUnauthorized
	}

	var snapshot *RegistrySnapshot
	if err := json.Unmarshal(data, &snapshot); err != nil || snapshot == nil {
		return nil, ErrUnauthorized
	}

	linkSecretBytes, valid := validateSnapshot(snapshot, instanceID)
	if !valid {
		return nil, ErrUnauthorized
	}
	snapshot.linkSecretBytes = linkSecretBytes
	return snapshot, nil
}

// TokenRegistry authenticates Bearer tokens against a registry file.
type TokenRegistry struct {
	path string
	now  func() time.Time
}

// NewTokenRegistry creates a registry reader. A nil clock uses time.Now.
func NewTokenRegistry(path string, now func() time.Time) *TokenRegistry {
	if now == nil {
		now = time.Now
	}
	return &TokenRegistry{path: path, now: now}
}

// LoadSnapshot reloads and validates the registry for instanceID.
func (registry *TokenRegistry) LoadSnapshot(instanceID string) (*RegistrySnapshot, error) {
	return LoadSnapshot(registry.path, instanceID)
}

// Authenticate validates authorization and reloads the registry on every call.
func (registry *TokenRegistry) Authenticate(authorization, instanceID string) (*AuthContext, error) {
	token, valid := parseBearer(authorization)
	if !valid {
		return nil, ErrUnauthorized
	}

	snapshot, err := LoadSnapshot(registry.path, instanceID)
	if err != nil {
		return nil, ErrUnauthorized
	}

	hash := TokenHash(token)
	var matched *TokenRegistryToken
	for i := range snapshot.Tokens {
		candidate := &snapshot.Tokens[i]
		if subtle.ConstantTimeCompare([]byte(hash), []byte(candidate.Hash)) == 1 {
			matched = candidate
		}
	}

	if matched == nil || matched.Revoked ||
		(matched.NotAfter != nil && !matched.NotAfter.After(registry.now())) {
		return nil, ErrUnauthorized
	}

	scopes := make(map[string]struct{}, len(matched.Scopes))
	for _, scope := range matched.Scopes {
		scopes[scope] = struct{}{}
	}
	return &AuthContext{
		TokenID:            matched.TokenID,
		Scopes:             scopes,
		IsRoot:             matched.Root,
		Authorization:      authorization,
		RegistryGeneration: snapshot.Generation,
	}, nil
}

func parseBearer(authorization string) (string, bool) {
	const prefix = "Bearer "
	if len(authorization) != len(prefix)+TokenHexLength ||
		!strings.HasPrefix(authorization, prefix) {
		return "", false
	}
	token := authorization[len(prefix):]
	return token, IsValidToken(token)
}

func validateSnapshot(snapshot *RegistrySnapshot, instanceID string) ([]byte, bool) {
	if snapshot.SchemaVersion != SupportedSchemaVersion ||
		snapshot.Generation < 0 ||
		snapshot.InstanceID != instanceID ||
		!isAnyHex(snapshot.LinkSecret, TokenHexLength) ||
		snapshot.Tokens == nil {
		return nil, false
	}

	linkSecretBytes, err := hex.DecodeString(snapshot.LinkSecret)
	if err != nil {
		return nil, false
	}

	tokenIDs := make(map[string]struct{}, len(snapshot.Tokens))
	hashes := make(map[string]struct{}, len(snapshot.Tokens))
	for _, token := range snapshot.Tokens {
		if strings.TrimSpace(token.TokenID) == "" ||
			utf16Length(token.TokenID) > MaxTokenIDLength ||
			hasControl(token.TokenID) {
			return nil, false
		}
		if _, exists := tokenIDs[token.TokenID]; exists {
			return nil, false
		}
		tokenIDs[token.TokenID] = struct{}{}

		if !isLowerHex(token.Hash, TokenHexLength) {
			return nil, false
		}
		if _, exists := hashes[token.Hash]; exists {
			return nil, false
		}
		hashes[token.Hash] = struct{}{}

		if !hasZeroOffset(token.CreatedAt) ||
			(token.NotAfter != nil && !hasZeroOffset(*token.NotAfter)) ||
			token.Scopes == nil || len(token.Scopes) > MaxTokenScopes {
			return nil, false
		}

		scopes := make(map[string]struct{}, len(token.Scopes))
		for _, scope := range token.Scopes {
			if _, allowed := validScopes[scope]; !allowed {
				return nil, false
			}
			if _, exists := scopes[scope]; exists {
				return nil, false
			}
			scopes[scope] = struct{}{}
		}
	}
	return linkSecretBytes, true
}

func utf16Length(value string) int {
	return len(utf16.Encode([]rune(value)))
}

func hasZeroOffset(value time.Time) bool {
	_, offset := value.Zone()
	return offset == 0
}

func hasControl(value string) bool {
	for _, character := range value {
		if unicode.IsControl(character) {
			return true
		}
	}
	return false
}
