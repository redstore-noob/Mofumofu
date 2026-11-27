// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package files

import (
	"context"
	"fmt"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
	"unicode"
	"unicode/utf16"
)

var booleanKeys = map[string]struct{}{
	"online-mode": {}, "white-list": {}, "enforce-whitelist": {}, "pvp": {}, "allow-flight": {},
	"spawn-monsters": {}, "spawn-animals": {}, "spawn-npcs": {}, "generate-structures": {},
	"allow-nether": {}, "enable-command-block": {}, "enable-query": {}, "enable-rcon": {},
	"enable-jmx-monitoring": {}, "sync-chunk-writes": {}, "use-native-transport": {},
	"prevent-proxy-connections": {}, "hardcore": {}, "broadcast-console-to-ops": {},
	"broadcast-rcon-to-ops": {}, "log-ips": {}, "require-resource-pack": {}, "debug": {},
	"enable-status": {}, "hide-online-players": {},
}

var integerKeys = map[string][2]int{
	"server-port": {1, 65535}, "query.port": {1, 65535}, "rcon.port": {1, 65535},
	"max-players": {0, 100000}, "view-distance": {3, 32}, "simulation-distance": {3, 32},
	"spawn-protection": {0, 1_000_000}, "op-permission-level": {1, 4},
	"function-permission-level": {1, 4}, "player-idle-timeout": {0, 2_147_483_647},
	"rate-limit": {0, 2_147_483_647}, "max-tick-time": {-1, 2_147_483_647},
	"network-compression-threshold": {-1, 2_147_483_647}, "max-world-size": {1, 29_999_984},
}

var knownKeys = func() map[string]struct{} {
	result := make(map[string]struct{}, len(booleanKeys)+len(integerKeys)+15)
	for key := range booleanKeys {
		result[key] = struct{}{}
	}
	for key := range integerKeys {
		result[key] = struct{}{}
	}
	for _, key := range []string{
		"difficulty", "gamemode", "motd", "level-name", "level-seed", "level-type",
		"resource-pack", "resource-pack-sha1", "resource-pack-id", "server-ip",
		"initial-enabled-packs", "initial-disabled-packs", "generator-settings",
		"region-file-compression", "management-server-host",
	} {
		result[key] = struct{}{}
	}
	return result
}()

type propertyLine struct {
	key *string
	raw string
}

type document struct {
	lines  []propertyLine
	values map[string]string
	order  []string
}

type Snapshot struct {
	Revision        string
	RestartRequired bool
	Properties      map[string]string
	Order           []string
}

type PatchResult struct {
	Revision        string
	RestartRequired bool
	AppliedAt       time.Time
	Properties      map[string]string
	Order           []string
}

// PropertyService patches server.properties while preserving comments, line
// order, duplicate-key placement, and the C# key validation rules.
type PropertyService struct {
	files           *Service
	now             func() time.Time
	mu              sync.Mutex
	restartRequired bool
}

func NewPropertyService(files *Service, now func() time.Time) *PropertyService {
	if now == nil {
		now = time.Now
	}
	return &PropertyService{files: files, now: now}
}

func (s *PropertyService) Get(ctx context.Context) (Snapshot, error) {
	content, err := s.files.ReadProperties(ctx)
	if err != nil {
		return Snapshot{}, err
	}
	parsed := parseProperties(content.Content)
	properties, order := withoutSecret(parsed)
	s.mu.Lock()
	restart := s.restartRequired
	s.mu.Unlock()
	return Snapshot{Revision: content.Revision, RestartRequired: restart, Properties: properties, Order: order}, nil
}

func (s *PropertyService) Patch(ctx context.Context, expectedRevision string, changes map[string]*string) (PatchResult, error) {
	if len(changes) == 0 {
		return PatchResult{}, invalidRequest("At least one property change is required.")
	}
	content, err := s.files.ReadProperties(ctx)
	if err != nil {
		return PatchResult{}, err
	}
	if content.Revision != expectedRevision {
		return PatchResult{}, revisionConflict(expectedRevision)
	}
	parsed := parseProperties(content.Content)
	updated := make(map[string]string, len(parsed.values)+len(changes))
	for key, value := range parsed.values {
		updated[key] = value
	}
	for rawKey, value := range changes {
		key, err := validatePropertyKey(rawKey)
		if err != nil {
			return PatchResult{}, err
		}
		if strings.EqualFold(key, "rcon.password") {
			return PatchResult{}, &Error{Status: 403, Code: "forbidden", Message: "rcon.password is neither readable nor remotely mutable."}
		}
		if value == nil {
			if _, known := knownKeys[key]; known {
				return PatchResult{}, invalidRequest(fmt.Sprintf("The known server property '%s' cannot be removed.", key))
			}
			delete(updated, key)
			continue
		}
		if err := validatePropertyValue(key, *value); err != nil {
			return PatchResult{}, err
		}
		updated[key] = *value
	}
	serialized := serializeProperties(parsed.lines, updated)
	written, err := s.files.WriteProperties(ctx, serialized, expectedRevision)
	if err != nil {
		return PatchResult{}, err
	}
	s.mu.Lock()
	s.restartRequired = true
	s.mu.Unlock()
	properties, order := withoutSecret(document{values: updated, order: mapOrder(updated)})
	return PatchResult{
		Revision: written.Revision, RestartRequired: true, AppliedAt: s.now().UTC(),
		Properties: properties, Order: order,
	}, nil
}

func (s *PropertyService) NotifyServerStarted() {
	s.mu.Lock()
	s.restartRequired = false
	s.mu.Unlock()
}

func AuditChanges(changes map[string]*string) (setKeys, removedKeys string) {
	var set, removed []string
	for key, value := range changes {
		if value == nil {
			removed = append(removed, key)
		} else {
			set = append(set, key)
		}
	}
	sort.Strings(set)
	sort.Strings(removed)
	return strings.Join(set, ","), strings.Join(removed, ",")
}

func parseProperties(content string) document {
	content = strings.ReplaceAll(content, "\r\n", "\n")
	content = strings.ReplaceAll(content, "\r", "\n")
	result := document{values: make(map[string]string)}
	for _, raw := range strings.Split(content, "\n") {
		trimmed := strings.TrimSpace(raw)
		equals := strings.IndexByte(trimmed, '=')
		if trimmed == "" || strings.HasPrefix(trimmed, "#") || equals < 0 {
			result.lines = append(result.lines, propertyLine{raw: raw})
			continue
		}
		key := strings.TrimSpace(trimmed[:equals])
		value := trimmed[equals+1:]
		keyCopy := key
		result.lines = append(result.lines, propertyLine{key: &keyCopy, raw: raw})
		if _, exists := result.values[key]; !exists {
			result.order = append(result.order, key)
		}
		result.values[key] = value
	}
	return result
}

func serializeProperties(lines []propertyLine, values map[string]string) string {
	var output strings.Builder
	written := make(map[string]struct{}, len(values))
	for _, line := range lines {
		if line.key == nil {
			if line.raw != "" {
				output.WriteString(line.raw)
				output.WriteByte('\n')
			}
			continue
		}
		if _, exists := written[*line.key]; exists {
			continue
		}
		written[*line.key] = struct{}{}
		if value, exists := values[*line.key]; exists {
			output.WriteString(*line.key)
			output.WriteByte('=')
			output.WriteString(value)
			output.WriteByte('\n')
		}
	}
	keys := make([]string, 0, len(values))
	for key := range values {
		keys = append(keys, key)
	}
	sort.Strings(keys)
	for _, key := range keys {
		if _, exists := written[key]; exists {
			continue
		}
		written[key] = struct{}{}
		output.WriteString(key)
		output.WriteByte('=')
		output.WriteString(values[key])
		output.WriteByte('\n')
	}
	return output.String()
}

func validatePropertyKey(raw string) (string, error) {
	key := strings.TrimSpace(raw)
	if key == "" || utf16Length(key) > 128 {
		return "", invalidRequest("A property key is invalid.")
	}
	for _, r := range key {
		if unicode.IsControl(r) || unicode.IsSpace(r) || r == '=' || r == '\\' || r == '/' {
			return "", invalidRequest("A property key is invalid.")
		}
	}
	return key, nil
}

func validatePropertyValue(key, value string) error {
	if utf16Length(value) > 4096 {
		return invalidRequest(fmt.Sprintf("The value for '%s' is invalid.", key))
	}
	for _, r := range value {
		if unicode.IsControl(r) {
			return invalidRequest(fmt.Sprintf("The value for '%s' is invalid.", key))
		}
	}
	if _, boolean := booleanKeys[key]; boolean && value != "true" && value != "false" {
		return invalidRequest(fmt.Sprintf("The value for '%s' must be 'true' or 'false'.", key))
	}
	if limits, integer := integerKeys[key]; integer {
		number, err := strconv.ParseInt(value, 10, 32)
		if err != nil || number < int64(limits[0]) || number > int64(limits[1]) {
			return invalidRequest(fmt.Sprintf("The value for '%s' is outside its valid range.", key))
		}
	}
	if key == "difficulty" && value != "peaceful" && value != "easy" && value != "normal" && value != "hard" ||
		key == "gamemode" && value != "survival" && value != "creative" && value != "adventure" && value != "spectator" {
		return invalidRequest(fmt.Sprintf("The value for '%s' is invalid.", key))
	}
	return nil
}

func withoutSecret(parsed document) (map[string]string, []string) {
	properties := make(map[string]string, len(parsed.values))
	order := make([]string, 0, len(parsed.order))
	for _, key := range parsed.order {
		if strings.EqualFold(key, "rcon.password") {
			continue
		}
		properties[key] = parsed.values[key]
		order = append(order, key)
	}
	return properties, order
}

func mapOrder(values map[string]string) []string {
	keys := make([]string, 0, len(values))
	for key := range values {
		keys = append(keys, key)
	}
	sort.Strings(keys)
	return keys
}

func utf16Length(value string) int { return len(utf16.Encode([]rune(value))) }
