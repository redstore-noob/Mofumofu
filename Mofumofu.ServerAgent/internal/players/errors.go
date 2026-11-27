// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package players

import (
	"net/http"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

// Error is a player API failure that can be passed directly to httpapi.WriteError.
type Error struct {
	Status  int
	Code    string
	Message string
	Details map[string]string
}

func (e *Error) Error() string                   { return e.Message }
func (e *Error) ErrorCode() string               { return e.Code }
func (e *Error) HTTPStatus() int                 { return e.Status }
func (e *Error) ErrorDetails() map[string]string { return e.Details }
func (e *Error) OperationIdentifier() string     { return "" }

var _ interface {
	error
	ErrorCode() string
	HTTPStatus() int
	ErrorDetails() map[string]string
	OperationIdentifier() string
} = (*Error)(nil)

func invalidRequest(message string) *Error {
	return &Error{Status: http.StatusBadRequest, Code: protocol.CodeInvalidRequest, Message: message}
}

func invalidPlayerName() *Error {
	return &Error{
		Status:  http.StatusBadRequest,
		Code:    protocol.CodeInvalidPlayerName,
		Message: "Player names must be 1-16 ASCII letters, digits, or underscores.",
	}
}

func notFound(message string) *Error {
	return &Error{Status: http.StatusNotFound, Code: protocol.CodeNotFound, Message: message}
}

func conflict(message string) *Error {
	return &Error{Status: http.StatusConflict, Code: protocol.CodeConflict, Message: message}
}

func revisionConflict(expected string) *Error {
	return &Error{
		Status:  http.StatusConflict,
		Code:    protocol.CodeRevisionConflict,
		Message: "The resource revision does not match.",
		Details: map[string]string{"expectedRevision": expected},
	}
}

func internalError() *Error {
	return &Error{
		Status:  http.StatusInternalServerError,
		Code:    protocol.CodeInternalError,
		Message: "The request could not be completed.",
	}
}
