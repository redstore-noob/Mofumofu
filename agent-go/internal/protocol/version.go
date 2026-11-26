// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Mofumofu

package protocol

import (
	"fmt"
	"strconv"
	"strings"
)

// ProtocolVersionError is both a Go error and an API error payload.
type ProtocolVersionError struct {
	APIError
}

// VersionError is an alias retained for callers that prefer the shorter name.
type VersionError = ProtocolVersionError

// ProtocolVersionException mirrors the C# type name while remaining a Go error.
type ProtocolVersionException = ProtocolVersionError

func (e *ProtocolVersionError) Error() string {
	if e == nil {
		return ""
	}
	return e.Message
}

// ValidateVersion validates exactly two unsigned Int32 decimal segments and
// accepts every minor version whose major segment is APIMajorVersion.
func ValidateVersion(apiVersion string) *ProtocolVersionError {
	if apiError := GetCompatibilityError(apiVersion); apiError != nil {
		return &ProtocolVersionError{APIError: *apiError}
	}
	return nil
}

// GetCompatibilityError returns the API error for an invalid or unsupported
// version, or nil when the version is compatible.
func GetCompatibilityError(apiVersion string) *APIError {
	major, _, ok := parseVersion(apiVersion)
	if !ok {
		return CreateInvalidAPIVersion(&apiVersion)
	}
	if major != APIMajorVersion {
		return CreateUnsupportedAPIVersion(apiVersion)
	}
	return nil
}

// IsCompatible reports whether apiVersion can be used by this Agent.
func IsCompatible(apiVersion string) bool {
	return GetCompatibilityError(apiVersion) == nil
}

// EnsureCompatible returns a ProtocolVersionError when validation fails.
func EnsureCompatible(apiVersion string) error {
	if err := ValidateVersion(apiVersion); err != nil {
		return err
	}
	return nil
}

// CreateInvalidAPIVersion builds the exact invalid-version error contract.
// A nil requestedVersion represents an explicit JSON null or absent query.
func CreateInvalidAPIVersion(requestedVersion *string) *APIError {
	requested := ""
	messageVersion := "<null>"
	if requestedVersion != nil {
		requested = *requestedVersion
		messageVersion = requested
	}
	return &APIError{
		APIVersion: stringPointer(APIVersion),
		Code:       CodeInvalidAPIVersion,
		Message: fmt.Sprintf(
			"Invalid apiVersion '%s'. Expected a version in 'major.minor' form, such as '%s'.",
			messageVersion,
			APIVersion,
		),
		Details: map[string]string{
			"requestedVersion": requested,
			"supportedVersion": APIVersion,
		},
	}
}

// CreateUnsupportedAPIVersion builds the exact unsupported-major error contract.
func CreateUnsupportedAPIVersion(requestedVersion string) *APIError {
	major := strings.Split(requestedVersion, ".")[0]
	return &APIError{
		APIVersion: stringPointer(APIVersion),
		Code:       CodeUnsupportedAPIVersion,
		Message: fmt.Sprintf(
			"Protocol major version '%s' is not compatible with supported major version '%d' (apiVersion '%s'). Use a compatible client and server.",
			major,
			APIMajorVersion,
			APIVersion,
		),
		Details: map[string]string{
			"requestedVersion": requestedVersion,
			"requestedMajor":   major,
			"supportedMajor":   strconv.Itoa(APIMajorVersion),
			"supportedVersion": APIVersion,
		},
	}
}

func parseVersion(apiVersion string) (uint32, uint32, bool) {
	if apiVersion == "" || strings.TrimSpace(apiVersion) != apiVersion {
		return 0, 0, false
	}
	parts := strings.Split(apiVersion, ".")
	if len(parts) != 2 {
		return 0, 0, false
	}
	major, ok := parseVersionSegment(parts[0])
	if !ok {
		return 0, 0, false
	}
	minor, ok := parseVersionSegment(parts[1])
	if !ok {
		return 0, 0, false
	}
	return major, minor, true
}

func parseVersionSegment(segment string) (uint32, bool) {
	if segment == "" {
		return 0, false
	}
	for i := 0; i < len(segment); i++ {
		if segment[i] < '0' || segment[i] > '9' {
			return 0, false
		}
	}
	value, err := strconv.ParseUint(segment, 10, 31)
	if err != nil {
		return 0, false
	}
	return uint32(value), true
}
