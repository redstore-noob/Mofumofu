// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package events

import (
	"context"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

func TestJournalCursorReplayResetAndPersistence(t *testing.T) {
	path := filepath.Join(t.TempDir(), "events.jsonl")
	now := func() time.Time { return time.Unix(1_800_000_000, 0).UTC() }
	journal, err := OpenJournal(path, 3, now, nil)
	if err != nil {
		t.Fatal(err)
	}
	ctx := context.Background()
	if err := journal.AppendConsole(ctx, "one"); err != nil {
		t.Fatal(err)
	}
	event := protocol.NewServerEvent()
	event.EventID = "evt-1"
	event.Type = protocol.EventTypeServerStateChanged
	if err := journal.AppendEvent(ctx, event); err != nil {
		t.Fatal(err)
	}
	operation := protocol.NewOperationInfo()
	operation.ID = "op-1"
	operation.Kind = "control.start"
	operation.State = protocol.Ptr(protocol.OperationStateSucceeded)
	if err := journal.PublishOperation(ctx, operation); err != nil {
		t.Fatal(err)
	}
	if journal.LatestCursor() != "3" {
		t.Fatalf("latest=%s", journal.LatestCursor())
	}
	entries, err := journal.RecentConsole(ctx, 100)
	if err != nil || len(entries) != 1 || entries[0].Message != "one" {
		t.Fatalf("entries=%+v err=%v", entries, err)
	}
	if contains, _ := journal.ContainsEventID(ctx, "evt-1"); !contains {
		t.Fatal("event ID not found")
	}

	replay, err := journal.Open(ctx, protocol.Ptr("1"), snapshotFactory)
	if err != nil {
		t.Fatal(err)
	}
	defer replay.Close()
	if len(replay.Initial) != 2 || frameCursor(replay.Initial[0]) != "2" || frameCursor(replay.Initial[1]) != "3" {
		t.Fatalf("replay=%+v", replay.Initial)
	}
	empty := ""
	reset, err := journal.Open(ctx, &empty, snapshotFactory)
	if err != nil {
		t.Fatal(err)
	}
	defer reset.Close()
	if len(reset.Initial) != 2 {
		t.Fatalf("reset initial=%+v", reset.Initial)
	}
	if !isResetFrame(reset.Initial[0]) {
		t.Fatalf("first frame=%T", reset.Initial[0])
	}
	if snapshot, ok := reset.Initial[1].(protocol.SnapshotFrame); !ok || snapshot.Cursor != "3" {
		t.Fatalf("snapshot=%+v", reset.Initial[1])
	}
	if err := journal.Close(ctx); err != nil {
		t.Fatal(err)
	}

	reopened, err := OpenJournal(path, 2, now, nil)
	if err != nil {
		t.Fatal(err)
	}
	defer reopened.Close(ctx)
	if reopened.LatestCursor() != "3" {
		t.Fatalf("reopened latest=%s", reopened.LatestCursor())
	}
	tooOld, err := reopened.Open(ctx, protocol.Ptr("0"), snapshotFactory)
	if err != nil {
		t.Fatal(err)
	}
	defer tooOld.Close()
	if !isResetFrame(tooOld.Initial[0]) {
		t.Fatalf("old cursor did not reset: %+v", tooOld.Initial)
	}
	payload, _ := os.ReadFile(path)
	lines := 0
	for _, line := range splitLines(string(payload)) {
		if line != "" {
			lines++
		}
	}
	if lines != 2 {
		t.Fatalf("journal was not compacted: %s", payload)
	}
}

func TestJournalSlowConsumerIsRemoved(t *testing.T) {
	journal, err := OpenJournal(filepath.Join(t.TempDir(), "events.jsonl"), 2, time.Now, nil)
	if err != nil {
		t.Fatal(err)
	}
	ctx := context.Background()
	subscription, err := journal.Open(ctx, nil, snapshotFactory)
	if err != nil {
		t.Fatal(err)
	}
	for i := 0; i < 3; i++ {
		if err := journal.AppendConsole(ctx, "line"); err != nil {
			t.Fatal(err)
		}
	}
	if _, ok := <-subscription.Frames; !ok {
		t.Fatal("consumer closed before capacity")
	}
	if _, ok := <-subscription.Frames; !ok {
		t.Fatal("consumer closed before second frame")
	}
	if _, ok := <-subscription.Frames; ok {
		t.Fatal("slow consumer was not removed")
	}
	subscription.Close()
	_ = journal.Close(ctx)
}

func isResetFrame(frame protocol.WebSocketFrame) bool {
	switch frame.(type) {
	case protocol.ResetFrame, *protocol.ResetFrame:
		return true
	default:
		return false
	}
}

func snapshotFactory(cursor string) protocol.SnapshotFrame {
	frame := protocol.NewSnapshotFrame()
	frame.Cursor = cursor
	frame.SentAt = protocol.NewTimestamp(time.Unix(1_800_000_000, 0).UTC())
	return frame
}

func splitLines(value string) []string {
	var result []string
	start := 0
	for index, r := range value {
		if r == '\n' {
			result = append(result, value[start:index])
			start = index + 1
		}
	}
	if start < len(value) {
		result = append(result, value[start:])
	}
	return result
}
