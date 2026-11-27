// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package server

import (
	"bufio"
	"context"
	"encoding/binary"
	"fmt"
	"io"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/rcon"
)

func TestFakeJavaProcess(t *testing.T) {
	if os.Getenv("MOFU_FAKE_JAVA") != "1" {
		return
	}
	runFakeJava()
	os.Exit(0)
}

func runFakeJava() {
	fmt.Println("[12:00:00] [Server thread/INFO] Starting fake Minecraft server")
	fmt.Fprintln(os.Stderr, "[12:00:01] [Server thread/INFO] Done (0.1s)! For help, type \"help\"")
	time.Sleep(100 * time.Millisecond)
	scanner := bufio.NewScanner(os.Stdin)
	for scanner.Scan() {
		switch strings.TrimSpace(scanner.Text()) {
		case "list":
			fmt.Println("There are 0 of a max of 20 players online: ")
		case "say fallback":
			fmt.Println("[Server thread/INFO] [fallback]")
		case "crash":
			os.Exit(7)
		case "stop":
			if os.Getenv("MOFU_FAKE_IGNORE_STOP") == "1" {
				fmt.Println("[Server thread/INFO] Ignoring stop for force-kill test")
				continue
			}
			fmt.Println("[Server thread/INFO] Stopping fake server")
			return
		}
	}
}

func TestSupervisorStartConsoleCommandStopAndCursor(t *testing.T) {
	supervisor, _ := newTestSupervisor(t, false)
	console := make(chan string, 64)
	supervisor.options.OnConsole = func(line string) { console <- line }
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	if err := supervisor.Start(ctx); err != nil {
		t.Fatal(err)
	}
	waitForState(t, supervisor, StateRunning)
	if _, err := os.Stat(filepath.Join(supervisor.metadata.Directory, pidFileName)); err != nil {
		t.Fatalf("PID file missing: %v", err)
	}
	if err := supervisor.Command(ctx, "say fallback"); err != nil {
		t.Fatal(err)
	}
	waitForConsole(t, supervisor, "[fallback]")
	select {
	case line := <-console:
		if !strings.Contains(line, "Starting fake") && !strings.Contains(line, "Done (") {
			t.Fatalf("unexpected console callback: %q", line)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("no console callback")
	}
	before, _ := supervisor.RecentConsole(-1)
	if err := supervisor.Stop(ctx, false); err != nil {
		t.Fatal(err)
	}
	waitForState(t, supervisor, StateStopped)
	if _, err := os.Stat(filepath.Join(supervisor.metadata.Directory, pidFileName)); !os.IsNotExist(err) {
		t.Fatalf("PID file remains: %v", err)
	}
	if len(before) == 0 {
		t.Fatal("console ring was empty")
	}
	firstCursor := before[len(before)-1].Sequence
	if err := supervisor.Start(ctx); err != nil {
		t.Fatal(err)
	}
	waitForState(t, supervisor, StateRunning)
	lines, cursor := supervisor.RecentConsole(-1)
	if cursor <= firstCursor {
		t.Fatalf("console cursor reset: %d <= %d", cursor, firstCursor)
	}
	if len(lines) > ConsoleCapacity {
		t.Fatalf("ring too large: %d", len(lines))
	}
	supervisor.Close()
}

func TestSupervisorRestartAndForceKill(t *testing.T) {
	supervisor, _ := newTestSupervisor(t, false)
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	if err := supervisor.Start(ctx); err != nil {
		t.Fatal(err)
	}
	waitForState(t, supervisor, StateRunning)
	firstPID := supervisor.Snapshot().PID
	if err := supervisor.Restart(ctx); err != nil {
		t.Fatal(err)
	}
	waitForState(t, supervisor, StateRunning)
	if secondPID := supervisor.Snapshot().PID; secondPID == firstPID {
		t.Fatalf("restart kept PID %d", firstPID)
	}
	supervisor.options.StopGrace = 50 * time.Millisecond
	supervisor.options.ForceWait = time.Second
	supervisor.mu.Lock()
	supervisor.stopRequested = false
	supervisor.mu.Unlock()
	// The helper reads this environment variable from the parent at process start;
	// use force kill directly to verify the no-stdin-stop path.
	if err := supervisor.Kill(ctx); err != nil {
		t.Fatal(err)
	}
	waitForState(t, supervisor, StateStopped)
	supervisor.Close()
}

func TestSupervisorStopTimeoutKills(t *testing.T) {
	t.Setenv("MOFU_FAKE_IGNORE_STOP", "1")
	supervisor, _ := newTestSupervisor(t, false)
	supervisor.options.StopGrace = 50 * time.Millisecond
	supervisor.options.ForceWait = time.Second
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if err := supervisor.Start(ctx); err != nil {
		t.Fatal(err)
	}
	waitForState(t, supervisor, StateRunning)
	if err := supervisor.Stop(ctx, false); err != nil {
		t.Fatal(err)
	}
	waitForState(t, supervisor, StateStopped)
}

func TestSupervisorAutoRestartOnce(t *testing.T) {
	supervisor, _ := newTestSupervisor(t, true)
	supervisor.options.AutoRestartDelay = 20 * time.Millisecond
	supervisor.options.MaxAutoRestarts = 1
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	if err := supervisor.Start(ctx); err != nil {
		t.Fatal(err)
	}
	waitForState(t, supervisor, StateRunning)
	firstPID := supervisor.Snapshot().PID
	if err := supervisor.Command(ctx, "crash"); err != nil {
		t.Fatal(err)
	}
	waitForPIDChange(t, supervisor, firstPID)
	if err := supervisor.Command(ctx, "crash"); err != nil {
		t.Fatal(err)
	}
	waitForState(t, supervisor, StateCrashed)
	time.Sleep(100 * time.Millisecond)
	if state := supervisor.State(); state != StateCrashed {
		t.Fatalf("restart storm was not stopped: %s", state)
	}
	supervisor.Close()
}

func TestSupervisorRCONPriorityAndStdinFallback(t *testing.T) {
	supervisor, _ := newTestSupervisor(t, false)
	mock := newRCONMock(t, "secret")
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	if err := supervisor.Start(ctx); err != nil {
		t.Fatal(err)
	}
	waitForState(t, supervisor, StateRunning)
	supervisor.mu.Lock()
	supervisor.rcon = RconConfiguration{Enabled: true, Port: mock.port, Password: "secret"}
	supervisor.mu.Unlock()
	if err := supervisor.Command(ctx, "say via-rcon"); err != nil {
		t.Fatal(err)
	}
	time.Sleep(50 * time.Millisecond)
	lines, _ := supervisor.RecentConsole(-1)
	for _, line := range lines {
		if strings.Contains(line.Message, "via-rcon") {
			t.Fatal("RCON command unexpectedly used stdin")
		}
	}
	mock.close()
	if err := supervisor.Command(ctx, "say fallback"); err != nil {
		t.Fatal(err)
	}
	waitForConsole(t, supervisor, "[fallback]")
	supervisor.Close()
}

func TestEnsureRconPreservesPasswordAndRepairsPort(t *testing.T) {
	directory := t.TempDir()
	payload := "# keep\nenable-rcon=false\nrcon.port=0\nrcon.password=existing\n"
	if err := os.WriteFile(filepath.Join(directory, "server.properties"), []byte(payload), 0o600); err != nil {
		t.Fatal(err)
	}
	configuration, err := ensureRcon(directory)
	if err != nil {
		t.Fatal(err)
	}
	if !configuration.Enabled || configuration.Port != 25575 || configuration.Password != "existing" {
		t.Fatalf("configuration=%+v", configuration)
	}
	updated, _ := os.ReadFile(filepath.Join(directory, "server.properties"))
	if !strings.Contains(string(updated), "enable-rcon=true") || !strings.Contains(string(updated), "rcon.port=25575") ||
		!strings.Contains(string(updated), "rcon.password=existing") || !strings.Contains(string(updated), "# keep") {
		t.Fatalf("updated properties:\n%s", updated)
	}
}

func TestParsePlayerListNames(t *testing.T) {
	names := ParsePlayerListNames("There are 2 of a max of 20 players online: Alice, bob_1,  ")
	if strings.Join(names, ",") != "Alice,bob_1" {
		t.Fatalf("names=%v", names)
	}
	if names := ParsePlayerListNames("not a list: Alice"); names != nil {
		t.Fatalf("invalid response parsed: %v", names)
	}
}

func newTestSupervisor(t *testing.T, autoRestart bool) (*Supervisor, *Metadata) {
	t.Helper()
	root := t.TempDir()
	directory := filepath.Join(root, "survival")
	if err := os.Mkdir(directory, 0o750); err != nil {
		t.Fatal(err)
	}
	payload := fmt.Sprintf(`{"id":"survival","name":"Survival","core":"vanilla","mcVersion":"1.21.4","maxPlayers":20,"autoRestart":%t}`, autoRestart)
	if err := os.WriteFile(filepath.Join(directory, "server.json"), []byte(payload), 0o600); err != nil {
		t.Fatal(err)
	}
	metadata, err := LoadMetadata(root, "survival")
	if err != nil {
		t.Fatal(err)
	}
	options := Options{
		NoEnsureRcon:     true,
		JavaPathOverride: "fake-java",
		Launch: func(_ string, _ []string, directory string) (*exec.Cmd, error) {
			command := exec.Command(os.Args[0], "-test.run=^TestFakeJavaProcess$")
			command.Dir = directory
			command.Env = append(os.Environ(), "MOFU_FAKE_JAVA=1")
			return command, nil
		},
	}
	supervisor := NewSupervisor(metadata, options)
	t.Cleanup(supervisor.Close)
	return supervisor, metadata
}

func waitForState(t *testing.T, supervisor *Supervisor, wanted string) {
	t.Helper()
	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		if supervisor.State() == wanted {
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatalf("state=%s lastError=%q, want %s", supervisor.State(), supervisor.LastError(), wanted)
}

func waitForPIDChange(t *testing.T, supervisor *Supervisor, previous int) {
	t.Helper()
	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		snapshot := supervisor.Snapshot()
		if snapshot.PID != 0 && snapshot.PID != previous && snapshot.State == StateRunning {
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatalf("PID did not change from %d: %+v", previous, supervisor.Snapshot())
}

func waitForConsole(t *testing.T, supervisor *Supervisor, substring string) {
	t.Helper()
	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		lines, _ := supervisor.RecentConsole(-1)
		for _, line := range lines {
			if strings.Contains(line.Message, substring) {
				return
			}
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatalf("console did not contain %q", substring)
}

type rconMock struct {
	listener net.Listener
	port     int
	closed   chan struct{}
	once     sync.Once
}

func newRCONMock(t *testing.T, password string) *rconMock {
	t.Helper()
	listener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	mock := &rconMock{listener: listener, port: listener.Addr().(*net.TCPAddr).Port, closed: make(chan struct{})}
	go func() {
		for {
			connection, err := listener.Accept()
			if err != nil {
				return
			}
			go mock.serve(connection, password)
		}
	}()
	t.Cleanup(mock.close)
	return mock
}

func (m *rconMock) serve(connection net.Conn, password string) {
	defer connection.Close()
	auth, err := readRPacket(connection)
	if err != nil || auth.Type != rcon.TypeAuth || string(auth.Body) != password {
		return
	}
	if err := writeRPacket(connection, auth.RequestID, rcon.TypeResponse, nil); err != nil {
		return
	}
	command, err := readRPacket(connection)
	if err != nil {
		return
	}
	body := "There are 2 of a max of 20 players online: Alice, bob_1"
	_ = writeRPacket(connection, command.RequestID, rcon.TypeResponse, []byte(body))
}

func (m *rconMock) close() {
	m.once.Do(func() {
		close(m.closed)
		_ = m.listener.Close()
	})
}

func readRPacket(reader io.Reader) (rcon.Packet, error) {
	var length [4]byte
	if _, err := io.ReadFull(reader, length[:]); err != nil {
		return rcon.Packet{}, err
	}
	size := int(binary.LittleEndian.Uint32(length[:]))
	payload := make([]byte, size)
	if _, err := io.ReadFull(reader, payload); err != nil {
		return rcon.Packet{}, err
	}
	packet, consumed, err := rcon.DecodePacket(append(length[:], payload...), rcon.DefaultMaxPacket)
	if err != nil {
		return rcon.Packet{}, err
	}
	if consumed != 4+size {
		return rcon.Packet{}, fmt.Errorf("unexpected packet size")
	}
	return packet, nil
}

func writeRPacket(writer io.Writer, requestID int32, packetType int32, body []byte) error {
	packet, err := rcon.EncodePacket(requestID, packetType, body, rcon.DefaultMaxPacket)
	if err != nil {
		return err
	}
	_, err = writer.Write(packet)
	return err
}
