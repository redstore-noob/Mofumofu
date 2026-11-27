// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Mofumofu

package protocol_test

import (
	"reflect"
	"strings"
	"testing"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

func TestDTOJSONFieldNamesAndOrder(t *testing.T) {
	tests := []struct {
		typeOf    reflect.Type
		fields    []string
		frameType string
	}{
		{reflect.TypeOf(protocol.APIError{}), []string{"apiVersion", "code", "message", "details", "requestId", "operationId"}, ""},
		{reflect.TypeOf(protocol.OperationInfo{}), []string{"id", "kind", "state", "createdAt", "startedAt", "completedAt", "progressPercent", "message", "metadata", "error"}, ""},
		{reflect.TypeOf(protocol.OperationResponse{}), []string{"apiVersion", "operation"}, ""},
		{reflect.TypeOf(protocol.ConfirmationPrepareResponse{}), []string{"apiVersion", "confirmationId", "action", "summary", "createdAt", "expiresAt", "expiresInSeconds"}, ""},
		{reflect.TypeOf(protocol.ConfirmationExecuteRequest{}), []string{"apiVersion", "confirmationId"}, ""},
		{reflect.TypeOf(protocol.ConfirmationExecuteResponse{}), []string{"apiVersion", "operation"}, ""},
		{reflect.TypeOf(protocol.ServerEvent{}), []string{"eventId", "type", "occurredAt", "message", "details"}, ""},
		{reflect.TypeOf(protocol.HandshakeRequest{}), []string{"apiVersion", "clientName", "clientVersion", "requestedCapabilities"}, ""},
		{reflect.TypeOf(protocol.HandshakeResponse{}), []string{"apiVersion", "instanceId", "serverName", "deploymentMode", "serverImplementationVersion", "capabilities", "scopes", "serverTime", "webSocketPath", "tokenId"}, ""},
		{reflect.TypeOf(protocol.StatusResponse{}), []string{"apiVersion", "instanceId", "state", "onlinePlayers", "maxPlayers", "cpuPercent", "memoryUsedBytes", "memoryMaxBytes", "tps", "uptimeSeconds", "minecraftVersion", "motd", "observedAt"}, ""},
		{reflect.TypeOf(protocol.ConsoleCommandRequest{}), []string{"apiVersion", "command"}, ""},
		{reflect.TypeOf(protocol.ConsoleCommandResponse{}), []string{"apiVersion", "accepted", "sentAt", "operationId"}, ""},
		{reflect.TypeOf(protocol.ConsoleEntry{}), []string{"sequence", "timestamp", "level", "source", "message"}, ""},
		{reflect.TypeOf(protocol.PlayerInfo{}), []string{"name", "uuid", "joinedAt", "onlineSeconds", "pingMilliseconds", "gameMode"}, ""},
		{reflect.TypeOf(protocol.OnlinePlayersResponse{}), []string{"apiVersion", "onlineCount", "maxPlayers", "observedAt", "players"}, ""},
		{reflect.TypeOf(protocol.PlayerListEntry{}), []string{"name", "uuid", "opLevel", "bypassesPlayerLimit", "reason", "source", "createdAt", "expiresAt"}, ""},
		{reflect.TypeOf(protocol.PlayerListResponse{}), []string{"apiVersion", "kind", "revision", "entries"}, ""},
		{reflect.TypeOf(protocol.PlayerAddRequest{}), []string{"apiVersion", "playerName", "expectedRevision", "opLevel", "bypassesPlayerLimit", "reason", "expiresAt"}, ""},
		{reflect.TypeOf(protocol.PlayerRemoveRequest{}), []string{"apiVersion", "playerName", "expectedRevision"}, ""},
		{reflect.TypeOf(protocol.PlayerMutationResponse{}), []string{"apiVersion", "kind", "revision", "entry", "removed"}, ""},
		{reflect.TypeOf(protocol.FileEntry{}), []string{"path", "isDirectory", "sizeBytes", "modifiedAt", "revision", "writable"}, ""},
		{reflect.TypeOf(protocol.FileListResponse{}), []string{"apiVersion", "path", "entries"}, ""},
		{reflect.TypeOf(protocol.FileContentResponse{}), []string{"apiVersion", "path", "content", "encoding", "revision", "sizeBytes", "modifiedAt"}, ""},
		{reflect.TypeOf(protocol.FileWriteRequest{}), []string{"apiVersion", "path", "content", "expectedRevision"}, ""},
		{reflect.TypeOf(protocol.FileWriteResponse{}), []string{"apiVersion", "path", "revision", "sizeBytes", "modifiedAt"}, ""},
		{reflect.TypeOf(protocol.FileCreateRequest{}), []string{"apiVersion", "path", "isDirectory"}, ""},
		{reflect.TypeOf(protocol.FileRenameRequest{}), []string{"apiVersion", "path", "newName", "expectedRevision"}, ""},
		{reflect.TypeOf(protocol.FileMutationResponse{}), []string{"apiVersion", "path", "revision", "isDirectory", "modifiedAt"}, ""},
		{reflect.TypeOf(protocol.FileDeleteRequest{}), []string{"apiVersion", "path", "expectedRevision"}, ""},
		{reflect.TypeOf(protocol.ServerPropertiesResponse{}), []string{"apiVersion", "revision", "restartRequired", "properties"}, ""},
		{reflect.TypeOf(protocol.ServerPropertiesPatchRequest{}), []string{"apiVersion", "expectedRevision", "changes"}, ""},
		{reflect.TypeOf(protocol.ServerPropertiesPatchResponse{}), []string{"apiVersion", "revision", "restartRequired", "appliedAt", "properties"}, ""},
		{reflect.TypeOf(protocol.BackupMetadata{}), []string{"id", "label", "description", "createdAt", "sizeBytes", "sha256", "minecraftVersion"}, ""},
		{reflect.TypeOf(protocol.BackupListResponse{}), []string{"apiVersion", "backups"}, ""},
		{reflect.TypeOf(protocol.BackupCreateRequest{}), []string{"apiVersion", "label", "description"}, ""},
		{reflect.TypeOf(protocol.BackupCreateResponse{}), []string{"apiVersion", "backup", "operation"}, ""},
		{reflect.TypeOf(protocol.BackupRestoreRequest{}), []string{"apiVersion", "backupId"}, ""},
		{reflect.TypeOf(protocol.BackupDeleteRequest{}), []string{"apiVersion", "backupId"}, ""},
		{reflect.TypeOf(protocol.ControlRequest{}), []string{"apiVersion", "action"}, ""},
		{reflect.TypeOf(protocol.ControlResponse{}), []string{"apiVersion", "action", "operation"}, ""},
		{reflect.TypeOf(protocol.TokenInfo{}), []string{"id", "name", "isRoot", "scopes", "createdAt", "expiresAt", "lastUsedAt", "revokedAt"}, ""},
		{reflect.TypeOf(protocol.TokenListResponse{}), []string{"apiVersion", "tokens"}, ""},
		{reflect.TypeOf(protocol.TokenDeriveRequest{}), []string{"apiVersion", "name", "scopes", "expiresAt"}, ""},
		{reflect.TypeOf(protocol.TokenIssuedResponse{}), []string{"apiVersion", "token", "tokenInfo"}, ""},
		{reflect.TypeOf(protocol.TokenRevokeRequest{}), []string{"apiVersion", "tokenId"}, ""},
		{reflect.TypeOf(protocol.TokenRevokeResponse{}), []string{"apiVersion", "tokenId", "revokedAt"}, ""},
		{reflect.TypeOf(protocol.TokenRotateRequest{}), []string{"apiVersion", "tokenId", "gracePeriodSeconds"}, ""},
		{reflect.TypeOf(protocol.TokenRotateResponse{}), []string{"apiVersion", "token", "tokenInfo", "previousTokenExpiresAt"}, ""},
		{reflect.TypeOf(protocol.ConsoleFrame{}), []string{"apiVersion", "cursor", "sentAt", "entries"}, protocol.FrameTypeConsole},
		{reflect.TypeOf(protocol.EventFrame{}), []string{"apiVersion", "cursor", "sentAt", "event"}, protocol.FrameTypeEvent},
		{reflect.TypeOf(protocol.SnapshotFrame{}), []string{"apiVersion", "cursor", "sentAt", "status", "players", "recentConsole", "activeOperations"}, protocol.FrameTypeSnapshot},
		{reflect.TypeOf(protocol.ResetFrame{}), []string{"apiVersion", "cursor", "sentAt", "reason", "requiresSnapshot"}, protocol.FrameTypeReset},
		{reflect.TypeOf(protocol.OperationFrame{}), []string{"apiVersion", "cursor", "sentAt", "operation"}, protocol.FrameTypeOperation},
	}

	for _, test := range tests {
		t.Run(test.typeOf.String(), func(t *testing.T) {
			var actual []string
			for i := 0; i < test.typeOf.NumField(); i++ {
				field := test.typeOf.Field(i)
				if !field.IsExported() {
					continue
				}
				tag := field.Tag.Get("json")
				name := strings.Split(tag, ",")[0]
				if name == "" {
					t.Errorf("field %s has no JSON name", field.Name)
				}
				if strings.Contains(tag, "omitempty") {
					t.Errorf("field %s uses omitempty", field.Name)
				}
				actual = append(actual, name)
			}
			expected := test.fields
			if !reflect.DeepEqual(actual, expected) {
				t.Fatalf("JSON fields = %#v\nwant          = %#v", actual, expected)
			}
		})
	}
}

func TestAllDTOConstructorsMarshal(t *testing.T) {
	values := []any{
		protocol.NewAPIError(),
		protocol.NewOperationInfo(),
		protocol.NewOperationResponse(),
		protocol.NewConfirmationPrepareResponse(),
		protocol.NewConfirmationExecuteRequest(),
		protocol.NewConfirmationExecuteResponse(),
		protocol.NewServerEvent(),
		protocol.NewHandshakeRequest(),
		protocol.NewHandshakeResponse(),
		protocol.NewStatusResponse(),
		protocol.NewConsoleCommandRequest(),
		protocol.NewConsoleCommandResponse(),
		protocol.NewConsoleEntry(),
		protocol.NewPlayerInfo(),
		protocol.NewOnlinePlayersResponse(),
		protocol.NewPlayerListEntry(),
		protocol.NewPlayerListResponse(),
		protocol.NewPlayerAddRequest(),
		protocol.NewPlayerRemoveRequest(),
		protocol.NewPlayerMutationResponse(),
		protocol.NewFileEntry(),
		protocol.NewFileListResponse(),
		protocol.NewFileContentResponse(),
		protocol.NewFileWriteRequest(),
		protocol.NewFileWriteResponse(),
		protocol.NewFileCreateRequest(),
		protocol.NewFileRenameRequest(),
		protocol.NewFileMutationResponse(),
		protocol.NewFileDeleteRequest(),
		protocol.NewServerPropertiesResponse(),
		protocol.NewServerPropertiesPatchRequest(),
		protocol.NewServerPropertiesPatchResponse(),
		protocol.NewBackupMetadata(),
		protocol.NewBackupListResponse(),
		protocol.NewBackupCreateRequest(),
		protocol.NewBackupCreateResponse(),
		protocol.NewBackupRestoreRequest(),
		protocol.NewBackupDeleteRequest(),
		protocol.NewControlRequest(),
		protocol.NewControlResponse(),
		protocol.NewTokenInfo(),
		protocol.NewTokenListResponse(),
		protocol.NewTokenDeriveRequest(),
		protocol.NewTokenIssuedResponse(),
		protocol.NewTokenRevokeRequest(),
		protocol.NewTokenRevokeResponse(),
		protocol.NewTokenRotateRequest(),
		protocol.NewTokenRotateResponse(),
		protocol.NewConsoleFrame(),
		protocol.NewEventFrame(),
		protocol.NewSnapshotFrame(),
		protocol.NewResetFrame(),
		protocol.NewOperationFrame(),
	}
	for _, value := range values {
		t.Run(reflect.TypeOf(value).String(), func(t *testing.T) {
			if _, err := protocol.Marshal(value); err != nil {
				t.Fatalf("Marshal() error = %v", err)
			}
		})
	}
}

func TestEveryVersionedDTOImplementsIProtocolMessage(t *testing.T) {
	messages := []protocol.IProtocolMessage{
		protocol.APIError{},
		protocol.OperationResponse{},
		protocol.ConfirmationPrepareResponse{},
		protocol.ConfirmationExecuteRequest{},
		protocol.ConfirmationExecuteResponse{},
		protocol.HandshakeRequest{},
		protocol.HandshakeResponse{},
		protocol.StatusResponse{},
		protocol.ConsoleCommandRequest{},
		protocol.ConsoleCommandResponse{},
		protocol.OnlinePlayersResponse{},
		protocol.PlayerListResponse{},
		protocol.PlayerAddRequest{},
		protocol.PlayerRemoveRequest{},
		protocol.PlayerMutationResponse{},
		protocol.FileListResponse{},
		protocol.FileContentResponse{},
		protocol.FileWriteRequest{},
		protocol.FileWriteResponse{},
		protocol.FileCreateRequest{},
		protocol.FileRenameRequest{},
		protocol.FileMutationResponse{},
		protocol.FileDeleteRequest{},
		protocol.ServerPropertiesResponse{},
		protocol.ServerPropertiesPatchRequest{},
		protocol.ServerPropertiesPatchResponse{},
		protocol.BackupListResponse{},
		protocol.BackupCreateRequest{},
		protocol.BackupCreateResponse{},
		protocol.BackupRestoreRequest{},
		protocol.BackupDeleteRequest{},
		protocol.ControlRequest{},
		protocol.ControlResponse{},
		protocol.TokenListResponse{},
		protocol.TokenDeriveRequest{},
		protocol.TokenIssuedResponse{},
		protocol.TokenRevokeRequest{},
		protocol.TokenRevokeResponse{},
		protocol.TokenRotateRequest{},
		protocol.TokenRotateResponse{},
		protocol.ConsoleFrame{},
		protocol.EventFrame{},
		protocol.SnapshotFrame{},
		protocol.ResetFrame{},
		protocol.OperationFrame{},
	}
	for _, message := range messages {
		if got := message.GetAPIVersion(); got != protocol.APIVersion {
			t.Errorf("%T.GetAPIVersion() = %q, want %q", message, got, protocol.APIVersion)
		}
	}
}

func TestEveryConfirmationPrepareDTOImplementsInterface(t *testing.T) {
	requests := []protocol.ConfirmationPrepareRequest{
		protocol.FileDeleteRequest{},
		protocol.ServerPropertiesPatchRequest{},
		protocol.BackupRestoreRequest{},
		protocol.BackupDeleteRequest{},
		protocol.ControlRequest{},
	}
	for _, request := range requests {
		if got := request.GetAPIVersion(); got != protocol.APIVersion {
			t.Errorf("%T.GetAPIVersion() = %q, want %q", request, got, protocol.APIVersion)
		}
	}
}
