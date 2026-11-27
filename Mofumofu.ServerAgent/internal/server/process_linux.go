// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

//go:build linux

package server

import (
	"errors"
	"fmt"
	"os"
	"strconv"
	"time"
)

// /proc reports CPU and start times in USER_HZ units, which is 100 on every
// Linux ABI.
const linuxClockTicks = 100

func processChildren() (map[int][]int, error) {
	entries, err := os.ReadDir("/proc")
	if err != nil {
		return nil, err
	}
	children := make(map[int][]int)
	for _, entry := range entries {
		child, parseErr := strconv.Atoi(entry.Name())
		if parseErr != nil {
			continue
		}
		if parent, _, statErr := processStat(child); statErr == nil {
			children[parent] = append(children[parent], child)
		}
	}
	return children, nil
}

func processStat(pid int) (int, []string, error) {
	payload, err := os.ReadFile(fmt.Sprintf("/proc/%d/stat", pid))
	if err != nil {
		return 0, nil, err
	}
	return parseProcStat(string(payload))
}

func processCPUSeconds(pid int) (float64, error) {
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
	return (user + system) / linuxClockTicks, nil
}

func processMemoryBytes(pid int) (int64, error) {
	payload, err := os.ReadFile(fmt.Sprintf("/proc/%d/status", pid))
	if err != nil {
		return 0, err
	}
	return parseVmRSS(string(payload))
}

func processStartTime(pid int) (time.Time, error) {
	statPayload, err := os.ReadFile("/proc/stat")
	if err != nil {
		return time.Time{}, err
	}
	boot, err := parseBootTime(string(statPayload))
	if err != nil {
		return time.Time{}, err
	}
	_, fields, err := processStat(pid)
	if err != nil {
		return time.Time{}, err
	}
	if len(fields) < 20 {
		return time.Time{}, errors.New("short /proc stat")
	}
	ticks, err := strconv.ParseInt(fields[19], 10, 64)
	if err != nil {
		return time.Time{}, err
	}
	return boot.Add(time.Duration(ticks) * (time.Second / linuxClockTicks)), nil
}
