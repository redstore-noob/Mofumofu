// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package tokens

import (
	"bytes"
	"encoding/hex"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

const (
	testInstanceID = "survival"
	testTokenA     = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
	testTokenB     = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
)

func validSnapshot() *RegistrySnapshot {
	createdAt := time.Date(2025, 12, 31, 23, 0, 0, 0, time.UTC)
	notAfter := time.Date(2026, 1, 2, 0, 0, 0, 0, time.UTC)
	return &RegistrySnapshot{
		SchemaVersion: SupportedSchemaVersion,
		InstanceID:    testInstanceID,
		Generation:    7,
		LinkSecret:    strings.Repeat("aB", TokenRandomBytes),
		Tokens: []TokenRegistryToken{
			{
				TokenID:   "reader",
				Hash:      TokenHash(testTokenA),
				Scopes:    []string{ScopeRead, ScopeFiles},
				CreatedAt: createdAt,
				NotAfter:  &notAfter,
			},
			{
				TokenID:   "root-token",
				Hash:      TokenHash(testTokenB),
				Scopes:    []string{},
				CreatedAt: createdAt,
				Root:      true,
			},
		},
	}
}

func marshalSnapshot(t *testing.T, snapshot *RegistrySnapshot) []byte {
	t.Helper()
	data, err := json.Marshal(snapshot)
	if err != nil {
		t.Fatalf("marshal snapshot: %v", err)
	}
	return data
}

func writeRegistryJSON(t *testing.T, path string, data []byte) {
	t.Helper()
	if err := os.WriteFile(path, data, 0o600); err != nil {
		t.Fatalf("write registry: %v", err)
	}
}

func writeSnapshot(t *testing.T, path string, snapshot *RegistrySnapshot) {
	t.Helper()
	writeRegistryJSON(t, path, marshalSnapshot(t, snapshot))
}

func requireUnauthorized(t *testing.T, err error) {
	t.Helper()
	if !errors.Is(err, ErrUnauthorized) {
		t.Fatalf("error = %v, want ErrUnauthorized", err)
	}
}

func TestRegistrySnapshotJSONShape(t *testing.T) {
	snapshot := &RegistrySnapshot{
		SchemaVersion: 1,
		InstanceID:    "survival",
		Generation:    3,
		LinkSecret:    "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff",
		Tokens: []TokenRegistryToken{{
			TokenID:   "token",
			Hash:      TokenHash(testTokenA),
			Scopes:    []string{},
			CreatedAt: time.Date(2026, 1, 2, 3, 4, 5, 0, time.UTC),
		}},
	}

	want := `{"schemaVersion":1,"instanceId":"survival","generation":3,` +
		`"linkSecret":"00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff",` +
		`"tokens":[{"tokenId":"token","hash":"` + TokenHash(testTokenA) +
		`","scopes":[],"createdAt":"2026-01-02T03:04:05Z","notAfter":null,` +
		`"revoked":false,"root":false}]}`
	if got := string(marshalSnapshot(t, snapshot)); got != want {
		t.Fatalf("JSON = %s, want %s", got, want)
	}
}

func TestLoadSnapshotValidAndDecodesLinkSecret(t *testing.T) {
	path := filepath.Join(t.TempDir(), "registry.json")
	snapshot := validSnapshot()
	writeSnapshot(t, path, snapshot)

	loaded, err := LoadSnapshot(path, testInstanceID)
	if err != nil {
		t.Fatalf("LoadSnapshot: %v", err)
	}
	if loaded.SchemaVersion != snapshot.SchemaVersion ||
		loaded.InstanceID != snapshot.InstanceID ||
		loaded.Generation != snapshot.Generation ||
		len(loaded.Tokens) != len(snapshot.Tokens) {
		t.Fatalf("loaded snapshot does not match: %+v", loaded)
	}

	wantSecret, err := hex.DecodeString(snapshot.LinkSecret)
	if err != nil {
		t.Fatalf("decode expected secret: %v", err)
	}
	if got := loaded.LinkSecretBytes(); !bytes.Equal(got, wantSecret) {
		t.Fatalf("link secret bytes = %x, want %x", got, wantSecret)
	}
	firstCopy := loaded.LinkSecretBytes()
	firstCopy[0] ^= 0xff
	if got := loaded.LinkSecretBytes(); !bytes.Equal(got, wantSecret) {
		t.Fatal("LinkSecretBytes exposed mutable registry state")
	}
}

func TestLoadSnapshotAcceptsEmptyTokensAndUnknownFields(t *testing.T) {
	path := filepath.Join(t.TempDir(), "registry.json")
	snapshot := validSnapshot()
	snapshot.Tokens = []TokenRegistryToken{}
	data := marshalSnapshot(t, snapshot)
	data = bytes.Replace(data, []byte(`"schemaVersion"`), []byte(`"SCHEMAVERSION"`), 1)
	data = append(data[:len(data)-1], []byte(`,"futureField":{"enabled":true}}`)...)
	writeRegistryJSON(t, path, data)

	if _, err := LoadSnapshot(path, testInstanceID); err != nil {
		t.Fatalf("LoadSnapshot with empty tokens and unknown field: %v", err)
	}
}

func TestLoadSnapshotRejectsInvalidRegistry(t *testing.T) {
	tests := []struct {
		name   string
		mutate func(*RegistrySnapshot)
	}{
		{
			name: "unsupported schema",
			mutate: func(snapshot *RegistrySnapshot) {
				snapshot.SchemaVersion = 2
			},
		},
		{
			name: "negative generation",
			mutate: func(snapshot *RegistrySnapshot) {
				snapshot.Generation = -1
			},
		},
		{
			name: "instance mismatch",
			mutate: func(snapshot *RegistrySnapshot) {
				snapshot.InstanceID = "other"
			},
		},
		{
			name: "short link secret",
			mutate: func(snapshot *RegistrySnapshot) {
				snapshot.LinkSecret = snapshot.LinkSecret[:TokenHexLength-1]
			},
		},
		{
			name: "non-hex link secret",
			mutate: func(snapshot *RegistrySnapshot) {
				snapshot.LinkSecret = "g" + snapshot.LinkSecret[1:]
			},
		},
		{
			name: "null tokens",
			mutate: func(snapshot *RegistrySnapshot) {
				snapshot.Tokens = nil
			},
		},
		{
			name: "blank token ID",
			mutate: func(snapshot *RegistrySnapshot) {
				snapshot.Tokens[0].TokenID = " \t"
			},
		},
		{
			name: "long token ID",
			mutate: func(snapshot *RegistrySnapshot) {
				snapshot.Tokens[0].TokenID = strings.Repeat("x", MaxTokenIDLength+1)
			},
		},
		{
			name: "control in token ID",
			mutate: func(snapshot *RegistrySnapshot) {
				snapshot.Tokens[0].TokenID = "bad\x00id"
			},
		},
		{
			name: "duplicate token ID",
			mutate: func(snapshot *RegistrySnapshot) {
				snapshot.Tokens[1].TokenID = snapshot.Tokens[0].TokenID
			},
		},
		{
			name: "uppercase hash",
			mutate: func(snapshot *RegistrySnapshot) {
				snapshot.Tokens[0].Hash = strings.ToUpper(snapshot.Tokens[0].Hash)
			},
		},
		{
			name: "short hash",
			mutate: func(snapshot *RegistrySnapshot) {
				snapshot.Tokens[0].Hash = snapshot.Tokens[0].Hash[:TokenHexLength-1]
			},
		},
		{
			name: "non-hex hash",
			mutate: func(snapshot *RegistrySnapshot) {
				snapshot.Tokens[0].Hash = "g" + snapshot.Tokens[0].Hash[1:]
			},
		},
		{
			name: "duplicate hash",
			mutate: func(snapshot *RegistrySnapshot) {
				snapshot.Tokens[1].Hash = snapshot.Tokens[0].Hash
			},
		},
		{
			name: "createdAt nonzero offset",
			mutate: func(snapshot *RegistrySnapshot) {
				snapshot.Tokens[0].CreatedAt = snapshot.Tokens[0].CreatedAt.In(time.FixedZone("plus", 3600))
			},
		},
		{
			name: "notAfter nonzero offset",
			mutate: func(snapshot *RegistrySnapshot) {
				notAfter := snapshot.Tokens[0].NotAfter.In(time.FixedZone("minus", -3600))
				snapshot.Tokens[0].NotAfter = &notAfter
			},
		},
		{
			name: "null scopes",
			mutate: func(snapshot *RegistrySnapshot) {
				snapshot.Tokens[0].Scopes = nil
			},
		},
		{
			name: "too many scopes",
			mutate: func(snapshot *RegistrySnapshot) {
				snapshot.Tokens[0].Scopes = []string{
					ScopeRead, ScopePlayers, ScopeFiles, ScopeBackup, ScopeControl, ScopeCommand, ScopeRead,
				}
			},
		},
		{
			name: "unknown scope",
			mutate: func(snapshot *RegistrySnapshot) {
				snapshot.Tokens[0].Scopes = []string{"*"}
			},
		},
		{
			name: "duplicate scope",
			mutate: func(snapshot *RegistrySnapshot) {
				snapshot.Tokens[0].Scopes = []string{ScopeRead, ScopeRead}
			},
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			path := filepath.Join(t.TempDir(), "registry.json")
			snapshot := validSnapshot()
			test.mutate(snapshot)
			writeSnapshot(t, path, snapshot)
			_, err := LoadSnapshot(path, testInstanceID)
			requireUnauthorized(t, err)
		})
	}
}

func TestLoadSnapshotRejectsMalformedJSONAndMissingFile(t *testing.T) {
	validJSON := marshalSnapshot(t, validSnapshot())
	nullTokenJSON := bytes.Replace(validJSON, []byte(`"tokens":[`), []byte(`"tokens":[null,`), 1)
	tests := []struct {
		name string
		data []byte
	}{
		{name: "invalid JSON", data: []byte(`{"schemaVersion":`)},
		{name: "null snapshot", data: []byte(`null`)},
		{name: "array snapshot", data: []byte(`[]`)},
		{name: "trailing JSON", data: append(append([]byte{}, validJSON...), []byte(` {}`)...)},
		{name: "null token", data: nullTokenJSON},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			path := filepath.Join(t.TempDir(), "registry.json")
			writeRegistryJSON(t, path, test.data)
			_, err := LoadSnapshot(path, testInstanceID)
			requireUnauthorized(t, err)
		})
	}

	_, err := LoadSnapshot(filepath.Join(t.TempDir(), "missing.json"), testInstanceID)
	requireUnauthorized(t, err)
}

func TestLoadSnapshotEnforcesFourMiBLimit(t *testing.T) {
	path := filepath.Join(t.TempDir(), "registry.json")
	data := marshalSnapshot(t, validSnapshot())
	padded := append([]byte{}, data[:len(data)-1]...)
	padded = append(padded, []byte(`,"padding":"`)...)
	paddingLength := MaxRegistryBytes - len(padded) - 2
	if paddingLength < 0 {
		t.Fatal("test registry unexpectedly exceeds limit")
	}
	padded = append(padded, bytes.Repeat([]byte("x"), paddingLength)...)
	padded = append(padded, []byte(`"}`)...)
	if len(padded) != MaxRegistryBytes {
		t.Fatalf("padded registry length = %d, want %d", len(padded), MaxRegistryBytes)
	}
	writeRegistryJSON(t, path, padded)
	if _, err := LoadSnapshot(path, testInstanceID); err != nil {
		t.Fatalf("LoadSnapshot at exact limit: %v", err)
	}

	writeRegistryJSON(t, path, append(padded, ' '))
	_, err := LoadSnapshot(path, testInstanceID)
	requireUnauthorized(t, err)
}

func TestAuthenticateValidContextAndReloadsRegistry(t *testing.T) {
	path := filepath.Join(t.TempDir(), "registry.json")
	snapshot := validSnapshot()
	writeSnapshot(t, path, snapshot)
	now := time.Date(2026, 1, 1, 0, 0, 0, 0, time.UTC)
	registry := NewTokenRegistry(path, func() time.Time { return now })
	authorization := "Bearer " + testTokenA

	context, err := registry.Authenticate(authorization, testInstanceID)
	if err != nil {
		t.Fatalf("Authenticate: %v", err)
	}
	if context.TokenID != "reader" || context.IsRoot ||
		context.Authorization != authorization || context.RegistryGeneration != 7 {
		t.Fatalf("unexpected auth context: %+v", context)
	}
	if len(context.Scopes) != 2 || !context.HasScope(ScopeRead) || !context.HasScope(ScopeFiles) ||
		context.HasScope(ScopeControl) {
		t.Fatalf("unexpected scopes: %v", context.Scopes)
	}

	snapshot.Tokens[0].Revoked = true
	writeSnapshot(t, path, snapshot)
	_, err = registry.Authenticate(authorization, testInstanceID)
	requireUnauthorized(t, err)
}

func TestAuthenticateRootIsNotScopeWildcard(t *testing.T) {
	path := filepath.Join(t.TempDir(), "registry.json")
	writeSnapshot(t, path, validSnapshot())
	now := time.Date(2026, 1, 1, 0, 0, 0, 0, time.UTC)
	registry := NewTokenRegistry(path, func() time.Time { return now })

	context, err := registry.Authenticate("Bearer "+testTokenB, testInstanceID)
	if err != nil {
		t.Fatalf("Authenticate root: %v", err)
	}
	if !context.IsRoot {
		t.Fatal("root token was not marked as root")
	}
	if len(context.Scopes) != 0 || context.HasScope(ScopeRead) || context.HasScope("*") {
		t.Fatalf("root unexpectedly granted scopes: %v", context.Scopes)
	}
}

func TestAuthenticateRejectsExpiredAndRevokedTokens(t *testing.T) {
	now := time.Date(2026, 1, 1, 0, 0, 0, 0, time.UTC)
	before := now.Add(-time.Nanosecond)
	equal := now
	after := now.Add(time.Nanosecond)
	tests := []struct {
		name       string
		notAfter   *time.Time
		revoked    bool
		wantAuthor bool
	}{
		{name: "no expiry", wantAuthor: true},
		{name: "expired", notAfter: &before},
		{name: "expiry equals now", notAfter: &equal},
		{name: "not yet expired", notAfter: &after, wantAuthor: true},
		{name: "revoked", revoked: true},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			path := filepath.Join(t.TempDir(), "registry.json")
			snapshot := validSnapshot()
			snapshot.Tokens[0].NotAfter = test.notAfter
			snapshot.Tokens[0].Revoked = test.revoked
			writeSnapshot(t, path, snapshot)
			registry := NewTokenRegistry(path, func() time.Time { return now })

			_, err := registry.Authenticate("Bearer "+testTokenA, testInstanceID)
			if test.wantAuthor && err != nil {
				t.Fatalf("Authenticate: %v", err)
			}
			if !test.wantAuthor {
				requireUnauthorized(t, err)
			}
		})
	}
}

func TestAuthenticateRequiresStrictBearer(t *testing.T) {
	path := filepath.Join(t.TempDir(), "registry.json")
	writeSnapshot(t, path, validSnapshot())
	now := time.Date(2026, 1, 1, 0, 0, 0, 0, time.UTC)
	registry := NewTokenRegistry(path, func() time.Time { return now })
	valid := "Bearer " + testTokenA
	tests := []struct {
		name          string
		authorization string
		wantAuthor    bool
	}{
		{name: "valid", authorization: valid, wantAuthor: true},
		{name: "empty", authorization: ""},
		{name: "lowercase scheme", authorization: "bearer " + testTokenA},
		{name: "uppercase scheme", authorization: "BEARER " + testTokenA},
		{name: "missing space", authorization: "Bearer" + testTokenA},
		{name: "double space", authorization: "Bearer  " + testTokenA},
		{name: "tab separator", authorization: "Bearer\t" + testTokenA},
		{name: "leading space", authorization: " " + valid},
		{name: "trailing space", authorization: valid + " "},
		{name: "trailing newline", authorization: valid + "\n"},
		{name: "uppercase token", authorization: "Bearer " + strings.ToUpper(testTokenA)},
		{name: "non-hex token", authorization: "Bearer " + strings.Repeat("g", TokenHexLength)},
		{name: "short token", authorization: "Bearer " + testTokenA[:TokenHexLength-1]},
		{name: "long token", authorization: "Bearer " + testTokenA + "a"},
		{name: "wrong scheme", authorization: "Basic " + testTokenA},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			_, err := registry.Authenticate(test.authorization, testInstanceID)
			if test.wantAuthor && err != nil {
				t.Fatalf("Authenticate: %v", err)
			}
			if !test.wantAuthor {
				requireUnauthorized(t, err)
			}
		})
	}
}

func TestAuthenticateUnifiesRegistryAndLookupFailures(t *testing.T) {
	path := filepath.Join(t.TempDir(), "registry.json")
	now := time.Date(2026, 1, 1, 0, 0, 0, 0, time.UTC)
	registry := NewTokenRegistry(path, func() time.Time { return now })

	_, err := registry.Authenticate("Bearer "+testTokenA, testInstanceID)
	requireUnauthorized(t, err)

	writeRegistryJSON(t, path, []byte(`{"broken":`))
	_, err = registry.Authenticate("Bearer "+testTokenA, testInstanceID)
	requireUnauthorized(t, err)

	writeSnapshot(t, path, validSnapshot())
	_, err = registry.Authenticate("Bearer "+testTokenA, "other-instance")
	requireUnauthorized(t, err)

	unknownToken := strings.Repeat("c", TokenHexLength)
	_, err = registry.Authenticate("Bearer "+unknownToken, testInstanceID)
	requireUnauthorized(t, err)
}
