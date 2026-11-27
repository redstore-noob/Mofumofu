// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

// Package rcon implements the loopback-only Minecraft RCON client used by the
// Mofumofu ServerAgent. A Client authenticates during Dial and serializes all
// subsequent commands on its connection.
package rcon

import (
	"context"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"math"
	"net"
	"strconv"
	"strings"
	"sync"
	"time"
)

const (
	// LoopbackHost is the only host a Client will dial.
	LoopbackHost = "127.0.0.1"

	// DefaultConnectTimeout bounds the TCP dial operation.
	DefaultConnectTimeout = 5 * time.Second
	// DefaultReadWriteTimeout bounds each packet write and each packet read.
	DefaultReadWriteTimeout = 8 * time.Second
	// DefaultMaxPacket limits the protocol length prefix, excluding its own four bytes and padding.
	DefaultMaxPacket = 4096
	// DefaultMaxInput limits encoded authentication and command bodies.
	DefaultMaxInput = 4080
	// DefaultMaxResponsePackets limits the packets combined for one command response.
	DefaultMaxResponsePackets = 128
	// DefaultMaxResponseBytes limits the combined response body size.
	DefaultMaxResponseBytes = 1 << 20

	minConfigMaxPacket = 16
)

var (
	// ErrInvalidConfig means required client settings or limits are invalid.
	ErrInvalidConfig = errors.New("rcon: invalid configuration")
	// ErrClientClosed means the client is nil, closed, or has no connection.
	ErrClientClosed = errors.New("rcon: client is closed")
	// ErrEmptyCommand means a command is empty or whitespace-only.
	ErrEmptyCommand = errors.New("rcon: command must not be empty")
	// ErrInputTooLarge means an authentication or command body exceeds MaxInput.
	ErrInputTooLarge = errors.New("rcon: input too large")
	// ErrAuthenticationFailed means the server rejected the password.
	ErrAuthenticationFailed = errors.New("rcon: authentication failed")
	// ErrUnexpectedPacket means a response has the wrong request ID or packet type.
	ErrUnexpectedPacket = errors.New("rcon: unexpected packet")
	// ErrResponseTooLarge means a response exceeds its packet or byte limit.
	ErrResponseTooLarge = errors.New("rcon: response too large")
)

// Config configures a Client. Port and Password are required. Zero durations or
// limits select their corresponding defaults; negative values are rejected.
// MaxPacket limits the protocol length prefix, while MaxInput limits body bytes.
type Config struct {
	Port               int
	Password           string
	ConnectTimeout     time.Duration
	ReadWriteTimeout   time.Duration
	MaxPacket          int
	MaxInput           int
	MaxResponsePackets int
	MaxResponseBytes   int
}

// DefaultConfig returns the default timeouts and limits. Port and Password must
// still be supplied before Dial.
func DefaultConfig() Config {
	return Config{
		ConnectTimeout:     DefaultConnectTimeout,
		ReadWriteTimeout:   DefaultReadWriteTimeout,
		MaxPacket:          DefaultMaxPacket,
		MaxInput:           DefaultMaxInput,
		MaxResponsePackets: DefaultMaxResponsePackets,
		MaxResponseBytes:   DefaultMaxResponseBytes,
	}
}

// String redacts the password so accidental configuration logging is safe.
func (c Config) String() string {
	c.Password = "[REDACTED]"
	type configAlias Config
	return fmt.Sprintf("%+v", configAlias(c))
}

// GoString redacts the password in detailed formatting as well.
func (c Config) GoString() string {
	return c.String()
}

func (c Config) normalized() (Config, error) {
	defaults := DefaultConfig()
	if c.Port < 1 || c.Port > 65535 {
		return Config{}, fmt.Errorf("%w: port must be between 1 and 65535", ErrInvalidConfig)
	}
	if strings.TrimSpace(c.Password) == "" {
		return Config{}, fmt.Errorf("%w: password is required", ErrInvalidConfig)
	}
	if strings.IndexByte(c.Password, 0) >= 0 {
		return Config{}, fmt.Errorf("%w: password contains a NUL byte", ErrInvalidConfig)
	}
	if c.ConnectTimeout == 0 {
		c.ConnectTimeout = defaults.ConnectTimeout
	}
	if c.ReadWriteTimeout == 0 {
		c.ReadWriteTimeout = defaults.ReadWriteTimeout
	}
	if c.MaxPacket == 0 {
		c.MaxPacket = defaults.MaxPacket
	}
	if c.MaxInput == 0 {
		c.MaxInput = defaults.MaxInput
	}
	if c.MaxResponsePackets == 0 {
		c.MaxResponsePackets = defaults.MaxResponsePackets
	}
	if c.MaxResponseBytes == 0 {
		c.MaxResponseBytes = defaults.MaxResponseBytes
	}

	if c.ConnectTimeout < 0 {
		return Config{}, fmt.Errorf("%w: connect timeout must not be negative", ErrInvalidConfig)
	}
	if c.ReadWriteTimeout < 0 {
		return Config{}, fmt.Errorf("%w: read/write timeout must not be negative", ErrInvalidConfig)
	}
	if c.MaxPacket < minConfigMaxPacket || int64(c.MaxPacket) > int64(math.MaxInt32) {
		return Config{}, fmt.Errorf("%w: max packet must be between %d and %d", ErrInvalidConfig, minConfigMaxPacket, int64(math.MaxInt32))
	}
	if c.MaxInput < 1 || c.MaxInput > c.MaxPacket-packetOverhead {
		return Config{}, fmt.Errorf("%w: max input must be between 1 and %d", ErrInvalidConfig, c.MaxPacket-packetOverhead)
	}
	if c.MaxResponsePackets < 1 {
		return Config{}, fmt.Errorf("%w: max response packets must be positive", ErrInvalidConfig)
	}
	if c.MaxResponseBytes < 1 {
		return Config{}, fmt.Errorf("%w: max response bytes must be positive", ErrInvalidConfig)
	}
	if len(c.Password) > c.MaxInput {
		return Config{}, fmt.Errorf("%w: auth body is %d bytes, limit is %d", ErrInputTooLarge, len(c.Password), c.MaxInput)
	}
	return c, nil
}

// Client is an authenticated RCON connection. Its zero value is not usable;
// construct one with Dial. Command calls are serialized and may run concurrently
// with Close, which interrupts any blocked I/O.
type Client struct {
	config Config
	conn   net.Conn
	slots  chan struct{}

	stateMu sync.RWMutex
	closed  bool

	// requestID is protected by the single request slot.
	requestID int32
}

// Dial connects to exactly 127.0.0.1 using IPv4, authenticates with cfg.Password,
// and returns the ready-to-use client. Every failure closes the new connection.
func Dial(ctx context.Context, cfg Config) (*Client, error) {
	if ctx == nil {
		return nil, fmt.Errorf("%w: context must not be nil", ErrInvalidConfig)
	}
	normalized, err := cfg.normalized()
	if err != nil {
		return nil, err
	}
	if err := ctx.Err(); err != nil {
		return nil, err
	}

	address := net.JoinHostPort(LoopbackHost, strconv.Itoa(normalized.Port))
	dialer := net.Dialer{Timeout: normalized.ConnectTimeout}
	conn, err := dialer.DialContext(ctx, "tcp4", address)
	if err != nil {
		return nil, fmt.Errorf("rcon: connect to %s: %w", address, err)
	}

	client := &Client{
		config: normalized,
		conn:   conn,
		slots:  make(chan struct{}, 1),
	}
	// The password is only needed for the authentication exchange.
	client.config.Password = ""
	if err := client.authenticate(ctx, normalized.Password); err != nil {
		_ = client.Close()
		return nil, fmt.Errorf("rcon: authenticate: %w", err)
	}
	return client, nil
}

// Execute authenticates a new loopback connection, runs one command, and closes
// the connection. Use a Client from Dial to reuse an authenticated connection.
func Execute(ctx context.Context, cfg Config, command string) (string, error) {
	if err := validateCommand(command); err != nil {
		return "", err
	}
	client, err := Dial(ctx, cfg)
	if err != nil {
		return "", err
	}
	defer func() { _ = client.Close() }()
	return client.Command(ctx, command)
}

// Command sends command on the authenticated connection and returns the
// concatenated response body. An empty or whitespace-only command is rejected.
// Any I/O or protocol error closes the connection because its packet stream can
// no longer be correlated safely.
func (c *Client) Command(ctx context.Context, command string) (string, error) {
	if err := validateCommand(command); err != nil {
		return "", err
	}
	if ctx == nil {
		return "", fmt.Errorf("%w: context must not be nil", ErrInvalidConfig)
	}
	if c == nil {
		return "", ErrClientClosed
	}
	if err := c.acquire(ctx); err != nil {
		return "", err
	}
	defer func() { <-c.slots }()
	if _, err := c.connection(); err != nil {
		return "", err
	}
	if len(command) > c.config.MaxInput {
		return "", fmt.Errorf("%w: command body is %d bytes, limit is %d", ErrInputTooLarge, len(command), c.config.MaxInput)
	}

	requestID, err := c.beginRequest(ctx, TypeCommand, []byte(command), "command")
	if err != nil {
		return "", err
	}

	initialCapacity := min(c.config.MaxInput, c.config.MaxResponseBytes, DefaultMaxPacket)
	response := make([]byte, 0, initialCapacity)
	responseBytes := 0
	for packetCount := 0; ; packetCount++ {
		if packetCount >= c.config.MaxResponsePackets {
			_ = c.Close()
			return "", fmt.Errorf("%w: packet count exceeds %d", ErrResponseTooLarge, c.config.MaxResponsePackets)
		}
		packet, err := c.readResponse(ctx, requestID)
		if err != nil {
			return "", err
		}
		if len(packet.Body) > c.config.MaxResponseBytes-responseBytes {
			_ = c.Close()
			return "", fmt.Errorf("%w: body bytes exceed %d", ErrResponseTooLarge, c.config.MaxResponseBytes)
		}
		response = append(response, packet.Body...)
		responseBytes += len(packet.Body)

		// Match the Source RCON continuation rule: a non-empty body reaching
		// MaxPacket-16 fills an aligned maximum-sized response packet.
		if len(packet.Body) == 0 || len(packet.Body) < c.config.MaxPacket-16 {
			return string(response), nil
		}
	}
}

// Close closes the underlying connection. It is safe to call repeatedly and
// concurrently with Command; the first call reports the connection close error.
func (c *Client) Close() error {
	if c == nil {
		return nil
	}
	c.stateMu.Lock()
	if c.closed {
		c.stateMu.Unlock()
		return nil
	}
	c.closed = true
	conn := c.conn
	c.stateMu.Unlock()
	if conn == nil {
		return nil
	}
	return conn.Close()
}

func validateCommand(command string) error {
	if strings.TrimSpace(command) == "" {
		return ErrEmptyCommand
	}
	return nil
}

func (c *Client) authenticate(ctx context.Context, password string) error {
	if err := c.acquire(ctx); err != nil {
		return err
	}
	defer func() { <-c.slots }()

	requestID := c.nextRequestID()
	if err := c.writeRequest(ctx, requestID, TypeAuth, []byte(password), "auth"); err != nil {
		return err
	}
	packet, err := c.readPacket(ctx)
	if err != nil {
		return err
	}
	if packet.RequestID == AuthFailedRequestID {
		return ErrAuthenticationFailed
	}
	if err := validateResponse(packet, requestID); err != nil {
		return err
	}
	return nil
}

func (c *Client) beginRequest(ctx context.Context, packetType int32, body []byte, name string) (int32, error) {
	requestID := c.nextRequestID()
	if err := c.writeRequest(ctx, requestID, packetType, body, name); err != nil {
		return 0, err
	}
	return requestID, nil
}

func (c *Client) writeRequest(ctx context.Context, requestID, packetType int32, body []byte, name string) error {
	if len(body) > c.config.MaxInput {
		return fmt.Errorf("%w: %s body is %d bytes, limit is %d", ErrInputTooLarge, name, len(body), c.config.MaxInput)
	}
	wire, err := EncodePacket(requestID, packetType, body, c.config.MaxPacket)
	if err != nil {
		return fmt.Errorf("rcon: encode %s packet: %w", name, err)
	}

	operationCtx, cancel := context.WithTimeout(ctx, c.config.ReadWriteTimeout)
	defer cancel()
	if err := operationCtx.Err(); err != nil {
		return err
	}
	conn, err := c.connection()
	if err != nil {
		return fmt.Errorf("rcon: write packet: %w", err)
	}
	deadline, _ := operationCtx.Deadline()
	if err := conn.SetWriteDeadline(deadline); err != nil {
		return fmt.Errorf("rcon: write packet: %w", c.ioError(operationCtx, err))
	}
	restore := interruptDeadline(operationCtx, conn, true)
	defer restore()

	err = writeFull(conn, wire)
	if err != nil {
		_ = c.Close()
		return fmt.Errorf("rcon: write packet: %w", c.ioError(operationCtx, err))
	}
	if err := operationCtx.Err(); err != nil {
		_ = c.Close()
		return err
	}
	return nil
}

func (c *Client) readResponse(ctx context.Context, requestID int32) (Packet, error) {
	packet, err := c.readPacket(ctx)
	if err != nil {
		_ = c.Close()
		return Packet{}, err
	}
	if err := validateResponse(packet, requestID); err != nil {
		_ = c.Close()
		return Packet{}, err
	}
	return packet, nil
}

func validateResponse(packet Packet, requestID int32) error {
	if packet.RequestID != requestID || packet.Type != TypeResponse {
		return fmt.Errorf("%w: request ID %d (want %d), type %d (want %d)",
			ErrUnexpectedPacket, packet.RequestID, requestID, packet.Type, TypeResponse)
	}
	return nil
}

func (c *Client) readPacket(ctx context.Context) (Packet, error) {
	operationCtx, cancel := context.WithTimeout(ctx, c.config.ReadWriteTimeout)
	defer cancel()
	if err := operationCtx.Err(); err != nil {
		return Packet{}, err
	}
	conn, err := c.connection()
	if err != nil {
		return Packet{}, fmt.Errorf("rcon: read packet: %w", err)
	}
	deadline, _ := operationCtx.Deadline()
	if err := conn.SetReadDeadline(deadline); err != nil {
		return Packet{}, fmt.Errorf("rcon: read packet: %w", c.ioError(operationCtx, err))
	}
	restore := interruptDeadline(operationCtx, conn, false)
	defer restore()

	var prefix [packetPrefixLength]byte
	if _, err := io.ReadFull(conn, prefix[:]); err != nil {
		return Packet{}, fmt.Errorf("rcon: read packet: %w", c.ioError(operationCtx, err))
	}
	length := int32(binary.LittleEndian.Uint32(prefix[:]))
	if length < packetOverhead || int64(length) > int64(c.config.MaxPacket) {
		return Packet{}, fmt.Errorf("%w: got %d, want %d..%d", ErrInvalidLength, length, packetOverhead, c.config.MaxPacket)
	}
	bodyLength := int(length) - packetOverhead
	unpaddedLength := packetPrefixLength + packetHeaderLength + bodyLength + 2
	wireLength := unpaddedLength + ((-unpaddedLength) & 7)
	wire := make([]byte, wireLength)
	copy(wire, prefix[:])
	if _, err := io.ReadFull(conn, wire[packetPrefixLength:]); err != nil {
		return Packet{}, fmt.Errorf("rcon: read packet: %w", c.ioError(operationCtx, err))
	}
	packet, consumed, err := DecodePacket(wire, c.config.MaxPacket)
	if err != nil {
		return Packet{}, fmt.Errorf("rcon: decode packet: %w", c.ioError(operationCtx, err))
	}
	if consumed != len(wire) {
		return Packet{}, fmt.Errorf("%w: decoded size %d, expected %d", ErrInvalidPadding, consumed, len(wire))
	}
	if err := operationCtx.Err(); err != nil {
		return Packet{}, err
	}
	return packet, nil
}

func (c *Client) acquire(ctx context.Context) error {
	if c.slots == nil {
		return ErrClientClosed
	}
	select {
	case c.slots <- struct{}{}:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}

func (c *Client) nextRequestID() int32 {
	if c.requestID == math.MaxInt32 {
		c.requestID = 0
	}
	c.requestID++
	return c.requestID
}

func (c *Client) connection() (net.Conn, error) {
	c.stateMu.RLock()
	defer c.stateMu.RUnlock()
	if c.closed || c.conn == nil {
		return nil, ErrClientClosed
	}
	return c.conn, nil
}

func (c *Client) isClosed() bool {
	c.stateMu.RLock()
	defer c.stateMu.RUnlock()
	return c.closed
}

func (c *Client) ioError(ctx context.Context, err error) error {
	if ctxErr := ctx.Err(); ctxErr != nil {
		return ctxErr
	}
	// The network deadline and context timer share an instant but may be
	// delivered by different goroutines. Normalize an elapsed deadline even if
	// the context's timer callback has not run yet.
	if deadline, ok := ctx.Deadline(); ok && !time.Now().Before(deadline) {
		return context.DeadlineExceeded
	}
	if errors.Is(err, net.ErrClosed) && c.isClosed() {
		return fmt.Errorf("%w: %w", ErrClientClosed, err)
	}
	return err
}

func interruptDeadline(ctx context.Context, conn net.Conn, write bool) func() {
	finished := make(chan struct{})
	stop := context.AfterFunc(ctx, func() {
		if write {
			_ = conn.SetWriteDeadline(time.Now())
		} else {
			_ = conn.SetReadDeadline(time.Now())
		}
		close(finished)
	})
	return func() {
		if !stop() {
			<-finished
		}
		if write {
			_ = conn.SetWriteDeadline(time.Time{})
		} else {
			_ = conn.SetReadDeadline(time.Time{})
		}
	}
}

func writeFull(conn net.Conn, data []byte) error {
	for len(data) > 0 {
		written, err := conn.Write(data)
		if err != nil {
			return err
		}
		if written == 0 {
			return io.ErrShortWrite
		}
		data = data[written:]
	}
	return nil
}
