// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Mofumofu

package protocol

import "reflect"

func newDTO[T any](value T) T {
	initialized := reflect.ValueOf(&value).Elem()
	if err := initializeDefaults(initialized); err != nil {
		panic("protocol: invalid DTO default: " + err.Error())
	}
	return value
}

func NewAPIError() APIError {
	return newDTO(APIError{})
}

func NewOperationInfo() OperationInfo {
	return newDTO(OperationInfo{})
}

func NewOperationResponse() OperationResponse {
	return newDTO(OperationResponse{Operation: NewOperationInfo()})
}

func NewConfirmationPrepareResponse() ConfirmationPrepareResponse {
	return newDTO(ConfirmationPrepareResponse{})
}

func NewConfirmationExecuteRequest() ConfirmationExecuteRequest {
	return newDTO(ConfirmationExecuteRequest{})
}

func NewConfirmationExecuteResponse() ConfirmationExecuteResponse {
	return newDTO(ConfirmationExecuteResponse{Operation: NewOperationInfo()})
}

func NewServerEvent() ServerEvent {
	return newDTO(ServerEvent{})
}

func NewHandshakeRequest() HandshakeRequest {
	return newDTO(HandshakeRequest{})
}

func NewHandshakeResponse() HandshakeResponse {
	return newDTO(HandshakeResponse{})
}

func NewStatusResponse() StatusResponse {
	return newDTO(StatusResponse{})
}

func NewConsoleCommandRequest() ConsoleCommandRequest {
	return newDTO(ConsoleCommandRequest{})
}

func NewConsoleCommandResponse() ConsoleCommandResponse {
	return newDTO(ConsoleCommandResponse{})
}

func NewConsoleEntry() ConsoleEntry {
	return newDTO(ConsoleEntry{})
}

func NewPlayerInfo() PlayerInfo {
	return newDTO(PlayerInfo{})
}

func NewOnlinePlayersResponse() OnlinePlayersResponse {
	return newDTO(OnlinePlayersResponse{})
}

func NewPlayerListEntry() PlayerListEntry {
	return newDTO(PlayerListEntry{})
}

func NewPlayerListResponse() PlayerListResponse {
	return newDTO(PlayerListResponse{})
}

func NewPlayerAddRequest() PlayerAddRequest {
	return newDTO(PlayerAddRequest{})
}

func NewPlayerRemoveRequest() PlayerRemoveRequest {
	return newDTO(PlayerRemoveRequest{})
}

func NewPlayerMutationResponse() PlayerMutationResponse {
	return newDTO(PlayerMutationResponse{})
}

func NewFileEntry() FileEntry {
	return newDTO(FileEntry{})
}

func NewFileListResponse() FileListResponse {
	return newDTO(FileListResponse{})
}

func NewFileContentResponse() FileContentResponse {
	return newDTO(FileContentResponse{})
}

func NewFileWriteRequest() FileWriteRequest {
	return newDTO(FileWriteRequest{})
}

func NewFileWriteResponse() FileWriteResponse {
	return newDTO(FileWriteResponse{})
}

func NewFileCreateRequest() FileCreateRequest {
	return newDTO(FileCreateRequest{})
}

func NewFileRenameRequest() FileRenameRequest {
	return newDTO(FileRenameRequest{})
}

func NewFileMutationResponse() FileMutationResponse {
	return newDTO(FileMutationResponse{})
}

func NewFileDeleteRequest() FileDeleteRequest {
	return newDTO(FileDeleteRequest{})
}

func NewServerPropertiesResponse() ServerPropertiesResponse {
	return newDTO(ServerPropertiesResponse{})
}

func NewServerPropertiesPatchRequest() ServerPropertiesPatchRequest {
	return newDTO(ServerPropertiesPatchRequest{})
}

func NewServerPropertiesPatchResponse() ServerPropertiesPatchResponse {
	return newDTO(ServerPropertiesPatchResponse{})
}

func NewBackupMetadata() BackupMetadata {
	return newDTO(BackupMetadata{})
}

func NewBackupListResponse() BackupListResponse {
	return newDTO(BackupListResponse{})
}

func NewBackupCreateRequest() BackupCreateRequest {
	return newDTO(BackupCreateRequest{})
}

func NewBackupCreateResponse() BackupCreateResponse {
	return newDTO(BackupCreateResponse{Backup: NewBackupMetadata(), Operation: NewOperationInfo()})
}

func NewBackupRestoreRequest() BackupRestoreRequest {
	return newDTO(BackupRestoreRequest{})
}

func NewBackupDeleteRequest() BackupDeleteRequest {
	return newDTO(BackupDeleteRequest{})
}

func NewControlRequest() ControlRequest {
	return newDTO(ControlRequest{})
}

func NewControlResponse() ControlResponse {
	return newDTO(ControlResponse{Operation: NewOperationInfo()})
}

func NewTokenInfo() TokenInfo {
	return newDTO(TokenInfo{})
}

func NewTokenListResponse() TokenListResponse {
	return newDTO(TokenListResponse{})
}

func NewTokenDeriveRequest() TokenDeriveRequest {
	return newDTO(TokenDeriveRequest{})
}

func NewTokenIssuedResponse() TokenIssuedResponse {
	return newDTO(TokenIssuedResponse{TokenInfo: NewTokenInfo()})
}

func NewTokenRevokeRequest() TokenRevokeRequest {
	return newDTO(TokenRevokeRequest{})
}

func NewTokenRevokeResponse() TokenRevokeResponse {
	return newDTO(TokenRevokeResponse{})
}

func NewTokenRotateRequest() TokenRotateRequest {
	return newDTO(TokenRotateRequest{})
}

func NewTokenRotateResponse() TokenRotateResponse {
	return newDTO(TokenRotateResponse{TokenInfo: NewTokenInfo()})
}

func NewProbeEventRequest() ProbeEventRequest {
	return newDTO(ProbeEventRequest{})
}

func NewAgentAcknowledgementResponse() AgentAcknowledgementResponse {
	return newDTO(AgentAcknowledgementResponse{})
}

func NewConsoleFrame() ConsoleFrame {
	return newDTO(ConsoleFrame{})
}

func NewEventFrame() EventFrame {
	return newDTO(EventFrame{Event: NewServerEvent()})
}

func NewSnapshotFrame() SnapshotFrame {
	return newDTO(SnapshotFrame{Status: NewStatusResponse()})
}

func NewResetFrame() ResetFrame {
	return newDTO(ResetFrame{})
}

func NewOperationFrame() OperationFrame {
	return newDTO(OperationFrame{Operation: NewOperationInfo()})
}
