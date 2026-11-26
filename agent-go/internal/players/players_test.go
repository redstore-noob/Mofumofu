// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package players

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

var testPlayerTime = time.Date(2026, time.November, 27, 12, 34, 56, 0, time.UTC)

type fakePlayerCore struct {
	mu         sync.Mutex
	running    bool
	commands   []string
	commandErr error
	online     []string
	onCommand  func(string) error
}

func (f *fakePlayerCore) IsRunning() bool {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.running
}

func (f *fakePlayerCore) Command(ctx context.Context, command string) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	f.mu.Lock()
	f.commands = append(f.commands, command)
	hook := f.onCommand
	commandErr := f.commandErr
	f.mu.Unlock()
	if hook != nil {
		if err := hook(command); err != nil {
			return err
		}
	}
	return commandErr
}

func (f *fakePlayerCore) OnlineNames(context.Context) []string {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([]string(nil), f.online...)
}

func (f *fakePlayerCore) commandSnapshot() []string {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([]string(nil), f.commands...)
}

func newPlayerTestService(t *testing.T, core Core) (*Service, string) {
	t.Helper()
	directory := t.TempDir()
	return New(directory, core, func() time.Time { return testPlayerTime }), directory
}

func requirePlayerError(t *testing.T, err error, status int, code string) *Error {
	t.Helper()
	var apiError *Error
	if !errors.As(err, &apiError) {
		t.Fatalf("error = %#v, want *players.Error", err)
	}
	if apiError.HTTPStatus() != status || apiError.ErrorCode() != code {
		t.Fatalf("error status/code = %d/%q, want %d/%q (message %q)",
			apiError.HTTPStatus(), apiError.ErrorCode(), status, code, apiError.Error())
	}
	if apiError.OperationIdentifier() != "" {
		t.Fatalf("player error operation ID = %q, want empty", apiError.OperationIdentifier())
	}
	return apiError
}

func requirePlayerList(t *testing.T, service *Service, kind string) protocol.PlayerListResponse {
	t.Helper()
	response, err := service.List(context.Background(), kind)
	if err != nil {
		t.Fatalf("List(%q): %v", kind, err)
	}
	return response
}

func requirePlayerEntry(t *testing.T, entry protocol.PlayerListEntry, name, uuid string) {
	t.Helper()
	if entry.Name != name {
		t.Fatalf("entry.Name = %q, want %q", entry.Name, name)
	}
	if entry.UUID == nil || entry.UUID.String() != uuid {
		t.Fatalf("entry.UUID = %#v, want %q", entry.UUID, uuid)
	}
}

func TestListSortsOrdinallyAndHashesCanonicalDTOJSON(t *testing.T) {
	service, directory := newPlayerTestService(t, &fakePlayerCore{})
	path := filepath.Join(directory, "whitelist.json")
	writePlayerFixture(t, directory, "whitelist.json", `[
  {"uuid":"5627DD98E6BE3C21B8A8E92344183641","name":"Steve"},
  {"uuid":"bad","name":"Notch"},
  {"uuid":"36532b5e-c442-3dbb-a24c-c7e55d0f979a","name":"Alex"}
]`)
	response := requirePlayerList(t, service, kindWhitelist)
	if response.Kind != kindWhitelist || response.GetAPIVersion() != "1.0" {
		t.Fatalf("unexpected list metadata: %#v", response)
	}
	names := make([]string, 0, len(response.Entries))
	for _, entry := range response.Entries {
		names = append(names, entry.Name)
	}
	if !reflect.DeepEqual(names, []string{"Alex", "Notch", "Steve"}) {
		t.Fatalf("sorted names = %#v", names)
	}
	requirePlayerEntry(t, response.Entries[0], "Alex", "36532b5e-c442-3dbb-a24c-c7e55d0f979a")
	requirePlayerEntry(t, response.Entries[1], "Notch", "b50ad385-829d-3141-a216-7e7d7539ba7f")
	requirePlayerEntry(t, response.Entries[2], "Steve", "5627dd98-e6be-3c21-b8a8-e92344183641")

	canonical := `[{"name":"Alex","uuid":"36532b5e-c442-3dbb-a24c-c7e55d0f979a"},{"name":"Notch","uuid":"b50ad385-829d-3141-a216-7e7d7539ba7f"},{"name":"Steve","uuid":"5627dd98-e6be-3c21-b8a8-e92344183641"}]`
	hash := sha256.Sum256([]byte(canonical))
	expectedRevision := "pl_" + hex.EncodeToString(hash[:])
	if response.Revision != expectedRevision {
		t.Fatalf("revision = %q, want %q", response.Revision, expectedRevision)
	}
	payload, err := protocol.Marshal(response.Entries)
	if err != nil || string(payload) != canonical {
		t.Fatalf("protocol.Marshal(entries) = %s, %v; want %s", payload, err, canonical)
	}

	writePlayerFixture(t, directory, "whitelist.json", `[
  {"name":"Alex","uuid":"36532b5ec4423dbba24cc7e55d0f979a"},
  {"name":"Steve","uuid":"5627dd98-e6be-3c21-b8a8-e92344183641"},
  {"name":"Notch","uuid":"invalid"}
]`)
	reordered := requirePlayerList(t, service, kindWhitelist)
	if reordered.Revision != response.Revision || !reflect.DeepEqual(reordered.Entries, response.Entries) {
		t.Fatalf("file reordering changed API entries/revision: %#v", reordered)
	}
	if err := os.WriteFile(path, []byte("[\n\t{ \"name\" : \"Alex\", \"uuid\" : \"36532b5ec4423dbba24cc7e55d0f979a\" },\n\t{ \"name\" : \"Notch\", \"uuid\" : \"bad\" },\n\t{ \"name\" : \"Steve\", \"uuid\" : \"5627dd98e6be3c21b8a8e92344183641\" }\n]\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	if reformatted := requirePlayerList(t, service, kindWhitelist); reformatted.Revision != response.Revision {
		t.Fatalf("file formatting changed revision from %q to %q", response.Revision, reformatted.Revision)
	}
}

func TestEmptyListRevisionAndDamagedFiles(t *testing.T) {
	service, directory := newPlayerTestService(t, &fakePlayerCore{})
	hash := sha256.Sum256([]byte("[]"))
	expectedRevision := "pl_" + hex.EncodeToString(hash[:])
	for _, kind := range []string{kindWhitelist, kindOps, kindBans} {
		t.Run(kind+"/missing", func(t *testing.T) {
			response := requirePlayerList(t, service, kind)
			if response.Entries == nil || len(response.Entries) != 0 || response.Revision != expectedRevision {
				t.Fatalf("missing file response = %#v", response)
			}
		})
		t.Run(kind+"/damaged", func(t *testing.T) {
			writePlayerFixture(t, directory, playerFilePath(directory, kind), `[{"name":"Steve"`)
			response := requirePlayerList(t, service, kind)
			if response.Entries == nil || len(response.Entries) != 0 || response.Revision != expectedRevision {
				t.Fatalf("damaged file response = %#v", response)
			}
		})
	}
}

func TestListOpsAndBansDTOFields(t *testing.T) {
	service, directory := newPlayerTestService(t, &fakePlayerCore{})
	writePlayerFixture(t, directory, "ops.json", `[
  {"uuid":"bad","name":" Builder ","level":2,"bypassesPlayerLimit":true},
  {"name":"Default","level":0}
]`)
	ops := requirePlayerList(t, service, kindOps)
	if len(ops.Entries) != 2 {
		t.Fatalf("ops entries = %#v", ops.Entries)
	}
	requirePlayerEntry(t, ops.Entries[0], "Builder", offlineUUID("Builder").String())
	if ops.Entries[0].OpLevel == nil || *ops.Entries[0].OpLevel != 2 ||
		ops.Entries[0].BypassesPlayerLimit == nil || !*ops.Entries[0].BypassesPlayerLimit {
		t.Fatalf("ops fields = %#v", ops.Entries[0])
	}
	if ops.Entries[1].OpLevel == nil || *ops.Entries[1].OpLevel != 0 ||
		ops.Entries[1].BypassesPlayerLimit == nil || *ops.Entries[1].BypassesPlayerLimit {
		t.Fatalf("default ops fields = %#v", ops.Entries[1])
	}
	payload, err := protocol.Marshal(ops.Entries)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(payload), `"opLevel":2`) || !strings.Contains(string(payload), `"bypassesPlayerLimit":true`) {
		t.Fatalf("ops JSON omitted false/zero-capable fields: %s", payload)
	}

	writePlayerFixture(t, directory, "banned-players.json", `[
  {"uuid":"00112233445566778899aabbccddeeff","name":" Grief ","created":"2026-11-27 12:34:56 +0800","source":"Console","expires":"forever","reason":"Rule & <test> 玩家"},
  {"name":"Empty","created":"bad","expires":""}
]`)
	bans := requirePlayerList(t, service, kindBans)
	if len(bans.Entries) != 2 {
		t.Fatalf("ban entries = %#v", bans.Entries)
	}
	requirePlayerEntry(t, bans.Entries[0], "Empty", offlineUUID("Empty").String())
	if bans.Entries[0].CreatedAt != nil || bans.Entries[0].ExpiresAt != nil ||
		bans.Entries[0].Source == nil || *bans.Entries[0].Source != "" ||
		bans.Entries[0].Reason == nil || *bans.Entries[0].Reason != "" {
		t.Fatalf("empty ban fields = %#v", bans.Entries[0])
	}
	requirePlayerEntry(t, bans.Entries[1], "Grief", "00112233-4455-6677-8899-aabbccddeeff")
	if bans.Entries[1].CreatedAt == nil || bans.Entries[1].CreatedAt.String() != "2026-11-27T04:34:56+00:00" ||
		bans.Entries[1].ExpiresAt != nil || bans.Entries[1].Source == nil || *bans.Entries[1].Source != "Console" ||
		bans.Entries[1].Reason == nil || *bans.Entries[1].Reason != "Rule & <test> 玩家" {
		t.Fatalf("ban fields = %#v", bans.Entries[1])
	}
	payload, err = protocol.Marshal(bans.Entries)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(payload), `"createdAt":"2026-11-27T04:34:56+00:00"`) ||
		strings.Contains(string(payload), `"expiresAt"`) || !strings.Contains(string(payload), `\u0026`) {
		t.Fatalf("ban protocol JSON mismatch: %s", payload)
	}
}

func TestOfflineWhitelistMutation(t *testing.T) {
	core := &fakePlayerCore{}
	service, directory := newPlayerTestService(t, core)
	before := requirePlayerList(t, service, kindWhitelist)
	added, err := service.Add(context.Background(), kindWhitelist, protocol.PlayerAddRequest{PlayerName: "Steve"})
	if err != nil {
		t.Fatalf("Add: %v", err)
	}
	requirePlayerEntry(t, added.Entry, "Steve", "5627dd98-e6be-3c21-b8a8-e92344183641")
	if added.Kind != kindWhitelist || added.Removed || added.Revision == before.Revision {
		t.Fatalf("unexpected add response: %#v", added)
	}
	entries := readWhitelist(playerFilePath(directory, kindWhitelist))
	if len(entries) != 1 || entries[0].Name != "Steve" || entries[0].UUID != "5627dd98-e6be-3c21-b8a8-e92344183641" {
		t.Fatalf("whitelist file = %#v", entries)
	}

	removed, err := service.Remove(context.Background(), kindWhitelist, protocol.PlayerRemoveRequest{
		PlayerName:       "steve",
		ExpectedRevision: &added.Revision,
	})
	if err != nil {
		t.Fatalf("Remove: %v", err)
	}
	if !removed.Removed || !reflect.DeepEqual(removed.Entry, added.Entry) || removed.Revision != before.Revision {
		t.Fatalf("unexpected remove response: %#v", removed)
	}
	if entries := readWhitelist(playerFilePath(directory, kindWhitelist)); len(entries) != 0 {
		t.Fatalf("whitelist was not emptied: %#v", entries)
	}
	if commands := core.commandSnapshot(); len(commands) != 0 {
		t.Fatalf("offline mutation sent commands: %#v", commands)
	}
}

func TestOfflineOpsMutationAndDefaults(t *testing.T) {
	service, directory := newPlayerTestService(t, &fakePlayerCore{})
	added, err := service.Add(context.Background(), kindOps, protocol.PlayerAddRequest{
		PlayerName:          "Builder",
		OpLevel:             protocol.Ptr(2),
		BypassesPlayerLimit: protocol.Ptr(true),
	})
	if err != nil {
		t.Fatalf("Add: %v", err)
	}
	requirePlayerEntry(t, added.Entry, "Builder", offlineUUID("Builder").String())
	if added.Entry.OpLevel == nil || *added.Entry.OpLevel != 2 ||
		added.Entry.BypassesPlayerLimit == nil || !*added.Entry.BypassesPlayerLimit {
		t.Fatalf("added op fields = %#v", added.Entry)
	}
	entries := readOps(playerFilePath(directory, kindOps))
	if len(entries) != 1 || entries[0].Level != 2 || !entries[0].BypassesPlayerLimit {
		t.Fatalf("ops file = %#v", entries)
	}
	removed, err := service.Remove(context.Background(), kindOps, protocol.PlayerRemoveRequest{PlayerName: "builder"})
	if err != nil {
		t.Fatalf("Remove: %v", err)
	}
	if !removed.Removed || !reflect.DeepEqual(removed.Entry, added.Entry) {
		t.Fatalf("unexpected remove response: %#v", removed)
	}

	defaulted, err := service.Add(context.Background(), kindOps, protocol.PlayerAddRequest{PlayerName: "Defaulted"})
	if err != nil {
		t.Fatalf("Add defaulted: %v", err)
	}
	if defaulted.Entry.OpLevel == nil || *defaulted.Entry.OpLevel != 4 ||
		defaulted.Entry.BypassesPlayerLimit == nil || *defaulted.Entry.BypassesPlayerLimit {
		t.Fatalf("default op fields = %#v", defaulted.Entry)
	}
}

func TestOfflineBanMutationAndDefaults(t *testing.T) {
	service, directory := newPlayerTestService(t, &fakePlayerCore{})
	expires := protocol.NewTimestamp(time.Date(2026, time.December, 1, 1, 2, 3, 0, time.FixedZone("UTC+2", 2*60*60)))
	added, err := service.Add(context.Background(), kindBans, protocol.PlayerAddRequest{
		PlayerName: "Grief",
		Reason:     protocol.Ptr("Rule & <test> 玩家"),
		ExpiresAt:  &expires,
	})
	if err != nil {
		t.Fatalf("Add: %v", err)
	}
	requirePlayerEntry(t, added.Entry, "Grief", offlineUUID("Grief").String())
	if added.Entry.CreatedAt == nil || added.Entry.CreatedAt.String() != "2026-11-27T12:34:56+00:00" ||
		added.Entry.ExpiresAt == nil || added.Entry.ExpiresAt.String() != "2026-11-30T23:02:03+00:00" ||
		added.Entry.Source == nil || *added.Entry.Source != "Mofumofu.ServerAgent" ||
		added.Entry.Reason == nil || *added.Entry.Reason != "Rule & <test> 玩家" {
		t.Fatalf("added ban fields = %#v", added.Entry)
	}
	entries := readBans(playerFilePath(directory, kindBans))
	if len(entries) != 1 || entries[0].Created != "2026-11-27 12:34:56 +0000" ||
		entries[0].Expires != "2026-11-30 23:02:03 +0000" || entries[0].Source != "Mofumofu.ServerAgent" {
		t.Fatalf("ban file = %#v", entries)
	}
	removed, err := service.Remove(context.Background(), kindBans, protocol.PlayerRemoveRequest{PlayerName: "grief"})
	if err != nil {
		t.Fatalf("Remove: %v", err)
	}
	if !removed.Removed || !reflect.DeepEqual(removed.Entry, added.Entry) {
		t.Fatalf("unexpected remove response: %#v", removed)
	}

	defaulted, err := service.Add(context.Background(), kindBans, protocol.PlayerAddRequest{PlayerName: "Defaulted"})
	if err != nil {
		t.Fatalf("Add defaulted: %v", err)
	}
	if defaulted.Entry.Reason == nil || *defaulted.Entry.Reason != "Banned by an operator." ||
		defaulted.Entry.ExpiresAt != nil || defaulted.Entry.Source == nil || *defaulted.Entry.Source != "Mofumofu.ServerAgent" {
		t.Fatalf("default ban fields = %#v", defaulted.Entry)
	}
	entries = readBans(playerFilePath(directory, kindBans))
	if len(entries) != 1 || entries[0].Expires != "forever" || entries[0].Reason != "Banned by an operator." {
		t.Fatalf("default ban file = %#v", entries)
	}
}

func TestDuplicateMissingAndRevisionConflicts(t *testing.T) {
	core := &fakePlayerCore{}
	service, directory := newPlayerTestService(t, core)
	path := writePlayerFixture(t, directory, "whitelist.json", `[{"uuid":"b50ad385-829d-3141-a216-7e7d7539ba7f","name":"Notch"}]`)
	original := readPlayerFixture(t, path)
	current := requirePlayerList(t, service, kindWhitelist)

	_, err := service.Add(context.Background(), kindWhitelist, protocol.PlayerAddRequest{PlayerName: "notch"})
	requirePlayerError(t, err, 409, "conflict")
	wrong := "pl_wrong"
	_, err = service.Add(context.Background(), kindWhitelist, protocol.PlayerAddRequest{
		PlayerName: "Steve", ExpectedRevision: &wrong,
	})
	apiError := requirePlayerError(t, err, 409, "revision_conflict")
	if apiError.ErrorDetails()["expectedRevision"] != wrong {
		t.Fatalf("revision details = %#v", apiError.ErrorDetails())
	}
	_, err = service.Remove(context.Background(), kindWhitelist, protocol.PlayerRemoveRequest{
		PlayerName: "Missing", ExpectedRevision: &wrong,
	})
	requirePlayerError(t, err, 409, "revision_conflict")
	_, err = service.Remove(context.Background(), kindWhitelist, protocol.PlayerRemoveRequest{PlayerName: "Missing"})
	requirePlayerError(t, err, 404, "not_found")
	if actual := readPlayerFixture(t, path); actual != original {
		t.Fatalf("rejected mutation changed file: %q", actual)
	}
	if commands := core.commandSnapshot(); len(commands) != 0 {
		t.Fatalf("rejected mutation sent commands: %#v", commands)
	}

	emptyExpected := ""
	added, err := service.Add(context.Background(), kindWhitelist, protocol.PlayerAddRequest{
		PlayerName: "Steve", ExpectedRevision: &emptyExpected,
	})
	if err != nil {
		t.Fatalf("empty expected revision should be ignored: %v", err)
	}
	removed, err := service.Remove(context.Background(), kindWhitelist, protocol.PlayerRemoveRequest{
		PlayerName: "Steve", ExpectedRevision: &added.Revision,
	})
	if err != nil || !removed.Removed {
		t.Fatalf("exact expected revision failed: %#v, %v", removed, err)
	}
	if after := requirePlayerList(t, service, kindWhitelist); after.Revision != current.Revision {
		t.Fatalf("revision after add/remove = %q, want %q", after.Revision, current.Revision)
	}
}

func TestKindAndRequestValidation(t *testing.T) {
	service, _ := newPlayerTestService(t, &fakePlayerCore{})
	_, err := service.List(context.Background(), "players")
	requirePlayerError(t, err, 404, "not_found")
	_, err = service.Add(context.Background(), "", protocol.PlayerAddRequest{PlayerName: "Steve"})
	requirePlayerError(t, err, 404, "not_found")
	_, err = service.Remove(context.Background(), "OPS", protocol.PlayerRemoveRequest{PlayerName: "Steve"})
	requirePlayerError(t, err, 404, "not_found")

	invalidNames := []string{"", " Steve", "Steve ", "bad-name", "玩家", strings.Repeat("a", 17)}
	for _, name := range invalidNames {
		t.Run("name/"+fmt.Sprintf("%q", name), func(t *testing.T) {
			_, err := service.Add(context.Background(), kindWhitelist, protocol.PlayerAddRequest{PlayerName: name})
			requirePlayerError(t, err, 400, "invalid_player_name")
			_, err = service.Remove(context.Background(), kindWhitelist, protocol.PlayerRemoveRequest{PlayerName: name})
			requirePlayerError(t, err, 400, "invalid_player_name")
		})
	}
	for _, level := range []int{0, 5, -1} {
		_, err := service.Add(context.Background(), kindOps, protocol.PlayerAddRequest{
			PlayerName: "Steve", OpLevel: &level,
		})
		requirePlayerError(t, err, 400, "invalid_request")
	}
	validReason := strings.Repeat("😀", 128)
	if _, err := service.Add(context.Background(), kindBans, protocol.PlayerAddRequest{
		PlayerName: "Steve", Reason: &validReason,
	}); err != nil {
		t.Fatalf("256 UTF-16 code units should be accepted: %v", err)
	}
	tooLong := strings.Repeat("😀", 129)
	_, err = service.Add(context.Background(), kindBans, protocol.PlayerAddRequest{
		PlayerName: "Steve", Reason: &tooLong,
	})
	requirePlayerError(t, err, 400, "invalid_request")
	for _, reason := range []string{"line\nbreak", "delete\x7f", "next\u0085"} {
		t.Run(fmt.Sprintf("reason/%q", reason), func(t *testing.T) {
			_, err := service.Add(context.Background(), kindWhitelist, protocol.PlayerAddRequest{
				PlayerName: "Steve", Reason: &reason,
			})
			requirePlayerError(t, err, 400, "invalid_request")
		})
	}
}

func TestOfflineSaveNormalizesExistingEntriesAndFailsAtomicallyBeforeWrite(t *testing.T) {
	service, directory := newPlayerTestService(t, &fakePlayerCore{})
	whitelistPath := writePlayerFixture(t, directory, "whitelist.json", `[
  {"uuid":"BAD","name":" Good "},
  {"uuid":"00112233445566778899aabbccddeeff","name":"bad-name"}
]`)
	if _, err := service.Add(context.Background(), kindWhitelist, protocol.PlayerAddRequest{PlayerName: "Steve"}); err != nil {
		t.Fatalf("whitelist Add: %v", err)
	}
	entries := readWhitelist(whitelistPath)
	if len(entries) != 2 || entries[0].Name != "Good" || entries[0].UUID != offlineUUID("Good").String() || entries[1].Name != "Steve" {
		t.Fatalf("whitelist normalization = %#v", entries)
	}

	opsPath := writePlayerFixture(t, directory, "ops.json", `[
  {"name":"Valid","level":99},
  {"name":"bad-name","level":1}
]`)
	original := readPlayerFixture(t, opsPath)
	_, err := service.Add(context.Background(), kindOps, protocol.PlayerAddRequest{PlayerName: "Steve"})
	requirePlayerError(t, err, 500, "internal_error")
	if actual := readPlayerFixture(t, opsPath); actual != original {
		t.Fatalf("failed ops mutation changed file: %q", actual)
	}

	bansPath := writePlayerFixture(t, directory, "banned-players.json", `[{"name":"bad-name"}]`)
	original = readPlayerFixture(t, bansPath)
	_, err = service.Add(context.Background(), kindBans, protocol.PlayerAddRequest{PlayerName: "Steve"})
	requirePlayerError(t, err, 500, "internal_error")
	if actual := readPlayerFixture(t, bansPath); actual != original {
		t.Fatalf("failed ban mutation changed file: %q", actual)
	}
}

func TestRunningAddCommandsAndFallbackEntries(t *testing.T) {
	states := []string{"starting", "running", "stopping"}
	for _, state := range states {
		for _, kind := range []string{kindWhitelist, kindOps, kindBans} {
			t.Run(state+"/"+kind, func(t *testing.T) {
				core := &fakePlayerCore{running: true}
				service, directory := newPlayerTestService(t, core)
				expires := protocol.NewTimestamp(testPlayerTime.Add(24 * time.Hour))
				response, err := service.Add(context.Background(), kind, protocol.PlayerAddRequest{
					PlayerName:          "Steve",
					OpLevel:             protocol.Ptr(2),
					BypassesPlayerLimit: protocol.Ptr(true),
					Reason:              protocol.Ptr("Because"),
					ExpiresAt:           &expires,
				})
				if err != nil {
					t.Fatalf("Add: %v", err)
				}
				wantCommand := map[string]string{
					kindWhitelist: "whitelist add Steve",
					kindOps:       "op Steve",
					kindBans:      "ban Steve Because",
				}[kind]
				if commands := core.commandSnapshot(); !reflect.DeepEqual(commands, []string{wantCommand}) {
					t.Fatalf("commands = %#v, want %q", commands, wantCommand)
				}
				if _, err := os.Stat(playerFilePath(directory, kind)); !os.IsNotExist(err) {
					t.Fatalf("running mutation wrote a player file: %v", err)
				}
				requirePlayerEntry(t, response.Entry, "Steve", offlineUUID("Steve").String())
				if response.Removed {
					t.Fatal("Add returned removed=true")
				}
				if kind == kindOps && (response.Entry.OpLevel == nil || *response.Entry.OpLevel != 2 ||
					response.Entry.BypassesPlayerLimit == nil || !*response.Entry.BypassesPlayerLimit) {
					t.Fatalf("fallback op DTO = %#v", response.Entry)
				}
				if kind == kindBans && (response.Entry.ExpiresAt == nil || response.Entry.Reason == nil || *response.Entry.Reason != "Because") {
					t.Fatalf("fallback ban DTO = %#v", response.Entry)
				}
				hash := sha256.Sum256([]byte("[]"))
				if response.Revision != "pl_"+hex.EncodeToString(hash[:]) {
					t.Fatalf("fallback revision = %q, want empty-list revision", response.Revision)
				}
			})
		}
	}
}

func TestRunningRemoveCommandsReturnOriginalEntry(t *testing.T) {
	for _, kind := range []string{kindWhitelist, kindOps, kindBans} {
		t.Run(kind, func(t *testing.T) {
			core := &fakePlayerCore{running: true}
			service, directory := newPlayerTestService(t, core)
			fixture := map[string]string{
				kindWhitelist: `[{"uuid":"b50ad385-829d-3141-a216-7e7d7539ba7f","name":"Notch"}]`,
				kindOps:       `[{"uuid":"b50ad385-829d-3141-a216-7e7d7539ba7f","name":"Notch","level":3,"bypassesPlayerLimit":true}]`,
				kindBans:      `[{"uuid":"b50ad385-829d-3141-a216-7e7d7539ba7f","name":"Notch","created":"2026-11-27 12:34:56 +0000","source":"Console","expires":"forever","reason":"Reason"}]`,
			}[kind]
			path := writePlayerFixture(t, directory, playerFilePath(directory, kind), fixture)
			original := readPlayerFixture(t, path)
			before := requirePlayerList(t, service, kind)
			response, err := service.Remove(context.Background(), kind, protocol.PlayerRemoveRequest{PlayerName: "notch"})
			if err != nil {
				t.Fatalf("Remove: %v", err)
			}
			wantCommand := map[string]string{
				kindWhitelist: "whitelist remove notch",
				kindOps:       "deop notch",
				kindBans:      "pardon notch",
			}[kind]
			if commands := core.commandSnapshot(); !reflect.DeepEqual(commands, []string{wantCommand}) {
				t.Fatalf("commands = %#v, want %q", commands, wantCommand)
			}
			if !response.Removed || !reflect.DeepEqual(response.Entry, before.Entries[0]) || response.Revision != before.Revision {
				t.Fatalf("remove response = %#v, before = %#v", response, before)
			}
			if actual := readPlayerFixture(t, path); actual != original {
				t.Fatalf("running remove changed the file: %q", actual)
			}
		})
	}
}

func TestRunningMutationRereadsFilesAfterCommand(t *testing.T) {
	t.Run("add", func(t *testing.T) {
		core := &fakePlayerCore{running: true}
		service, directory := newPlayerTestService(t, core)
		path := playerFilePath(directory, kindWhitelist)
		core.onCommand = func(string) error {
			return os.WriteFile(path, []byte(`[{"uuid":"00112233445566778899aabbccddeeff","name":"Steve"}]`), 0o644)
		}
		response, err := service.Add(context.Background(), kindWhitelist, protocol.PlayerAddRequest{PlayerName: "Steve"})
		if err != nil {
			t.Fatalf("Add: %v", err)
		}
		requirePlayerEntry(t, response.Entry, "Steve", "00112233-4455-6677-8899-aabbccddeeff")
		updated := requirePlayerList(t, service, kindWhitelist)
		if response.Revision != updated.Revision || !reflect.DeepEqual(response.Entry, updated.Entries[0]) {
			t.Fatalf("Add did not use the reread DTO/revision: %#v vs %#v", response, updated)
		}
	})
	t.Run("remove", func(t *testing.T) {
		core := &fakePlayerCore{running: true}
		service, directory := newPlayerTestService(t, core)
		path := writePlayerFixture(t, directory, "whitelist.json", `[{"uuid":"b50ad385-829d-3141-a216-7e7d7539ba7f","name":"Notch"}]`)
		before := requirePlayerList(t, service, kindWhitelist)
		core.onCommand = func(string) error { return os.WriteFile(path, []byte("[]\n"), 0o644) }
		response, err := service.Remove(context.Background(), kindWhitelist, protocol.PlayerRemoveRequest{PlayerName: "Notch"})
		if err != nil {
			t.Fatalf("Remove: %v", err)
		}
		if !reflect.DeepEqual(response.Entry, before.Entries[0]) {
			t.Fatalf("Remove returned post-command entry: %#v", response.Entry)
		}
		after := requirePlayerList(t, service, kindWhitelist)
		if response.Revision != after.Revision || len(after.Entries) != 0 {
			t.Fatalf("Remove did not use post-command revision: %#v vs %#v", response, after)
		}
	})
}

func TestRunningCommandFailureIsReturned(t *testing.T) {
	sentinel := errors.New("command transport failed")
	core := &fakePlayerCore{running: true, commandErr: sentinel}
	service, _ := newPlayerTestService(t, core)
	_, err := service.Add(context.Background(), kindWhitelist, protocol.PlayerAddRequest{PlayerName: "Steve"})
	if !errors.Is(err, sentinel) {
		t.Fatalf("Add error = %v, want sentinel", err)
	}
	writePlayerFixture(t, service.directory, "whitelist.json", `[{"name":"Steve"}]`)
	_, err = service.Remove(context.Background(), kindWhitelist, protocol.PlayerRemoveRequest{PlayerName: "Steve"})
	if !errors.Is(err, sentinel) {
		t.Fatalf("Remove error = %v, want sentinel", err)
	}
}

func TestMutationsAreSerialized(t *testing.T) {
	firstEntered := make(chan struct{})
	secondEntered := make(chan struct{})
	releaseFirst := make(chan struct{})
	var releaseOnce sync.Once
	release := func() { releaseOnce.Do(func() { close(releaseFirst) }) }
	defer release()
	var calls atomic.Int32
	core := &fakePlayerCore{running: true}
	core.onCommand = func(string) error {
		if calls.Add(1) == 1 {
			close(firstEntered)
			<-releaseFirst
		} else {
			close(secondEntered)
		}
		return nil
	}
	service, _ := newPlayerTestService(t, core)
	type result struct {
		response protocol.PlayerMutationResponse
		err      error
	}
	results := make(chan result, 2)
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	go func() {
		response, err := service.Add(ctx, kindWhitelist, protocol.PlayerAddRequest{PlayerName: "First"})
		results <- result{response: response, err: err}
	}()
	select {
	case <-firstEntered:
	case <-ctx.Done():
		t.Fatal("first command did not start")
	}
	go func() {
		response, err := service.Add(ctx, kindWhitelist, protocol.PlayerAddRequest{PlayerName: "Second"})
		results <- result{response: response, err: err}
	}()
	select {
	case <-secondEntered:
		t.Fatal("second mutation entered Core.Command while the first mutation was active")
	case <-time.After(100 * time.Millisecond):
	}
	release()
	for count := 0; count < 2; count++ {
		select {
		case result := <-results:
			if result.err != nil {
				t.Fatalf("serialized Add: %v", result.err)
			}
		case <-ctx.Done():
			t.Fatal("serialized mutations did not finish")
		}
	}
	if calls.Load() != 2 {
		t.Fatalf("Core.Command calls = %d, want 2", calls.Load())
	}
}

func TestMutationWaitHonorsCancellation(t *testing.T) {
	service := New(t.TempDir(), &fakePlayerCore{}, nil)
	if err := service.acquire(context.Background()); err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	_, err := service.Add(ctx, kindWhitelist, protocol.PlayerAddRequest{PlayerName: "Steve"})
	if !errors.Is(err, context.Canceled) {
		t.Fatalf("Add error = %v, want context.Canceled", err)
	}
	service.release()
}
