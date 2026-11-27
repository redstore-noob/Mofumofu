// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package httpapi

import (
	"bytes"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestValidateVersion(t *testing.T) {
	valid := []string{"1.0", "1.99", "01.00", "1.2147483647"}
	for _, version := range valid {
		if err := validateVersion(version); err != nil {
			t.Errorf("%q: %v", version, err)
		}
	}
	invalid := []string{"", "1", "1.0.0", "v1.0", "+1.0", "1.-1", "1.2147483648", "2147483648.0"}
	for _, version := range invalid {
		err := validateVersion(version)
		var apiErr *Error
		if !errors.As(err, &apiErr) || apiErr.Code != "invalid_api_version" {
			t.Errorf("%q: got %v", version, err)
		}
	}
	for _, version := range []string{"0.9", "2.0"} {
		err := validateVersion(version)
		var apiErr *Error
		if !errors.As(err, &apiErr) || apiErr.Code != "unsupported_api_version" {
			t.Errorf("%q: got %v", version, err)
		}
	}
}

func TestReadBodyDefaultsAndValidatesVersion(t *testing.T) {
	var target struct {
		APIVersion string `json:"apiVersion"`
		Command    string `json:"command"`
	}
	request := httptest.NewRequest(http.MethodPost, "/", strings.NewReader(`{"command":"list"}`))
	if err := ReadBody(request, &target); err != nil {
		t.Fatal(err)
	}
	if target.Command != "list" {
		t.Fatalf("got %+v", target)
	}

	request = httptest.NewRequest(http.MethodPost, "/", strings.NewReader(`{"ApiVersion":"2.0","command":"list"}`))
	err := ReadBody(request, &target)
	var apiErr *Error
	if !errors.As(err, &apiErr) || apiErr.Code != "unsupported_api_version" {
		t.Fatalf("got %v", err)
	}

	for _, body := range []string{"null", `{"apiVersion":null}`, `{"apiVersion":2}`, `{`} {
		request = httptest.NewRequest(http.MethodPost, "/", strings.NewReader(body))
		err = ReadBody(request, &target)
		if !errors.As(err, &apiErr) || apiErr.Code != "invalid_request" {
			t.Errorf("%s: got %v", body, err)
		}
	}
}

func TestReadBodyRejectsOversizeWithoutReadingEverything(t *testing.T) {
	request := httptest.NewRequest(http.MethodPut, "/", bytes.NewReader(make([]byte, MaxRequestBodyBytes+1)))
	var target map[string]any
	err := ReadBody(request, &target)
	var apiErr *Error
	if !errors.As(err, &apiErr) || apiErr.Status != http.StatusRequestEntityTooLarge {
		t.Fatalf("got %v", err)
	}
}

func TestWriteErrorShapeAndAuthenticationHeader(t *testing.T) {
	recorder := httptest.NewRecorder()
	request := httptest.NewRequest(http.MethodGet, "/", nil)
	request = request.WithContext(request.Context())
	WriteError(recorder, request, Unauthorized())
	if recorder.Code != http.StatusUnauthorized || recorder.Header().Get("WWW-Authenticate") != "Bearer" {
		t.Fatalf("status=%d headers=%v", recorder.Code, recorder.Header())
	}
	payload, _ := io.ReadAll(recorder.Body)
	if !strings.Contains(string(payload), `"apiVersion":"1.0"`) ||
		!strings.Contains(string(payload), `"code":"unauthorized"`) ||
		!strings.Contains(string(payload), `"requestId":""`) {
		t.Fatalf("unexpected error: %s", payload)
	}
	if strings.Contains(string(payload), `"operationId"`) || strings.Contains(string(payload), `"details"`) {
		t.Fatalf("null fields were not omitted: %s", payload)
	}
}
