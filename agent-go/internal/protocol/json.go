// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Mofumofu

package protocol

import (
	"bytes"
	"encoding/json"
	"fmt"
	"reflect"
	"sort"
	"strconv"
	"strings"
	"unicode/utf8"
)

const protocolDefaultTag = "protocolDefault"

var (
	protocolMessageType = reflect.TypeOf((*IProtocolMessage)(nil)).Elem()
	jsonNumberType      = reflect.TypeOf(json.Number(""))
)

// Ptr returns a pointer to value. It is useful for DTO fields whose nil value
// selects a C# property initializer while a non-nil false, zero, or empty value
// must be written explicitly.
func Ptr[T any](value T) *T {
	return &value
}

func stringPointer(value string) *string {
	return Ptr(value)
}

// Marshal serializes v with the RemoteProtocol JSON contract. In particular,
// nil properties are omitted, non-nil empty collections are retained, map null
// values are retained, and strings use System.Text.Json's default escaping.
func Marshal(v any) ([]byte, error) {
	return appendJSONValue(nil, reflect.ValueOf(v))
}

// Unmarshal deserializes one JSON value into v. It rejects a top-level null and
// trailing non-whitespace data, ignores unknown fields, and initializes DTO
// defaults before decoding. WebSocketFrame values are dispatched by type.
func Unmarshal(data []byte, v any) error {
	if bytes.Equal(bytes.TrimSpace(data), []byte("null")) {
		return fmt.Errorf("The JSON value was null.")
	}

	if frame, ok := v.(*WebSocketFrame); ok {
		fields, err := readTopLevelObject(data)
		if err != nil {
			return err
		}
		frameType, err := stringField(fields, "type", true)
		if err != nil {
			return err
		}
		var concrete WebSocketFrame
		switch frameType {
		case FrameTypeConsole:
			concrete = &ConsoleFrame{}
		case FrameTypeEvent:
			concrete = &EventFrame{}
		case FrameTypeSnapshot:
			concrete = &SnapshotFrame{}
		case FrameTypeReset:
			concrete = &ResetFrame{}
		case FrameTypeOperation:
			concrete = &OperationFrame{}
		default:
			return fmt.Errorf("unknown WebSocket frame type %q", frameType)
		}
		if err := unmarshalInitialized(data, concrete, true); err != nil {
			return err
		}
		*frame = concrete
		return nil
	}

	if message, ok := v.(*IProtocolMessage); ok {
		if *message == nil {
			return fmt.Errorf("cannot deserialize into a nil IProtocolMessage without a concrete type")
		}
		return unmarshalInitialized(data, *message, true)
	}

	value := reflect.ValueOf(v)
	if !value.IsValid() || value.Kind() != reflect.Pointer || value.IsNil() {
		return json.Unmarshal(data, v)
	}
	if value.Elem().Kind() != reflect.Struct {
		return json.Unmarshal(data, v)
	}
	return unmarshalInitialized(data, v, value.Elem().Type().Implements(protocolMessageType))
}

func unmarshalInitialized(data []byte, target any, rejectNullAPIVersion bool) error {
	value := reflect.ValueOf(target)
	if !value.IsValid() || value.Kind() != reflect.Pointer || value.IsNil() || value.Elem().Kind() != reflect.Struct {
		return json.Unmarshal(data, target)
	}
	if err := initializeDefaults(value.Elem()); err != nil {
		return err
	}
	if rejectNullAPIVersion {
		fields, err := readTopLevelObject(data)
		if err != nil {
			return err
		}
		if err := rejectNullNamedField(fields, "apiVersion"); err != nil {
			return err
		}
	}
	return json.Unmarshal(data, target)
}

func initializeDefaults(value reflect.Value) error {
	typeOf := value.Type()
	if typeOf.Implements(protocolMessageType) || reflect.PointerTo(typeOf).Implements(protocolMessageType) {
		initializeAPIVersionField(value)
	}
	for i := 0; i < value.NumField(); i++ {
		fieldInfo := typeOf.Field(i)
		if fieldInfo.PkgPath != "" {
			continue
		}
		defaultValue := fieldInfo.Tag.Get(protocolDefaultTag)
		if defaultValue == "" {
			continue
		}
		field := value.Field(i)
		if defaultValue == "empty" {
			if field.Kind() == reflect.Slice {
				field.Set(reflect.MakeSlice(field.Type(), 0, 0))
			} else if field.Kind() == reflect.Map {
				field.Set(reflect.MakeMap(field.Type()))
			} else {
				return fmt.Errorf("empty protocol default on %s.%s must be a slice or map", typeOf, fieldInfo.Name)
			}
			continue
		}
		if field.Kind() != reflect.Pointer {
			return fmt.Errorf("protocol default on %s.%s must be a pointer", typeOf, fieldInfo.Name)
		}
		pointer := reflect.New(field.Type().Elem())
		if err := setScalarDefault(pointer.Elem(), defaultValue); err != nil {
			return fmt.Errorf("protocol default on %s.%s: %w", typeOf, fieldInfo.Name, err)
		}
		field.Set(pointer)
	}
	return nil
}

func initializeAPIVersionField(value reflect.Value) {
	typeOf := value.Type()
	for i := 0; i < typeOf.NumField(); i++ {
		fieldInfo := typeOf.Field(i)
		if fieldInfo.PkgPath != "" {
			continue
		}
		name, _ := parseJSONTag(fieldInfo.Tag.Get("json"))
		if name == "" {
			name = lowerFirst(fieldInfo.Name)
		}
		if !strings.EqualFold(name, "apiVersion") {
			continue
		}
		field := value.Field(i)
		switch field.Kind() {
		case reflect.String:
			field.SetString(APIVersion)
		case reflect.Pointer:
			if field.Type().Elem().Kind() == reflect.String {
				pointer := reflect.New(field.Type().Elem())
				pointer.Elem().SetString(APIVersion)
				field.Set(pointer)
			}
		}
		return
	}
}

func setScalarDefault(value reflect.Value, text string) error {
	switch value.Kind() {
	case reflect.String:
		value.SetString(text)
	case reflect.Bool:
		parsed, err := strconv.ParseBool(text)
		if err != nil {
			return err
		}
		value.SetBool(parsed)
	case reflect.Int, reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64:
		parsed, err := strconv.ParseInt(text, 10, value.Type().Bits())
		if err != nil {
			return err
		}
		value.SetInt(parsed)
	case reflect.Uint, reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64:
		parsed, err := strconv.ParseUint(text, 10, value.Type().Bits())
		if err != nil {
			return err
		}
		value.SetUint(parsed)
	case reflect.Float32, reflect.Float64:
		parsed, err := strconv.ParseFloat(text, value.Type().Bits())
		if err != nil {
			return err
		}
		value.SetFloat(parsed)
	default:
		return fmt.Errorf("unsupported scalar type %s", value.Type())
	}
	return nil
}

func appendJSONValue(buffer []byte, value reflect.Value) ([]byte, error) {
	if !value.IsValid() {
		return append(buffer, "null"...), nil
	}
	for value.Kind() == reflect.Interface {
		if value.IsNil() {
			return append(buffer, "null"...), nil
		}
		value = value.Elem()
	}
	if value.Kind() == reflect.Pointer && value.IsNil() {
		return append(buffer, "null"...), nil
	}

	if value.CanInterface() {
		if _, ok := value.Interface().(WebSocketFrame); ok && value.Kind() == reflect.Struct {
			frame := value.Interface().(WebSocketFrame)
			buffer = append(buffer, `{"type":`...)
			buffer = appendJSONString(buffer, frame.webSocketFrameType())
			buffer = append(buffer, ',')
			fields, err := appendJSONStructFields(nil, value)
			if err != nil {
				return nil, err
			}
			buffer = append(buffer, fields[1:len(fields)-1]...)
			return append(buffer, '}'), nil
		}
	}
	if value.Kind() == reflect.Pointer {
		return appendJSONValue(buffer, value.Elem())
	}
	if value.CanInterface() {
		if marshaler, ok := value.Interface().(json.Marshaler); ok {
			encoded, err := marshaler.MarshalJSON()
			if err != nil {
				return nil, err
			}
			if !json.Valid(encoded) {
				return nil, fmt.Errorf("invalid JSON returned by %s.MarshalJSON", value.Type())
			}
			return append(buffer, encoded...), nil
		}
	}
	if value.Type() == jsonNumberType {
		return append(buffer, value.String()...), nil
	}

	switch value.Kind() {
	case reflect.String:
		return appendJSONString(buffer, value.String()), nil
	case reflect.Bool:
		return strconv.AppendBool(buffer, value.Bool()), nil
	case reflect.Int, reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64:
		return strconv.AppendInt(buffer, value.Int(), 10), nil
	case reflect.Uint, reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64, reflect.Uintptr:
		return strconv.AppendUint(buffer, value.Uint(), 10), nil
	case reflect.Float32, reflect.Float64:
		encoded, err := json.Marshal(value.Interface())
		if err != nil {
			return nil, err
		}
		return append(buffer, encoded...), nil
	case reflect.Slice:
		if value.IsNil() {
			return append(buffer, "null"...), nil
		}
		if value.Type().Elem().Kind() == reflect.Uint8 {
			encoded, err := json.Marshal(value.Interface())
			if err != nil {
				return nil, err
			}
			return append(buffer, encoded...), nil
		}
		buffer = append(buffer, '[')
		for i := 0; i < value.Len(); i++ {
			if i != 0 {
				buffer = append(buffer, ',')
			}
			var err error
			buffer, err = appendJSONValue(buffer, value.Index(i))
			if err != nil {
				return nil, err
			}
		}
		return append(buffer, ']'), nil
	case reflect.Array:
		buffer = append(buffer, '[')
		for i := 0; i < value.Len(); i++ {
			if i != 0 {
				buffer = append(buffer, ',')
			}
			var err error
			buffer, err = appendJSONValue(buffer, value.Index(i))
			if err != nil {
				return nil, err
			}
		}
		return append(buffer, ']'), nil
	case reflect.Map:
		if value.IsNil() {
			return append(buffer, "null"...), nil
		}
		return appendJSONMap(buffer, value)
	case reflect.Struct:
		return appendJSONStructFields(buffer, value)
	default:
		return nil, fmt.Errorf("unsupported JSON type %s", value.Type())
	}
}

func appendJSONStructFields(buffer []byte, value reflect.Value) ([]byte, error) {
	buffer = append(buffer, '{')
	wroteField := false
	typeOf := value.Type()
	for i := 0; i < value.NumField(); i++ {
		fieldInfo := typeOf.Field(i)
		if fieldInfo.PkgPath != "" {
			continue
		}
		tag := fieldInfo.Tag.Get("json")
		name, _ := parseJSONTag(tag)
		if name == "-" {
			continue
		}
		field := value.Field(i)
		if fieldInfo.Anonymous && name == "" {
			flattened, err := appendJSONStructFields(nil, field)
			if err != nil {
				return nil, err
			}
			if len(flattened) > 2 {
				if wroteField {
					buffer = append(buffer, ',')
				}
				buffer = append(buffer, flattened[1:len(flattened)-1]...)
				wroteField = true
			}
			continue
		}
		if name == "" {
			name = lowerFirst(fieldInfo.Name)
		}

		defaultValue := fieldInfo.Tag.Get(protocolDefaultTag)
		if isNilProperty(field) {
			if defaultValue == "" || defaultValue == "empty" {
				continue
			}
			if wroteField {
				buffer = append(buffer, ',')
			}
			buffer = appendJSONString(buffer, name)
			buffer = append(buffer, ':')
			var err error
			buffer, err = appendJSONDefault(buffer, field.Type(), defaultValue)
			if err != nil {
				return nil, err
			}
			wroteField = true
			continue
		}

		if wroteField {
			buffer = append(buffer, ',')
		}
		buffer = appendJSONString(buffer, name)
		buffer = append(buffer, ':')
		var err error
		buffer, err = appendJSONValue(buffer, field)
		if err != nil {
			return nil, err
		}
		wroteField = true
	}
	return append(buffer, '}'), nil
}

func appendJSONDefault(buffer []byte, fieldType reflect.Type, text string) ([]byte, error) {
	if fieldType.Kind() != reflect.Pointer {
		return nil, fmt.Errorf("protocol default type %s must be a pointer", fieldType)
	}
	value := reflect.New(fieldType.Elem()).Elem()
	if err := setScalarDefault(value, text); err != nil {
		return nil, err
	}
	return appendJSONValue(buffer, value)
}

func appendJSONMap(buffer []byte, value reflect.Value) ([]byte, error) {
	typeOf := value.Type()
	if typeOf.Key().Kind() != reflect.String {
		return nil, fmt.Errorf("unsupported JSON map key type %s", typeOf.Key())
	}
	keys := value.MapKeys()
	sort.Slice(keys, func(i, j int) bool {
		left := keys[i].String()
		right := keys[j].String()
		leftRank, leftRanked := protocolMapKeyRank(left)
		rightRank, rightRanked := protocolMapKeyRank(right)
		if leftRanked && rightRanked && leftRank != rightRank {
			return leftRank < rightRank
		}
		if comparison := strings.Compare(strings.ToLower(left), strings.ToLower(right)); comparison != 0 {
			return comparison < 0
		}
		return left < right
	})

	buffer = append(buffer, '{')
	for i, key := range keys {
		if i != 0 {
			buffer = append(buffer, ',')
		}
		buffer = appendJSONString(buffer, key.String())
		buffer = append(buffer, ':')
		var err error
		buffer, err = appendJSONValue(buffer, value.MapIndex(key))
		if err != nil {
			return nil, err
		}
	}
	return append(buffer, '}'), nil
}

func protocolMapKeyRank(key string) (int, bool) {
	switch key {
	case "requestedVersion":
		return 0, true
	case "requestedMajor":
		return 1, true
	case "supportedMajor":
		return 2, true
	case "supportedVersion":
		return 3, true
	default:
		return 0, false
	}
}

func parseJSONTag(tag string) (string, string) {
	if tag == "" {
		return "", ""
	}
	parts := strings.Split(tag, ",")
	if len(parts) == 1 {
		return parts[0], ""
	}
	return parts[0], strings.Join(parts[1:], ",")
}

func lowerFirst(value string) string {
	if value == "" {
		return value
	}
	runes := []rune(value)
	runes[0] = []rune(strings.ToLower(string(runes[0])))[0]
	return string(runes)
}

func isNilProperty(value reflect.Value) bool {
	switch value.Kind() {
	case reflect.Chan, reflect.Func, reflect.Interface, reflect.Map, reflect.Pointer, reflect.Slice:
		return value.IsNil()
	default:
		return false
	}
}

func appendJSONString(buffer []byte, value string) []byte {
	const upperHex = "0123456789ABCDEF"
	buffer = append(buffer, '"')
	for i := 0; i < len(value); {
		current := value[i]
		if current < utf8.RuneSelf {
			switch current {
			case '"':
				buffer = append(buffer, `\u0022`...)
			case '\\':
				buffer = append(buffer, `\\`...)
			case '\b':
				buffer = append(buffer, `\b`...)
			case '\f':
				buffer = append(buffer, `\f`...)
			case '\n':
				buffer = append(buffer, `\n`...)
			case '\r':
				buffer = append(buffer, `\r`...)
			case '\t':
				buffer = append(buffer, `\t`...)
			case '&':
				buffer = append(buffer, `\u0026`...)
			case '\'':
				buffer = append(buffer, `\u0027`...)
			case '+':
				buffer = append(buffer, `\u002B`...)
			case '<':
				buffer = append(buffer, `\u003C`...)
			case '>':
				buffer = append(buffer, `\u003E`...)
			case '`':
				buffer = append(buffer, `\u0060`...)
			default:
				if current < 0x20 || current == 0x7F {
					buffer = append(buffer, '\\', 'u', '0', '0', upperHex[current>>4], upperHex[current&0x0F])
				} else {
					buffer = append(buffer, current)
				}
			}
			i++
			continue
		}

		runeValue, size := utf8.DecodeRuneInString(value[i:])
		if runeValue == utf8.RuneError && size == 1 {
			runeValue = utf8.RuneError
		}
		if runeValue <= 0xFFFF {
			buffer = appendHexRune(buffer, runeValue, upperHex)
		} else {
			codepoint := runeValue - 0x10000
			buffer = appendHexRune(buffer, 0xD800+(codepoint>>10), upperHex)
			buffer = appendHexRune(buffer, 0xDC00+(codepoint&0x3FF), upperHex)
		}
		i += size
	}
	return append(buffer, '"')
}

func appendHexRune(buffer []byte, value rune, upperHex string) []byte {
	return append(buffer, '\\', 'u',
		upperHex[(value>>12)&0x0F],
		upperHex[(value>>8)&0x0F],
		upperHex[(value>>4)&0x0F],
		upperHex[value&0x0F],
	)
}

type topLevelField struct {
	name  string
	value json.RawMessage
}

func readTopLevelObject(data []byte) ([]topLevelField, error) {
	decoder := json.NewDecoder(bytes.NewReader(data))
	token, err := decoder.Token()
	if err != nil {
		return nil, err
	}
	if token == nil {
		return nil, fmt.Errorf("The JSON value was null.")
	}
	delimiter, ok := token.(json.Delim)
	if !ok || delimiter != '{' {
		return nil, nil
	}

	var fields []topLevelField
	for decoder.More() {
		keyToken, err := decoder.Token()
		if err != nil {
			return nil, err
		}
		key, ok := keyToken.(string)
		if !ok {
			return nil, fmt.Errorf("JSON object key must be a string")
		}
		var raw json.RawMessage
		if err := decoder.Decode(&raw); err != nil {
			return nil, err
		}
		fields = append(fields, topLevelField{name: key, value: raw})
	}
	if _, err := decoder.Token(); err != nil {
		return nil, err
	}
	return fields, nil
}

func stringField(fields []topLevelField, name string, required bool) (string, error) {
	var result string
	found := false
	isNull := false
	for _, field := range fields {
		if !strings.EqualFold(field.name, name) {
			continue
		}
		found = true
		isNull = bytes.Equal(bytes.TrimSpace(field.value), []byte("null"))
		if isNull {
			result = ""
			continue
		}
		if err := json.Unmarshal(field.value, &result); err != nil {
			return "", err
		}
	}
	if required && !found {
		return "", fmt.Errorf("missing required JSON field %q", name)
	}
	if found && isNull {
		return "", fmt.Errorf("%s must not be null", name)
	}
	return result, nil
}

func rejectNullNamedField(fields []topLevelField, name string) error {
	for i := len(fields) - 1; i >= 0; i-- {
		if strings.EqualFold(fields[i].name, name) {
			if bytes.Equal(bytes.TrimSpace(fields[i].value), []byte("null")) {
				return fmt.Errorf("%s must not be null", name)
			}
			return nil
		}
	}
	return nil
}

func apiVersionValue(value *string) string {
	if value == nil {
		return APIVersion
	}
	return *value
}
