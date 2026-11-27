// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package server

import (
	"errors"
	"strconv"
	"strings"
	"time"
)

// parseProcStat reads the fields of a Linux /proc/<pid>/stat line. fields[0] is
// the state (stat field 3), so stat field n is fields[n-3].
func parseProcStat(text string) (int, []string, error) {
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

func parseVmRSS(text string) (int64, error) {
	for _, line := range strings.Split(text, "\n") {
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

func parseBootTime(text string) (time.Time, error) {
	for _, line := range strings.Split(text, "\n") {
		if value, found := strings.CutPrefix(line, "btime "); found {
			seconds, err := strconv.ParseInt(strings.TrimSpace(value), 10, 64)
			if err != nil {
				return time.Time{}, err
			}
			return time.Unix(seconds, 0), nil
		}
	}
	return time.Time{}, errors.New("btime not found")
}

// parsePSChildren builds a parent-to-children map from `ps -A -o pid=,ppid=`.
func parsePSChildren(text string) map[int][]int {
	children := make(map[int][]int)
	for _, line := range strings.Split(text, "\n") {
		fields := strings.Fields(line)
		if len(fields) != 2 {
			continue
		}
		pid, pidErr := strconv.Atoi(fields[0])
		parent, parentErr := strconv.Atoi(fields[1])
		if pidErr != nil || parentErr != nil {
			continue
		}
		children[parent] = append(children[parent], pid)
	}
	return children
}

// parseProcessClock parses the cumulative CPU time printed by ps, such as
// "0:01.25", "1:02:03", or "2-01:02:03.5", and returns seconds.
func parseProcessClock(text string) (float64, error) {
	value := strings.TrimSpace(text)
	days := 0.0
	if before, after, found := strings.Cut(value, "-"); found {
		count, err := strconv.Atoi(before)
		if err != nil {
			return 0, err
		}
		days = float64(count)
		value = after
	}
	parts := strings.Split(value, ":")
	if len(parts) > 3 {
		return 0, errors.New("invalid process time")
	}
	seconds := 0.0
	for _, part := range parts {
		number, err := strconv.ParseFloat(part, 64)
		if err != nil {
			return 0, err
		}
		seconds = seconds*60 + number
	}
	return days*86400 + seconds, nil
}

// parseLstart parses the start time printed by `ps -o lstart=`, for example
// "Fri Nov 27 10:00:00 2026".
func parseLstart(text string) (time.Time, error) {
	return time.ParseInLocation("Mon Jan _2 15:04:05 2006", strings.TrimSpace(text), time.Local)
}
