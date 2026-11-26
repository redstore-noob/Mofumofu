// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package events

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"os"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

var (
	ErrClosed         = errors.New("event journal is closed")
	ErrConsumerBehind = errors.New("WebSocket consumer fell behind the journal")
)

type record struct {
	frame   protocol.WebSocketFrame
	eventID string
	console []protocol.ConsoleEntry
}

type appendRequest struct {
	ctx     context.Context
	factory func(int64, time.Time) (protocol.WebSocketFrame, error)
	eventID string
	console []protocol.ConsoleEntry
	done    chan error
}

type subscriber struct {
	channel chan protocol.WebSocketFrame
}

// Journal is the durable, cursor-ordered console/event/operation stream. Console
// input is queued without waiting for disk so a slow journal cannot block the
// Minecraft process; event and operation appends wait for durability.
type Journal struct {
	path     string
	capacity int
	now      func() time.Time
	onError  func(error)

	mu          sync.Mutex
	frames      []record
	latest      int64
	subscribers map[uint64]*subscriber
	nextID      uint64

	queueMu sync.Mutex
	queue   []appendRequest
	closed  bool
	wake    chan struct{}
	closeCh chan struct{}
	drained chan struct{}
}

func OpenJournal(path string, capacity int, now func() time.Time, onError func(error)) (*Journal, error) {
	if capacity <= 0 {
		return nil, errors.New("journal capacity must be positive")
	}
	if now == nil {
		now = time.Now
	}
	journal := &Journal{
		path: path, capacity: capacity, now: now, onError: onError,
		subscribers: make(map[uint64]*subscriber), drained: make(chan struct{}),
		wake: make(chan struct{}, 1), closeCh: make(chan struct{}),
	}
	if err := journal.load(); err != nil {
		return nil, err
	}
	go journal.drain()
	return journal, nil
}

func (j *Journal) EnqueueConsole(line string) error {
	return j.enqueue(context.Background(), func(cursor int64, sentAt time.Time) (protocol.WebSocketFrame, error) {
		frame := protocol.NewConsoleFrame()
		frame.Cursor = formatCursor(cursor)
		frame.SentAt = protocol.NewTimestamp(sentAt)
		frame.Entries = []protocol.ConsoleEntry{{
			Sequence: cursor, Timestamp: protocol.NewTimestamp(sentAt), Level: "INFO", Source: "minecraft", Message: line,
		}}
		return frame, nil
	}, "", []protocol.ConsoleEntry{{
		Sequence: 0, Timestamp: protocol.NewTimestamp(j.now()), Level: "INFO", Source: "minecraft", Message: line,
	}}, nil)
}

func (j *Journal) AppendConsole(ctx context.Context, line string) error {
	done := make(chan error, 1)
	err := j.enqueue(ctx, func(cursor int64, sentAt time.Time) (protocol.WebSocketFrame, error) {
		frame := protocol.NewConsoleFrame()
		frame.Cursor = formatCursor(cursor)
		frame.SentAt = protocol.NewTimestamp(sentAt)
		entry := protocol.ConsoleEntry{Sequence: cursor, Timestamp: protocol.NewTimestamp(sentAt), Level: "INFO", Source: "minecraft", Message: line}
		frame.Entries = []protocol.ConsoleEntry{entry}
		return frame, nil
	}, "", nil, done)
	if err != nil {
		return err
	}
	select {
	case err := <-done:
		return err
	case <-ctx.Done():
		return ctx.Err()
	}
}

func (j *Journal) AppendEvent(ctx context.Context, event protocol.ServerEvent) error {
	done := make(chan error, 1)
	err := j.enqueue(ctx, func(cursor int64, sentAt time.Time) (protocol.WebSocketFrame, error) {
		frame := protocol.NewEventFrame()
		frame.Cursor = formatCursor(cursor)
		frame.SentAt = protocol.NewTimestamp(sentAt)
		frame.Event = event
		return frame, nil
	}, event.EventID, nil, done)
	if err != nil {
		return err
	}
	select {
	case err := <-done:
		return err
	case <-ctx.Done():
		return ctx.Err()
	}
}

// PublishOperation implements operations.Publisher.
func (j *Journal) PublishOperation(ctx context.Context, operation protocol.OperationInfo) error {
	done := make(chan error, 1)
	err := j.enqueue(ctx, func(cursor int64, sentAt time.Time) (protocol.WebSocketFrame, error) {
		frame := protocol.NewOperationFrame()
		frame.Cursor = formatCursor(cursor)
		frame.SentAt = protocol.NewTimestamp(sentAt)
		frame.Operation = operation
		return frame, nil
	}, "", nil, done)
	if err != nil {
		return err
	}
	select {
	case err := <-done:
		return err
	case <-ctx.Done():
		return ctx.Err()
	}
}

func (j *Journal) enqueue(ctx context.Context, factory func(int64, time.Time) (protocol.WebSocketFrame, error), eventID string, console []protocol.ConsoleEntry, done chan error) error {
	j.queueMu.Lock()
	if j.closed {
		j.queueMu.Unlock()
		return ErrClosed
	}
	j.queue = append(j.queue, appendRequest{ctx: ctx, factory: factory, eventID: eventID, console: console, done: done})
	j.queueMu.Unlock()
	select {
	case j.wake <- struct{}{}:
	default:
	}
	return nil
}

func (j *Journal) drain() {
	for {
		j.queueMu.Lock()
		if len(j.queue) == 0 {
			closed := j.closed
			j.queueMu.Unlock()
			if closed {
				close(j.drained)
				return
			}
			select {
			case <-j.wake:
			case <-j.closeCh:
			}
			continue
		}
		request := j.queue[0]
		j.queue[0] = appendRequest{}
		j.queue = j.queue[1:]
		closed := j.closed
		j.queueMu.Unlock()

		err := j.append(request)
		if request.done != nil {
			request.done <- err
		} else if err != nil && j.onError != nil {
			j.onError(err)
		}
		if closed && len(j.queue) == 0 {
			j.queueMu.Lock()
			empty := len(j.queue) == 0
			j.queueMu.Unlock()
			if empty {
				close(j.drained)
				return
			}
		}
	}
}

func (j *Journal) append(request appendRequest) error {
	if err := request.ctx.Err(); err != nil {
		return err
	}
	j.mu.Lock()
	defer j.mu.Unlock()
	cursor := j.latest + 1
	sentAt := j.now()
	frame, err := request.factory(cursor, sentAt)
	if err != nil {
		return err
	}
	parsed, err := parseCursor(frameCursor(frame))
	if err != nil || parsed != cursor {
		return errors.New("journal frame did not use its assigned cursor")
	}
	payload, err := protocol.Marshal(frame)
	if err != nil {
		return err
	}
	if err := persistAppend(j.path, append(payload, '\n')); err != nil {
		return err
	}
	console := request.console
	if consoleFrame, ok := frame.(*protocol.ConsoleFrame); ok {
		console = consoleFrame.Entries
	} else if consoleFrame, ok := frame.(protocol.ConsoleFrame); ok {
		console = consoleFrame.Entries
	}
	j.frames = append(j.frames, record{frame: frame, eventID: request.eventID, console: console})
	j.latest = cursor
	if len(j.frames) > j.capacity {
		j.frames = append([]record(nil), j.frames[len(j.frames)-j.capacity:]...)
		if err := j.compactLocked(); err != nil {
			return err
		}
	}
	for id, current := range j.subscribers {
		select {
		case current.channel <- frame:
		default:
			delete(j.subscribers, id)
			close(current.channel)
		}
	}
	return nil
}

type Subscription struct {
	Initial []protocol.WebSocketFrame
	Frames  <-chan protocol.WebSocketFrame
	once    sync.Once
	journal *Journal
	id      uint64
}

func (s *Subscription) Close() {
	if s == nil {
		return
	}
	s.once.Do(func() {
		s.journal.mu.Lock()
		if current, ok := s.journal.subscribers[s.id]; ok {
			delete(s.journal.subscribers, s.id)
			close(current.channel)
		}
		s.journal.mu.Unlock()
	})
}

func (j *Journal) Open(ctx context.Context, cursor *string, snapshotFactory func(string) protocol.SnapshotFrame) (*Subscription, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	j.mu.Lock()
	defer j.mu.Unlock()
	latest := formatCursor(j.latest)
	initial := make([]protocol.WebSocketFrame, 0)
	if cursor == nil {
		initial = append(initial, snapshotFactory(latest))
	} else {
		position, parseErr := parseCursor(*cursor)
		valid := parseErr == nil && position >= 0 && position <= j.latest
		if valid && len(j.frames) > 0 {
			earliest, _ := parseCursor(frameCursor(j.frames[0].frame))
			valid = position >= earliest-1
		}
		if !valid {
			reset := protocol.NewResetFrame()
			reset.Cursor = latest
			reset.SentAt = protocol.NewTimestamp(j.now())
			reset.Reason = "The requested cursor is invalid or older than the retained journal."
			reset.RequiresSnapshot = protocol.Ptr(true)
			initial = append(initial, reset, snapshotFactory(latest))
		} else {
			for _, item := range j.frames {
				positionOfFrame, _ := parseCursor(frameCursor(item.frame))
				if positionOfFrame > position {
					initial = append(initial, item.frame)
				}
			}
		}
	}
	j.nextID++
	id := j.nextID
	channel := make(chan protocol.WebSocketFrame, j.capacity)
	j.subscribers[id] = &subscriber{channel: channel}
	return &Subscription{Initial: initial, Frames: channel, journal: j, id: id}, nil
}

func (j *Journal) ContainsEventID(ctx context.Context, eventID string) (bool, error) {
	if err := ctx.Err(); err != nil {
		return false, err
	}
	j.mu.Lock()
	defer j.mu.Unlock()
	for _, item := range j.frames {
		if item.eventID == eventID {
			return true, nil
		}
	}
	return false, nil
}

func (j *Journal) RecentConsole(ctx context.Context, maximum int) ([]protocol.ConsoleEntry, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	j.mu.Lock()
	defer j.mu.Unlock()
	entries := make([]protocol.ConsoleEntry, 0)
	for _, item := range j.frames {
		entries = append(entries, item.console...)
	}
	if len(entries) > maximum {
		entries = entries[len(entries)-maximum:]
	}
	return entries, nil
}

func (j *Journal) ConsoleAfter(ctx context.Context, after int64, maximum int) ([]protocol.ConsoleEntry, string, error) {
	if err := ctx.Err(); err != nil {
		return nil, "", err
	}
	j.mu.Lock()
	defer j.mu.Unlock()
	entries := make([]protocol.ConsoleEntry, 0)
	for _, item := range j.frames {
		for _, entry := range item.console {
			if entry.Sequence > after {
				entries = append(entries, entry)
			}
		}
	}
	if len(entries) > maximum {
		entries = entries[len(entries)-maximum:]
	}
	return entries, formatCursor(j.latest), nil
}

func (j *Journal) LatestCursor() string {
	j.mu.Lock()
	defer j.mu.Unlock()
	return formatCursor(j.latest)
}

func (j *Journal) Close(ctx context.Context) error {
	j.queueMu.Lock()
	if !j.closed {
		j.closed = true
		close(j.closeCh)
	}
	j.queueMu.Unlock()
	select {
	case <-j.drained:
		j.mu.Lock()
		for id, current := range j.subscribers {
			delete(j.subscribers, id)
			close(current.channel)
		}
		j.mu.Unlock()
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}

func (j *Journal) load() error {
	if directory := filepath.Dir(j.path); directory != "" {
		if err := os.MkdirAll(directory, 0o750); err != nil {
			return err
		}
	}
	payload, err := os.ReadFile(j.path)
	if os.IsNotExist(err) {
		return nil
	}
	if err != nil {
		return err
	}
	if len(payload) == 0 {
		return nil
	}
	lines := strings.Split(strings.TrimSuffix(string(payload), "\n"), "\n")
	var previous int64
	for index, line := range lines {
		if strings.TrimSpace(line) == "" {
			continue
		}
		var frame protocol.WebSocketFrame
		if err := protocol.Unmarshal([]byte(line), &frame); err != nil {
			if index == len(lines)-1 {
				break
			}
			return err
		}
		cursor, err := parseCursor(frameCursor(frame))
		if err != nil || cursor <= previous {
			return errors.New("event journal cursors are not strictly increasing")
		}
		j.frames = append(j.frames, record{frame: frame, eventID: eventIDOf(frame), console: consoleOf(frame)})
		previous = cursor
	}
	j.latest = previous
	if len(j.frames) > j.capacity {
		j.frames = append([]record(nil), j.frames[len(j.frames)-j.capacity:]...)
		return j.compactLocked()
	}
	return nil
}

func (j *Journal) compactLocked() error {
	temporary := j.path + "." + randomID() + ".tmp"
	file, err := os.OpenFile(temporary, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0o600)
	if err != nil {
		return err
	}
	for _, item := range j.frames {
		payload, err := protocol.Marshal(item.frame)
		if err != nil {
			file.Close()
			_ = os.Remove(temporary)
			return err
		}
		if _, err := file.Write(append(payload, '\n')); err != nil {
			file.Close()
			_ = os.Remove(temporary)
			return err
		}
	}
	if err := file.Sync(); err != nil {
		file.Close()
		_ = os.Remove(temporary)
		return err
	}
	if err := file.Close(); err != nil {
		_ = os.Remove(temporary)
		return err
	}
	if runtime.GOOS == "windows" {
		_ = os.Remove(j.path)
	}
	if err := os.Rename(temporary, j.path); err != nil {
		_ = os.Remove(temporary)
		return err
	}
	return nil
}

func persistAppend(path string, payload []byte) error {
	file, err := os.OpenFile(path, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o600)
	if err != nil {
		return err
	}
	if _, err := file.Write(payload); err != nil {
		file.Close()
		return err
	}
	if err := file.Sync(); err != nil {
		file.Close()
		return err
	}
	return file.Close()
}

func frameCursor(frame protocol.WebSocketFrame) string {
	switch value := frame.(type) {
	case *protocol.ConsoleFrame:
		return value.Cursor
	case protocol.ConsoleFrame:
		return value.Cursor
	case *protocol.EventFrame:
		return value.Cursor
	case protocol.EventFrame:
		return value.Cursor
	case *protocol.OperationFrame:
		return value.Cursor
	case protocol.OperationFrame:
		return value.Cursor
	case *protocol.SnapshotFrame:
		return value.Cursor
	case protocol.SnapshotFrame:
		return value.Cursor
	case *protocol.ResetFrame:
		return value.Cursor
	case protocol.ResetFrame:
		return value.Cursor
	default:
		return ""
	}
}

func eventIDOf(frame protocol.WebSocketFrame) string {
	switch value := frame.(type) {
	case *protocol.EventFrame:
		return value.Event.EventID
	case protocol.EventFrame:
		return value.Event.EventID
	default:
		return ""
	}
}

func consoleOf(frame protocol.WebSocketFrame) []protocol.ConsoleEntry {
	switch value := frame.(type) {
	case *protocol.ConsoleFrame:
		return value.Entries
	case protocol.ConsoleFrame:
		return value.Entries
	default:
		return nil
	}
}

func parseCursor(value string) (int64, error) {
	if value == "" {
		return 0, errors.New("empty cursor")
	}
	for _, r := range value {
		if r < '0' || r > '9' {
			return 0, errors.New("invalid cursor")
		}
	}
	return strconv.ParseInt(value, 10, 64)
}

func formatCursor(value int64) string { return strconv.FormatInt(value, 10) }

func randomID() string {
	var random [16]byte
	_, _ = rand.Read(random[:])
	return hex.EncodeToString(random[:])
}
