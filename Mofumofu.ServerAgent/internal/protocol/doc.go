// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Mofumofu

// Package protocol implements the Mofumofu remote Agent wire contract.
//
// Marshal and Unmarshal preserve the C# protocol's field order, null handling,
// DateTimeOffset representation, polymorphic WebSocket discriminator, and
// System.Text.Json default string escaping. Scalar DTO fields with a non-empty
// C# initializer are pointers: nil selects that initializer, while Ptr can carry
// an explicit empty string, zero, or false without losing it. Nil slices and
// maps are omitted; non-nil empty collections are written. The New... constructors
// and Unmarshal seed the C# collection initializers before use.
package protocol
