// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package operations

// Error maps an in-memory operation or confirmation failure to the Agent HTTP
// error contract without importing the transport package.
type Error struct {
	Status      int
	Code        string
	Message     string
	Details     map[string]string
	OperationID string
}

func (e *Error) Error() string                   { return e.Message }
func (e *Error) ErrorCode() string               { return e.Code }
func (e *Error) HTTPStatus() int                 { return e.Status }
func (e *Error) ErrorDetails() map[string]string { return e.Details }
func (e *Error) OperationIdentifier() string     { return e.OperationID }
