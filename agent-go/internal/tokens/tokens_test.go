// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package tokens

import (
	"strings"
	"testing"
)

func TestGenerateToken(t *testing.T) {
	first, err := GenerateToken()
	if err != nil {
		t.Fatalf("GenerateToken: %v", err)
	}
	second, err := GenerateToken()
	if err != nil {
		t.Fatalf("GenerateToken: %v", err)
	}
	if !IsValidToken(first) || !IsValidToken(second) {
		t.Fatalf("generated invalid tokens: %q, %q", first, second)
	}
	if first == second {
		t.Fatal("generated tokens unexpectedly match")
	}
}

func TestTokenHash(t *testing.T) {
	tests := []struct {
		token string
		want  string
	}{
		{
			token: "abc",
			want:  "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
		},
		{
			token: "",
			want:  "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
		},
	}
	for _, test := range tests {
		if got := TokenHash(test.token); got != test.want {
			t.Errorf("TokenHash(%q) = %q, want %q", test.token, got, test.want)
		}
	}
}

func TestIsValidToken(t *testing.T) {
	valid := strings.Repeat("a", TokenHexLength)
	tests := []struct {
		name  string
		token string
		want  bool
	}{
		{name: "valid lowercase", token: valid, want: true},
		{name: "valid digits", token: strings.Repeat("0", TokenHexLength), want: true},
		{name: "empty", token: ""},
		{name: "short", token: valid[:TokenHexLength-1]},
		{name: "long", token: valid + "a"},
		{name: "uppercase", token: strings.ToUpper(valid)},
		{name: "non-hex", token: "g" + valid[1:]},
		{name: "whitespace", token: valid[:TokenHexLength-1] + " "},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			if got := IsValidToken(test.token); got != test.want {
				t.Fatalf("IsValidToken(%q) = %v, want %v", test.token, got, test.want)
			}
		})
	}
}
