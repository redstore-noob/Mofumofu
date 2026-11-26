// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package rcon

import (
	"bytes"
	"encoding/binary"
	"encoding/hex"
	"errors"
	"fmt"
	"testing"
)

type knownVector struct {
	name       string
	requestID  int32
	packetType int32
	body       string
	wireHex    string
}

var knownVectors = []knownVector{
	{
		name:       "auth",
		requestID:  1,
		packetType: TypeAuth,
		body:       "password",
		wireHex: "12000000" +
			"01000000" +
			"03000000" +
			"70617373776f7264" +
			"00000000",
	},
	{
		name:       "command",
		requestID:  2,
		packetType: TypeCommand,
		body:       "list",
		wireHex: "0e000000" +
			"02000000" +
			"02000000" +
			"6c697374" +
			"00000000" +
			"00000000",
	},
	{
		name:       "response",
		requestID:  2,
		packetType: TypeResponse,
		body:       "ok",
		wireHex: "0c000000" +
			"02000000" +
			"00000000" +
			"6f6b" +
			"0000",
	},
	{
		name:       "empty response",
		requestID:  7,
		packetType: TypeResponse,
		body:       "",
		wireHex: "0a000000" +
			"07000000" +
			"00000000" +
			"00000000",
	},
}

func TestEncodePacketKnownVectors(t *testing.T) {
	for _, vector := range knownVectors {
		t.Run(vector.name, func(t *testing.T) {
			want, err := hex.DecodeString(vector.wireHex)
			if err != nil {
				t.Fatalf("decode test vector: %v", err)
			}
			got, err := EncodePacket(vector.requestID, vector.packetType, []byte(vector.body), DefaultMaxPacket)
			if err != nil {
				t.Fatalf("EncodePacket: %v", err)
			}
			if !bytes.Equal(got, want) {
				t.Fatalf("packet bytes = %x, want %x", got, want)
			}
		})
	}
}

func TestDecodePacketKnownVectors(t *testing.T) {
	for _, vector := range knownVectors {
		t.Run(vector.name, func(t *testing.T) {
			wire, err := hex.DecodeString(vector.wireHex)
			if err != nil {
				t.Fatalf("decode test vector: %v", err)
			}
			packet, consumed, err := DecodePacket(wire, DefaultMaxPacket)
			if err != nil {
				t.Fatalf("DecodePacket: %v", err)
			}
			if consumed != len(wire) {
				t.Fatalf("consumed = %d, want %d", consumed, len(wire))
			}
			if packet.RequestID != vector.requestID || packet.Type != vector.packetType || string(packet.Body) != vector.body {
				t.Fatalf("packet = {%d %d %q}, want {%d %d %q}",
					packet.RequestID, packet.Type, packet.Body, vector.requestID, vector.packetType, vector.body)
			}
		})
	}
}

func TestPacketPaddingAndRoundTrip(t *testing.T) {
	for bodyLength := 0; bodyLength <= 32; bodyLength++ {
		t.Run(fmt.Sprintf("body_%d", bodyLength), func(t *testing.T) {
			body := make([]byte, bodyLength)
			for index := range body {
				body[index] = byte('a' + index%26)
			}
			wire, err := EncodePacket(42, TypeCommand, body, DefaultMaxPacket)
			if err != nil {
				t.Fatalf("EncodePacket: %v", err)
			}
			if len(wire)%8 != 0 {
				t.Fatalf("wire length %d is not aligned to 8", len(wire))
			}
			if got := binary.LittleEndian.Uint32(wire[:4]); got != uint32(bodyLength+10) {
				t.Fatalf("length prefix = %d, want %d", got, bodyLength+10)
			}
			for _, value := range wire[12+bodyLength:] {
				if value != 0 {
					t.Fatalf("non-zero terminator or padding byte %#x", value)
				}
			}
			packet, consumed, err := DecodePacket(wire, DefaultMaxPacket)
			if err != nil {
				t.Fatalf("DecodePacket: %v", err)
			}
			if consumed != len(wire) || packet.RequestID != 42 || packet.Type != TypeCommand || !bytes.Equal(packet.Body, body) {
				t.Fatalf("round trip = (%+v, %d), want body %x and size %d", packet, consumed, body, len(wire))
			}
		})
	}
}

func TestDecodePacketFragmentation(t *testing.T) {
	wire, err := hex.DecodeString(knownVectors[0].wireHex)
	if err != nil {
		t.Fatal(err)
	}
	for split := 0; split < len(wire); split++ {
		_, consumed, err := DecodePacket(wire[:split], DefaultMaxPacket)
		if !errors.Is(err, ErrIncompletePacket) || consumed != 0 {
			t.Fatalf("split %d: error = %v, consumed = %d; want ErrIncompletePacket", split, err, consumed)
		}
	}
	packet, consumed, err := DecodePacket(wire, DefaultMaxPacket)
	if err != nil || consumed != len(wire) || string(packet.Body) != "password" {
		t.Fatalf("complete packet = (%+v, %d, %v)", packet, consumed, err)
	}
}

func TestDecodePacketCoalesced(t *testing.T) {
	var stream []byte
	for _, vector := range knownVectors {
		wire, err := hex.DecodeString(vector.wireHex)
		if err != nil {
			t.Fatal(err)
		}
		stream = append(stream, wire...)
	}
	for index, vector := range knownVectors {
		packet, consumed, err := DecodePacket(stream, DefaultMaxPacket)
		if err != nil {
			t.Fatalf("packet %d: %v", index, err)
		}
		if consumed != len(vector.wireHex)/2 || string(packet.Body) != vector.body {
			t.Fatalf("packet %d = (%+v, %d), want body %q", index, packet, consumed, vector.body)
		}
		stream = stream[consumed:]
	}
	if len(stream) != 0 {
		t.Fatalf("%d bytes remain after coalesced stream", len(stream))
	}
}

func TestEncodePacketLimits(t *testing.T) {
	const maxPacket = 64
	body := bytes.Repeat([]byte{'x'}, maxPacket-10)
	if _, err := EncodePacket(1, TypeCommand, body, maxPacket); err != nil {
		t.Fatalf("maximum-size body: %v", err)
	}
	_, err := EncodePacket(1, TypeCommand, append(body, 'x'), maxPacket)
	if !errors.Is(err, ErrPacketTooLarge) {
		t.Fatalf("oversize error = %v, want ErrPacketTooLarge", err)
	}
	if _, err := EncodePacket(1, TypeCommand, []byte{'a', 0, 'b'}, maxPacket); !errors.Is(err, ErrInvalidBody) {
		t.Fatalf("NUL body error = %v, want ErrInvalidBody", err)
	}
	if _, err := EncodePacket(1, TypeCommand, nil, 9); err == nil {
		t.Fatal("EncodePacket accepted maxPacket below protocol overhead")
	}
}

func TestEncodePacketDefaultInputBoundary(t *testing.T) {
	body := bytes.Repeat([]byte{'x'}, DefaultMaxInput)
	wire, err := EncodePacket(1, TypeCommand, body, DefaultMaxPacket)
	if err != nil {
		t.Fatalf("EncodePacket: %v", err)
	}
	if len(wire) != DefaultMaxPacket {
		t.Fatalf("wire length = %d, want %d", len(wire), DefaultMaxPacket)
	}
	if got := binary.LittleEndian.Uint32(wire[:4]); got != DefaultMaxInput+10 {
		t.Fatalf("length prefix = %d, want %d", got, DefaultMaxInput+10)
	}
	packet, consumed, err := DecodePacket(wire, DefaultMaxPacket)
	if err != nil {
		t.Fatalf("DecodePacket: %v", err)
	}
	if consumed != len(wire) || !bytes.Equal(packet.Body, body) {
		t.Fatalf("boundary packet consumed %d of %d bytes or changed body", consumed, len(wire))
	}
}

func TestDecodePacketMalformed(t *testing.T) {
	auth, err := hex.DecodeString(knownVectors[0].wireHex)
	if err != nil {
		t.Fatal(err)
	}
	oversize := make([]byte, 4)
	binary.LittleEndian.PutUint32(oversize, DefaultMaxPacket+1)

	tests := []struct {
		name string
		data []byte
		want error
	}{
		{name: "length below overhead", data: []byte{9, 0, 0, 0}, want: ErrInvalidLength},
		{name: "negative length", data: []byte{0xff, 0xff, 0xff, 0xff}, want: ErrInvalidLength},
		{name: "oversize length", data: oversize, want: ErrInvalidLength},
		{name: "missing required padding", data: auth[:len(auth)-1], want: ErrIncompletePacket},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			_, consumed, err := DecodePacket(test.data, DefaultMaxPacket)
			if !errors.Is(err, test.want) || consumed != 0 {
				t.Fatalf("error = %v, consumed = %d; want %v", err, consumed, test.want)
			}
		})
	}

	badTerminator := bytes.Clone(auth)
	badTerminator[12+len(knownVectors[0].body)] = 1
	badAlignment := bytes.Clone(auth)
	badAlignment[len(badAlignment)-1] = 1
	embeddedNUL, err := EncodePacket(1, TypeCommand, []byte("abc"), DefaultMaxPacket)
	if err != nil {
		t.Fatal(err)
	}
	embeddedNUL[13] = 0
	for name, data := range map[string][]byte{
		"non-zero terminator": badTerminator,
		"non-zero alignment":  badAlignment,
		"embedded NUL":        embeddedNUL,
	} {
		t.Run(name, func(t *testing.T) {
			if _, _, err := DecodePacket(data, DefaultMaxPacket); err == nil {
				t.Fatal("DecodePacket accepted malformed packet")
			}
		})
	}
	if _, _, err := DecodePacket(auth, 9); err == nil {
		t.Fatal("DecodePacket accepted maxPacket below protocol overhead")
	}
}

func TestAuthFailurePacketRoundTrip(t *testing.T) {
	wire, err := EncodePacket(AuthFailedRequestID, TypeResponse, nil, DefaultMaxPacket)
	if err != nil {
		t.Fatal(err)
	}
	packet, _, err := DecodePacket(wire, DefaultMaxPacket)
	if err != nil {
		t.Fatal(err)
	}
	if packet.RequestID != AuthFailedRequestID || packet.Type != TypeResponse || len(packet.Body) != 0 {
		t.Fatalf("unexpected auth failure packet: %+v", packet)
	}
}
