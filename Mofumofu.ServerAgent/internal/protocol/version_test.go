// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Mofumofu

package protocol_test

import (
	"errors"
	"reflect"
	"testing"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

func TestValidateVersionAcceptsExactlyTwoUnsignedInt32Segments(t *testing.T) {
	for _, version := range []string{
		"1.0",
		"01.00",
		"0001.000",
		"1.2147483647",
		"0000000000000000000000000000000000000001.0",
	} {
		t.Run(version, func(t *testing.T) {
			if err := protocol.ValidateVersion(version); err != nil {
				t.Fatalf("ValidateVersion(%q) = %v, want nil", version, err)
			}
			if !protocol.IsCompatible(version) {
				t.Fatalf("IsCompatible(%q) = false, want true", version)
			}
			if err := protocol.EnsureCompatible(version); err != nil {
				t.Fatalf("EnsureCompatible(%q) = %v, want nil", version, err)
			}
		})
	}
}

func TestValidateVersionRejectsMalformedAndOutOfRangeSegments(t *testing.T) {
	for _, version := range []string{
		"",
		" ",
		"1",
		"1.0.0",
		".0",
		"1.",
		"+1.0",
		"-1.0",
		"1.-0",
		"1.+0",
		" 1.0",
		"1.0 ",
		"1 .0",
		"1. 0",
		"１.0",
		"1.2147483648",
		"2147483648.0",
		"1.0\n",
	} {
		t.Run(version, func(t *testing.T) {
			err := protocol.ValidateVersion(version)
			if err == nil {
				t.Fatalf("ValidateVersion(%q) = nil, want invalid_api_version", version)
			}
			if err.Code != protocol.CodeInvalidAPIVersion {
				t.Fatalf("error code = %q, want %q", err.Code, protocol.CodeInvalidAPIVersion)
			}
			wantDetails := map[string]string{
				"requestedVersion": version,
				"supportedVersion": protocol.APIVersion,
			}
			if !reflect.DeepEqual(err.Details, wantDetails) {
				t.Fatalf("Details = %#v, want %#v", err.Details, wantDetails)
			}
		})
	}
}

func TestValidateVersionRejectsUnsupportedMajors(t *testing.T) {
	for _, version := range []string{"0.0", "2.0", "2147483647.0", "0002.000"} {
		t.Run(version, func(t *testing.T) {
			err := protocol.ValidateVersion(version)
			if err == nil {
				t.Fatalf("ValidateVersion(%q) = nil, want unsupported_api_version", version)
			}
			if err.Code != protocol.CodeUnsupportedAPIVersion {
				t.Fatalf("error code = %q, want %q", err.Code, protocol.CodeUnsupportedAPIVersion)
			}
			if protocol.IsCompatible(version) {
				t.Fatalf("IsCompatible(%q) = true, want false", version)
			}
		})
	}
}

func TestVersionErrorsExposeTypedCodeMessageAndDetails(t *testing.T) {
	err := protocol.ValidateVersion("2.3")
	if err == nil {
		t.Fatal("ValidateVersion() = nil")
	}
	var typed *protocol.ProtocolVersionError
	if !errors.As(err, &typed) {
		t.Fatalf("error type = %T, want *protocol.ProtocolVersionError", err)
	}
	if typed.Message != "Protocol major version '2' is not compatible with supported major version '1' (apiVersion '1.0'). Use a compatible client and server." {
		t.Fatalf("Message = %q", typed.Message)
	}
	wantDetails := map[string]string{
		"requestedVersion": "2.3",
		"requestedMajor":   "2",
		"supportedMajor":   "1",
		"supportedVersion": "1.0",
	}
	if !reflect.DeepEqual(typed.Details, wantDetails) {
		t.Fatalf("Details = %#v, want %#v", typed.Details, wantDetails)
	}
	if typed.GetAPIVersion() != "1.0" {
		t.Fatalf("GetAPIVersion() = %q, want 1.0", typed.GetAPIVersion())
	}
}

func TestInvalidVersionFactoriesMatchContracts(t *testing.T) {
	invalid := protocol.CreateInvalidAPIVersion(nil)
	if invalid.Code != protocol.CodeInvalidAPIVersion || invalid.Message != "Invalid apiVersion '<null>'. Expected a version in 'major.minor' form, such as '1.0'." {
		t.Fatalf("invalid error = %#v", invalid)
	}
	if !reflect.DeepEqual(invalid.Details, map[string]string{"requestedVersion": "", "supportedVersion": "1.0"}) {
		t.Fatalf("invalid Details = %#v", invalid.Details)
	}

	unsupported := protocol.CreateUnsupportedAPIVersion("0002.000")
	if unsupported.Details["requestedMajor"] != "0002" {
		t.Fatalf("requestedMajor = %q, want original text 0002", unsupported.Details["requestedMajor"])
	}

	encoded := mustMarshal(t, protocol.CreateInvalidAPIVersion(protocol.Ptr("1.0.0")))
	const expected = `{"apiVersion":"1.0","code":"invalid_api_version","message":"Invalid apiVersion \u00271.0.0\u0027. Expected a version in \u0027major.minor\u0027 form, such as \u00271.0\u0027.","details":{"requestedVersion":"1.0.0","supportedVersion":"1.0"}}`
	if encoded != expected {
		t.Fatalf("Marshal() = %s\nwant       = %s", encoded, expected)
	}

	encoded = mustMarshal(t, protocol.CreateUnsupportedAPIVersion("2.3"))
	const unsupportedExpected = `{"apiVersion":"1.0","code":"unsupported_api_version","message":"Protocol major version \u00272\u0027 is not compatible with supported major version \u00271\u0027 (apiVersion \u00271.0\u0027). Use a compatible client and server.","details":{"requestedVersion":"2.3","requestedMajor":"2","supportedMajor":"1","supportedVersion":"1.0"}}`
	if encoded != unsupportedExpected {
		t.Fatalf("unsupported Marshal() = %s\nwant                    = %s", encoded, unsupportedExpected)
	}
}
