// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Mofumofu

package protocol_test

import (
	"reflect"
	"testing"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

func TestProtocolConstantLists(t *testing.T) {
	if got, want := protocol.KnownCapabilities, []string{"read", "players", "files", "backup", "control", "command", "console", "events", "properties", "tokens"}; !reflect.DeepEqual(got, want) {
		t.Fatalf("KnownCapabilities = %#v, want %#v", got, want)
	}
	if got, want := protocol.AllScopes, []string{"read", "players", "files", "backup", "control", "command"}; !reflect.DeepEqual(got, want) {
		t.Fatalf("AllScopes = %#v, want %#v", got, want)
	}
	if protocol.APIVersion != "1.0" || protocol.APIMajorVersion != 1 || protocol.WebSocketPath != "/ws" {
		t.Fatalf("unexpected core constants: %q, %d, %q", protocol.APIVersion, protocol.APIMajorVersion, protocol.WebSocketPath)
	}
	if protocol.TokenHexLength != 64 || protocol.ConfirmationLifetimeSeconds != 60 || protocol.DefaultTokenRotationGraceSeconds != 300 {
		t.Fatal("unexpected token or confirmation constants")
	}
}

func TestProtocolErrorCodes(t *testing.T) {
	codes := map[string]string{
		"invalid_request":           protocol.CodeInvalidRequest,
		"invalid_api_version":       protocol.CodeInvalidAPIVersion,
		"unsupported_api_version":   protocol.CodeUnsupportedAPIVersion,
		"unauthorized":              protocol.CodeUnauthorized,
		"forbidden":                 protocol.CodeForbidden,
		"insufficient_scope":        protocol.CodeInsufficientScope,
		"capability_not_supported":  protocol.CodeCapabilityNotSupported,
		"not_found":                 protocol.CodeNotFound,
		"conflict":                  protocol.CodeConflict,
		"revision_conflict":         protocol.CodeRevisionConflict,
		"invalid_token":             protocol.CodeInvalidToken,
		"invalid_player_name":       protocol.CodeInvalidPlayerName,
		"invalid_command":           protocol.CodeInvalidCommand,
		"invalid_path":              protocol.CodeInvalidPath,
		"payload_too_large":         protocol.CodePayloadTooLarge,
		"rate_limited":              protocol.CodeRateLimited,
		"server_offline":            protocol.CodeServerOffline,
		"server_running":            protocol.CodeServerRunning,
		"confirmation_expired":      protocol.CodeConfirmationExpired,
		"confirmation_already_used": protocol.CodeConfirmationAlreadyUsed,
		"confirmation_not_found":    protocol.CodeConfirmationNotFound,
		"operation_not_found":       protocol.CodeOperationNotFound,
		"operation_failed":          protocol.CodeOperationFailed,
		"internal_error":            protocol.CodeInternalError,
	}
	for expected, actual := range codes {
		if actual != expected {
			t.Errorf("error code = %q, want %q", actual, expected)
		}
	}
}

func TestProtocolDomainConstants(t *testing.T) {
	constants := [][2]string{
		{protocol.DeploymentModeAgent, "agent"},
		{protocol.DeploymentModeProbe, "probe"},
		{protocol.ServerStateOffline, "offline"},
		{protocol.ServerStateStarting, "starting"},
		{protocol.ServerStateRunning, "running"},
		{protocol.ServerStateStopping, "stopping"},
		{protocol.ServerStateCrashed, "crashed"},
		{protocol.ControlActionStart, "start"},
		{protocol.ControlActionStop, "stop"},
		{protocol.ControlActionRestart, "restart"},
		{protocol.ControlActionKill, "kill"},
		{protocol.PlayerListWhitelist, "whitelist"},
		{protocol.PlayerListOps, "ops"},
		{protocol.PlayerListBans, "bans"},
		{protocol.OperationStateQueued, "queued"},
		{protocol.OperationStateRunning, "running"},
		{protocol.OperationStateSucceeded, "succeeded"},
		{protocol.OperationStateFailed, "failed"},
		{protocol.ConfirmationActionControlStart, "control.start"},
		{protocol.ConfirmationActionControlStop, "control.stop"},
		{protocol.ConfirmationActionControlRestart, "control.restart"},
		{protocol.ConfirmationActionControlKill, "control.kill"},
		{protocol.ConfirmationActionBackupRestore, "backup.restore"},
		{protocol.ConfirmationActionBackupDelete, "backup.delete"},
		{protocol.ConfirmationActionFileDelete, "file.delete"},
		{protocol.ConfirmationActionPropertiesPatch, "properties.patch"},
		{protocol.EventTypePlayerJoined, "player.joined"},
		{protocol.EventTypePlayerLeft, "player.left"},
		{protocol.EventTypeServerStateChanged, "server.state_changed"},
		{protocol.EventTypeServerCrashed, "server.crashed"},
		{protocol.EventTypeServerAutoRestarted, "server.auto_restarted"},
		{protocol.EventTypeBackupRestoreReady, "backup.restore_ready"},
		{protocol.FrameTypeConsole, "console"},
		{protocol.FrameTypeEvent, "event"},
		{protocol.FrameTypeSnapshot, "snapshot"},
		{protocol.FrameTypeReset, "reset"},
		{protocol.FrameTypeOperation, "operation"},
	}
	for _, constant := range constants {
		if constant[0] != constant[1] {
			t.Errorf("constant = %q, want %q", constant[0], constant[1])
		}
	}
}

func TestGetServerBasePath(t *testing.T) {
	if _, err := protocol.GetServerBasePath(" \t"); err == nil {
		t.Fatal("GetServerBasePath(empty) error = nil")
	}
	got, err := protocol.GetServerBasePath("a/b c")
	if err != nil {
		t.Fatalf("GetServerBasePath() error = %v", err)
	}
	if want := "/v1/servers/a%2Fb%20c"; got != want {
		t.Fatalf("GetServerBasePath() = %q, want %q", got, want)
	}
}
