// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package httpapi

import (
	"io"
	"net"
	"net/http"
	"strings"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/service"
)

func (s *Server) probeEvents(w http.ResponseWriter, r *http.Request) {
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		WriteError(w, r, errorf(http.StatusForbidden, "forbidden", "Probe callbacks are restricted to loopback."))
		return
	}
	address := net.ParseIP(host)
	if address == nil || !address.IsLoopback() {
		WriteError(w, r, errorf(http.StatusForbidden, "forbidden", "Probe callbacks are restricted to loopback."))
		return
	}
	if r.ContentLength > MaxRequestBodyBytes {
		WriteError(w, r, PayloadTooLarge())
		return
	}
	payload, err := io.ReadAll(io.LimitReader(r.Body, MaxRequestBodyBytes+1))
	if err != nil {
		WriteError(w, r, InvalidRequest("The Probe event body is invalid JSON."))
		return
	}
	if len(payload) > MaxRequestBodyBytes {
		WriteError(w, r, PayloadTooLarge())
		return
	}
	ack, duplicate, err := s.service.AcceptProbeEvent(r.Context(), service.ProbeEventHeaders{
		InstanceID: headerValue(r, "X-Mofumofu-Instance-Id"),
		Timestamp:  headerValue(r, "X-Mofumofu-Timestamp"),
		Nonce:      headerValue(r, "X-Mofumofu-Nonce"),
		Signature:  headerValue(r, "X-Mofumofu-Signature"),
	}, payload)
	if err != nil {
		WriteError(w, r, err)
		return
	}
	status := http.StatusAccepted
	if duplicate {
		status = http.StatusOK
	}
	writeJSON(w, status, ack)
}

func headerValue(r *http.Request, name string) string {
	return strings.Join(r.Header.Values(name), ",")
}
