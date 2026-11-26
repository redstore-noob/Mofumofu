// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package httpapi

import (
	"bufio"
	"errors"
	"log/slog"
	"net"
	"net/http"
	"reflect"
	"strings"
	"time"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/audit"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/service"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/tokens"
)

// Server is the HTTP/WS transport. Route, scope, and rate-category metadata
// stay centralized here while handlers delegate to the shared service layer.
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
	mux.HandleFunc("POST /v1/servers/{instanceId}/handshake", secure(tokens.ScopeRead, tokens.CategoryRead, false, s.handshake))
	mux.HandleFunc("GET /v1/servers/{instanceId}/status", secure(tokens.ScopeRead, tokens.CategoryRead, false, s.status))
	mux.HandleFunc("POST /v1/servers/{instanceId}/console/commands", secure(tokens.ScopeCommand, tokens.CategoryWrite, false, s.command))
	mux.HandleFunc("GET /v1/servers/{instanceId}/players/online", secure(tokens.ScopeRead, tokens.CategoryRead, false, s.playersOnline))
	mux.HandleFunc("GET /v1/servers/{instanceId}/players/{kind}", secure(tokens.ScopePlayers, tokens.CategoryRead, false, s.playerList))
	mux.HandleFunc("POST /v1/servers/{instanceId}/players/{kind}/add", secure(tokens.ScopePlayers, tokens.CategoryWrite, false, s.playerAdd))
	mux.HandleFunc("POST /v1/servers/{instanceId}/players/{kind}/remove", secure(tokens.ScopePlayers, tokens.CategoryWrite, false, s.playerRemove))
	mux.HandleFunc("GET /v1/servers/{instanceId}/files", secure(tokens.ScopeFiles, tokens.CategoryRead, false, s.files))
	mux.HandleFunc("GET /v1/servers/{instanceId}/files/content", secure(tokens.ScopeFiles, tokens.CategoryRead, false, s.fileContent))
	mux.HandleFunc("POST /v1/servers/{instanceId}/files/create", secure(tokens.ScopeFiles, tokens.CategoryWrite, false, s.fileCreate))
	mux.HandleFunc("POST /v1/servers/{instanceId}/files/rename", secure(tokens.ScopeFiles, tokens.CategoryWrite, false, s.fileRename))
	mux.HandleFunc("PUT /v1/servers/{instanceId}/files/content", secure(tokens.ScopeFiles, tokens.CategoryWrite, false, s.fileWrite))
	mux.HandleFunc("POST /v1/servers/{instanceId}/files/delete/prepare", secure(tokens.ScopeFiles, tokens.CategorySensitive, false, s.fileDeletePrepare))
	mux.HandleFunc("GET /v1/servers/{instanceId}/properties", secure(tokens.ScopeFiles, tokens.CategoryRead, false, s.properties))
	mux.HandleFunc("POST /v1/servers/{instanceId}/properties/prepare", secure(tokens.ScopeFiles, tokens.CategorySensitive, false, s.propertiesPrepare))
	mux.HandleFunc("GET /v1/servers/{instanceId}/backups", secure(tokens.ScopeBackup, tokens.CategorySensitive, false, s.backups))
	mux.HandleFunc("POST /v1/servers/{instanceId}/backups", secure(tokens.ScopeBackup, tokens.CategorySensitive, false, s.backupCreate))
	mux.HandleFunc("POST /v1/servers/{instanceId}/backups/restore/prepare", secure(tokens.ScopeBackup, tokens.CategorySensitive, false, s.backupRestorePrepare))
	mux.HandleFunc("POST /v1/servers/{instanceId}/backups/delete/prepare", secure(tokens.ScopeBackup, tokens.CategorySensitive, false, s.backupDeletePrepare))
	mux.HandleFunc("POST /v1/servers/{instanceId}/control/prepare", secure(tokens.ScopeControl, tokens.CategorySensitive, false, s.controlPrepare))
	mux.HandleFunc("POST /v1/servers/{instanceId}/confirm/execute", secure("", tokens.CategorySensitive, false, s.confirmExecute))
	mux.HandleFunc("GET /v1/servers/{instanceId}/operations/{operationId}", secure("", tokens.CategoryRead, false, s.operation))
	mux.HandleFunc("GET /v1/servers/{instanceId}/tokens", secure("", tokens.CategoryRead, true, s.tokenList))
	mux.HandleFunc("POST /v1/servers/{instanceId}/tokens/derive", secure("", tokens.CategoryWrite, true, s.tokenDerive))
	mux.HandleFunc("POST /v1/servers/{instanceId}/tokens/revoke", secure("", tokens.CategoryWrite, true, s.tokenRevoke))
	mux.HandleFunc("POST /v1/servers/{instanceId}/tokens/rotate", secure("", tokens.CategoryWrite, true, s.tokenRotate))
	mux.HandleFunc("GET /v1/servers/{instanceId}/ws", secure(tokens.ScopeRead, tokens.CategoryRead, false, s.websocket))
	mux.HandleFunc("POST /internal/probe/events", s.probeEvents)
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

func (s *Server) playersOnline(w http.ResponseWriter, r *http.Request) {
	authentication, err := Authentication(r.Context())
	if err != nil {
		WriteError(w, r, err)
		return
	}
	response, err := s.service.OnlinePlayers(r.Context(), audit.Actor{TokenID: authentication.TokenID, Authorization: authentication.Authorization})
	if err != nil {
		WriteError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, response)
}

func (s *Server) playerList(w http.ResponseWriter, r *http.Request) {
	response, err := s.service.PlayerList(r.Context(), r.PathValue("kind"))
	if err != nil {
		WriteError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, response)
}

func (s *Server) playerAdd(w http.ResponseWriter, r *http.Request) {
	var request protocol.PlayerAddRequest
	if err := ReadBody(r, &request); err != nil {
		WriteError(w, r, err)
		return
	}
	authentication, err := Authentication(r.Context())
	if err != nil {
		WriteError(w, r, err)
		return
	}
	response, err := s.service.AddPlayer(r.Context(), audit.Actor{TokenID: authentication.TokenID}, r.PathValue("kind"), request)
	if err != nil {
		writeServiceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, response)
}

func (s *Server) playerRemove(w http.ResponseWriter, r *http.Request) {
	var request protocol.PlayerRemoveRequest
	if err := ReadBody(r, &request); err != nil {
		WriteError(w, r, err)
		return
	}
	authentication, err := Authentication(r.Context())
	if err != nil {
		WriteError(w, r, err)
		return
	}
	response, err := s.service.RemovePlayer(r.Context(), audit.Actor{TokenID: authentication.TokenID}, r.PathValue("kind"), request)
	if err != nil {
		writeServiceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, response)
}

func (s *Server) files(w http.ResponseWriter, r *http.Request) {
	response, err := s.service.Files(r.Context(), queryValue(r, "path"))
	if err != nil {
		WriteError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, response)
}

func (s *Server) fileContent(w http.ResponseWriter, r *http.Request) {
	response, err := s.service.ReadFile(r.Context(), queryValue(r, "path"))
	if err != nil {
		WriteError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, response)
}

func (s *Server) fileCreate(w http.ResponseWriter, r *http.Request) {
	var request protocol.FileCreateRequest
	if err := ReadBody(r, &request); err != nil {
		WriteError(w, r, err)
		return
	}
	authentication, err := Authentication(r.Context())
	if err != nil {
		WriteError(w, r, err)
		return
	}
	response, err := s.service.CreateFile(r.Context(), audit.Actor{TokenID: authentication.TokenID}, request.Path, request.IsDirectory)
	if err != nil {
		writeServiceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, response)
}

func (s *Server) fileRename(w http.ResponseWriter, r *http.Request) {
	var request protocol.FileRenameRequest
	if err := ReadBody(r, &request); err != nil {
		WriteError(w, r, err)
		return
	}
	authentication, err := Authentication(r.Context())
	if err != nil {
		WriteError(w, r, err)
		return
	}
	response, err := s.service.RenameFile(r.Context(), audit.Actor{TokenID: authentication.TokenID}, request.Path, request.NewName, request.ExpectedRevision)
	if err != nil {
		writeServiceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, response)
}

func (s *Server) fileWrite(w http.ResponseWriter, r *http.Request) {
	var request protocol.FileWriteRequest
	if err := ReadBody(r, &request); err != nil {
		WriteError(w, r, err)
		return
	}
	authentication, err := Authentication(r.Context())
	if err != nil {
		WriteError(w, r, err)
		return
	}
	response, err := s.service.WriteFile(r.Context(), audit.Actor{TokenID: authentication.TokenID}, request.Path, request.Content, request.ExpectedRevision)
	if err != nil {
		writeServiceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, response)
}

func (s *Server) fileDeletePrepare(w http.ResponseWriter, r *http.Request) {
	var request protocol.FileDeleteRequest
	if err := ReadBody(r, &request); err != nil {
		WriteError(w, r, err)
		return
	}
	authentication, err := Authentication(r.Context())
	if err != nil {
		WriteError(w, r, err)
		return
	}
	response, err := s.service.PrepareFileDelete(r.Context(), audit.Actor{TokenID: authentication.TokenID}, request.Path, request.ExpectedRevision)
	if err != nil {
		writeServiceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, response)
}

func (s *Server) propertiesPrepare(w http.ResponseWriter, r *http.Request) {
	var request protocol.ServerPropertiesPatchRequest
	if err := ReadBody(r, &request); err != nil {
		WriteError(w, r, err)
		return
	}
	authentication, err := Authentication(r.Context())
	if err != nil {
		WriteError(w, r, err)
		return
	}
	response, err := s.service.PreparePropertiesPatch(r.Context(), audit.Actor{TokenID: authentication.TokenID}, request.ExpectedRevision, request.Changes)
	if err != nil {
		writeServiceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, response)
}

func (s *Server) confirmExecute(w http.ResponseWriter, r *http.Request) {
	var request protocol.ConfirmationExecuteRequest
	if err := ReadBody(r, &request); err != nil {
		WriteError(w, r, err)
		return
	}
	authentication, err := Authentication(r.Context())
	if err != nil {
		WriteError(w, r, err)
		return
	}
	operation, err := s.service.ExecuteConfirmation(r.Context(), audit.Actor{TokenID: authentication.TokenID}, request.ConfirmationID, authentication.HasScope)
	if err != nil {
		writeServiceError(w, r, err)
		return
	}
	response := protocol.NewConfirmationExecuteResponse()
	response.Operation = operation
	writeJSON(w, http.StatusOK, response)
}

func (s *Server) operation(w http.ResponseWriter, r *http.Request) {
	authentication, err := Authentication(r.Context())
	if err != nil {
		WriteError(w, r, err)
		return
	}
	operation, scope, ok := s.service.Operation(r.PathValue("operationId"))
	if !ok {
		WriteError(w, r, errorf(http.StatusNotFound, "operation_not_found", "The operation was not found."))
		return
	}
	if !authentication.HasScope(scope) {
		WriteError(w, r, errorf(http.StatusForbidden, "insufficient_scope", "The operation requires its original scope."))
		return
	}
	response := protocol.NewOperationResponse()
	response.Operation = operation
	writeJSON(w, http.StatusOK, response)
}

func (s *Server) tokenList(w http.ResponseWriter, r *http.Request) {
	authentication, err := Authentication(r.Context())
	if err != nil {
		WriteError(w, r, err)
		return
	}
	response, err := s.service.Tokens(r.Context(), audit.Actor{TokenID: authentication.TokenID, Authorization: authentication.Authorization})
	if err != nil {
		writeServiceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, response)
}

func (s *Server) tokenDerive(w http.ResponseWriter, r *http.Request) {
	var request protocol.TokenDeriveRequest
	if err := ReadBody(r, &request); err != nil {
		WriteError(w, r, err)
		return
	}
	authentication, err := Authentication(r.Context())
	if err != nil {
		WriteError(w, r, err)
		return
	}
	response, err := s.service.DeriveToken(r.Context(), audit.Actor{TokenID: authentication.TokenID, Authorization: authentication.Authorization}, request)
	if err != nil {
		writeServiceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, response)
}

func (s *Server) tokenRevoke(w http.ResponseWriter, r *http.Request) {
	var request protocol.TokenRevokeRequest
	if err := ReadBody(r, &request); err != nil {
		WriteError(w, r, err)
		return
	}
	authentication, err := Authentication(r.Context())
	if err != nil {
		WriteError(w, r, err)
		return
	}
	response, err := s.service.RevokeToken(r.Context(), audit.Actor{TokenID: authentication.TokenID, Authorization: authentication.Authorization}, request)
	if err != nil {
		writeServiceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, response)
}

func (s *Server) tokenRotate(w http.ResponseWriter, r *http.Request) {
	var request protocol.TokenRotateRequest
	if err := ReadBody(r, &request); err != nil {
		WriteError(w, r, err)
		return
	}
	authentication, err := Authentication(r.Context())
	if err != nil {
		WriteError(w, r, err)
		return
	}
	response, err := s.service.RotateToken(r.Context(), audit.Actor{TokenID: authentication.TokenID, Authorization: authentication.Authorization}, request)
	if err != nil {
		writeServiceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, response)
}

func (s *Server) backups(w http.ResponseWriter, r *http.Request) {
	response, err := s.service.Backups(r.Context())
	if err != nil {
		WriteError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, response)
}

func (s *Server) backupCreate(w http.ResponseWriter, r *http.Request) {
	var request protocol.BackupCreateRequest
	if err := ReadBody(r, &request); err != nil {
		WriteError(w, r, err)
		return
	}
	authentication, err := Authentication(r.Context())
	if err != nil {
		WriteError(w, r, err)
		return
	}
	response, err := s.service.CreateBackup(r.Context(), audit.Actor{TokenID: authentication.TokenID}, request.Label, request.Description)
	if err != nil {
		writeServiceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, response)
}

func (s *Server) backupRestorePrepare(w http.ResponseWriter, r *http.Request) {
	s.prepareBackup(w, r, true)
}

func (s *Server) backupDeletePrepare(w http.ResponseWriter, r *http.Request) {
	s.prepareBackup(w, r, false)
}

func (s *Server) prepareBackup(w http.ResponseWriter, r *http.Request, restore bool) {
	var request protocol.BackupRestoreRequest
	if err := ReadBody(r, &request); err != nil {
		WriteError(w, r, err)
		return
	}
	authentication, err := Authentication(r.Context())
	if err != nil {
		WriteError(w, r, err)
		return
	}
	response, err := s.service.PrepareBackup(r.Context(), audit.Actor{TokenID: authentication.TokenID}, request.BackupID, restore)
	if err != nil {
		writeServiceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, response)
}

func (s *Server) controlPrepare(w http.ResponseWriter, r *http.Request) {
	var request protocol.ControlRequest
	if err := ReadBody(r, &request); err != nil {
		WriteError(w, r, err)
		return
	}
	authentication, err := Authentication(r.Context())
	if err != nil {
		WriteError(w, r, err)
		return
	}
	response, err := s.service.PrepareControl(r.Context(), audit.Actor{TokenID: authentication.TokenID}, request.Action)
	if err != nil {
		writeServiceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, response)
}

func (s *Server) properties(w http.ResponseWriter, r *http.Request) {
	response, err := s.service.Properties(r.Context())
	if err != nil {
		WriteError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, response)
}

func (s *Server) command(w http.ResponseWriter, r *http.Request) {
	var request protocol.ConsoleCommandRequest
	if err := ReadBody(r, &request); err != nil {
		WriteError(w, r, err)
		return
	}
	authentication, err := Authentication(r.Context())
	if err != nil {
		WriteError(w, r, err)
		return
	}
	if err := s.service.Command(r.Context(), audit.Actor{TokenID: authentication.TokenID}, request.Command); err != nil {
		if errors.Is(err, audit.ErrUnavailable) {
			WriteError(w, r, AuditUnavailable())
			return
		}
		WriteError(w, r, err)
		return
	}
	response := protocol.NewConsoleCommandResponse()
	response.Accepted = true
	response.SentAt = protocol.NewTimestamp(time.Now())
	writeJSON(w, http.StatusOK, response)
}

func (s *Server) status(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusOK, s.service.Status(r.Context()))
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

func (w *trackingWriter) Hijack() (net.Conn, *bufio.ReadWriter, error) {
	hijacker, ok := w.ResponseWriter.(http.Hijacker)
	if !ok {
		return nil, nil, errors.New("WebSocket upgrade is not supported by this writer")
	}
	return hijacker.Hijack()
}

func (w *trackingWriter) Unwrap() http.ResponseWriter { return w.ResponseWriter }

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

func queryValue(r *http.Request, name string) string {
	values, ok := r.URL.Query()[name]
	if !ok {
		return ""
	}
	return strings.Join(values, ",")
}

func writeServiceError(w http.ResponseWriter, r *http.Request, err error) {
	if errors.Is(err, audit.ErrUnavailable) {
		WriteError(w, r, AuditUnavailable())
		return
	}
	WriteError(w, r, err)
}
