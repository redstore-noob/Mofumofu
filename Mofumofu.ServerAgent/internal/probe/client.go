// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

// Package probe implements the loopback HTTP gateway to optional NekoProbe.
package probe

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

const MaxResponseBytes = 4 * 1024 * 1024

var ErrUnavailable = errors.New("probe unavailable")

type Response struct {
	StatusCode int
	Content    []byte
}

type Client struct {
	base *url.URL
	http *http.Client
}

func New(baseURL string) (*Client, error) {
	parsed, err := url.Parse(strings.TrimRight(baseURL, "/") + "/")
	if err != nil {
		return nil, err
	}
	transport := &http.Transport{Proxy: nil, ForceAttemptHTTP2: false}
	return &Client{
		base: parsed,
		http: &http.Client{Timeout: 5 * time.Second, Transport: transport},
	}, nil
}

func (c *Client) Send(ctx context.Context, method, path, authorization string, body any, query string) (Response, error) {
	if !strings.HasPrefix(path, "/") || strings.Contains(path, "..") {
		return Response{}, fmt.Errorf("invalid fixed Probe path")
	}
	target := c.base.ResolveReference(&url.URL{Path: strings.TrimPrefix(path, "/"), RawQuery: query})
	var reader io.Reader
	if body != nil {
		payload, err := protocol.Marshal(body)
		if err != nil {
			return Response{}, err
		}
		reader = bytes.NewReader(payload)
	}
	request, err := http.NewRequestWithContext(ctx, method, target.String(), reader)
	if err != nil {
		return Response{}, err
	}
	request.Header.Set("Authorization", authorization)
	request.Header.Set("Accept", "application/json")
	if body != nil {
		request.Header.Set("Content-Type", "application/json")
	}
	response, err := c.http.Do(request)
	if err != nil {
		return Response{}, fmt.Errorf("%w: %v", ErrUnavailable, err)
	}
	defer response.Body.Close()
	if response.ContentLength > MaxResponseBytes {
		return Response{}, fmt.Errorf("%w: response too large", ErrUnavailable)
	}
	payload, err := io.ReadAll(io.LimitReader(response.Body, MaxResponseBytes+1))
	if err != nil {
		return Response{}, fmt.Errorf("%w: %v", ErrUnavailable, err)
	}
	if len(payload) > MaxResponseBytes {
		return Response{}, fmt.Errorf("%w: response too large", ErrUnavailable)
	}
	return Response{StatusCode: response.StatusCode, Content: payload}, nil
}

func (r Response) Decode(target any) error {
	if err := protocol.Unmarshal(r.Content, target); err != nil {
		return err
	}
	return nil
}

func APIErrorFrom(response Response) (*protocol.APIError, bool) {
	var apiError protocol.APIError
	if err := protocol.Unmarshal(response.Content, &apiError); err != nil {
		return nil, false
	}
	return &apiError, true
}
