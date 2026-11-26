// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

//go:build !linux

package server

import (
	"errors"
	"os/exec"
	"strconv"
)

func configureProcess(*exec.Cmd) {}

func killProcessTree(command *exec.Cmd) error {
	if command == nil || command.Process == nil {
		return nil
	}
	return command.Process.Kill()
}

func killPID(pid int) error {
	return exec.Command("taskkill", "/PID", strconv.Itoa(pid), "/T", "/F").Run()
}

func processExists(int) bool { return false }

func latestLogBusy(string) bool { return false }

func processCPUTicks(int) (float64, error) { return 0, errors.New("process metrics are Linux-only") }

func processMemoryBytes(int) (int64, error) { return 0, errors.New("process metrics are Linux-only") }
