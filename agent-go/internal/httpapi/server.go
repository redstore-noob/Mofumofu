// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package httpapi

import (
	"encoding/json"
	"log/slog"
	"net/http"
	"reflect"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/service"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/tokens"
)

// Server is the HTTP/WS transport. Later phases replace capability handlers
// without changing the route, scope, or rate-category registrations below.
type Server struct {
	service  *service.Service
	security *Security
	logger   *slog.Logger
}

func New(application *service.Service, security *Security, logger *slog.Logger) *Server {
	if logger == nil {
		logger = slog.Default()
	}
	return &Server{service: application, security: security, logger: logger}
}

func (s *Server) Handler() http.Handler {
	mux := http.NewServeMux()
	secure := func(scope string, category tokens.RateCategory, root bool, handler http.HandlerFunc) http.HandlerFunc {
		return s.security.Wrap(scope, category, root, handler)
	}
	stub := func(body bool) http.HandlerFunc {
		return func(w http.ResponseWriter, r *http.Request) {
			if body {
				var request map[string]json.RawMessage
				if err := ReadBody(r, &request); err != nil {
					WriteError(w, r, err)
					return
				}
			}
			WriteError(w, r, CapabilityNotSupported())
		}
	}

	mux.HandleFunc("POST /v1/servers/{instanceId}/handshake", secure(tokens.ScopeRead, tokens.CategoryRead, false, s.handshake))
	mux.HandleFunc("GET /v1/servers/{instanceId}/status", secure(tokens.ScopeRead, tokens.CategoryRead, false, s.status))
	mux.HandleFunc("POST /v1/servers/{instanceId}/console/commands", secure(tokens.ScopeCommand, tokens.CategoryWrite, false, stub(true)))
	mux.HandleFunc("GET /v1/servers/{instanceId}/players/online", secure(tokens.ScopeRead, tokens.CategoryRead, false, stub(false)))
	mux.HandleFunc("GET /v1/servers/{instanceId}/players/{kind}", secure(tokens.ScopePlayers, tokens.CategoryRead, false, stub(false)))
	mux.HandleFunc("POST /v1/servers/{instanceId}/players/{kind}/add", secure(tokens.ScopePlayers, tokens.CategoryWrite, false, stub(true)))
	mux.HandleFunc("POST /v1/servers/{instanceId}/players/{kind}/remove", secure(tokens.ScopePlayers, tokens.CategoryWrite, false, stub(true)))
	mux.HandleFunc("GET /v1/servers/{instanceId}/files", secure(tokens.ScopeFiles, tokens.CategoryRead, false, stub(false)))
	mux.HandleFunc("GET /v1/servers/{instanceId}/files/content", secure(tokens.ScopeFiles, tokens.CategoryRead, false, stub(false)))
	mux.HandleFunc("POST /v1/servers/{instanceId}/files/create", secure(tokens.ScopeFiles, tokens.CategoryWrite, false, stub(true)))
	mux.HandleFunc("POST /v1/servers/{instanceId}/files/rename", secure(tokens.ScopeFiles, tokens.CategoryWrite, false, stub(true)))
	mux.HandleFunc("PUT /v1/servers/{instanceId}/files/content", secure(tokens.ScopeFiles, tokens.CategoryWrite, false, stub(true)))
	mux.HandleFunc("POST /v1/servers/{instanceId}/files/delete/prepare", secure(tokens.ScopeFiles, tokens.CategorySensitive, false, stub(true)))
	mux.HandleFunc("GET /v1/servers/{instanceId}/properties", secure(tokens.ScopeFiles, tokens.CategoryRead, false, stub(false)))
	mux.HandleFunc("POST /v1/servers/{instanceId}/properties/prepare", secure(tokens.ScopeFiles, tokens.CategorySensitive, false, stub(true)))
	mux.HandleFunc("GET /v1/servers/{instanceId}/backups", secure(tokens.ScopeBackup, tokens.CategorySensitive, false, stub(false)))
	mux.HandleFunc("POST /v1/servers/{instanceId}/backups", secure(tokens.ScopeBackup, tokens.CategorySensitive, false, stub(true)))
	mux.HandleFunc("POST /v1/servers/{instanceId}/backups/restore/prepare", secure(tokens.ScopeBackup, tokens.CategorySensitive, false, stub(true)))
	mux.HandleFunc("POST /v1/servers/{instanceId}/backups/delete/prepare", secure(tokens.ScopeBackup, tokens.CategorySensitive, false, stub(true)))
	mux.HandleFunc("POST /v1/servers/{instanceId}/control/prepare", secure(tokens.ScopeControl, tokens.CategorySensitive, false, stub(true)))
	mux.HandleFunc("POST /v1/servers/{instanceId}/confirm/execute", secure("", tokens.CategorySensitive, false, stub(true)))
	mux.HandleFunc("GET /v1/servers/{instanceId}/operations/{operationId}", secure("", tokens.CategoryRead, false, stub(false)))
	mux.HandleFunc("GET /v1/servers/{instanceId}/tokens", secure("", tokens.CategoryRead, true, stub(false)))
	mux.HandleFunc("POST /v1/servers/{instanceId}/tokens/derive", secure("", tokens.CategoryWrite, true, stub(true)))
	mux.HandleFunc("POST /v1/servers/{instanceId}/tokens/revoke", secure("", tokens.CategoryWrite, true, stub(true)))
	mux.HandleFunc("POST /v1/servers/{instanceId}/tokens/rotate", secure("", tokens.CategoryWrite, true, stub(true)))
	mux.HandleFunc("GET /v1/servers/{instanceId}/ws", secure(tokens.ScopeRead, tokens.CategoryRead, false, s.webSocketPlaceholder))
	mux.HandleFunc("POST /internal/probe/events", func(w http.ResponseWriter, r *http.Request) {
		WriteError(w, r, CapabilityNotSupported())
	})
	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		WriteError(w, r, NotFound(""))
	})

	return RequestID(s.recoverer(mux))
}

func (s *Server) handshake(w http.ResponseWriter, r *http.Request) {
	var request protocol.HandshakeRequest
	if err := ReadBody(r, &request); err != nil {
		WriteError(w, r, err)
		return
	}
	authentication, err := Authentication(r.Context())
	if err != nil {
		WriteError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, s.service.Handshake(authentication))
}

func (s *Server) status(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusOK, s.service.Status(r.Context()))
}

func (s *Server) webSocketPlaceholder(w http.ResponseWriter, r *http.Request) {
	if !isWebSocketUpgrade(r) {
		WriteError(w, r, InvalidRequest("A WebSocket upgrade is required."))
		return
	}
	WriteError(w, r, CapabilityNotSupported())
}

type trackingWriter struct {
	http.ResponseWriter
	wrote bool
}

func (w *trackingWriter) WriteHeader(status int) {
	w.wrote = true
	w.ResponseWriter.WriteHeader(status)
}

func (w *trackingWriter) Write(payload []byte) (int, error) {
	w.wrote = true
	return w.ResponseWriter.Write(payload)
}

func (s *Server) recoverer(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		tracked := &trackingWriter{ResponseWriter: w}
		defer func() {
			if recovered := recover(); recovered != nil {
				s.logger.Error("unhandled Agent request panic", "panicType", reflectTypeName(recovered))
				if !tracked.wrote {
					WriteError(w, r, errorf(http.StatusInternalServerError, "internal_error", "The request could not be completed."))
				}
			}
		}()
		next.ServeHTTP(tracked, r)
	})
}

func reflectTypeName(value any) string {
	return reflect.TypeOf(value).String()
}
