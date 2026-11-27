// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Mofumofu

package protocol

type HandshakeRequest struct {
	APIVersion            *string  `json:"apiVersion" protocolDefault:"1.0"`
	ClientName            string   `json:"clientName"`
	ClientVersion         string   `json:"clientVersion"`
	RequestedCapabilities []string `json:"requestedCapabilities" protocolDefault:"empty"`
}

func (r HandshakeRequest) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

type HandshakeResponse struct {
	APIVersion                  *string   `json:"apiVersion" protocolDefault:"1.0"`
	InstanceID                  string    `json:"instanceId"`
	ServerName                  string    `json:"serverName"`
	DeploymentMode              string    `json:"deploymentMode"`
	ServerImplementationVersion string    `json:"serverImplementationVersion"`
	Capabilities                []string  `json:"capabilities" protocolDefault:"empty"`
	Scopes                      []string  `json:"scopes" protocolDefault:"empty"`
	ServerTime                  Timestamp `json:"serverTime"`
	WebSocketPath               *string   `json:"webSocketPath" protocolDefault:"/ws"`
	TokenID                     *string   `json:"tokenId"`
}

func (r HandshakeResponse) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}
