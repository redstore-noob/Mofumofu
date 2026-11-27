// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package install

import "strings"

// Allowlist is the operator-configured install policy. Empty lists deny everything.
type Allowlist struct {
	// Cores contains exact core names: vanilla, paper, fabric, neoforge.
	Cores []string
	// MinecraftVersions contains exact IDs or "<prefix>.*" patterns. "1.21.*"
	// matches "1.21" and "1.21.4" but not "1.210".
	MinecraftVersions []string
}

// Check returns a forbidden error unless core and minecraftVersion are allowed.
func (a Allowlist) Check(core Core, minecraftVersion string) error {
	coreAllowed := false
	for _, allowed := range a.Cores {
		if allowed == string(core) {
			coreAllowed = true
			break
		}
	}
	if !coreAllowed {
		return forbiddenf("This core is not allowed by the Agent install allowlist.")
	}
	for _, entry := range a.MinecraftVersions {
		if matchesVersionEntry(entry, minecraftVersion) {
			return nil
		}
	}
	return forbiddenf("This Minecraft version is not allowed by the Agent install allowlist.")
}

func matchesVersionEntry(entry, version string) bool {
	if prefix, ok := strings.CutSuffix(entry, ".*"); ok {
		return version == prefix || strings.HasPrefix(version, prefix+".")
	}
	return entry == version
}
