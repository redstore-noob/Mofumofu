// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package install

import (
	"runtime"
	"strings"
)

// ServerJarName is the vanilla core file name and the fallback for unsafe names.
const ServerJarName = "server.jar"

// MaxVersionIDLength matches C# ValidateVersionId.
const MaxVersionIDLength = 64

// scriptUnsafeChars reject shell or cmd metacharacters in start-script values.
const scriptUnsafeChars = "\"'&|<>^%!$`;\r\n\x00"

// windowsInvalidFileNameChars mirrors .NET Path.GetInvalidFileNameChars on Windows.
const windowsInvalidFileNameChars = "\"<>|\\/:*?"

// ValidateVersionID mirrors C# ValidateVersionId: 1-64 ASCII letters, digits, '.', '-', '_', '+'.
func ValidateVersionID(value, name string) error {
	if strings.TrimSpace(value) == "" || len(value) > MaxVersionIDLength {
		return invalidf(name + " is invalid")
	}
	for index := 0; index < len(value); index++ {
		character := value[index]
		asciiLetterOrDigit := (character >= '0' && character <= '9') ||
			(character >= 'a' && character <= 'z') || (character >= 'A' && character <= 'Z')
		if !asciiLetterOrDigit && character != '.' && character != '-' && character != '_' && character != '+' {
			return invalidf(name + " contains invalid characters")
		}
	}
	return nil
}

// SanitizeFileName mirrors C# SanitizeFileName. Unsafe or empty names fall back
// to server.jar. The invalid-character set follows the host OS, as .NET does.
func SanitizeFileName(name string) string {
	fileName := strings.TrimSpace(strings.ReplaceAll(name, "\\", "/"))
	if index := strings.LastIndex(fileName, "/"); index >= 0 {
		fileName = fileName[index+1:]
	}
	if fileName == "" || fileName == "." || fileName == ".." ||
		strings.ContainsAny(fileName, invalidFileNameChars()) ||
		strings.ContainsAny(fileName, scriptUnsafeChars) {
		return ServerJarName
	}
	if runtime.GOOS == "windows" && containsControl(fileName) {
		return ServerJarName
	}
	return fileName
}

func invalidFileNameChars() string {
	if runtime.GOOS == "windows" {
		return windowsInvalidFileNameChars
	}
	return ""
}

func containsControl(value string) bool {
	for index := 0; index < len(value); index++ {
		if value[index] < 32 {
			return true
		}
	}
	return false
}

// SanitizeScriptValue mirrors C# SanitizeScriptValue: CR, LF, and NUL are
// dropped, and other shell metacharacters are rejected.
func SanitizeScriptValue(value string) (string, error) {
	var builder strings.Builder
	builder.Grow(len(value))
	for index := 0; index < len(value); index++ {
		character := value[index]
		if character == '\r' || character == '\n' || character == 0 {
			continue
		}
		if strings.IndexByte(scriptUnsafeChars, character) >= 0 {
			return "", invalidf("launch values contain characters that cannot be written to a start script")
		}
		builder.WriteByte(character)
	}
	return builder.String(), nil
}

// SanitizePropertyValue mirrors ServerManage.SanitizePropertyValue.
func SanitizePropertyValue(value string) string {
	return strings.Map(func(r rune) rune {
		if r == '\r' || r == '\n' || r == 0 {
			return -1
		}
		return r
	}, value)
}

// NeoForgeVersionPrefix maps "1.21.4" to "21.4." and date-style "26.1" to "26.1.".
func NeoForgeVersionPrefix(minecraftVersion string) (string, error) {
	trimmed := strings.TrimSpace(minecraftVersion)
	trimmed = strings.TrimPrefix(trimmed, "1.")
	if trimmed == "" || strings.HasPrefix(trimmed, ".") {
		return "", invalidf("NeoForge does not support this Minecraft version")
	}
	return trimmed + ".", nil
}
