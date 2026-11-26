// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package probe

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestSharedCallbackGolden(t *testing.T) {
	payload, err := os.ReadFile(filepath.Join("..", "..", "..", "tests", "contracts", "probe-event-golden.json"))
	if err != nil {
		t.Fatal(err)
	}
	var fixture struct {
		LinkSecret string `json:"linkSecret"`
		InstanceID string `json:"instanceId"`
		Method     string `json:"method"`
		Path       string `json:"path"`
		Timestamp  string `json:"timestamp"`
		Nonce      string `json:"nonce"`
		Body       string `json:"body"`
		BodySHA256 string `json:"bodySha256"`
		Canonical  string `json:"canonical"`
		Signature  string `json:"signature"`
	}
	if err := json.Unmarshal(payload, &fixture); err != nil {
		t.Fatal(err)
	}
	secret, err := LinkSecret(fixture.LinkSecret)
	if err != nil {
		t.Fatal(err)
	}
	canonical := Canonical(fixture.Method, fixture.Path, fixture.InstanceID, fixture.Timestamp, fixture.Nonce, []byte(fixture.Body))
	if canonical != fixture.Canonical || !strings.Contains(canonical, fixture.BodySHA256) {
		t.Fatalf("canonical mismatch:\n%s\n%s", canonical, fixture.Canonical)
	}
	if got := Signature(secret, canonical); got != fixture.Signature {
		t.Fatalf("signature=%s want=%s", got, fixture.Signature)
	}
	for _, supplied := range []string{fixture.Signature, "sha256=" + fixture.Signature, "HMAC-SHA256=" + strings.ToUpper(fixture.Signature)} {
		if !VerifySignature(secret, canonical, supplied) {
			t.Fatalf("valid signature rejected: %s", supplied)
		}
	}
	if VerifySignature(secret, canonical, "bad") || VerifySignature(secret, canonical+"x", fixture.Signature) {
		t.Fatal("invalid signature accepted")
	}
}

func TestClientTransportAndLimits(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("Authorization") != "Bearer token" || r.URL.Query().Get("apiVersion") != "1.0" {
			t.Error("request metadata changed")
		}
		payload, _ := io.ReadAll(r.Body)
		if !strings.Contains(string(payload), `"name":"test"`) {
			t.Errorf("body=%s", payload)
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = io.WriteString(w, `{"apiVersion":"1.0","tokens":[]}`)
	}))
	defer server.Close()
	client, err := New(server.URL)
	if err != nil {
		t.Fatal(err)
	}
	response, err := client.Send(context.Background(), http.MethodPost, "/v1/servers/survival/tokens/derive", "Bearer token",
		map[string]any{"name": "test"}, "apiVersion=1.0")
	if err != nil {
		t.Fatal(err)
	}
	var decoded struct {
		Tokens []any `json:"tokens"`
	}
	if err := response.Decode(&decoded); err != nil || decoded.Tokens == nil {
		t.Fatalf("decode=%+v err=%v", decoded, err)
	}
	if _, err := client.Send(context.Background(), http.MethodGet, "relative", "", nil, ""); err == nil {
		t.Fatal("relative Probe path accepted")
	}
	if _, err := client.Send(context.Background(), http.MethodGet, "/../escape", "", nil, ""); err == nil {
		t.Fatal("traversal Probe path accepted")
	}
}

func TestClientUnavailableAndOversize(t *testing.T) {
	client, _ := New("http://127.0.0.1:1")
	if _, err := client.Send(context.Background(), http.MethodGet, "/status", "", nil, ""); !errors.Is(err, ErrUnavailable) {
		t.Fatalf("got %v", err)
	}
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Length", "4194305")
		_, _ = io.WriteString(w, "x")
	}))
	defer server.Close()
	client, _ = New(server.URL)
	if _, err := client.Send(context.Background(), http.MethodGet, "/status", "", nil, ""); !errors.Is(err, ErrUnavailable) {
		t.Fatalf("oversize got %v", err)
	}
}
