// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Mofumofu

package protocol

import (
	"errors"
	"net/url"
	"strings"
)

const (
	APIVersion                       = "1.0"
	APIMajorVersion                  = 1
	ServerCollectionPath             = "/v1/servers"
	ServerBasePath                   = "/v1/servers/{instanceId}"
	WebSocketPath                    = "/ws"
	TokenRandomBytes                 = 32
	TokenHexLength                   = TokenRandomBytes * 2
	MaxPlayerNameLength              = 16
	MaxCommandLength                 = 256
	ConfirmationLifetimeSeconds      = 60
	DefaultTokenRotationGraceSeconds = 300
)

// GetServerBasePath returns the collection-relative path for an instance ID.
func GetServerBasePath(instanceID string) (string, error) {
	if strings.TrimSpace(instanceID) == "" {
		return "", errors.New("instance ID cannot be empty")
	}
	return ServerCollectionPath + "/" + url.PathEscape(instanceID), nil
}

// Capability values are open-ended. Receivers must ignore unknown values.
const (
	CapabilityRead       = "read"
	CapabilityPlayers    = "players"
	CapabilityFiles      = "files"
	CapabilityBackup     = "backup"
	CapabilityControl    = "control"
	CapabilityCommand    = "command"
	CapabilityConsole    = "console"
	CapabilityEvents     = "events"
	CapabilityProperties = "properties"
	CapabilityTokens     = "tokens"
)

var KnownCapabilities = []string{
	CapabilityRead,
	CapabilityPlayers,
	CapabilityFiles,
	CapabilityBackup,
	CapabilityControl,
	CapabilityCommand,
	CapabilityConsole,
	CapabilityEvents,
	CapabilityProperties,
	CapabilityTokens,
}

const (
	ScopeRead    = "read"
	ScopePlayers = "players"
	ScopeFiles   = "files"
	ScopeBackup  = "backup"
	ScopeControl = "control"
	ScopeCommand = "command"
)

var AllScopes = []string{
	ScopeRead,
	ScopePlayers,
	ScopeFiles,
	ScopeBackup,
	ScopeControl,
	ScopeCommand,
}

const (
	DeploymentModeAgent = "agent"
	DeploymentModeProbe = "probe"
)

const (
	ServerStateOffline  = "offline"
	ServerStateStarting = "starting"
	ServerStateRunning  = "running"
	ServerStateStopping = "stopping"
	ServerStateCrashed  = "crashed"
)

const (
	ControlActionStart   = "start"
	ControlActionStop    = "stop"
	ControlActionRestart = "restart"
	ControlActionKill    = "kill"
)

const (
	PlayerListWhitelist = "whitelist"
	PlayerListOps       = "ops"
	PlayerListBans      = "bans"
)

const (
	OperationStateQueued    = "queued"
	OperationStateRunning   = "running"
	OperationStateSucceeded = "succeeded"
	OperationStateFailed    = "failed"
)

const (
	ConfirmationActionControlStart    = "control.start"
	ConfirmationActionControlStop     = "control.stop"
	ConfirmationActionControlRestart  = "control.restart"
	ConfirmationActionControlKill     = "control.kill"
	ConfirmationActionBackupRestore   = "backup.restore"
	ConfirmationActionBackupDelete    = "backup.delete"
	ConfirmationActionFileDelete      = "file.delete"
	ConfirmationActionPropertiesPatch = "properties.patch"
)

const (
	EventTypePlayerJoined        = "player.joined"
	EventTypePlayerLeft          = "player.left"
	EventTypeServerStateChanged  = "server.state_changed"
	EventTypeServerCrashed       = "server.crashed"
	EventTypeServerAutoRestarted = "server.auto_restarted"
	EventTypeBackupRestoreReady  = "backup.restore_ready"
)

const (
	FrameTypeConsole   = "console"
	FrameTypeEvent     = "event"
	FrameTypeSnapshot  = "snapshot"
	FrameTypeReset     = "reset"
	FrameTypeOperation = "operation"
)

const (
	CodeInvalidRequest          = "invalid_request"
	CodeInvalidAPIVersion       = "invalid_api_version"
	CodeUnsupportedAPIVersion   = "unsupported_api_version"
	CodeUnauthorized            = "unauthorized"
	CodeForbidden               = "forbidden"
	CodeInsufficientScope       = "insufficient_scope"
	CodeCapabilityNotSupported  = "capability_not_supported"
	CodeNotFound                = "not_found"
	CodeConflict                = "conflict"
	CodeRevisionConflict        = "revision_conflict"
	CodeInvalidToken            = "invalid_token"
	CodeInvalidPlayerName       = "invalid_player_name"
	CodeInvalidCommand          = "invalid_command"
	CodeInvalidPath             = "invalid_path"
	CodePayloadTooLarge         = "payload_too_large"
	CodeRateLimited             = "rate_limited"
	CodeServerOffline           = "server_offline"
	CodeServerRunning           = "server_running"
	CodeConfirmationExpired     = "confirmation_expired"
	CodeConfirmationAlreadyUsed = "confirmation_already_used"
	CodeConfirmationNotFound    = "confirmation_not_found"
	CodeOperationNotFound       = "operation_not_found"
	CodeOperationFailed         = "operation_failed"
	CodeInternalError           = "internal_error"
)
