// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package httpapi

import (
	"context"
	"net/http"
	"time"

	"github.com/coder/websocket"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/audit"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

const maxWebSocketFrameBytes = 2 * 1024 * 1024

func (s *Server) websocket(w http.ResponseWriter, r *http.Request) {
	if !isWebSocketUpgrade(r) {
		WriteError(w, r, InvalidRequest("A WebSocket upgrade is required."))
		return
	}
	authentication, err := Authentication(r.Context())
	if err != nil {
		WriteError(w, r, err)
		return
	}
	actor := audit.Actor{TokenID: authentication.TokenID, Authorization: authentication.Authorization}
	snapshot, err := s.service.WebSnapshot(r.Context(), actor)
	if err != nil {
		WriteError(w, r, err)
		return
	}
	var cursor *string
	if _, ok := r.URL.Query()["cursor"]; ok {
		value := queryValue(r, "cursor")
		cursor = &value
	}
	subscription, err := s.service.OpenSubscription(r.Context(), cursor, func(position string) protocol.SnapshotFrame {
		frame := protocol.NewSnapshotFrame()
		frame.Cursor = position
		frame.SentAt = protocol.NewTimestamp(time.Now())
		frame.Status = snapshot.Status
		frame.Players = snapshot.Players
		frame.RecentConsole = snapshot.RecentConsole
		frame.ActiveOperations = snapshot.ActiveOperations
		return frame
	})
	if err != nil {
		WriteError(w, r, err)
		return
	}
	defer subscription.Close()

	connection, err := websocket.Accept(w, r, &websocket.AcceptOptions{InsecureSkipVerify: true})
	if err != nil {
		return
	}
	connection.SetReadLimit(maxWebSocketFrameBytes)
	ctx, cancel := context.WithCancel(r.Context())
	defer cancel()
	defer connection.Close(websocket.StatusNormalClosure, "")
	go func() {
		for {
			if _, _, err := connection.Read(ctx); err != nil {
				cancel()
				return
			}
		}
	}()
	for _, frame := range subscription.Initial {
		if err := writeWebSocketFrame(ctx, connection, frame); err != nil {
			return
		}
	}
	for {
		select {
		case <-ctx.Done():
			return
		case frame, ok := <-subscription.Frames:
			if !ok {
				return
			}
			if err := writeWebSocketFrame(ctx, connection, frame); err != nil {
				return
			}
		}
	}
}

func writeWebSocketFrame(ctx context.Context, connection *websocket.Conn, frame protocol.WebSocketFrame) error {
	payload, err := protocol.Marshal(frame)
	if err != nil {
		return err
	}
	if len(payload) > maxWebSocketFrameBytes {
		return context.Canceled
	}
	return connection.Write(ctx, websocket.MessageText, payload)
}
