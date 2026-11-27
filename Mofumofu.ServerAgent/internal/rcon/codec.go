// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package rcon

import (
	"bytes"
	"encoding/binary"
	"errors"
	"fmt"
	"math"
)

const (
	// TypeResponse identifies a server response packet.
	TypeResponse int32 = 0
	// TypeCommand identifies a command packet.
	TypeCommand int32 = 2
	// TypeAuth identifies an authentication packet.
	TypeAuth int32 = 3
	// AuthFailedRequestID is the request ID used by servers when authentication fails.
	AuthFailedRequestID int32 = -1

	packetHeaderLength = 8
	packetPrefixLength = 4
	packetOverhead     = 10 // Request ID, type, and the two required NUL bytes.
)

var (
	// ErrIncompletePacket means more bytes are needed before decoding can finish.
	ErrIncompletePacket = errors.New("rcon: incomplete packet")
	// ErrInvalidLength means the length prefix is outside the protocol limits.
	ErrInvalidLength = errors.New("rcon: invalid packet length")
	// ErrPacketTooLarge means a body cannot fit within the configured packet limit.
	ErrPacketTooLarge = errors.New("rcon: packet too large")
	// ErrInvalidPadding means packet terminator or padding bytes are not NUL.
	ErrInvalidPadding = errors.New("rcon: invalid packet padding")
	// ErrInvalidBody means a body contains an embedded NUL byte.
	ErrInvalidBody = errors.New("rcon: invalid packet body")
)

// Packet is a decoded RCON packet. Body excludes the terminator and padding.
type Packet struct {
	RequestID int32
	Type      int32
	Body      []byte
}

// EncodePacket encodes one length-prefixed little-endian RCON packet. maxPacket
// limits the value of the length prefix, excluding the four-byte prefix and any
// alignment padding. A body must not contain NUL bytes.
func EncodePacket(requestID, packetType int32, body []byte, maxPacket int) ([]byte, error) {
	if maxPacket < packetOverhead {
		return nil, fmt.Errorf("rcon: max packet must be at least %d", packetOverhead)
	}
	if bytes.IndexByte(body, 0) >= 0 {
		return nil, fmt.Errorf("%w: embedded NUL byte", ErrInvalidBody)
	}
	if len(body) > maxPacket-packetOverhead {
		return nil, fmt.Errorf("%w: body is %d bytes, length-prefix limit is %d", ErrPacketTooLarge, len(body), maxPacket)
	}

	length := int64(len(body)) + packetOverhead
	if length > math.MaxInt32 {
		return nil, fmt.Errorf("%w: body length exceeds int32", ErrPacketTooLarge)
	}
	unpaddedLength := packetPrefixLength + packetHeaderLength + len(body) + 2
	padding := (-unpaddedLength) & 7
	wire := make([]byte, unpaddedLength+padding)
	binary.LittleEndian.PutUint32(wire[0:4], uint32(length))
	binary.LittleEndian.PutUint32(wire[4:8], uint32(requestID))
	binary.LittleEndian.PutUint32(wire[8:12], uint32(packetType))
	copy(wire[12:], body)
	// The remaining bytes are the required two NUL bytes plus zero padding.
	return wire, nil
}

// DecodePacket decodes the first packet in data and returns its wire size. It
// accepts fragmented or coalesced TCP data. ErrIncompletePacket is returned
// until the complete packet, including alignment padding, is available.
func DecodePacket(data []byte, maxPacket int) (Packet, int, error) {
	if maxPacket < packetOverhead {
		return Packet{}, 0, fmt.Errorf("rcon: max packet must be at least %d", packetOverhead)
	}
	if len(data) < packetPrefixLength {
		return Packet{}, 0, ErrIncompletePacket
	}

	length := int32(binary.LittleEndian.Uint32(data[0:4]))
	if length < packetOverhead || int64(length) > int64(maxPacket) {
		return Packet{}, 0, fmt.Errorf("%w: got %d, want %d..%d", ErrInvalidLength, length, packetOverhead, maxPacket)
	}

	bodyLength := int(length) - packetOverhead
	unpaddedLength := packetPrefixLength + packetHeaderLength + bodyLength + 2
	padding := (-unpaddedLength) & 7
	wireLength := unpaddedLength + padding
	if len(data) < wireLength {
		return Packet{}, 0, ErrIncompletePacket
	}
	if bytes.IndexByte(data[packetPrefixLength+packetHeaderLength:packetPrefixLength+packetHeaderLength+bodyLength], 0) >= 0 {
		return Packet{}, 0, fmt.Errorf("%w: embedded NUL byte", ErrInvalidBody)
	}
	for _, value := range data[packetPrefixLength+packetHeaderLength+bodyLength : wireLength] {
		if value != 0 {
			return Packet{}, 0, fmt.Errorf("%w: non-NUL byte after body", ErrInvalidPadding)
		}
	}

	body := bytes.Clone(data[packetPrefixLength+packetHeaderLength : packetPrefixLength+packetHeaderLength+bodyLength])
	return Packet{
		RequestID: int32(binary.LittleEndian.Uint32(data[4:8])),
		Type:      int32(binary.LittleEndian.Uint32(data[8:12])),
		Body:      body,
	}, wireLength, nil
}
