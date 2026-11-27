// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package server

// Error maps supervisor failures to the existing Agent error contract.
type Error struct {
	Status  int
	Code    string
	Message string
}

func (e *Error) Error() string                   { return e.Message }
func (e *Error) ErrorCode() string               { return e.Code }
func (e *Error) HTTPStatus() int                 { return e.Status }
func (e *Error) ErrorDetails() map[string]string { return nil }
func (e *Error) OperationIdentifier() string     { return "" }

func ServerOffline() *Error {
	return &Error{Status: 409, Code: "server_offline", Message: "The server is not running."}
}

func ServerRunning() *Error {
	return &Error{Status: 409, Code: "server_running", Message: "The server is already running."}
}

func InvalidCommand() *Error {
	return &Error{Status: 400, Code: "invalid_command", Message: "Commands must be 1-256 non-control UTF-16 code units and not blank."}
}
