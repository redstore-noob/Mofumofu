// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package probe

import (
	"crypto/hmac"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/hex"
	"fmt"
	"strings"
)

// Canonical reproduces the shared NekoProbe/ServerAgent callback vector.
func Canonical(method, path, instanceID, timestamp, nonce string, body []byte) string {
	digest := sha256.Sum256(body)
	return strings.ToUpper(method) + "\n" + path + "\n" + instanceID + "\n" + timestamp + "\n" + nonce + "\n" + hex.EncodeToString(digest[:])
}

func Signature(linkSecret []byte, canonical string) string {
	mac := hmac.New(sha256.New, linkSecret)
	_, _ = mac.Write([]byte(canonical))
	return hex.EncodeToString(mac.Sum(nil))
}

func VerifySignature(linkSecret []byte, canonical, supplied string) bool {
	value := supplied
	if index := strings.IndexByte(value, '='); index >= 0 {
		prefix := value[:index]
		if !strings.EqualFold(prefix, "sha256") && !strings.EqualFold(prefix, "hmac-sha256") {
			return false
		}
		value = value[index+1:]
	}
	decoded, err := hex.DecodeString(value)
	if err != nil || len(decoded) != sha256.Size {
		return false
	}
	expected, _ := hex.DecodeString(Signature(linkSecret, canonical))
	return subtle.ConstantTimeCompare(expected, decoded) == 1
}

func LinkSecret(value string) ([]byte, error) {
	decoded, err := hex.DecodeString(value)
	if err != nil || len(decoded) != sha256.Size {
		return nil, fmt.Errorf("link secret must be 32 bytes of hex")
	}
	return decoded, nil
}
