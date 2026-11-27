// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

// Package install ports MofuCore's ServerDownload flow to the Go Agent: core
// version resolution, hardened HTTPS downloads, and initial server files. It
// performs no authorization; the service layer applies the allowlist and the
// confirmation flow before calling Install.
package install

import (
	"net/http"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

// Error carries the stable API code and HTTP status for an install failure.
// Messages never include download URLs or query strings.
type Error struct {
	Status  int
	Code    string
	Message string
}

func (e *Error) Error() string     { return e.Message }
func (e *Error) ErrorCode() string { return e.Code }
func (e *Error) HTTPStatus() int   { return e.Status }

func invalidf(message string) *Error {
	return &Error{Status: http.StatusBadRequest, Code: protocol.CodeInvalidRequest, Message: message}
}

func forbiddenf(message string) *Error {
	return &Error{Status: http.StatusForbidden, Code: protocol.CodeForbidden, Message: message}
}

func conflictf(message string) *Error {
	return &Error{Status: http.StatusConflict, Code: protocol.CodeConflict, Message: message}
}

func failedf(message string) *Error {
	return &Error{Status: http.StatusInternalServerError, Code: protocol.CodeOperationFailed, Message: message}
}
