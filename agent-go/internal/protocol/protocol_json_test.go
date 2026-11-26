// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Mofumofu

package protocol_test

import (
	"reflect"
	"strings"
	"testing"
	"time"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

func mustMarshal(t *testing.T, value any) string {
	t.Helper()
	encoded, err := protocol.Marshal(value)
	if err != nil {
		t.Fatalf("Marshal() error = %v", err)
	}
	return string(encoded)
}

func TestHandshakeSerializationMatchesGoldenAndKeepsUnknownCapabilities(t *testing.T) {
	response := protocol.HandshakeResponse{
		InstanceID:                  "survival",
		ServerName:                  "Survival",
		DeploymentMode:              protocol.DeploymentModeAgent,
		ServerImplementationVersion: "agent-1.0.0",
		Capabilities:                []string{protocol.CapabilityRead, protocol.CapabilityConsole, "future.capability"},
		Scopes:                      []string{protocol.ScopeRead},
		ServerTime:                  protocol.NewTimestamp(time.Date(2026, 11, 22, 8, 0, 0, 0, time.UTC)),
	}
	const expected = `{"apiVersion":"1.0","instanceId":"survival","serverName":"Survival","deploymentMode":"agent","serverImplementationVersion":"agent-1.0.0","capabilities":["read","console","future.capability"],"scopes":["read"],"serverTime":"2026-11-22T08:00:00+00:00","webSocketPath":"/ws"}`

	encoded := mustMarshal(t, response)
	if encoded != expected {
		t.Fatalf("Marshal() = %s\nwant       = %s", encoded, expected)
	}
	var roundTrip protocol.HandshakeResponse
	if err := protocol.Unmarshal([]byte(encoded), &roundTrip); err != nil {
		t.Fatalf("Unmarshal() error = %v", err)
	}
	if !reflect.DeepEqual(roundTrip.Capabilities, response.Capabilities) {
		t.Fatalf("Capabilities = %#v, want %#v", roundTrip.Capabilities, response.Capabilities)
	}
	if got := roundTrip.GetAPIVersion(); got != "1.0" {
		t.Fatalf("GetAPIVersion() = %q, want 1.0", got)
	}
}

func TestWebSocketSerializationMatchesGoldenAndRoundTripsConcreteFrame(t *testing.T) {
	var frame protocol.WebSocketFrame = protocol.ResetFrame{
		Cursor: "cursor-18",
		SentAt: protocol.NewTimestamp(time.Date(2026, 11, 22, 8, 0, 0, 0, time.UTC)),
		Reason: "cache_overflow",
	}
	const expected = `{"type":"reset","apiVersion":"1.0","cursor":"cursor-18","sentAt":"2026-11-22T08:00:00+00:00","reason":"cache_overflow","requiresSnapshot":true}`

	encoded := mustMarshal(t, frame)
	if encoded != expected {
		t.Fatalf("Marshal() = %s\nwant       = %s", encoded, expected)
	}
	var roundTrip protocol.WebSocketFrame
	if err := protocol.Unmarshal([]byte(encoded), &roundTrip); err != nil {
		t.Fatalf("Unmarshal() error = %v", err)
	}
	reset, ok := roundTrip.(*protocol.ResetFrame)
	if !ok {
		t.Fatalf("round-trip type = %T, want *protocol.ResetFrame", roundTrip)
	}
	if reset.Reason != "cache_overflow" || reset.RequiresSnapshot == nil || !*reset.RequiresSnapshot {
		t.Fatalf("reset frame = %#v", reset)
	}
}

func TestAPIErrorSerializationMatchesGoldenAndOmitsNullOptionals(t *testing.T) {
	apiError := protocol.APIError{
		Code:      protocol.CodeRevisionConflict,
		Message:   "The resource revision does not match.",
		Details:   map[string]string{"path": "server.properties"},
		RequestID: protocol.Ptr("req-1"),
	}
	const expected = `{"apiVersion":"1.0","code":"revision_conflict","message":"The resource revision does not match.","details":{"path":"server.properties"},"requestId":"req-1"}`

	if encoded := mustMarshal(t, apiError); encoded != expected {
		t.Fatalf("Marshal() = %s\nwant       = %s", encoded, expected)
	}
}

func TestPropertiesPatchPreservesDictionaryKeysAndNullRemoval(t *testing.T) {
	request := protocol.ServerPropertiesPatchRequest{
		ExpectedRevision: "r1",
		Changes: map[string]*string{
			"max-players":   protocol.Ptr("20"),
			"View-Distance": nil,
		},
	}
	const expected = `{"apiVersion":"1.0","expectedRevision":"r1","changes":{"max-players":"20","View-Distance":null}}`

	var _ protocol.ConfirmationPrepareRequest = request
	if encoded := mustMarshal(t, request); encoded != expected {
		t.Fatalf("Marshal() = %s\nwant       = %s", encoded, expected)
	}
}

func TestFileCreateRenameAndMutationSerializationMatchesGolden(t *testing.T) {
	create := protocol.FileCreateRequest{Path: "config/new.txt", IsDirectory: false}
	rename := protocol.FileRenameRequest{Path: "config/old.txt", NewName: "new.txt", ExpectedRevision: "r7"}
	mutation := protocol.FileMutationResponse{
		Path:        "config/new.txt",
		Revision:    "r8",
		IsDirectory: false,
		ModifiedAt:  protocol.NewTimestamp(time.Date(2026, 11, 22, 8, 0, 0, 0, time.UTC)),
	}

	tests := []struct {
		name     string
		value    any
		expected string
	}{
		{"create", create, `{"apiVersion":"1.0","path":"config/new.txt","isDirectory":false}`},
		{"rename", rename, `{"apiVersion":"1.0","path":"config/old.txt","newName":"new.txt","expectedRevision":"r7"}`},
		{"mutation", mutation, `{"apiVersion":"1.0","path":"config/new.txt","revision":"r8","isDirectory":false,"modifiedAt":"2026-11-22T08:00:00+00:00"}`},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			if encoded := mustMarshal(t, test.value); encoded != test.expected {
				t.Fatalf("Marshal() = %s\nwant       = %s", encoded, test.expected)
			}
		})
	}
}

func TestBackupDeleteSerializationIsPrepareRequestWithBackupID(t *testing.T) {
	request := protocol.BackupDeleteRequest{BackupID: "bkp-42"}
	var _ protocol.ConfirmationPrepareRequest = request
	const expected = `{"apiVersion":"1.0","backupId":"bkp-42"}`
	if encoded := mustMarshal(t, request); encoded != expected {
		t.Fatalf("Marshal() = %s\nwant       = %s", encoded, expected)
	}
}

func TestControlStartIsAlsoPrepareRequest(t *testing.T) {
	request := protocol.ControlRequest{Action: protocol.ControlActionStart}
	var _ protocol.ConfirmationPrepareRequest = request
	const expected = `{"apiVersion":"1.0","action":"start"}`
	if encoded := mustMarshal(t, request); encoded != expected {
		t.Fatalf("Marshal() = %s\nwant       = %s", encoded, expected)
	}
}

func TestMarshalPreservesEmptyCollectionsObjectsAndExplicitZeroValues(t *testing.T) {
	tests := []struct {
		name     string
		value    any
		expected string
	}{
		{
			name:     "nil optional properties omitted",
			value:    protocol.APIError{},
			expected: `{"apiVersion":"1.0","code":"","message":""}`,
		},
		{
			name:     "nil slice omitted",
			value:    protocol.ServerPropertiesPatchRequest{ExpectedRevision: "r1"},
			expected: `{"apiVersion":"1.0","expectedRevision":"r1"}`,
		},
		{
			name:     "empty slice and map retained",
			value:    protocol.ServerPropertiesPatchRequest{ExpectedRevision: "r1", Changes: map[string]*string{}},
			expected: `{"apiVersion":"1.0","expectedRevision":"r1","changes":{}}`,
		},
		{
			name:     "zero value object retained",
			value:    protocol.OperationResponse{},
			expected: `{"apiVersion":"1.0","operation":{"id":"","kind":"","state":"queued","createdAt":"0001-01-01T00:00:00+00:00"}}`,
		},
		{
			name:     "plain false and zero retained",
			value:    protocol.StatusResponse{},
			expected: `{"apiVersion":"1.0","instanceId":"","state":"offline","onlinePlayers":0,"maxPlayers":0,"observedAt":"0001-01-01T00:00:00+00:00"}`,
		},
		{
			name:     "explicit empty default string retained",
			value:    protocol.FileContentResponse{Encoding: protocol.Ptr("")},
			expected: `{"apiVersion":"1.0","path":"","content":"","encoding":"","revision":"","sizeBytes":0}`,
		},
		{
			name:     "explicit zero default integer retained",
			value:    protocol.TokenRotateRequest{GracePeriodSeconds: protocol.Ptr(0)},
			expected: `{"apiVersion":"1.0","tokenId":"","gracePeriodSeconds":0}`,
		},
		{
			name:     "explicit false default boolean retained",
			value:    protocol.ResetFrame{RequiresSnapshot: protocol.Ptr(false)},
			expected: `{"type":"reset","apiVersion":"1.0","cursor":"","sentAt":"0001-01-01T00:00:00+00:00","reason":"","requiresSnapshot":false}`,
		},
		{
			name:     "empty slices retained",
			value:    protocol.HandshakeResponse{Capabilities: []string{}, Scopes: []string{}},
			expected: `{"apiVersion":"1.0","instanceId":"","serverName":"","deploymentMode":"","serverImplementationVersion":"","capabilities":[],"scopes":[],"serverTime":"0001-01-01T00:00:00+00:00","webSocketPath":"/ws"}`,
		},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			if encoded := mustMarshal(t, test.value); encoded != test.expected {
				t.Fatalf("Marshal() = %s\nwant       = %s", encoded, test.expected)
			}
		})
	}
}

func TestConstructorsInitializeCSharpDefaults(t *testing.T) {
	tests := []struct {
		name     string
		value    any
		expected string
	}{
		{
			name:     "console frame",
			value:    protocol.NewConsoleFrame(),
			expected: `{"type":"console","apiVersion":"1.0","cursor":"","sentAt":"0001-01-01T00:00:00+00:00","entries":[]}`,
		},
		{
			name:     "snapshot frame",
			value:    protocol.NewSnapshotFrame(),
			expected: `{"type":"snapshot","apiVersion":"1.0","cursor":"","sentAt":"0001-01-01T00:00:00+00:00","status":{"apiVersion":"1.0","instanceId":"","state":"offline","onlinePlayers":0,"maxPlayers":0,"observedAt":"0001-01-01T00:00:00+00:00"},"players":[],"recentConsole":[],"activeOperations":[]}`,
		},
		{
			name:     "properties patch",
			value:    protocol.NewServerPropertiesPatchRequest(),
			expected: `{"apiVersion":"1.0","expectedRevision":"","changes":{}}`,
		},
		{
			name:     "token info",
			value:    protocol.NewTokenInfo(),
			expected: `{"id":"","name":"","isRoot":false,"scopes":[],"createdAt":"0001-01-01T00:00:00+00:00"}`,
		},
		{
			name:     "operation response",
			value:    protocol.NewOperationResponse(),
			expected: `{"apiVersion":"1.0","operation":{"id":"","kind":"","state":"queued","createdAt":"0001-01-01T00:00:00+00:00"}}`,
		},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			if encoded := mustMarshal(t, test.value); encoded != test.expected {
				t.Fatalf("Marshal() = %s\nwant       = %s", encoded, test.expected)
			}
		})
	}
}

func TestMarshalUsesSystemTextJsonDefaultStringEscaping(t *testing.T) {
	message := "<>&'\"+`\\\b\f\n\r\t\x00é\u2028\u2029😀世界\x7f"
	apiError := protocol.APIError{Message: message}
	expected := "{\"apiVersion\":\"1.0\",\"code\":\"\",\"message\":\"\\u003C\\u003E\\u0026\\u0027\\u0022\\u002B\\u0060\\\\\\b\\f\\n\\r\\t\\u0000\\u00E9\\u2028\\u2029\\uD83D\\uDE00\\u4E16\\u754C\\u007F\"}"
	if encoded := mustMarshal(t, apiError); encoded != expected {
		t.Fatalf("Marshal() = %s\nwant       = %s", encoded, expected)
	}

	invalidUTF8 := protocol.APIError{Message: string([]byte{0xff})}
	if encoded := mustMarshal(t, invalidUTF8); encoded != `{"apiVersion":"1.0","code":"","message":"\uFFFD"}` {
		t.Fatalf("invalid UTF-8 Marshal() = %s", encoded)
	}
}

func TestUnmarshalIsCaseInsensitiveIgnoresUnknownFieldsAndInitializesDefaults(t *testing.T) {
	var request protocol.HandshakeRequest
	input := `{"CLIENTNAME":"launcher","unknown":{"nested":true},"REQUESTEDCAPABILITIES":[]}`
	if err := protocol.Unmarshal([]byte(input), &request); err != nil {
		t.Fatalf("Unmarshal() error = %v", err)
	}
	if request.ClientName != "launcher" || request.APIVersion == nil || *request.APIVersion != "1.0" {
		t.Fatalf("request = %#v", request)
	}
	if request.RequestedCapabilities == nil || len(request.RequestedCapabilities) != 0 {
		t.Fatalf("RequestedCapabilities = %#v, want non-nil empty slice", request.RequestedCapabilities)
	}
	const expected = `{"apiVersion":"1.0","clientName":"launcher","clientVersion":"","requestedCapabilities":[]}`
	if encoded := mustMarshal(t, request); encoded != expected {
		t.Fatalf("round-trip Marshal() = %s\nwant               = %s", encoded, expected)
	}

	var emptyVersion protocol.HandshakeRequest
	if err := protocol.Unmarshal([]byte(`{"apiVersion":""}`), &emptyVersion); err != nil {
		t.Fatalf("explicit empty apiVersion error = %v", err)
	}
	if emptyVersion.APIVersion == nil || *emptyVersion.APIVersion != "" {
		t.Fatalf("explicit empty apiVersion was not preserved: %#v", emptyVersion.APIVersion)
	}
	if encoded := mustMarshal(t, emptyVersion); encoded != `{"apiVersion":"","clientName":"","clientVersion":"","requestedCapabilities":[]}` {
		t.Fatalf("explicit empty apiVersion Marshal() = %s", encoded)
	}

	reused := protocol.HandshakeRequest{APIVersion: protocol.Ptr("9.9"), RequestedCapabilities: []string{"old"}}
	if err := protocol.Unmarshal([]byte(`{}`), &reused); err != nil {
		t.Fatalf("reused target Unmarshal() error = %v", err)
	}
	if reused.GetAPIVersion() != "1.0" || reused.RequestedCapabilities == nil || len(reused.RequestedCapabilities) != 0 {
		t.Fatalf("reused target defaults = %#v", reused)
	}
}

type customProtocolMessage struct {
	APIVersion string `json:"apiVersion"`
	Value      string `json:"value"`
}

func (m customProtocolMessage) GetAPIVersion() string {
	return m.APIVersion
}

func TestUnmarshalDefaultsAPIVersionForIProtocolMessageImplementations(t *testing.T) {
	message := customProtocolMessage{APIVersion: "9.9"}
	if err := protocol.Unmarshal([]byte(`{"value":"ok"}`), &message); err != nil {
		t.Fatalf("Unmarshal() error = %v", err)
	}
	if message.APIVersion != "1.0" || message.Value != "ok" {
		t.Fatalf("message = %#v", message)
	}
	if err := protocol.Unmarshal([]byte(`{"apiVersion":null}`), &message); err == nil {
		t.Fatal("null apiVersion error = nil, want error")
	}
}

func TestUnmarshalRejectsNullTopLevelNullAPIVersionAndTrailingJSON(t *testing.T) {
	var request protocol.HandshakeRequest
	tests := []struct {
		name  string
		input string
	}{
		{"top-level null", " \n null \t"},
		{"null apiVersion", `{"apiVersion":null}`},
		{"case-insensitive null apiVersion", `{"APIVERSION":null}`},
		{"trailing object", `{"apiVersion":"1.0"} {}`},
		{"trailing scalar", `{"apiVersion":"1.0"} false`},
		{"invalid JSON", `{"apiVersion":`},
		{"wrong top-level type", `[]`},
		{"wrong field type", `{"clientName":3}`},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			if err := protocol.Unmarshal([]byte(test.input), &request); err == nil {
				t.Fatal("Unmarshal() error = nil, want error")
			}
		})
	}

	var response protocol.OperationResponse
	if err := protocol.Unmarshal([]byte(`{"operation":{"error":{"apiVersion":null,"code":"x","message":"x"}}}`), &response); err == nil {
		t.Fatal("nested null apiVersion error = nil, want error")
	}
	var snapshot protocol.SnapshotFrame
	if err := protocol.Unmarshal([]byte(`{"type":"snapshot","status":{"apiVersion":null}}`), &snapshot); err == nil {
		t.Fatal("nested StatusResponse null apiVersion error = nil, want error")
	}
}

func TestUnmarshalInitializesNestedCollectionDefaults(t *testing.T) {
	var tokens protocol.TokenListResponse
	if err := protocol.Unmarshal([]byte(`{"tokens":[{}]}`), &tokens); err != nil {
		t.Fatalf("TokenListResponse Unmarshal() error = %v", err)
	}
	if tokens.Tokens[0].Scopes == nil {
		t.Fatal("nested TokenInfo.Scopes = nil, want empty slice")
	}

	var event protocol.EventFrame
	if err := protocol.Unmarshal([]byte(`{"event":{}}`), &event); err != nil {
		t.Fatalf("EventFrame Unmarshal() error = %v", err)
	}
	if event.Event.Details == nil {
		t.Fatal("nested ServerEvent.Details = nil, want empty map")
	}

	var snapshot protocol.SnapshotFrame
	if err := protocol.Unmarshal([]byte(`{"status":{},"activeOperations":[{}]}`), &snapshot); err != nil {
		t.Fatalf("SnapshotFrame Unmarshal() error = %v", err)
	}
	if snapshot.Status.State == nil || *snapshot.Status.State != protocol.ServerStateOffline {
		t.Fatalf("nested StatusResponse.State = %#v", snapshot.Status.State)
	}
	if len(snapshot.ActiveOperations) != 1 || snapshot.ActiveOperations[0].State == nil || *snapshot.ActiveOperations[0].State != protocol.OperationStateQueued {
		t.Fatalf("nested OperationInfo.State = %#v", snapshot.ActiveOperations)
	}
}

func TestAllWebSocketFramesRoundTripWithTypeFirst(t *testing.T) {
	frames := []protocol.WebSocketFrame{
		&protocol.ConsoleFrame{Entries: []protocol.ConsoleEntry{}},
		&protocol.EventFrame{Event: protocol.ServerEvent{Details: map[string]string{}}},
		&protocol.SnapshotFrame{Players: []protocol.PlayerInfo{}, RecentConsole: []protocol.ConsoleEntry{}, ActiveOperations: []protocol.OperationInfo{}},
		&protocol.ResetFrame{},
		&protocol.OperationFrame{},
	}
	for _, frame := range frames {
		frameType := frameTypeName(frame)
		t.Run(frameType, func(t *testing.T) {
			encoded := mustMarshal(t, frame)
			if !strings.HasPrefix(encoded, `{"type":"`+frameType+`","apiVersion":"1.0"`) {
				t.Fatalf("Marshal() = %s, want type and apiVersion first", encoded)
			}
			var decoded protocol.WebSocketFrame
			if err := protocol.Unmarshal([]byte(encoded), &decoded); err != nil {
				t.Fatalf("Unmarshal() error = %v", err)
			}
			if reflect.TypeOf(decoded) != reflect.TypeOf(frame) {
				t.Fatalf("decoded type = %T, want %T", decoded, frame)
			}
		})
	}
}

func frameTypeName(frame protocol.WebSocketFrame) string {
	switch frame.(type) {
	case *protocol.ConsoleFrame:
		return protocol.FrameTypeConsole
	case *protocol.EventFrame:
		return protocol.FrameTypeEvent
	case *protocol.SnapshotFrame:
		return protocol.FrameTypeSnapshot
	case *protocol.ResetFrame:
		return protocol.FrameTypeReset
	case *protocol.OperationFrame:
		return protocol.FrameTypeOperation
	default:
		return "unknown"
	}
}

func TestUnmarshalWebSocketRejectsMissingNullAndUnknownType(t *testing.T) {
	for _, input := range []string{
		`{"apiVersion":"1.0"}`,
		`{"type":null}`,
		`{"type":3}`,
		`{"type":"future.frame"}`,
	} {
		var frame protocol.WebSocketFrame
		if err := protocol.Unmarshal([]byte(input), &frame); err == nil {
			t.Fatalf("Unmarshal(%s) error = nil, want error", input)
		}
	}
}

func TestUUIDSerializationAndOptionalOmission(t *testing.T) {
	uuid, err := protocol.ParseUUID("00112233-4455-6677-8899-AABBCCDDEEFF")
	if err != nil {
		t.Fatalf("ParseUUID() error = %v", err)
	}
	player := protocol.PlayerInfo{Name: "survival", UUID: &uuid}
	const expected = `{"name":"survival","uuid":"00112233-4455-6677-8899-aabbccddeeff"}`
	if encoded := mustMarshal(t, player); encoded != expected {
		t.Fatalf("Marshal() = %s\nwant       = %s", encoded, expected)
	}
	var roundTrip protocol.PlayerInfo
	if err := protocol.Unmarshal([]byte(expected), &roundTrip); err != nil {
		t.Fatalf("Unmarshal() error = %v", err)
	}
	if roundTrip.UUID == nil || *roundTrip.UUID != uuid {
		t.Fatalf("round-trip UUID = %#v, want %s", roundTrip.UUID, uuid)
	}
	if encoded := mustMarshal(t, protocol.PlayerInfo{Name: "survival"}); encoded != `{"name":"survival"}` {
		t.Fatalf("nil UUID Marshal() = %s", encoded)
	}
}
