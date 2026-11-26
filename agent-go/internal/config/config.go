// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

// Package config loads and validates the Mofumofu ServerAgent configuration.
package config

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"net/url"
	"os"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"time"
)

const (
	// DefaultProbeBaseURL is the default loopback NekoProbe endpoint.
	DefaultProbeBaseURL = "http://127.0.0.1:17871"
	// DefaultBindURL is the default IPv4 loopback Agent listener.
	DefaultBindURL = "http://127.0.0.1:17870"
	// DefaultEventJournalCapacity is the shared console/event/operation capacity.
	DefaultEventJournalCapacity = 4096
	// MinEventJournalCapacity is the smallest supported journal capacity.
	MinEventJournalCapacity = 16
	// MaxEventJournalCapacity is the largest supported journal capacity.
	MaxEventJournalCapacity = 100000
	// RecommendedMaxPlayerDiffInterval is advisory; larger non-negative intervals are accepted.
	RecommendedMaxPlayerDiffInterval = 300 * time.Second
)

// Mode selects how the Agent interacts with the server process.
type Mode string

const (
	// ModeManaged lets the Agent own the server process lifecycle.
	ModeManaged Mode = "managed"
	// ModeAttach observes an externally managed server process.
	ModeAttach Mode = "attach"
)

// Config is the normalized runtime configuration. JSON input keeps the
// ServerAgent and GoAgent sections; PlayerDiffInterval is expressed in seconds
// in JSON and environment variables and as a duration in this struct.
type Config struct {
	ServerRootDirectory  string        `json:"ServerRootDirectory"`
	InstanceId           string        `json:"InstanceId"`
	ProbeRegistryPath    string        `json:"ProbeRegistryPath"`
	ProbeBaseUrl         string        `json:"ProbeBaseUrl"`
	BindUrl              string        `json:"BindUrl"`
	EventJournalPath     string        `json:"EventJournalPath"`
	AuditPath            string        `json:"AuditPath"`
	EventJournalCapacity int           `json:"EventJournalCapacity"`
	Mode                 Mode          `json:"Mode"`
	ProbeEnabled         bool          `json:"ProbeEnabled"`
	PlayerDiffInterval   time.Duration `json:"-"`
}

// Default returns the documented defaults. Required path and instance fields
// still need to be supplied by JSON, environment variables, or Validate.
func Default() Config {
	return Config{
		ProbeBaseUrl:         DefaultProbeBaseURL,
		BindUrl:              DefaultBindURL,
		EventJournalCapacity: DefaultEventJournalCapacity,
		Mode:                 ModeManaged,
		ProbeEnabled:         true,
		PlayerDiffInterval:   0,
	}
}

// Load reads configPath and applies defaults, JSON, environ, and args in that
// order. A --config option overrides configPath. A --urls option overrides every
// other source for BindUrl. environ uses ServerAgent__Field and GoAgent__Field.
func Load(configPath string, environ []string, args []string) (Config, error) {
	cli, err := parseCLI(args)
	if err != nil {
		return Config{}, err
	}
	if cli.configSet {
		configPath = cli.config
	}
	if strings.TrimSpace(configPath) == "" || strings.ContainsRune(configPath, '\x00') {
		return Config{}, errors.New("config: --config is required and must not contain NUL")
	}

	absolutePath, err := filepath.Abs(configPath)
	if err != nil {
		return Config{}, fmt.Errorf("config: resolve config path: %w", err)
	}
	absolutePath = filepath.Clean(absolutePath)
	data, err := os.ReadFile(absolutePath)
	if err != nil {
		return Config{}, fmt.Errorf("config: read %s: %w", absolutePath, err)
	}

	if bytes.Equal(bytes.TrimSpace(data), []byte("null")) {
		return Config{}, errors.New("config: top-level JSON value must not be null")
	}
	var document fileDocument
	if err := json.Unmarshal(data, &document); err != nil {
		return Config{}, fmt.Errorf("config: parse %s: %w", absolutePath, err)
	}

	cfg := Default()
	if document.ServerAgent.set {
		if err := document.ServerAgent.value.apply(&cfg); err != nil {
			return Config{}, err
		}
	}
	if document.GoAgent.set {
		if err := document.GoAgent.value.apply(&cfg); err != nil {
			return Config{}, err
		}
	}
	if err := applyEnvironment(&cfg, environ); err != nil {
		return Config{}, err
	}
	if cli.urlsSet {
		cfg.BindUrl = cli.urls
	}
	if err := cfg.NormalizeAndValidate(); err != nil {
		return Config{}, err
	}
	return cfg, nil
}

// Validate checks a copy of c without changing it.
func (c Config) Validate() error {
	_, err := c.normalized()
	return err
}

// NormalizeAndValidate cleans all paths, trims InstanceId, and validates c.
// The receiver is unchanged when validation fails.
func (c *Config) NormalizeAndValidate() error {
	if c == nil {
		return errors.New("config: nil Config")
	}
	normalized, err := c.normalized()
	if err != nil {
		return err
	}
	*c = normalized
	return nil
}

// InstanceDirectory returns the lexical instance root after validation.
func (c Config) InstanceDirectory() string {
	return filepath.Join(c.ServerRootDirectory, c.InstanceId)
}

// BindPort returns the validated listener port, including an explicit port 0.
func (c Config) BindPort() (int, error) {
	_, port, err := parseBindURL(c.BindUrl)
	if err != nil {
		return 0, err
	}
	return port, nil
}

func (c Config) normalized() (Config, error) {
	root, err := normalizeDirectory(c.ServerRootDirectory, "ServerAgent.ServerRootDirectory")
	if err != nil {
		return Config{}, err
	}
	c.ServerRootDirectory = root
	c.InstanceId = strings.TrimSpace(c.InstanceId)
	if err := validateInstanceID(c.InstanceId); err != nil {
		return Config{}, err
	}

	registry, err := normalizeFile(c.ProbeRegistryPath, "ServerAgent.ProbeRegistryPath")
	if err != nil {
		return Config{}, err
	}
	c.ProbeRegistryPath = registry
	journal, err := normalizeFile(c.EventJournalPath, "ServerAgent.EventJournalPath")
	if err != nil {
		return Config{}, err
	}
	c.EventJournalPath = journal
	audit, err := normalizeFile(c.AuditPath, "ServerAgent.AuditPath")
	if err != nil {
		return Config{}, err
	}
	c.AuditPath = audit

	if c.EventJournalCapacity < MinEventJournalCapacity || c.EventJournalCapacity > MaxEventJournalCapacity {
		return Config{}, fmt.Errorf("config: ServerAgent.EventJournalCapacity must be between %d and %d", MinEventJournalCapacity, MaxEventJournalCapacity)
	}
	if samePath(c.ProbeRegistryPath, c.EventJournalPath) ||
		samePath(c.ProbeRegistryPath, c.AuditPath) ||
		samePath(c.EventJournalPath, c.AuditPath) {
		return Config{}, errors.New("config: ServerAgent.ProbeRegistryPath, EventJournalPath, and AuditPath must be different files")
	}
	if c.Mode != ModeManaged && c.Mode != ModeAttach {
		return Config{}, fmt.Errorf("config: GoAgent.Mode must be %q or %q", ModeManaged, ModeAttach)
	}
	if c.PlayerDiffInterval < 0 {
		return Config{}, errors.New("config: GoAgent.PlayerDiffInterval must be non-negative")
	}
	if _, _, err := parseBindURL(c.BindUrl); err != nil {
		return Config{}, err
	}
	if _, _, err := parseProbeBaseURL(c.ProbeBaseUrl); err != nil {
		return Config{}, err
	}

	instance, err := filepath.Abs(c.InstanceDirectory())
	if err != nil {
		return Config{}, fmt.Errorf("config: resolve InstanceDirectory: %w", err)
	}
	instance = filepath.Clean(instance)
	if !hasLexicalRootPrefix(c.ServerRootDirectory, instance) {
		return Config{}, errors.New("config: InstanceDirectory escapes ServerAgent.ServerRootDirectory")
	}
	return c, nil
}

func normalizeDirectory(value, field string) (string, error) {
	if strings.TrimSpace(value) == "" || strings.ContainsRune(value, '\x00') {
		return "", fmt.Errorf("config: %s is required and must not contain NUL", field)
	}
	absolute, err := filepath.Abs(value)
	if err != nil {
		return "", fmt.Errorf("config: resolve %s: %w", field, err)
	}
	return filepath.Clean(absolute), nil
}

func normalizeFile(value, field string) (string, error) {
	path, err := normalizeDirectory(value, field)
	if err != nil {
		return "", err
	}
	volume := filepath.VolumeName(path)
	if strings.TrimRight(path, `/\`) == volume {
		return "", fmt.Errorf("config: %s must name a file, not a directory root", field)
	}
	return path, nil
}

func validateInstanceID(instanceID string) error {
	if instanceID == "" || instanceID == "." || instanceID == ".." ||
		strings.Contains(instanceID, "..") || strings.ContainsAny(instanceID, "/\\") ||
		strings.ContainsRune(instanceID, '\x00') {
		return errors.New("config: ServerAgent.InstanceId must be a single, valid server directory name")
	}
	return nil
}

func hasLexicalRootPrefix(root, instance string) bool {
	prefix := root
	if !strings.HasSuffix(prefix, string(filepath.Separator)) {
		prefix += string(filepath.Separator)
	}
	if len(instance) <= len(prefix) {
		return false
	}
	if runtime.GOOS == "windows" {
		return strings.EqualFold(instance[:len(prefix)], prefix)
	}
	return strings.HasPrefix(instance, prefix)
}

func samePath(left, right string) bool {
	if runtime.GOOS == "windows" {
		return strings.EqualFold(left, right)
	}
	return left == right
}

func parseBindURL(raw string) (*url.URL, int, error) {
	parsed, port, err := parseHTTPURL(raw, "ServerAgent.BindUrl")
	if err != nil {
		return nil, 0, err
	}
	if strings.Contains(parsed.Host, "[") || parsed.Hostname() != "127.0.0.1" {
		return nil, 0, errors.New("config: ServerAgent.BindUrl must use the literal IPv4 address 127.0.0.1")
	}
	escapedPath := parsed.EscapedPath()
	if escapedPath != "" && escapedPath != "/" {
		return nil, 0, errors.New("config: ServerAgent.BindUrl must not contain a URL path")
	}
	return parsed, port, nil
}

func parseProbeBaseURL(raw string) (*url.URL, int, error) {
	parsed, port, err := parseHTTPURL(raw, "ServerAgent.ProbeBaseUrl")
	if err != nil {
		return nil, 0, err
	}
	host := parsed.Hostname()
	if strings.EqualFold(host, "localhost") {
		return parsed, port, nil
	}
	address := net.ParseIP(host)
	if address == nil || !address.IsLoopback() {
		return nil, 0, errors.New("config: ServerAgent.ProbeBaseUrl must use localhost or a loopback IP address")
	}
	return parsed, port, nil
}

func parseHTTPURL(raw, field string) (*url.URL, int, error) {
	if strings.TrimSpace(raw) == "" {
		return nil, 0, fmt.Errorf("config: %s is required", field)
	}
	parsed, err := url.Parse(raw)
	if err != nil {
		return nil, 0, fmt.Errorf("config: %s must be an absolute HTTP(S) URL: %w", field, err)
	}
	if !parsed.IsAbs() || parsed.Opaque != "" ||
		(!strings.EqualFold(parsed.Scheme, "http") && !strings.EqualFold(parsed.Scheme, "https")) {
		return nil, 0, fmt.Errorf("config: %s must be an absolute HTTP(S) URL", field)
	}
	if parsed.User != nil {
		return nil, 0, fmt.Errorf("config: %s must not contain userinfo", field)
	}
	if parsed.RawQuery != "" || parsed.ForceQuery || strings.ContainsRune(raw, '?') {
		return nil, 0, fmt.Errorf("config: %s must not contain a query", field)
	}
	if parsed.Fragment != "" || strings.ContainsRune(raw, '#') {
		return nil, 0, fmt.Errorf("config: %s must not contain a fragment", field)
	}
	if parsed.Host == "" {
		return nil, 0, fmt.Errorf("config: %s must include a host", field)
	}
	port, err := urlPort(parsed)
	if err != nil {
		return nil, 0, fmt.Errorf("config: %s: %w", field, err)
	}
	return parsed, port, nil
}

func urlPort(parsed *url.URL) (int, error) {
	if strings.HasSuffix(parsed.Host, ":") {
		return 0, errors.New("port must not be empty")
	}
	value := parsed.Port()
	if value == "" {
		if strings.EqualFold(parsed.Scheme, "https") {
			return 443, nil
		}
		return 80, nil
	}
	port, err := strconv.Atoi(value)
	if err != nil || port < 0 || port > 65535 {
		return 0, errors.New("port must be between 0 and 65535")
	}
	return port, nil
}

type optional[T any] struct {
	value T
	set   bool
}

func (o *optional[T]) UnmarshalJSON(data []byte) error {
	if bytes.Equal(bytes.TrimSpace(data), []byte("null")) {
		return errors.New("value must not be null")
	}
	var value T
	if err := json.Unmarshal(data, &value); err != nil {
		return err
	}
	o.value = value
	o.set = true
	return nil
}

type fileDocument struct {
	ServerAgent optional[serverAgentJSON] `json:"ServerAgent"`
	GoAgent     optional[goAgentJSON]     `json:"GoAgent"`
}

type serverAgentJSON struct {
	ServerRootDirectory  optional[string] `json:"ServerRootDirectory"`
	InstanceId           optional[string] `json:"InstanceId"`
	ProbeRegistryPath    optional[string] `json:"ProbeRegistryPath"`
	ProbeBaseUrl         optional[string] `json:"ProbeBaseUrl"`
	BindUrl              optional[string] `json:"BindUrl"`
	EventJournalPath     optional[string] `json:"EventJournalPath"`
	AuditPath            optional[string] `json:"AuditPath"`
	EventJournalCapacity optional[int]    `json:"EventJournalCapacity"`
}

type goAgentJSON struct {
	Mode               optional[string] `json:"Mode"`
	ProbeEnabled       optional[bool]   `json:"ProbeEnabled"`
	PlayerDiffInterval optional[int]    `json:"PlayerDiffInterval"`
}

func (s serverAgentJSON) apply(cfg *Config) error {
	if s.ServerRootDirectory.set {
		cfg.ServerRootDirectory = s.ServerRootDirectory.value
	}
	if s.InstanceId.set {
		cfg.InstanceId = s.InstanceId.value
	}
	if s.ProbeRegistryPath.set {
		cfg.ProbeRegistryPath = s.ProbeRegistryPath.value
	}
	if s.ProbeBaseUrl.set {
		cfg.ProbeBaseUrl = s.ProbeBaseUrl.value
	}
	if s.BindUrl.set {
		cfg.BindUrl = s.BindUrl.value
	}
	if s.EventJournalPath.set {
		cfg.EventJournalPath = s.EventJournalPath.value
	}
	if s.AuditPath.set {
		cfg.AuditPath = s.AuditPath.value
	}
	if s.EventJournalCapacity.set {
		cfg.EventJournalCapacity = s.EventJournalCapacity.value
	}
	return nil
}

func (g goAgentJSON) apply(cfg *Config) error {
	if g.Mode.set {
		cfg.Mode = Mode(g.Mode.value)
	}
	if g.ProbeEnabled.set {
		cfg.ProbeEnabled = g.ProbeEnabled.value
	}
	if g.PlayerDiffInterval.set {
		interval, err := durationFromSeconds(g.PlayerDiffInterval.value)
		if err != nil {
			return fmt.Errorf("config: GoAgent.PlayerDiffInterval: %w", err)
		}
		cfg.PlayerDiffInterval = interval
	}
	return nil
}

func durationFromSeconds(seconds int) (time.Duration, error) {
	if seconds < 0 {
		return 0, errors.New("value must be non-negative")
	}
	if int64(seconds) > int64((1<<63-1)/time.Second) {
		return 0, errors.New("value is too large")
	}
	return time.Duration(seconds) * time.Second, nil
}

type cliOptions struct {
	config    string
	configSet bool
	urls      string
	urlsSet   bool
}

func parseCLI(args []string) (cliOptions, error) {
	var result cliOptions
	for index := 0; index < len(args); index++ {
		argument := args[index]
		if value, inline, matched := matchOption(argument, "--config"); matched {
			if !inline {
				var err error
				value, err = nextOptionValue(args, &index, "--config")
				if err != nil {
					return cliOptions{}, err
				}
			}
			if strings.TrimSpace(value) == "" {
				return cliOptions{}, errors.New("config: --config requires a non-empty value")
			}
			result.config = value
			result.configSet = true
			continue
		}
		if value, inline, matched := matchOption(argument, "--urls"); matched {
			if !inline {
				var err error
				value, err = nextOptionValue(args, &index, "--urls")
				if err != nil {
					return cliOptions{}, err
				}
			}
			if strings.TrimSpace(value) == "" {
				return cliOptions{}, errors.New("config: --urls requires a non-empty value")
			}
			result.urls = value
			result.urlsSet = true
		}
	}
	return result, nil
}

func matchOption(argument, name string) (value string, inline bool, matched bool) {
	if strings.EqualFold(argument, name) {
		return "", false, true
	}
	if len(argument) > len(name) && argument[len(name)] == '=' &&
		strings.EqualFold(argument[:len(name)], name) {
		return argument[len(name)+1:], true, true
	}
	return "", false, false
}

func nextOptionValue(args []string, index *int, name string) (string, error) {
	if *index+1 >= len(args) || strings.HasPrefix(args[*index+1], "--") {
		return "", fmt.Errorf("config: %s requires a value", name)
	}
	*index = *index + 1
	return args[*index], nil
}

func applyEnvironment(cfg *Config, environ []string) error {
	values := make(map[string]string, len(environ))
	for _, entry := range environ {
		name, value, found := strings.Cut(entry, "=")
		if found {
			values[strings.ToUpper(name)] = value
		}
	}

	if value, found := values["SERVERAGENT__SERVERROOTDIRECTORY"]; found {
		cfg.ServerRootDirectory = value
	}
	if value, found := values["SERVERAGENT__INSTANCEID"]; found {
		cfg.InstanceId = value
	}
	if value, found := values["SERVERAGENT__PROBEREGISTRYPATH"]; found {
		cfg.ProbeRegistryPath = value
	}
	if value, found := values["SERVERAGENT__PROBEBASEURL"]; found {
		cfg.ProbeBaseUrl = value
	}
	if value, found := values["SERVERAGENT__BINDURL"]; found {
		cfg.BindUrl = value
	}
	if value, found := values["SERVERAGENT__EVENTJOURNALPATH"]; found {
		cfg.EventJournalPath = value
	}
	if value, found := values["SERVERAGENT__AUDITPATH"]; found {
		cfg.AuditPath = value
	}
	if value, found := values["SERVERAGENT__EVENTJOURNALCAPACITY"]; found {
		capacity, err := strconv.Atoi(value)
		if err != nil {
			return fmt.Errorf("config: ServerAgent__EventJournalCapacity must be an integer: %w", err)
		}
		cfg.EventJournalCapacity = capacity
	}
	if value, found := values["GOAGENT__MODE"]; found {
		cfg.Mode = Mode(value)
	}
	if value, found := values["GOAGENT__PROBEENABLED"]; found {
		enabled, err := strconv.ParseBool(value)
		if err != nil {
			return fmt.Errorf("config: GoAgent__ProbeEnabled must be a boolean: %w", err)
		}
		cfg.ProbeEnabled = enabled
	}
	if value, found := values["GOAGENT__PLAYERDIFFINTERVAL"]; found {
		seconds, err := strconv.Atoi(value)
		if err != nil {
			return fmt.Errorf("config: GoAgent__PlayerDiffInterval must be an integer number of seconds: %w", err)
		}
		interval, err := durationFromSeconds(seconds)
		if err != nil {
			return fmt.Errorf("config: GoAgent__PlayerDiffInterval: %w", err)
		}
		cfg.PlayerDiffInterval = interval
	}
	return nil
}
