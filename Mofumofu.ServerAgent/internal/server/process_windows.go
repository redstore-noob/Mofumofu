// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

//go:build windows

package server

import (
	"bytes"
	"errors"
	"fmt"
	"os/exec"
	"strconv"
	"syscall"
	"time"
	"unsafe"
)

const (
	windowsSynchronize             = 0x00100000
	windowsProcessQueryInformation = 0x0400
	windowsProcessVMRead           = 0x0010
	windowsProcessQueryLimited     = 0x1000
	windowsStillActive             = 259
	windowsCreateNoWindow          = 0x08000000
	// windowsEpochOffset counts 100-nanosecond ticks between 1601 and 1970.
	windowsEpochOffset      = 116444736000000000
	windowsSharingViolation = syscall.Errno(32)
)

var procGetProcessMemoryInfo = syscall.NewLazyDLL("psapi.dll").NewProc("GetProcessMemoryInfo")

type processMemoryCounters struct {
	CB                         uint32
	PageFaultCount             uint32
	PeakWorkingSetSize         uintptr
	WorkingSetSize             uintptr
	QuotaPeakPagedPoolUsage    uintptr
	QuotaPagedPoolUsage        uintptr
	QuotaPeakNonPagedPoolUsage uintptr
	QuotaNonPagedPoolUsage     uintptr
	PagefileUsage              uintptr
	PeakPagefileUsage          uintptr
}

func configureProcess(command *exec.Cmd) {
	command.SysProcAttr = &syscall.SysProcAttr{CreationFlags: windowsCreateNoWindow}
}

func killProcessTree(command *exec.Cmd) error {
	if command == nil || command.Process == nil {
		return nil
	}
	if err := killPID(command.Process.Pid); err != nil {
		return command.Process.Kill()
	}
	return nil
}

// killPID terminates pid and its descendants. taskkill walks parent links, so
// children whose parent has already exited are not reached.
func killPID(pid int) error {
	if !processExists(pid) {
		return nil
	}
	command := exec.Command("taskkill", "/PID", strconv.Itoa(pid), "/T", "/F")
	command.SysProcAttr = &syscall.SysProcAttr{CreationFlags: windowsCreateNoWindow}
	output, err := command.CombinedOutput()
	if err != nil && processExists(pid) {
		return fmt.Errorf("taskkill %d: %w: %s", pid, err, bytes.TrimSpace(output))
	}
	return nil
}

func processExists(pid int) bool {
	handle, err := openProcess(pid, windowsSynchronize|windowsProcessQueryLimited)
	if err != nil {
		return errors.Is(err, syscall.ERROR_ACCESS_DENIED)
	}
	defer syscall.CloseHandle(handle)
	var code uint32
	if err := syscall.GetExitCodeProcess(handle, &code); err != nil {
		return true
	}
	return code == windowsStillActive
}

// latestLogBusy reports whether another handle holds path open. Opening with
// no sharing mode fails with a sharing violation exactly in that case.
func latestLogBusy(path string) bool {
	name, err := syscall.UTF16PtrFromString(path)
	if err != nil {
		return false
	}
	handle, err := syscall.CreateFile(name, syscall.GENERIC_READ|syscall.GENERIC_WRITE, 0, nil,
		syscall.OPEN_EXISTING, syscall.FILE_ATTRIBUTE_NORMAL, 0)
	if err != nil {
		return errors.Is(err, windowsSharingViolation)
	}
	_ = syscall.CloseHandle(handle)
	return false
}

func processCPUSeconds(pid int) (float64, error) {
	handle, err := openProcess(pid, windowsProcessQueryInformation)
	if err != nil {
		return 0, err
	}
	defer syscall.CloseHandle(handle)
	var creation, exit, kernel, user syscall.Filetime
	if err := syscall.GetProcessTimes(handle, &creation, &exit, &kernel, &user); err != nil {
		return 0, err
	}
	return float64(filetimeTicks(kernel)+filetimeTicks(user)) / 1e7, nil
}

func processMemoryBytes(pid int) (int64, error) {
	handle, err := openProcess(pid, windowsProcessQueryInformation|windowsProcessVMRead)
	if err != nil {
		return 0, err
	}
	defer syscall.CloseHandle(handle)
	var counters processMemoryCounters
	counters.CB = uint32(unsafe.Sizeof(counters))
	ok, _, callErr := procGetProcessMemoryInfo.Call(uintptr(handle), uintptr(unsafe.Pointer(&counters)), uintptr(counters.CB))
	if ok == 0 {
		if callErr != nil {
			return 0, callErr
		}
		return 0, errors.New("GetProcessMemoryInfo failed")
	}
	return int64(counters.WorkingSetSize), nil
}

func processStartTime(pid int) (time.Time, error) {
	handle, err := openProcess(pid, windowsProcessQueryInformation)
	if err != nil {
		return time.Time{}, err
	}
	defer syscall.CloseHandle(handle)
	var creation, exit, kernel, user syscall.Filetime
	if err := syscall.GetProcessTimes(handle, &creation, &exit, &kernel, &user); err != nil {
		return time.Time{}, err
	}
	return time.Unix(0, (filetimeTicks(creation)-windowsEpochOffset)*100), nil
}

func openProcess(pid int, access uint32) (syscall.Handle, error) {
	return syscall.OpenProcess(access, false, uint32(pid))
}

func filetimeTicks(value syscall.Filetime) int64 {
	return int64(value.HighDateTime)<<32 + int64(value.LowDateTime)
}
