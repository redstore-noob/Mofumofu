// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package protocol

// ServerInstallRequest is a Go Agent extension (not part of the C# contract).
// Remote callers cannot choose a Java executable; the Agent config supplies it.
type ServerInstallRequest struct {
	APIVersion       *string `json:"apiVersion" protocolDefault:"1.0"`
	Core             string  `json:"core"`
	MinecraftVersion string  `json:"minecraftVersion"`
	CoreVersion      *string `json:"coreVersion"`
	AcceptEula       bool    `json:"acceptEula"`
	Port             *int    `json:"port"`
	MaxPlayers       *int    `json:"maxPlayers"`
	MOTD             *string `json:"motd"`
}

func (r ServerInstallRequest) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

func (ServerInstallRequest) confirmationPrepareRequest() {}
