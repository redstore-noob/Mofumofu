// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

//go:build !linux && !darwin && !windows

package server

import (
	"errors"
	"os"
	"os/exec"
	"time"
)

var errProcessUnsupported = errors.New("process control is not supported on this platform")

func configureProcess(*exec.Cmd) {}

func killProcessTree(command *exec.Cmd) error {
	if command == nil || command.Process == nil {
		return nil
	}
	return command.Process.Kill()
}

func killPID(pid int) error {
	process, err := os.FindProcess(pid)
	if err != nil {
		return err
	}
	return process.Kill()
}

func processExists(int) bool { return false }

func latestLogBusy(string) bool { return false }

func processCPUSeconds(int) (float64, error) { return 0, errProcessUnsupported }

func processMemoryBytes(int) (int64, error) { return 0, errProcessUnsupported }

func processStartTime(int) (time.Time, error) { return time.Time{}, errProcessUnsupported }
