// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Mofumofu

package protocol

type ControlRequest struct {
	APIVersion *string `json:"apiVersion" protocolDefault:"1.0"`
	Action     string  `json:"action"`
}

func (r ControlRequest) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

func (ControlRequest) confirmationPrepareRequest() {}

type ControlResponse struct {
	APIVersion *string       `json:"apiVersion" protocolDefault:"1.0"`
	Action     string        `json:"action"`
	Operation  OperationInfo `json:"operation"`
}

func (r ControlResponse) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}
