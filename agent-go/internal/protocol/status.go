// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Mofumofu

package protocol

type StatusResponse struct {
	APIVersion       *string   `json:"apiVersion" protocolDefault:"1.0"`
	InstanceID       string    `json:"instanceId"`
	State            *string   `json:"state" protocolDefault:"offline"`
	OnlinePlayers    int       `json:"onlinePlayers"`
	MaxPlayers       int       `json:"maxPlayers"`
	CPUPercent       *float64  `json:"cpuPercent"`
	MemoryUsedBytes  *int64    `json:"memoryUsedBytes"`
	MemoryMaxBytes   *int64    `json:"memoryMaxBytes"`
	TPS              *float64  `json:"tps"`
	UptimeSeconds    *int64    `json:"uptimeSeconds"`
	MinecraftVersion *string   `json:"minecraftVersion"`
	MOTD             *string   `json:"motd"`
	ObservedAt       Timestamp `json:"observedAt"`
}

func (r StatusResponse) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

func (r *StatusResponse) UnmarshalJSON(data []byte) error {
	type alias StatusResponse
	var decoded alias
	if err := unmarshalInitialized(data, &decoded, true); err != nil {
		return err
	}
	*r = StatusResponse(decoded)
	return nil
}
