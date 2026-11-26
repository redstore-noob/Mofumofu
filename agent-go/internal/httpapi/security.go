// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package httpapi

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"strings"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/tokens"
)

type authenticationKey struct{}

// Security applies the same ordering as RequestSecurityMiddleware: instance,
// query version, authentication, root/scope, rate limit, then the endpoint.
type Security struct {
	InstanceID string
	Registry   *tokens.TokenRegistry
	Limiter    *tokens.RateLimiter
}

func (s *Security) Wrap(scope string, category tokens.RateCategory, root bool, next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if r.PathValue("instanceId") != s.InstanceID {
			WriteError(w, r, NotFound("The server instance was not found."))
			return
		}
		if r.Method == http.MethodGet || isWebSocketUpgrade(r) {
			if err := ValidateQueryVersion(r); err != nil {
				WriteError(w, r, err)
				return
			}
		}
		authentication, err := s.Registry.Authenticate(r.Header.Get("Authorization"), s.InstanceID)
		if err != nil {
			WriteError(w, r, Unauthorized())
			return
		}
		if root && !authentication.IsRoot {
			WriteError(w, r, errorf(http.StatusForbidden, "forbidden", "This endpoint requires a root token."))
			return
		}
		if !root && scope != "" && !authentication.HasScope(scope) {
			WriteError(w, r, errorf(http.StatusForbidden, "insufficient_scope", fmt.Sprintf("The '%s' scope is required.", scope)))
			return
		}
		if err := s.Limiter.Check(authentication.TokenID, category); err != nil {
			if errors.Is(err, tokens.ErrRateLimited) {
				WriteError(w, r, errorf(http.StatusTooManyRequests, "rate_limited", "The per-token request rate limit was exceeded."))
				return
			}
			WriteError(w, r, errorf(http.StatusInternalServerError, "internal_error", "The request could not be completed."))
			return
		}
		ctx := context.WithValue(r.Context(), authenticationKey{}, authentication)
		next(w, r.WithContext(ctx))
	}
}

func Authentication(ctx context.Context) (*tokens.AuthContext, error) {
	authentication, ok := ctx.Value(authenticationKey{}).(*tokens.AuthContext)
	if !ok {
		return nil, Unauthorized()
	}
	return authentication, nil
}

func isWebSocketUpgrade(r *http.Request) bool {
	return strings.EqualFold(r.Header.Get("Upgrade"), "websocket")
}

func CapabilityNotSupported() *Error {
	return errorf(http.StatusForbidden, "capability_not_supported", "This capability is not supported by the current Agent backend.")
}
