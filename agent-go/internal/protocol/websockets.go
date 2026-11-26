// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Mofumofu

package protocol

// WebSocketFrame is the polymorphic frame contract. The type discriminator is
// always written as the first JSON property.
type WebSocketFrame interface {
	IProtocolMessage
	webSocketFrameType() string
}

type ConsoleFrame struct {
	APIVersion *string        `json:"apiVersion" protocolDefault:"1.0"`
	Cursor     string         `json:"cursor"`
	SentAt     Timestamp      `json:"sentAt"`
	Entries    []ConsoleEntry `json:"entries" protocolDefault:"empty"`
}

func (f ConsoleFrame) GetAPIVersion() string {
	return apiVersionValue(f.APIVersion)
}

func (ConsoleFrame) webSocketFrameType() string {
	return FrameTypeConsole
}

type EventFrame struct {
	APIVersion *string     `json:"apiVersion" protocolDefault:"1.0"`
	Cursor     string      `json:"cursor"`
	SentAt     Timestamp   `json:"sentAt"`
	Event      ServerEvent `json:"event"`
}

func (f EventFrame) GetAPIVersion() string {
	return apiVersionValue(f.APIVersion)
}

func (EventFrame) webSocketFrameType() string {
	return FrameTypeEvent
}

type SnapshotFrame struct {
	APIVersion       *string         `json:"apiVersion" protocolDefault:"1.0"`
	Cursor           string          `json:"cursor"`
	SentAt           Timestamp       `json:"sentAt"`
	Status           StatusResponse  `json:"status"`
	Players          []PlayerInfo    `json:"players" protocolDefault:"empty"`
	RecentConsole    []ConsoleEntry  `json:"recentConsole" protocolDefault:"empty"`
	ActiveOperations []OperationInfo `json:"activeOperations" protocolDefault:"empty"`
}

func (f SnapshotFrame) GetAPIVersion() string {
	return apiVersionValue(f.APIVersion)
}

func (SnapshotFrame) webSocketFrameType() string {
	return FrameTypeSnapshot
}

type ResetFrame struct {
	APIVersion       *string   `json:"apiVersion" protocolDefault:"1.0"`
	Cursor           string    `json:"cursor"`
	SentAt           Timestamp `json:"sentAt"`
	Reason           string    `json:"reason"`
	RequiresSnapshot *bool     `json:"requiresSnapshot" protocolDefault:"true"`
}

func (f ResetFrame) GetAPIVersion() string {
	return apiVersionValue(f.APIVersion)
}

func (ResetFrame) webSocketFrameType() string {
	return FrameTypeReset
}

type OperationFrame struct {
	APIVersion *string       `json:"apiVersion" protocolDefault:"1.0"`
	Cursor     string        `json:"cursor"`
	SentAt     Timestamp     `json:"sentAt"`
	Operation  OperationInfo `json:"operation"`
}

func (f OperationFrame) GetAPIVersion() string {
	return apiVersionValue(f.APIVersion)
}

func (OperationFrame) webSocketFrameType() string {
	return FrameTypeOperation
}
