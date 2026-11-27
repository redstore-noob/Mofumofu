// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package players

import (
	"crypto/md5"
	"encoding/hex"
	"strings"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

func offlineUUID(name string) protocol.UUID {
	// MD5 and the version/variant bits are part of Minecraft's offline-player
	// identity format, not a password or general-purpose security hash.
	hash := md5.Sum([]byte("OfflinePlayer:" + name))
	hash[6] = (hash[6] & 0x0f) | 0x30
	hash[8] = (hash[8] & 0x3f) | 0x80
	var result protocol.UUID
	copy(result[:], hash[:])
	return result
}

func resolveUUID(value, name string) protocol.UUID {
	trimmed := strings.ToLower(strings.TrimSpace(value))
	compact := strings.ReplaceAll(trimmed, "-", "")
	if len(compact) == 32 {
		if decoded, err := hex.DecodeString(compact); err == nil {
			var result protocol.UUID
			copy(result[:], decoded)
			return result
		}
	}
	return offlineUUID(name)
}
