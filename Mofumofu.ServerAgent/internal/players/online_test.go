// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package players

import (
	"bytes"
	"context"
	"errors"
	"testing"
	"time"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

func TestOnlineUsesNamesOnlyAndOmitsNullFields(t *testing.T) {
	observed := time.Date(2026, time.November, 27, 12, 34, 56, 123456700, time.UTC)
	core := &fakePlayerCore{online: []string{"Steve", "Alex"}}
	service := New(t.TempDir(), core, func() time.Time { return observed })
	response, err := service.Online(context.Background(), 20)
	if err != nil {
		t.Fatalf("Online: %v", err)
	}
	if response.OnlineCount != 2 || response.MaxPlayers != 20 || response.ObservedAt.String() != "2026-11-27T12:34:56.1234567+00:00" {
		t.Fatalf("online metadata = %#v", response)
	}
	if len(response.Players) != 2 || response.Players[0].Name != "Steve" || response.Players[1].Name != "Alex" {
		t.Fatalf("online players = %#v", response.Players)
	}
	for _, player := range response.Players {
		if player.UUID != nil || player.JoinedAt != nil || player.OnlineSeconds != nil ||
			player.PingMilliseconds != nil || player.GameMode != nil {
			t.Fatalf("Online fabricated optional player fields: %#v", player)
		}
	}
	payload, err := protocol.Marshal(response)
	if err != nil {
		t.Fatal(err)
	}
	expected := `{"apiVersion":"1.0","onlineCount":2,"maxPlayers":20,"observedAt":"2026-11-27T12:34:56.1234567+00:00","players":[{"name":"Steve"},{"name":"Alex"}]}`
	if string(payload) != expected {
		t.Fatalf("online JSON = %s, want %s", payload, expected)
	}
}

func TestOnlineEmptyListAndCancellation(t *testing.T) {
	service := New(t.TempDir(), &fakePlayerCore{}, nil)
	response, err := service.Online(context.Background(), 0)
	if err != nil {
		t.Fatalf("Online: %v", err)
	}
	if response.OnlineCount != 0 || response.MaxPlayers != 0 || response.Players == nil || len(response.Players) != 0 {
		t.Fatalf("empty online response = %#v", response)
	}
	payload, err := protocol.Marshal(response)
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Contains(payload, []byte(`"players":[]`)) {
		t.Fatalf("empty online JSON must contain players:[]: %s", payload)
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	_, err = service.Online(ctx, 20)
	if !errors.Is(err, context.Canceled) {
		t.Fatalf("canceled Online error = %v", err)
	}
}
