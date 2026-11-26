// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Mofumofu

package protocol

import (
	"bytes"
	"encoding/hex"
	"encoding/json"
	"fmt"
)

// UUID is a 128-bit GUID using the lower-case hyphenated JSON representation.
type UUID [16]byte

// ParseUUID accepts the common D, N, B, and P GUID text forms.
func ParseUUID(value string) (UUID, error) {
	var result UUID
	text := value
	switch len(text) {
	case 38:
		if (text[0] != '{' || text[37] != '}') && (text[0] != '(' || text[37] != ')') {
			return result, fmt.Errorf("invalid UUID %q", value)
		}
		text = text[1:37]
	case 36:
		for _, index := range []int{8, 13, 18, 23} {
			if text[index] != '-' {
				return result, fmt.Errorf("invalid UUID %q", value)
			}
		}
		text = text[:8] + text[9:13] + text[14:18] + text[19:23] + text[24:]
	case 32:
	default:
		return result, fmt.Errorf("invalid UUID %q", value)
	}
	decoded, err := hex.DecodeString(text)
	if err != nil || len(decoded) != len(result) {
		return result, fmt.Errorf("invalid UUID %q", value)
	}
	copy(result[:], decoded)
	return result, nil
}

func (u UUID) String() string {
	text := hex.EncodeToString(u[:])
	return text[:8] + "-" + text[8:12] + "-" + text[12:16] + "-" + text[16:20] + "-" + text[20:]
}

// MarshalJSON writes the lower-case hyphenated GUID form.
func (u UUID) MarshalJSON() ([]byte, error) {
	return []byte(`"` + u.String() + `"`), nil
}

// UnmarshalJSON parses a GUID string.
func (u *UUID) UnmarshalJSON(data []byte) error {
	if bytes.Equal(bytes.TrimSpace(data), []byte("null")) {
		return fmt.Errorf("UUID must not be null")
	}
	var value string
	if err := json.Unmarshal(data, &value); err != nil {
		return err
	}
	parsed, err := ParseUUID(value)
	if err != nil {
		return err
	}
	*u = parsed
	return nil
}

// Uuid is an alias matching the C# spelling.
type Uuid = UUID

// ParseUuid is an alias matching the C# spelling.
func ParseUuid(value string) (UUID, error) {
	return ParseUUID(value)
}
