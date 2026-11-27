// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Mofumofu

package protocol

type ServerPropertiesResponse struct {
	APIVersion      *string           `json:"apiVersion" protocolDefault:"1.0"`
	Revision        string            `json:"revision"`
	RestartRequired bool              `json:"restartRequired"`
	Properties      map[string]string `json:"properties" protocolDefault:"empty"`
}

func (r ServerPropertiesResponse) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

type ServerPropertiesPatchRequest struct {
	APIVersion       *string            `json:"apiVersion" protocolDefault:"1.0"`
	ExpectedRevision string             `json:"expectedRevision"`
	Changes          map[string]*string `json:"changes" protocolDefault:"empty"`
}

func (r ServerPropertiesPatchRequest) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

func (ServerPropertiesPatchRequest) confirmationPrepareRequest() {}

type ServerPropertiesPatchResponse struct {
	APIVersion      *string           `json:"apiVersion" protocolDefault:"1.0"`
	Revision        string            `json:"revision"`
	RestartRequired *bool             `json:"restartRequired" protocolDefault:"true"`
	AppliedAt       Timestamp         `json:"appliedAt"`
	Properties      map[string]string `json:"properties" protocolDefault:"empty"`
}

func (r ServerPropertiesPatchResponse) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}
