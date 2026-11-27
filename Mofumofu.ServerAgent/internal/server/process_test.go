// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package server

import (
	"os"
	"os/exec"
	"runtime"
	"testing"
	"time"
)

func TestParseProcStatAndVmRSS(t *testing.T) {
	line := "42 (my (weird) name) S 7 1 1 0 -1 4194304 100 0 0 0 250 50 0 0 20 0 1 0 999 0 0"
	parent, fields, err := parseProcStat(line)
	if err != nil || parent != 7 || len(fields) < 20 {
		t.Fatalf("parent=%d fields=%v err=%v", parent, fields, err)
	}
	if fields[11] != "250" || fields[12] != "50" || fields[19] != "999" {
		t.Fatalf("unexpected fields: %v", fields)
	}
	if _, _, err := parseProcStat("42 (short) S 7"); err == nil {
		t.Fatal("short stat accepted")
	}
	memory, err := parseVmRSS("Name:\tjava\nVmRSS:\t   2048 kB\nVmSize:\t 4096 kB\n")
	if err != nil || memory != 2048*1024 {
		t.Fatalf("memory=%d err=%v", memory, err)
	}
	if _, err := parseVmRSS("Name:\tjava\n"); err == nil {
		t.Fatal("missing VmRSS accepted")
	}
}

func TestParseBootTime(t *testing.T) {
	boot, err := parseBootTime("cpu  1 2 3\nbtime 1700000000\nprocesses 9\n")
	if err != nil || !boot.Equal(time.Unix(1700000000, 0)) {
		t.Fatalf("boot=%v err=%v", boot, err)
	}
	if _, err := parseBootTime("cpu 1 2\n"); err == nil {
		t.Fatal("missing btime accepted")
	}
}

func TestParsePSChildren(t *testing.T) {
	children := parsePSChildren("  1     0\n 10     1\n 11     1\n 12    10\nbogus\n")
	if len(children[1]) != 2 || children[1][0] != 10 || children[1][1] != 11 {
		t.Fatalf("children of 1: %v", children[1])
	}
	if len(children[10]) != 1 || children[10][0] != 12 {
		t.Fatalf("children of 10: %v", children[10])
	}
}

func TestParseProcessClock(t *testing.T) {
	values := map[string]float64{
		"0:01.25":      1.25,
		"1:02:03":      3723,
		"2-01:02:03.5": 2*86400 + 3723.5,
		"  0:00.00  ":  0,
		"12:30":        750,
	}
	for text, want := range values {
		got, err := parseProcessClock(text)
		if err != nil || got != want {
			t.Errorf("%q: got %v err=%v want %v", text, got, err, want)
		}
	}
	for _, text := range []string{"", "x:01", "1:2:3:4", "a-01:00:00"} {
		if _, err := parseProcessClock(text); err == nil {
			t.Errorf("%q accepted", text)
		}
	}
}

func TestParseLstart(t *testing.T) {
	want := time.Date(2026, time.November, 27, 10, 0, 0, 0, time.Local)
	for _, text := range []string{"Fri Nov 27 10:00:00 2026", "  Fri Nov 27 10:00:00 2026\n"} {
		got, err := parseLstart(text)
		if err != nil || !got.Equal(want) {
			t.Fatalf("%q: got %v err=%v", text, got, err)
		}
	}
	padded, err := parseLstart("Mon Nov  2 09:00:00 2026")
	if err != nil || padded.Day() != 2 {
		t.Fatalf("padded day: %v err=%v", padded, err)
	}
}

func TestPIDFileOwnershipRejectsLaterProcess(t *testing.T) {
	if _, err := processStartTime(os.Getpid()); err != nil {
		t.Skipf("process start time unavailable: %v", err)
	}
	if !pidFileOwnsProcess(os.Getpid(), time.Now()) {
		t.Fatal("current process should match a PID file written now")
	}
	if pidFileOwnsProcess(os.Getpid(), time.Now().Add(-time.Hour)) {
		t.Fatal("process started after an hour-old PID file must not match")
	}
}

func TestProcessMetricsOfSelf(t *testing.T) {
	if !processControlSupported() {
		t.Skip("process metrics are not implemented on this platform")
	}
	memory, err := processMemoryBytes(os.Getpid())
	if err != nil || memory <= 0 {
		t.Fatalf("memory=%d err=%v", memory, err)
	}
	cpu, err := processCPUSeconds(os.Getpid())
	if err != nil || cpu < 0 {
		t.Fatalf("cpu=%v err=%v", cpu, err)
	}
}

func TestKillPIDIgnoresMissingProcess(t *testing.T) {
	if !processControlSupported() {
		t.Skip("process control is not implemented on this platform")
	}
	if err := killPID(999999999); err != nil {
		t.Fatalf("missing process: %v", err)
	}
	if processExists(999999999) {
		t.Fatal("missing process reported as existing")
	}
}

func TestKillProcessTreeTerminatesChild(t *testing.T) {
	if !processControlSupported() {
		t.Skip("process control is not implemented on this platform")
	}
	command := exec.Command(os.Args[0], "-test.run=^TestProcessHelper$")
	command.Env = append(os.Environ(), "MOFU_PROCESS_HELPER=1")
	configureProcess(command)
	if err := command.Start(); err != nil {
		t.Fatal(err)
	}
	pid := command.Process.Pid
	deadline := time.Now().Add(5 * time.Second)
	for !processExists(pid) && time.Now().Before(deadline) {
		time.Sleep(10 * time.Millisecond)
	}
	if !processExists(pid) {
		t.Fatal("helper did not start")
	}
	if err := killProcessTree(command); err != nil {
		t.Fatal(err)
	}
	done := make(chan error, 1)
	go func() { done <- command.Wait() }()
	select {
	case <-done:
	case <-time.After(10 * time.Second):
		t.Fatal("helper was not terminated")
	}
	if processExists(pid) {
		t.Fatal("helper still exists after termination")
	}
}

func TestProcessHelper(t *testing.T) {
	if os.Getenv("MOFU_PROCESS_HELPER") != "1" {
		return
	}
	time.Sleep(time.Minute)
}

func processControlSupported() bool {
	switch runtime.GOOS {
	case "linux", "darwin", "windows":
		return true
	default:
		return false
	}
}
