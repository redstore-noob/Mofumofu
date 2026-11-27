// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

//go:build darwin

package server

import (
	"errors"
	"os/exec"
	"strconv"
	"strings"
	"time"
)

// macOS has no /proc, so process facts come from ps.

func processChildren() (map[int][]int, error) {
	output, err := psOutput("-A", "-o", "pid=,ppid=")
	if err != nil {
		return nil, err
	}
	return parsePSChildren(output), nil
}

func processCPUSeconds(pid int) (float64, error) {
	output, err := psOutput("-o", "time=", "-p", strconv.Itoa(pid))
	if err != nil {
		return 0, err
	}
	return parseProcessClock(output)
}

func processMemoryBytes(pid int) (int64, error) {
	output, err := psOutput("-o", "rss=", "-p", strconv.Itoa(pid))
	if err != nil {
		return 0, err
	}
	kilobytes, err := strconv.ParseInt(output, 10, 64)
	if err != nil {
		return 0, err
	}
	return kilobytes * 1024, nil
}

func processStartTime(pid int) (time.Time, error) {
	output, err := psOutput("-o", "lstart=", "-p", strconv.Itoa(pid))
	if err != nil {
		return time.Time{}, err
	}
	return parseLstart(output)
}

func psOutput(args ...string) (string, error) {
	output, err := exec.Command("ps", args...).Output()
	if err != nil {
		return "", err
	}
	text := strings.TrimSpace(string(output))
	if text == "" {
		return "", errors.New("process not found")
	}
	return text, nil
}
