// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package confirmation

import "github.com/redstore-noob/Mofumofu/agent-go/internal/operations"

// Error is the shared Agent HTTP error contract.
type Error = operations.Error

func newError(status int, code, message string) *Error {
	return &Error{Status: status, Code: code, Message: message}
}
