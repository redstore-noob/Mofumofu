// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package httpapi

import (
	"net/http"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/audit"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

// installPrepare is a Go Agent extension. Execution goes through
// POST /confirm/execute and returns an operation under the control scope.
func (s *Server) installPrepare(w http.ResponseWriter, r *http.Request) {
	var request protocol.ServerInstallRequest
	if err := ReadBody(r, &request); err != nil {
		WriteError(w, r, err)
		return
	}
	authentication, err := Authentication(r.Context())
	if err != nil {
		WriteError(w, r, err)
		return
	}
	response, err := s.service.PrepareInstall(r.Context(), audit.Actor{TokenID: authentication.TokenID}, request)
	if err != nil {
		writeServiceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, response)
}
