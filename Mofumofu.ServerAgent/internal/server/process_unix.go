// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

//go:build linux || darwin

package server

import (
	"errors"
	"os"
	"os/exec"
	"syscall"
)

func configureProcess(command *exec.Cmd) {
	command.SysProcAttr = &syscall.SysProcAttr{Setpgid: true}
}

func killProcessTree(command *exec.Cmd) error {
	if command == nil || command.Process == nil {
		return nil
	}
	pid := command.Process.Pid
	if err := syscall.Kill(-pid, syscall.SIGKILL); err != nil {
		if errors.Is(err, syscall.ESRCH) {
			return nil
		}
		return command.Process.Kill()
	}
	return nil
}

func killPID(pid int) error {
	if err := syscall.Kill(-pid, syscall.SIGKILL); err == nil || errors.Is(err, syscall.ESRCH) {
		return nil
	}
	// A process started by the .NET Agent may not be a process-group leader.
	// Walk parent links so cutover orphan cleanup still removes children.
	children, _ := processChildren()
	var descendants func(int)
	descendants = func(parent int) {
		for _, child := range children[parent] {
			descendants(child)
			_ = syscall.Kill(child, syscall.SIGKILL)
		}
	}
	descendants(pid)
	if err := syscall.Kill(pid, syscall.SIGKILL); err != nil && !errors.Is(err, syscall.ESRCH) {
		return err
	}
	return nil
}

func processExists(pid int) bool {
	err := syscall.Kill(pid, 0)
	return err == nil || errors.Is(err, syscall.EPERM)
}

func latestLogBusy(path string) bool {
	file, err := os.OpenFile(path, os.O_RDWR|os.O_APPEND, 0)
	if err != nil {
		return false
	}
	defer file.Close()
	if err := syscall.Flock(int(file.Fd()), syscall.LOCK_EX|syscall.LOCK_NB); err != nil {
		return errors.Is(err, syscall.EWOULDBLOCK) || errors.Is(err, syscall.EAGAIN)
	}
	_ = syscall.Flock(int(file.Fd()), syscall.LOCK_UN)
	return false
}
