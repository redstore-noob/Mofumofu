// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package service

import (
	"context"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"testing"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/audit"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/players"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/probe"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/server"
)

type probeSupervisor struct {
	*fakeSupervisor
	names []string
}

func (f *probeSupervisor) OnlineNames(context.Context) []string {
	return append([]string(nil), f.names...)
}

func TestOnlinePlayersProbeFallbackAndError(t *testing.T) {
	application, fake, root := newPhase4Service(t)
	fake.SetState(server.StateRunning)
	provider := &probeSupervisor{fakeSupervisor: fake, names: []string{"Alice"}}
	application.supervisor = provider
	application.SetPlayers(players.New(filepath.Join(root, "survival"), provider, application.now), nil)
	probeServer := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/v1/servers/survival/players/online" || r.Header.Get("Authorization") != "Bearer daily-token" {
			t.Error("Probe request metadata changed")
		}
		_, _ = w.Write([]byte(`{"apiVersion":"1.0","onlineCount":1,"maxPlayers":20,"observedAt":"2026-11-22T12:34:56Z","players":[{"name":"Alice","uuid":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","pingMilliseconds":12}]}`))
	}))
	client, _ := probe.New(probeServer.URL)
	application.SetPlayers(players.New(filepath.Join(root, "survival"), provider, application.now), client)
	actor := audit.Actor{TokenID: "daily", Authorization: "Bearer daily-token"}
	online, err := application.OnlinePlayers(context.Background(), actor)
	if err != nil {
		t.Fatal(err)
	}
	if len(online.Players) != 1 || online.Players[0].UUID == nil || online.Players[0].PingMilliseconds == nil {
		t.Fatalf("online=%+v", online)
	}
	probeServer.Close()
	online, err = application.OnlinePlayers(context.Background(), actor)
	if err != nil {
		t.Fatal(err)
	}
	if len(online.Players) != 1 || online.Players[0].Name != "Alice" || online.Players[0].UUID != nil || online.Players[0].PingMilliseconds != nil {
		t.Fatalf("fallback=%+v", online)
	}

	errorServer := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusForbidden)
		_, _ = w.Write([]byte(`{"apiVersion":"1.0","code":"forbidden","message":"denied"}`))
	}))
	defer errorServer.Close()
	client, _ = probe.New(errorServer.URL)
	application.SetPlayers(players.New(filepath.Join(root, "survival"), provider, application.now), client)
	if _, err := application.OnlinePlayers(context.Background(), actor); err == nil {
		t.Fatal("Probe HTTP error incorrectly fell back")
	}
}
