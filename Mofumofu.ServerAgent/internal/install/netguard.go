// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package install

import (
	"context"
	"errors"
	"net"
	"net/url"
	"strings"
)

// LookupFunc resolves a host name. Tests inject it so no real DNS is used.
type LookupFunc func(ctx context.Context, host string) ([]net.IPAddr, error)

var errNotPublic = errors.New("download URL must be an absolute HTTPS URL that resolves to a public address")

// ValidatePublicHTTPSURL mirrors ServerNetworkGuard.ValidatePublicHttpUrlAsync:
// HTTPS only, no userinfo, no localhost, and no loopback, private, link-local,
// or reserved addresses after DNS resolution. Errors never echo the URL.
func ValidatePublicHTTPSURL(ctx context.Context, rawURL string, lookup LookupFunc) error {
	parsed, err := url.Parse(rawURL)
	if err != nil || !parsed.IsAbs() || parsed.Scheme != "https" {
		return errNotPublic
	}
	if parsed.User != nil || parsed.Hostname() == "" {
		return errNotPublic
	}
	host := strings.ToLower(parsed.Hostname())
	if host == "localhost" || strings.HasSuffix(host, ".localhost") {
		return errors.New("download URL must not point to this host")
	}
	if ip := net.ParseIP(host); ip != nil {
		if isBlockedAddress(ip) {
			return errors.New("download URL must not point to loopback, private, or link-local addresses")
		}
		return nil
	}
	addresses, err := resolveHost(ctx, host, lookup)
	if err != nil {
		return err
	}
	for _, address := range addresses {
		if isBlockedAddress(address) {
			return errors.New("download URL must not point to loopback, private, or link-local addresses")
		}
	}
	return nil
}

func resolveHost(ctx context.Context, host string, lookup LookupFunc) ([]net.IP, error) {
	if lookup == nil {
		lookup = defaultLookup
	}
	resolved, err := lookup(ctx, host)
	if err != nil || len(resolved) == 0 {
		return nil, errors.New("download host could not be resolved")
	}
	addresses := make([]net.IP, 0, len(resolved))
	for _, address := range resolved {
		addresses = append(addresses, address.IP)
	}
	return addresses, nil
}

func defaultLookup(ctx context.Context, host string) ([]net.IPAddr, error) {
	return net.DefaultResolver.LookupIPAddr(ctx, host)
}

func isBlockedAddress(ip net.IP) bool {
	if ipv4 := ip.To4(); ipv4 != nil {
		first, second, third := ipv4[0], ipv4[1], ipv4[2]
		switch {
		case first == 0, first == 10, first == 127, first >= 224:
			return true
		case first == 100 && second >= 64 && second <= 127:
			return true
		case first == 169 && second == 254:
			return true
		case first == 172 && second >= 16 && second <= 31:
			return true
		case first == 192 && (second == 168 || (second == 0 && third == 0)):
			return true
		case first == 198 && (second == 18 || second == 19):
			return true
		}
		return false
	}
	return ip.IsLoopback() || ip.IsUnspecified() || ip.IsMulticast() || ip.IsLinkLocalUnicast() ||
		ip.IsPrivate() || !ip.IsGlobalUnicast()
}

// redactURL keeps the scheme and host for diagnostics and drops path, query, and userinfo.
func redactURL(rawURL string) string {
	parsed, err := url.Parse(rawURL)
	if err != nil || parsed.Host == "" {
		return "(invalid URL)"
	}
	return parsed.Scheme + "://" + parsed.Host
}
