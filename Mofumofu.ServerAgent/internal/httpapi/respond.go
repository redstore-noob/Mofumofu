// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package httpapi

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"reflect"
	"strconv"
	"strings"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

const MaxRequestBodyBytes = 4 * 1024 * 1024

// Error is the Agent's mapped HTTP failure. ErrorCode lets audit record the
// same failed:<code> result as the C# implementation.
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

func errorf(status int, code, message string) *Error {
	return &Error{Status: status, Code: code, Message: message}
}

func InvalidRequest(message string) *Error {
	return errorf(http.StatusBadRequest, "invalid_request", message)
}

func NotFound(message string) *Error {
	if message == "" {
		message = "The requested resource was not found."
	}
	return errorf(http.StatusNotFound, "not_found", message)
}

func Conflict(message string) *Error {
	return errorf(http.StatusConflict, "conflict", message)
}

func RevisionConflict(expected string) *Error {
	return &Error{
		Status:  http.StatusConflict,
		Code:    "revision_conflict",
		Message: "The resource revision does not match.",
		Details: map[string]string{"expectedRevision": expected},
	}
}

func PayloadTooLarge() *Error {
	return errorf(http.StatusRequestEntityTooLarge, "payload_too_large", "The request body is too large.")
}

func AuditUnavailable() *Error {
	return errorf(http.StatusInternalServerError, "AUDIT_UNAVAILABLE", "Audit storage is unavailable; the write was not accepted.")
}

func Unauthorized() *Error {
	return errorf(http.StatusUnauthorized, "unauthorized", "The authentication registry is unavailable or invalid.")
}

func writeJSON(w http.ResponseWriter, status int, value any) {
	payload, err := protocol.Marshal(value)
	if err != nil {
		status = http.StatusInternalServerError
		payload = []byte(`{"apiVersion":"1.0","code":"internal_error","message":"The request could not be completed."}`)
	}
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	_, _ = w.Write(payload)
}

type errorBody struct {
	APIVersion  string             `json:"apiVersion"`
	Code        string             `json:"code"`
	Message     string             `json:"message"`
	Details     *map[string]string `json:"details,omitempty"`
	RequestID   string             `json:"requestId"`
	OperationID *string            `json:"operationId,omitempty"`
}

func WriteError(w http.ResponseWriter, r *http.Request, err error) {
	status := http.StatusInternalServerError
	code := "internal_error"
	message := "The request could not be completed."
	var details map[string]string
	var operationID string
	var failure interface{ HTTPStatus() int }
	if errors.As(err, &failure) && !isNilInterface(failure) {
		status = failure.HTTPStatus()
		code = "internal_error"
		message = err.Error()
		var coded interface{ ErrorCode() string }
		if errors.As(err, &coded) && coded.ErrorCode() != "" {
			code = coded.ErrorCode()
		}
		var detailed interface{ ErrorDetails() map[string]string }
		if errors.As(err, &detailed) {
			details = detailed.ErrorDetails()
		}
		var operated interface{ OperationIdentifier() string }
		if errors.As(err, &operated) {
			operationID = operated.OperationIdentifier()
		}
	}
	if status == http.StatusUnauthorized {
		w.Header().Set("WWW-Authenticate", "Bearer")
	}
	body := errorBody{
		APIVersion: "1.0",
		Code:       code,
		Message:    message,
		RequestID:  RequestIDFrom(r.Context()),
	}
	if details != nil {
		body.Details = &details
	}
	if operationID != "" {
		body.OperationID = &operationID
	}
	writeJSON(w, status, body)
}

// ReadBody enforces the Agent-wide 4 MiB limit before decoding one protocol
// object. POST/PUT apiVersion defaults to 1.0 when omitted.
func ReadBody(r *http.Request, target any) error {
	if r.ContentLength > MaxRequestBodyBytes {
		return PayloadTooLarge()
	}
	var reader io.Reader = r.Body
	if reader == nil {
		return InvalidRequest("The request body must be a valid protocol JSON object.")
	}
	payload, err := io.ReadAll(io.LimitReader(reader, MaxRequestBodyBytes+1))
	if err != nil {
		return InvalidRequest("The request body must be a valid protocol JSON object.")
	}
	if len(payload) > MaxRequestBodyBytes {
		return PayloadTooLarge()
	}
	if err := validateBodyVersion(payload); err != nil {
		return err
	}
	if err := protocol.Unmarshal(payload, target); err != nil {
		return InvalidRequest("The request body must be a valid protocol JSON object.")
	}
	return nil
}

func ValidateQueryVersion(r *http.Request) error {
	values, ok := r.URL.Query()["apiVersion"]
	if !ok {
		return invalidAPIVersion("")
	}
	return validateVersion(strings.Join(values, ","))
}

func validateBodyVersion(payload []byte) error {
	var envelope struct {
		APIVersion json.RawMessage `json:"apiVersion"`
	}
	if err := json.Unmarshal(payload, &envelope); err != nil {
		return InvalidRequest("The request body must be a valid protocol JSON object.")
	}
	if len(envelope.APIVersion) == 0 {
		return validateVersion("1.0")
	}
	if string(envelope.APIVersion) == "null" {
		return InvalidRequest("The request body must be a valid protocol JSON object.")
	}
	var version string
	if err := json.Unmarshal(envelope.APIVersion, &version); err != nil {
		return InvalidRequest("The request body must be a valid protocol JSON object.")
	}
	return validateVersion(version)
}

func invalidAPIVersion(version string) *Error {
	return &Error{
		Status:  http.StatusBadRequest,
		Code:    "invalid_api_version",
		Message: fmt.Sprintf("Invalid apiVersion '%s'. Expected a version in 'major.minor' form, such as '1.0'.", version),
		Details: map[string]string{"requestedVersion": version, "supportedVersion": "1.0"},
	}
}

func validateVersion(version string) error {
	parts := strings.Split(version, ".")
	if len(parts) != 2 || parts[0] == "" || parts[1] == "" || !decimal(parts[0]) || !decimal(parts[1]) {
		return invalidAPIVersion(version)
	}
	major, err := strconv.ParseInt(parts[0], 10, 32)
	if err != nil {
		return invalidAPIVersion(version)
	}
	if major != 1 {
		requestedMajor := parts[0]
		return &Error{
			Status:  http.StatusBadRequest,
			Code:    "unsupported_api_version",
			Message: fmt.Sprintf("Protocol major version '%s' is not compatible with supported major version '1' (apiVersion '1.0'). Use a compatible client and server.", requestedMajor),
			Details: map[string]string{
				"requestedVersion": version,
				"requestedMajor":   requestedMajor,
				"supportedMajor":   "1",
				"supportedVersion": "1.0",
			},
		}
	}
	if _, err := strconv.ParseInt(parts[1], 10, 32); err != nil {
		return invalidAPIVersion(version)
	}
	return nil
}

func decimal(value string) bool {
	for _, r := range value {
		if r < '0' || r > '9' {
			return false
		}
	}
	return true
}

func isNilInterface(value any) bool {
	if value == nil {
		return true
	}
	reflected := reflect.ValueOf(value)
	switch reflected.Kind() {
	case reflect.Chan, reflect.Func, reflect.Interface, reflect.Map, reflect.Pointer, reflect.Slice:
		return reflected.IsNil()
	default:
		return false
	}
}
