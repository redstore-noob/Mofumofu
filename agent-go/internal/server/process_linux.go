// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

//go:build linux

package server

import (
	"errors"
	"fmt"
	"os"
	"os/exec"
	"strconv"
	"strings"
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
	// Walk /proc parent links so cutover orphan cleanup still removes children.
	children := make(map[int][]int)
	entries, _ := os.ReadDir("/proc")
	for _, entry := range entries {
		child, parseErr := strconv.Atoi(entry.Name())
		if parseErr != nil {
			continue
		}
		if parent, _, statErr := processStat(child); statErr == nil {
			children[parent] = append(children[parent], child)
		}
	}
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

func processStat(pid int) (int, []string, error) {
	payload, err := os.ReadFile(fmt.Sprintf("/proc/%d/stat", pid))
	if err != nil {
		return 0, nil, err
	}
	text := string(payload)
	index := strings.LastIndexByte(text, ')')
	if index < 0 || index+2 >= len(text) {
		return 0, nil, errors.New("invalid /proc stat")
	}
	fields := strings.Fields(text[index+2:])
	if len(fields) < 13 {
		return 0, nil, errors.New("short /proc stat")
	}
	parent, err := strconv.Atoi(fields[1])
	if err != nil {
		return 0, nil, err
	}
	return parent, fields, nil
}

func processCPUTicks(pid int) (float64, error) {
	_, fields, err := processStat(pid)
	if err != nil {
		return 0, err
	}
	user, err := strconv.ParseFloat(fields[11], 64)
	if err != nil {
		return 0, err
	}
	system, err := strconv.ParseFloat(fields[12], 64)
	if err != nil {
		return 0, err
	}
	return user + system, nil
}

func processMemoryBytes(pid int) (int64, error) {
	payload, err := os.ReadFile(fmt.Sprintf("/proc/%d/status", pid))
	if err != nil {
		return 0, err
	}
	for _, line := range strings.Split(string(payload), "\n") {
		if strings.HasPrefix(line, "VmRSS:") {
			fields := strings.Fields(line)
			if len(fields) < 2 {
				break
			}
			kilobytes, err := strconv.ParseInt(fields[1], 10, 64)
			if err != nil {
				return 0, err
			}
			return kilobytes * 1024, nil
		}
	}
	return 0, errors.New("VmRSS not found")
}
