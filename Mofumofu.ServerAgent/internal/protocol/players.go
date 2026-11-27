// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Mofumofu

package protocol

type PlayerInfo struct {
	Name             string     `json:"name"`
	UUID             *UUID      `json:"uuid"`
	JoinedAt         *Timestamp `json:"joinedAt"`
	OnlineSeconds    *int64     `json:"onlineSeconds"`
	PingMilliseconds *int       `json:"pingMilliseconds"`
	GameMode         *string    `json:"gameMode"`
}

type OnlinePlayersResponse struct {
	APIVersion  *string      `json:"apiVersion" protocolDefault:"1.0"`
	OnlineCount int          `json:"onlineCount"`
	MaxPlayers  int          `json:"maxPlayers"`
	ObservedAt  Timestamp    `json:"observedAt"`
	Players     []PlayerInfo `json:"players" protocolDefault:"empty"`
}

func (r OnlinePlayersResponse) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

type PlayerListEntry struct {
	Name                string     `json:"name"`
	UUID                *UUID      `json:"uuid"`
	OpLevel             *int       `json:"opLevel"`
	BypassesPlayerLimit *bool      `json:"bypassesPlayerLimit"`
	Reason              *string    `json:"reason"`
	Source              *string    `json:"source"`
	CreatedAt           *Timestamp `json:"createdAt"`
	ExpiresAt           *Timestamp `json:"expiresAt"`
}

type PlayerListResponse struct {
	APIVersion *string           `json:"apiVersion" protocolDefault:"1.0"`
	Kind       string            `json:"kind"`
	Revision   string            `json:"revision"`
	Entries    []PlayerListEntry `json:"entries" protocolDefault:"empty"`
}

func (r PlayerListResponse) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

type PlayerAddRequest struct {
	APIVersion          *string    `json:"apiVersion" protocolDefault:"1.0"`
	PlayerName          string     `json:"playerName"`
	ExpectedRevision    *string    `json:"expectedRevision"`
	OpLevel             *int       `json:"opLevel"`
	BypassesPlayerLimit *bool      `json:"bypassesPlayerLimit"`
	Reason              *string    `json:"reason"`
	ExpiresAt           *Timestamp `json:"expiresAt"`
}

func (r PlayerAddRequest) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

type PlayerRemoveRequest struct {
	APIVersion       *string `json:"apiVersion" protocolDefault:"1.0"`
	PlayerName       string  `json:"playerName"`
	ExpectedRevision *string `json:"expectedRevision"`
}

func (r PlayerRemoveRequest) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

type PlayerMutationResponse struct {
	APIVersion *string         `json:"apiVersion" protocolDefault:"1.0"`
	Kind       string          `json:"kind"`
	Revision   string          `json:"revision"`
	Entry      PlayerListEntry `json:"entry"`
	Removed    bool            `json:"removed"`
}

func (r PlayerMutationResponse) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}
