// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package protocol

// ProbeEventRequest is the internal NekoProbe callback body.
type ProbeEventRequest struct {
	APIVersion *string           `json:"apiVersion" protocolDefault:"1.0"`
	EventID    string            `json:"eventId"`
	Type       string            `json:"type"`
	OccurredAt Timestamp         `json:"occurredAt"`
	Message    string            `json:"message"`
	Details    map[string]string `json:"details" protocolDefault:"empty"`
}

func (r ProbeEventRequest) GetAPIVersion() string { return apiVersionValue(r.APIVersion) }

type AgentAcknowledgementResponse struct {
	APIVersion *string `json:"apiVersion" protocolDefault:"1.0"`
	Accepted   bool    `json:"accepted"`
	Duplicate  bool    `json:"duplicate"`
}

func (r AgentAcknowledgementResponse) GetAPIVersion() string { return apiVersionValue(r.APIVersion) }
