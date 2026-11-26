// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package rcon

import (
	"bytes"
	"context"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"net"
	"strings"
	"testing"
	"time"
)

type mockRCONServer struct {
	listener net.Listener
	done     chan error
}

func startMockRCONServer(t *testing.T, handler func(net.Conn) error) *mockRCONServer {
	t.Helper()
	listener, err := net.Listen("tcp4", net.JoinHostPort(LoopbackHost, "0"))
	if err != nil {
		t.Fatalf("listen: %v", err)
	}
	server := &mockRCONServer{listener: listener, done: make(chan error, 1)}
	go func() {
		conn, err := listener.Accept()
		if err != nil {
			if errors.Is(err, net.ErrClosed) {
				server.done <- nil
				return
			}
			server.done <- err
			return
		}
		defer func() { _ = conn.Close() }()
		_ = conn.SetDeadline(time.Now().Add(5 * time.Second))
		server.done <- handler(conn)
	}()
	t.Cleanup(func() {
		_ = listener.Close()
		select {
		case err := <-server.done:
			if err != nil && !errors.Is(err, io.EOF) && !errors.Is(err, net.ErrClosed) {
				t.Errorf("mock server: %v", err)
			}
		case <-time.After(6 * time.Second):
			t.Error("mock server did not stop")
		}
	})
	return server
}

func (s *mockRCONServer) port(t *testing.T) int {
	t.Helper()
	port := s.listener.Addr().(*net.TCPAddr).Port
	return port
}

func mockConfig(port int) Config {
	cfg := DefaultConfig()
	cfg.Port = port
	cfg.Password = "test-password"
	cfg.ConnectTimeout = time.Second
	return cfg
}

func readMockPacket(conn net.Conn, maxPacket int) (Packet, error) {
	var prefix [4]byte
	if _, err := io.ReadFull(conn, prefix[:]); err != nil {
		return Packet{}, err
	}
	length := int32(binary.LittleEndian.Uint32(prefix[:]))
	if length < packetOverhead || int64(length) > int64(maxPacket) {
		return Packet{}, fmt.Errorf("invalid mock packet length %d", length)
	}
	bodyLength := int(length) - packetOverhead
	unpaddedLength := 4 + packetHeaderLength + bodyLength + 2
	wireLength := unpaddedLength + ((-unpaddedLength) & 7)
	wire := make([]byte, wireLength)
	copy(wire, prefix[:])
	if _, err := io.ReadFull(conn, wire[4:]); err != nil {
		return Packet{}, err
	}
	packet, consumed, err := DecodePacket(wire, maxPacket)
	if err != nil {
		return Packet{}, err
	}
	if consumed != len(wire) {
		return Packet{}, fmt.Errorf("mock packet consumed %d of %d bytes", consumed, len(wire))
	}
	return packet, nil
}

func writeMockPackets(conn net.Conn, packets ...[]byte) error {
	for _, packet := range packets {
		for len(packet) > 0 {
			written, err := conn.Write(packet)
			if err != nil {
				return err
			}
			if written == 0 {
				return io.ErrShortWrite
			}
			packet = packet[written:]
		}
	}
	return nil
}

func encodeMockPacket(requestID, packetType int32, body []byte, maxPacket int) ([]byte, error) {
	return EncodePacket(requestID, packetType, body, maxPacket)
}

func authenticateMock(conn net.Conn, password string, maxPacket int) (int32, error) {
	packet, err := readMockPacket(conn, maxPacket)
	if err != nil {
		return 0, err
	}
	if packet.Type != TypeAuth || string(packet.Body) != password {
		return 0, errors.New("mock server received an invalid auth request")
	}
	response, err := encodeMockPacket(packet.RequestID, TypeResponse, nil, maxPacket)
	if err != nil {
		return 0, err
	}
	if err := writeMockPackets(conn, response); err != nil {
		return 0, err
	}
	return packet.RequestID, nil
}

func expectMockClose(conn net.Conn) error {
	var one [1]byte
	_, err := conn.Read(one[:])
	if errors.Is(err, io.EOF) || errors.Is(err, net.ErrClosed) {
		return nil
	}
	if err == nil {
		return errors.New("mock server expected the client to close")
	}
	return err
}

func TestConfigDefaultsAndValidation(t *testing.T) {
	defaults := DefaultConfig()
	if defaults.ConnectTimeout != 5*time.Second || defaults.ReadWriteTimeout != 8*time.Second ||
		defaults.MaxPacket != 4096 || defaults.MaxInput != 4080 ||
		defaults.MaxResponsePackets != 128 || defaults.MaxResponseBytes != 1<<20 {
		t.Fatalf("unexpected defaults: %+v", defaults)
	}
	normalized, err := (Config{Port: 25575, Password: "secret"}).normalized()
	if err != nil {
		t.Fatalf("normalize zero-value options: %v", err)
	}
	if normalized.ConnectTimeout != defaults.ConnectTimeout || normalized.ReadWriteTimeout != defaults.ReadWriteTimeout ||
		normalized.MaxPacket != defaults.MaxPacket || normalized.MaxInput != defaults.MaxInput ||
		normalized.MaxResponsePackets != defaults.MaxResponsePackets || normalized.MaxResponseBytes != defaults.MaxResponseBytes {
		t.Fatalf("zero-value options did not use defaults: %+v", normalized)
	}

	tests := []struct {
		name   string
		mutate func(*Config)
	}{
		{name: "zero port", mutate: func(c *Config) { c.Port = 0 }},
		{name: "high port", mutate: func(c *Config) { c.Port = 65536 }},
		{name: "empty password", mutate: func(c *Config) { c.Password = "" }},
		{name: "blank password", mutate: func(c *Config) { c.Password = " \t" }},
		{name: "NUL password", mutate: func(c *Config) { c.Password = "a\x00b" }},
		{name: "negative connect timeout", mutate: func(c *Config) { c.ConnectTimeout = -1 }},
		{name: "negative read timeout", mutate: func(c *Config) { c.ReadWriteTimeout = -1 }},
		{name: "small packet limit", mutate: func(c *Config) { c.MaxPacket = 15 }},
		{name: "large input limit", mutate: func(c *Config) { c.MaxInput = c.MaxPacket - 9 }},
		{name: "negative packet limit", mutate: func(c *Config) { c.MaxPacket = -1 }},
		{name: "negative response packets", mutate: func(c *Config) { c.MaxResponsePackets = -1 }},
		{name: "negative response bytes", mutate: func(c *Config) { c.MaxResponseBytes = -1 }},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			cfg := mockConfig(25575)
			test.mutate(&cfg)
			if _, err := cfg.normalized(); !errors.Is(err, ErrInvalidConfig) {
				t.Fatalf("error = %v, want ErrInvalidConfig", err)
			}
		})
	}
}

func TestConfigFormattingRedactsPassword(t *testing.T) {
	cfg := mockConfig(25575)
	const password = "do-not-log-this-password"
	cfg.Password = password
	for _, format := range []string{"%v", "%+v", "%#v"} {
		formatted := fmt.Sprintf(format, cfg)
		if strings.Contains(formatted, password) || !strings.Contains(formatted, "[REDACTED]") {
			t.Fatalf("format %q leaked or failed to redact password: %s", format, formatted)
		}
	}
}

func TestDialAuthenticatesAndSerializesCommands(t *testing.T) {
	server := startMockRCONServer(t, func(conn net.Conn) error {
		if _, err := authenticateMock(conn, "test-password", DefaultMaxPacket); err != nil {
			return err
		}
		for index, command := range []string{"list", " say hello "} {
			packet, err := readMockPacket(conn, DefaultMaxPacket)
			if err != nil {
				return err
			}
			if packet.Type != TypeCommand || packet.RequestID != int32(index+2) || string(packet.Body) != command {
				return errors.New("mock server received an invalid command request")
			}
			body := []byte("There are 2 players")
			if index == 1 {
				body = nil
			}
			response, err := encodeMockPacket(packet.RequestID, TypeResponse, body, DefaultMaxPacket)
			if err != nil {
				return err
			}
			if err := writeMockPackets(conn, response); err != nil {
				return err
			}
		}
		return expectMockClose(conn)
	})
	client, err := Dial(context.Background(), mockConfig(server.port(t)))
	if err != nil {
		t.Fatalf("Dial: %v", err)
	}
	defer func() { _ = client.Close() }()
	host, _, err := net.SplitHostPort(client.conn.RemoteAddr().String())
	if err != nil || host != LoopbackHost {
		t.Fatalf("remote host = %q (%v), want %q", host, err, LoopbackHost)
	}
	if client.config.Password != "" {
		t.Fatal("client retained its password after authentication")
	}
	response, err := client.Command(context.Background(), "list")
	if err != nil || response != "There are 2 players" {
		t.Fatalf("Command(list) = (%q, %v)", response, err)
	}
	response, err = client.Command(context.Background(), " say hello ")
	if err != nil || response != "" {
		t.Fatalf("Command(say) = (%q, %v)", response, err)
	}
	if err := client.Close(); err != nil {
		t.Fatalf("Close: %v", err)
	}
	if err := client.Close(); err != nil {
		t.Fatalf("second Close: %v", err)
	}
	if _, err := client.Command(context.Background(), "list"); !errors.Is(err, ErrClientClosed) {
		t.Fatalf("Command after Close = %v, want ErrClientClosed", err)
	}
}

func TestExecuteAuthenticatesBeforeCommand(t *testing.T) {
	server := startMockRCONServer(t, func(conn net.Conn) error {
		if _, err := authenticateMock(conn, "test-password", DefaultMaxPacket); err != nil {
			return err
		}
		packet, err := readMockPacket(conn, DefaultMaxPacket)
		if err != nil {
			return err
		}
		if packet.Type != TypeCommand || packet.RequestID != 2 || string(packet.Body) != "seed" {
			return errors.New("mock server received an invalid command")
		}
		response, err := encodeMockPacket(packet.RequestID, TypeResponse, []byte("123"), DefaultMaxPacket)
		if err != nil {
			return err
		}
		if err := writeMockPackets(conn, response); err != nil {
			return err
		}
		return expectMockClose(conn)
	})
	response, err := Execute(context.Background(), mockConfig(server.port(t)), "seed")
	if err != nil || response != "123" {
		t.Fatalf("Execute = (%q, %v)", response, err)
	}
}

func TestEmptyCommandsAreRejected(t *testing.T) {
	for _, command := range []string{"", " \t\r\n"} {
		if _, err := Execute(context.Background(), Config{}, command); !errors.Is(err, ErrEmptyCommand) {
			t.Fatalf("Execute(%q) = %v, want ErrEmptyCommand", command, err)
		}
	}

	server := startMockRCONServer(t, func(conn net.Conn) error {
		if _, err := authenticateMock(conn, "test-password", DefaultMaxPacket); err != nil {
			return err
		}
		return expectMockClose(conn)
	})
	client, err := Dial(context.Background(), mockConfig(server.port(t)))
	if err != nil {
		t.Fatalf("Dial: %v", err)
	}
	defer func() { _ = client.Close() }()
	for _, command := range []string{"", " \t\n"} {
		if _, err := client.Command(context.Background(), command); !errors.Is(err, ErrEmptyCommand) {
			t.Fatalf("Command(%q) = %v, want ErrEmptyCommand", command, err)
		}
	}
}

func TestOversizePasswordIsRejectedBeforeDial(t *testing.T) {
	server := startMockRCONServer(t, func(conn net.Conn) error {
		return errors.New("client dialed before rejecting its oversized password")
	})
	cfg := mockConfig(server.port(t))
	cfg.Password = strings.Repeat("x", DefaultMaxInput+1)
	_, err := Dial(context.Background(), cfg)
	if !errors.Is(err, ErrInputTooLarge) {
		t.Fatalf("Dial error = %v, want ErrInputTooLarge", err)
	}
	if strings.Contains(err.Error(), cfg.Password) {
		t.Fatal("oversize password error exposed the password")
	}
}

func TestAuthenticationFailure(t *testing.T) {
	server := startMockRCONServer(t, func(conn net.Conn) error {
		packet, err := readMockPacket(conn, DefaultMaxPacket)
		if err != nil {
			return err
		}
		response, err := encodeMockPacket(AuthFailedRequestID, TypeResponse, nil, DefaultMaxPacket)
		if err != nil {
			return err
		}
		if packet.Type != TypeAuth {
			return errors.New("mock server expected auth")
		}
		if err := writeMockPackets(conn, response); err != nil {
			return err
		}
		return expectMockClose(conn)
	})
	_, err := Dial(context.Background(), mockConfig(server.port(t)))
	if !errors.Is(err, ErrAuthenticationFailed) {
		t.Fatalf("Dial error = %v, want ErrAuthenticationFailed", err)
	}
	if strings.Contains(err.Error(), "test-password") {
		t.Fatal("authentication error exposed the password")
	}
}

func TestAuthenticationRejectsMismatchedResponse(t *testing.T) {
	server := startMockRCONServer(t, func(conn net.Conn) error {
		if _, err := readMockPacket(conn, DefaultMaxPacket); err != nil {
			return err
		}
		response, err := encodeMockPacket(99, TypeResponse, nil, DefaultMaxPacket)
		if err != nil {
			return err
		}
		if err := writeMockPackets(conn, response); err != nil {
			return err
		}
		return expectMockClose(conn)
	})
	_, err := Dial(context.Background(), mockConfig(server.port(t)))
	if !errors.Is(err, ErrUnexpectedPacket) {
		t.Fatalf("Dial error = %v, want ErrUnexpectedPacket", err)
	}
}

func TestCommandCombinesCoalescedResponsePackets(t *testing.T) {
	first := strings.Repeat("a", DefaultMaxPacket-16)
	second := strings.Repeat("b", DefaultMaxPacket-16)
	server := startMockRCONServer(t, func(conn net.Conn) error {
		if _, err := authenticateMock(conn, "test-password", DefaultMaxPacket); err != nil {
			return err
		}
		packet, err := readMockPacket(conn, DefaultMaxPacket)
		if err != nil {
			return err
		}
		if packet.Type != TypeCommand || string(packet.Body) != "multi" {
			return errors.New("mock server received an invalid command")
		}
		firstWire, err := encodeMockPacket(packet.RequestID, TypeResponse, []byte(first), DefaultMaxPacket)
		if err != nil {
			return err
		}
		secondWire, err := encodeMockPacket(packet.RequestID, TypeResponse, []byte(second), DefaultMaxPacket)
		if err != nil {
			return err
		}
		finalWire, err := encodeMockPacket(packet.RequestID, TypeResponse, []byte("done"), DefaultMaxPacket)
		if err != nil {
			return err
		}
		if err := writeMockPackets(conn, append(append(firstWire, secondWire...), finalWire...)); err != nil {
			return err
		}
		return expectMockClose(conn)
	})
	client, err := Dial(context.Background(), mockConfig(server.port(t)))
	if err != nil {
		t.Fatalf("Dial: %v", err)
	}
	defer func() { _ = client.Close() }()
	response, err := client.Command(context.Background(), "multi")
	if err != nil {
		t.Fatalf("Command: %v", err)
	}
	if want := first + second + "done"; response != want {
		t.Fatalf("response length = %d, want %d", len(response), len(want))
	}
}

func TestCommandHandlesFragmentedResponse(t *testing.T) {
	server := startMockRCONServer(t, func(conn net.Conn) error {
		if _, err := authenticateMock(conn, "test-password", DefaultMaxPacket); err != nil {
			return err
		}
		if _, err := readMockPacket(conn, DefaultMaxPacket); err != nil {
			return err
		}
		response, err := encodeMockPacket(2, TypeResponse, []byte("fragmented"), DefaultMaxPacket)
		if err != nil {
			return err
		}
		for _, value := range response {
			if _, err := conn.Write([]byte{value}); err != nil {
				return err
			}
		}
		return expectMockClose(conn)
	})
	response, err := Execute(context.Background(), mockConfig(server.port(t)), "fragment")
	if err != nil || response != "fragmented" {
		t.Fatalf("Execute = (%q, %v)", response, err)
	}
}

func TestCommandRejectsOversizeInputWithoutPoisoningConnection(t *testing.T) {
	server := startMockRCONServer(t, func(conn net.Conn) error {
		if _, err := authenticateMock(conn, "test-password", DefaultMaxPacket); err != nil {
			return err
		}
		packet, err := readMockPacket(conn, DefaultMaxPacket)
		if err != nil {
			return err
		}
		if string(packet.Body) != "list" {
			return errors.New("mock server received an invalid command after oversize input")
		}
		response, err := encodeMockPacket(packet.RequestID, TypeResponse, []byte("ok"), DefaultMaxPacket)
		if err != nil {
			return err
		}
		if err := writeMockPackets(conn, response); err != nil {
			return err
		}
		return expectMockClose(conn)
	})
	client, err := Dial(context.Background(), mockConfig(server.port(t)))
	if err != nil {
		t.Fatalf("Dial: %v", err)
	}
	defer func() { _ = client.Close() }()
	command := strings.Repeat("x", DefaultMaxInput+1)
	_, err = client.Command(context.Background(), command)
	if !errors.Is(err, ErrInputTooLarge) {
		t.Fatalf("oversize Command error = %v, want ErrInputTooLarge", err)
	}
	if strings.Contains(err.Error(), command) {
		t.Fatal("oversize error exposed the command body")
	}
	response, err := client.Command(context.Background(), "list")
	if err != nil || response != "ok" {
		t.Fatalf("Command after rejected input = (%q, %v)", response, err)
	}
}

func TestCommandRejectsOversizeWirePacket(t *testing.T) {
	server := startMockRCONServer(t, func(conn net.Conn) error {
		if _, err := authenticateMock(conn, "test-password", DefaultMaxPacket); err != nil {
			return err
		}
		if _, err := readMockPacket(conn, DefaultMaxPacket); err != nil {
			return err
		}
		var prefix [4]byte
		binary.LittleEndian.PutUint32(prefix[:], DefaultMaxPacket+1)
		if err := writeMockPackets(conn, prefix[:]); err != nil {
			return err
		}
		return expectMockClose(conn)
	})
	client, err := Dial(context.Background(), mockConfig(server.port(t)))
	if err != nil {
		t.Fatalf("Dial: %v", err)
	}
	defer func() { _ = client.Close() }()
	_, err = client.Command(context.Background(), "list")
	if !errors.Is(err, ErrInvalidLength) {
		t.Fatalf("Command error = %v, want ErrInvalidLength", err)
	}
}

func TestCommandResponseLimits(t *testing.T) {
	t.Run("packet count", func(t *testing.T) {
		cfg := mockConfig(0)
		cfg.MaxPacket = 64
		cfg.MaxInput = 54
		cfg.MaxResponsePackets = 2
		cfg.MaxResponseBytes = 1024
		server := startMockRCONServer(t, func(conn net.Conn) error {
			if _, err := authenticateMock(conn, cfg.Password, cfg.MaxPacket); err != nil {
				return err
			}
			packet, err := readMockPacket(conn, cfg.MaxPacket)
			if err != nil {
				return err
			}
			body := bytes.Repeat([]byte{'x'}, cfg.MaxPacket-16)
			first, err := encodeMockPacket(packet.RequestID, TypeResponse, body, cfg.MaxPacket)
			if err != nil {
				return err
			}
			second, err := encodeMockPacket(packet.RequestID, TypeResponse, body, cfg.MaxPacket)
			if err != nil {
				return err
			}
			if err := writeMockPackets(conn, append(first, second...)); err != nil {
				return err
			}
			return expectMockClose(conn)
		})
		cfg.Port = server.port(t)
		client, err := Dial(context.Background(), cfg)
		if err != nil {
			t.Fatalf("Dial: %v", err)
		}
		defer func() { _ = client.Close() }()
		_, err = client.Command(context.Background(), "limit")
		if !errors.Is(err, ErrResponseTooLarge) || !strings.Contains(err.Error(), "packet count") {
			t.Fatalf("Command error = %v, want packet-count ErrResponseTooLarge", err)
		}
	})

	t.Run("body bytes", func(t *testing.T) {
		cfg := mockConfig(0)
		cfg.MaxPacket = 64
		cfg.MaxInput = 54
		cfg.MaxResponsePackets = 8
		cfg.MaxResponseBytes = 47
		server := startMockRCONServer(t, func(conn net.Conn) error {
			if _, err := authenticateMock(conn, cfg.Password, cfg.MaxPacket); err != nil {
				return err
			}
			packet, err := readMockPacket(conn, cfg.MaxPacket)
			if err != nil {
				return err
			}
			response, err := encodeMockPacket(packet.RequestID, TypeResponse, bytes.Repeat([]byte{'x'}, 48), cfg.MaxPacket)
			if err != nil {
				return err
			}
			if err := writeMockPackets(conn, response); err != nil {
				return err
			}
			return expectMockClose(conn)
		})
		cfg.Port = server.port(t)
		client, err := Dial(context.Background(), cfg)
		if err != nil {
			t.Fatalf("Dial: %v", err)
		}
		defer func() { _ = client.Close() }()
		_, err = client.Command(context.Background(), "limit")
		if !errors.Is(err, ErrResponseTooLarge) || !strings.Contains(err.Error(), "body bytes") {
			t.Fatalf("Command error = %v, want byte-limit ErrResponseTooLarge", err)
		}
	})

	t.Run("exact limits allowed", func(t *testing.T) {
		cfg := mockConfig(0)
		cfg.MaxPacket = 32
		cfg.MaxInput = 22
		cfg.MaxResponsePackets = 2
		cfg.MaxResponseBytes = 19
		server := startMockRCONServer(t, func(conn net.Conn) error {
			if _, err := authenticateMock(conn, cfg.Password, cfg.MaxPacket); err != nil {
				return err
			}
			packet, err := readMockPacket(conn, cfg.MaxPacket)
			if err != nil {
				return err
			}
			first, err := encodeMockPacket(packet.RequestID, TypeResponse, []byte("1234567890123456"), cfg.MaxPacket)
			if err != nil {
				return err
			}
			second, err := encodeMockPacket(packet.RequestID, TypeResponse, []byte("end"), cfg.MaxPacket)
			if err != nil {
				return err
			}
			return writeMockPackets(conn, append(first, second...))
		})
		cfg.Port = server.port(t)
		response, err := Execute(context.Background(), cfg, "limit")
		if err != nil || response != "1234567890123456end" {
			t.Fatalf("Execute = (%q, %v)", response, err)
		}
	})
}

func TestCommandRejectsUnexpectedResponse(t *testing.T) {
	tests := []struct {
		name       string
		requestID  int32
		packetType int32
	}{
		{name: "wrong request ID", requestID: 99, packetType: TypeResponse},
		{name: "auth failure ID", requestID: AuthFailedRequestID, packetType: TypeResponse},
		{name: "wrong type", requestID: 2, packetType: TypeCommand},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			server := startMockRCONServer(t, func(conn net.Conn) error {
				if _, err := authenticateMock(conn, "test-password", DefaultMaxPacket); err != nil {
					return err
				}
				if _, err := readMockPacket(conn, DefaultMaxPacket); err != nil {
					return err
				}
				response, err := encodeMockPacket(test.requestID, test.packetType, []byte("bad"), DefaultMaxPacket)
				if err != nil {
					return err
				}
				if err := writeMockPackets(conn, response); err != nil {
					return err
				}
				return expectMockClose(conn)
			})
			client, err := Dial(context.Background(), mockConfig(server.port(t)))
			if err != nil {
				t.Fatalf("Dial: %v", err)
			}
			defer func() { _ = client.Close() }()
			_, err = client.Command(context.Background(), "list")
			if !errors.Is(err, ErrUnexpectedPacket) {
				t.Fatalf("Command error = %v, want ErrUnexpectedPacket", err)
			}
		})
	}
}

func TestReadTimeout(t *testing.T) {
	requestSeen := make(chan struct{})
	server := startMockRCONServer(t, func(conn net.Conn) error {
		if _, err := authenticateMock(conn, "test-password", DefaultMaxPacket); err != nil {
			return err
		}
		if _, err := readMockPacket(conn, DefaultMaxPacket); err != nil {
			return err
		}
		close(requestSeen)
		return expectMockClose(conn)
	})
	cfg := mockConfig(server.port(t))
	cfg.ReadWriteTimeout = 40 * time.Millisecond
	client, err := Dial(context.Background(), cfg)
	if err != nil {
		t.Fatalf("Dial: %v", err)
	}
	defer func() { _ = client.Close() }()
	started := time.Now()
	_, err = client.Command(context.Background(), "list")
	if !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("Command error = %v, want context.DeadlineExceeded", err)
	}
	var timeout net.Error
	if !errors.As(err, &timeout) || !timeout.Timeout() {
		t.Fatalf("Command error %T does not report a timeout", err)
	}
	if elapsed := time.Since(started); elapsed > 2*time.Second {
		t.Fatalf("read timeout took %v", elapsed)
	}
	select {
	case <-requestSeen:
	default:
		t.Fatal("mock server did not receive the command")
	}
}

func TestReadTimeoutCoversPartialPacket(t *testing.T) {
	server := startMockRCONServer(t, func(conn net.Conn) error {
		if _, err := authenticateMock(conn, "test-password", DefaultMaxPacket); err != nil {
			return err
		}
		if _, err := readMockPacket(conn, DefaultMaxPacket); err != nil {
			return err
		}
		response, err := encodeMockPacket(2, TypeResponse, []byte("partial"), DefaultMaxPacket)
		if err != nil {
			return err
		}
		if err := writeMockPackets(conn, response[:7]); err != nil {
			return err
		}
		return expectMockClose(conn)
	})
	cfg := mockConfig(server.port(t))
	cfg.ReadWriteTimeout = 40 * time.Millisecond
	_, err := Execute(context.Background(), cfg, "list")
	if !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("Execute error = %v, want context.DeadlineExceeded", err)
	}
}

func TestContextCancellationInterruptsRead(t *testing.T) {
	requestSeen := make(chan struct{})
	server := startMockRCONServer(t, func(conn net.Conn) error {
		if _, err := authenticateMock(conn, "test-password", DefaultMaxPacket); err != nil {
			return err
		}
		if _, err := readMockPacket(conn, DefaultMaxPacket); err != nil {
			return err
		}
		close(requestSeen)
		return expectMockClose(conn)
	})
	cfg := mockConfig(server.port(t))
	cfg.ReadWriteTimeout = 5 * time.Second
	client, err := Dial(context.Background(), cfg)
	if err != nil {
		t.Fatalf("Dial: %v", err)
	}
	defer func() { _ = client.Close() }()
	ctx, cancel := context.WithCancel(context.Background())
	result := make(chan error, 1)
	go func() {
		_, err := client.Command(ctx, "list")
		result <- err
	}()
	select {
	case <-requestSeen:
	case <-time.After(2 * time.Second):
		t.Fatal("mock server did not receive the command")
	}
	cancel()
	select {
	case err := <-result:
		if !errors.Is(err, context.Canceled) {
			t.Fatalf("Command error = %v, want context.Canceled", err)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("Command did not stop after context cancellation")
	}
}

func TestCanceledContextWhileWaitingForRequestSlot(t *testing.T) {
	requestSeen := make(chan struct{})
	server := startMockRCONServer(t, func(conn net.Conn) error {
		if _, err := authenticateMock(conn, "test-password", DefaultMaxPacket); err != nil {
			return err
		}
		if _, err := readMockPacket(conn, DefaultMaxPacket); err != nil {
			return err
		}
		close(requestSeen)
		return expectMockClose(conn)
	})
	cfg := mockConfig(server.port(t))
	cfg.ReadWriteTimeout = 5 * time.Second
	client, err := Dial(context.Background(), cfg)
	if err != nil {
		t.Fatalf("Dial: %v", err)
	}
	defer func() { _ = client.Close() }()
	firstResult := make(chan error, 1)
	go func() {
		_, err := client.Command(context.Background(), "slow")
		firstResult <- err
	}()
	select {
	case <-requestSeen:
	case <-time.After(2 * time.Second):
		t.Fatal("mock server did not receive the first command")
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := client.Command(ctx, "never sent"); !errors.Is(err, context.Canceled) {
		t.Fatalf("waiting Command error = %v, want context.Canceled", err)
	}
	if err := client.Close(); err != nil {
		t.Fatalf("Close: %v", err)
	}
	select {
	case err := <-firstResult:
		if !errors.Is(err, ErrClientClosed) {
			t.Fatalf("first Command error = %v, want ErrClientClosed", err)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("Close did not interrupt the first command")
	}
}

func TestCloseInterruptsBlockedRead(t *testing.T) {
	requestSeen := make(chan struct{})
	server := startMockRCONServer(t, func(conn net.Conn) error {
		if _, err := authenticateMock(conn, "test-password", DefaultMaxPacket); err != nil {
			return err
		}
		if _, err := readMockPacket(conn, DefaultMaxPacket); err != nil {
			return err
		}
		close(requestSeen)
		return expectMockClose(conn)
	})
	cfg := mockConfig(server.port(t))
	cfg.ReadWriteTimeout = 5 * time.Second
	client, err := Dial(context.Background(), cfg)
	if err != nil {
		t.Fatalf("Dial: %v", err)
	}
	defer func() { _ = client.Close() }()
	result := make(chan error, 1)
	go func() {
		_, err := client.Command(context.Background(), "list")
		result <- err
	}()
	select {
	case <-requestSeen:
	case <-time.After(2 * time.Second):
		t.Fatal("mock server did not receive the command")
	}
	if err := client.Close(); err != nil {
		t.Fatalf("Close: %v", err)
	}
	select {
	case err := <-result:
		if !errors.Is(err, ErrClientClosed) {
			t.Fatalf("Command error = %v, want ErrClientClosed", err)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("Close did not interrupt the blocked read")
	}
}

func TestWriteTimeout(t *testing.T) {
	clientSide, serverSide := net.Pipe()
	defer func() { _ = serverSide.Close() }()
	cfg := DefaultConfig()
	cfg.ReadWriteTimeout = 40 * time.Millisecond
	client := &Client{config: cfg, conn: clientSide, slots: make(chan struct{}, 1)}
	defer func() { _ = client.Close() }()
	err := client.writeRequest(context.Background(), 1, TypeCommand, []byte("list"), "command")
	if !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("writeRequest error = %v, want context.DeadlineExceeded", err)
	}
	if !client.isClosed() {
		t.Fatal("write timeout did not close the client")
	}
}

func TestDialHonorsCanceledContext(t *testing.T) {
	server := startMockRCONServer(t, func(conn net.Conn) error {
		return expectMockClose(conn)
	})
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	_, err := Dial(ctx, mockConfig(server.port(t)))
	if !errors.Is(err, context.Canceled) {
		t.Fatalf("Dial error = %v, want context.Canceled", err)
	}
}

func TestDialOnlyUsesExactIPv4Loopback(t *testing.T) {
	// A listener on another 127/8 address proves Dial does not use the broader
	// IPv4 loopback range or resolve a hostname.
	listener, err := net.Listen("tcp4", net.JoinHostPort("127.0.0.2", "0"))
	if err != nil {
		t.Skipf("platform cannot bind a second IPv4 loopback address: %v", err)
	}
	defer func() { _ = listener.Close() }()
	port := listener.Addr().(*net.TCPAddr).Port
	guard, err := net.Listen("tcp4", net.JoinHostPort(LoopbackHost, fmt.Sprint(port)))
	if err != nil {
		t.Skipf("cannot isolate 127.0.0.1 port %d: %v", port, err)
	}
	_ = guard.Close()
	cfg := mockConfig(port)
	cfg.ConnectTimeout = 100 * time.Millisecond
	if _, err := Dial(context.Background(), cfg); err == nil {
		t.Fatal("Dial connected to a listener outside exact 127.0.0.1")
	}
}

func TestRequestIDWrapSkipsAuthFailureID(t *testing.T) {
	client := &Client{requestID: int32(^uint32(0) >> 1)}
	if got := client.nextRequestID(); got != 1 {
		t.Fatalf("wrapped request ID = %d, want 1", got)
	}
}
