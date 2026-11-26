// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Mofumofu

package protocol

type ConsoleCommandRequest struct {
	APIVersion *string `json:"apiVersion" protocolDefault:"1.0"`
	Command    string  `json:"command"`
}

func (r ConsoleCommandRequest) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

type ConsoleCommandResponse struct {
	APIVersion  *string   `json:"apiVersion" protocolDefault:"1.0"`
	Accepted    bool      `json:"accepted"`
	SentAt      Timestamp `json:"sentAt"`
	OperationID *string   `json:"operationId"`
}

func (r ConsoleCommandResponse) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

type ConsoleEntry struct {
	Sequence  int64     `json:"sequence"`
	Timestamp Timestamp `json:"timestamp"`
	Level     string    `json:"level"`
	Source    string    `json:"source"`
	Message   string    `json:"message"`
}
