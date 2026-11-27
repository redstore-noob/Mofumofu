// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package install

import (
	"context"
	"crypto/sha1"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"
)

const (
	// DefaultMojangManifestURL is the vanilla version manifest.
	DefaultMojangManifestURL = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"
	// DefaultPaperAPIURL is the PaperMC Fill v3 base.
	DefaultPaperAPIURL = "https://fill.papermc.io/v3"
	// DefaultFabricMetaURL is the Fabric meta v2 base.
	DefaultFabricMetaURL = "https://meta.fabricmc.net/v2"
	// DefaultNeoForgeMavenURL is the NeoForge Maven base.
	DefaultNeoForgeMavenURL = "https://maven.neoforged.net"
	// EulaURL is written into eula.txt.
	EulaURL = "https://aka.ms/MinecraftEULA"
	// MaxCoreDownloadBytes is the server core size limit (512 MiB).
	MaxCoreDownloadBytes int64 = 512 << 20
	// NeoForgeInstallerTimeout bounds the NeoForge installer process.
	NeoForgeInstallerTimeout = 10 * time.Minute

	maxMetadataBytes  = 32 << 20
	maxRedirects      = 5
	requestTimeout    = 10 * time.Minute
	dialTimeout       = 30 * time.Second
	progressStepBytes = 4 << 20
)

// Endpoints overrides the upstream bases. Empty fields use the defaults.
type Endpoints struct {
	MojangManifest string
	PaperAPI       string
	FabricMeta     string
	NeoForgeMaven  string
}

// Options configures a Client. A nil URLPolicy uses ValidatePublicHTTPSURL.
type Options struct {
	// HTTPClient supplies the transport when set. Its Transport and Timeout are
	// kept, and redirects are still validated by the policy.
	HTTPClient *http.Client
	UserAgent  string
	Endpoints  Endpoints
	URLPolicy  func(ctx context.Context, rawURL string) error
	Lookup     LookupFunc
	// MaxDownloadBytes overrides MaxCoreDownloadBytes for tests.
	MaxDownloadBytes int64
}

// Client talks to upstream core providers and downloads server cores.
type Client struct {
	http             *http.Client
	userAgent        string
	endpoints        Endpoints
	policy           func(context.Context, string) error
	lookup           LookupFunc
	maxDownloadBytes int64
}

// NewClient creates a Client. Without HTTPClient it uses a hardened transport
// that does not use proxies and re-checks dialed addresses.
func NewClient(options Options) *Client {
	client := &Client{
		userAgent:        options.UserAgent,
		endpoints:        withDefaultEndpoints(options.Endpoints),
		lookup:           options.Lookup,
		maxDownloadBytes: options.MaxDownloadBytes,
	}
	if client.lookup == nil {
		client.lookup = defaultLookup
	}
	if client.userAgent == "" {
		client.userAgent = "mofumofu-agent/dev"
	}
	if client.maxDownloadBytes <= 0 {
		client.maxDownloadBytes = MaxCoreDownloadBytes
	}
	client.policy = options.URLPolicy
	if client.policy == nil {
		lookup := client.lookup
		client.policy = func(ctx context.Context, rawURL string) error {
			return ValidatePublicHTTPSURL(ctx, rawURL, lookup)
		}
	}
	if options.HTTPClient != nil {
		client.http = &http.Client{Transport: options.HTTPClient.Transport, Timeout: options.HTTPClient.Timeout}
	} else {
		client.http = &http.Client{Transport: client.hardenedTransport(), Timeout: requestTimeout}
	}
	client.http.CheckRedirect = func(request *http.Request, via []*http.Request) error {
		if len(via) >= maxRedirects {
			return errors.New("too many redirects")
		}
		return client.policy(request.Context(), request.URL.String())
	}
	return client
}

func withDefaultEndpoints(endpoints Endpoints) Endpoints {
	if endpoints.MojangManifest == "" {
		endpoints.MojangManifest = DefaultMojangManifestURL
	}
	if endpoints.PaperAPI == "" {
		endpoints.PaperAPI = DefaultPaperAPIURL
	}
	if endpoints.FabricMeta == "" {
		endpoints.FabricMeta = DefaultFabricMetaURL
	}
	if endpoints.NeoForgeMaven == "" {
		endpoints.NeoForgeMaven = DefaultNeoForgeMavenURL
	}
	return endpoints
}

func (c *Client) hardenedTransport() *http.Transport {
	dialer := &net.Dialer{Timeout: dialTimeout}
	return &http.Transport{
		Proxy:                 nil,
		DialContext:           c.guardedDial(dialer),
		ForceAttemptHTTP2:     true,
		TLSHandshakeTimeout:   15 * time.Second,
		ResponseHeaderTimeout: 60 * time.Second,
		IdleConnTimeout:       90 * time.Second,
	}
}

// guardedDial re-resolves the host and refuses non-public addresses at connect
// time, which narrows the DNS-rebinding window left by the pre-request check.
func (c *Client) guardedDial(dialer *net.Dialer) func(ctx context.Context, network, address string) (net.Conn, error) {
	return func(ctx context.Context, network, address string) (net.Conn, error) {
		host, port, err := net.SplitHostPort(address)
		if err != nil {
			return nil, errors.New("download connection failed")
		}
		var addresses []net.IP
		if literal := net.ParseIP(host); literal != nil {
			addresses = []net.IP{literal}
		} else if addresses, err = resolveHost(ctx, host, c.lookup); err != nil {
			return nil, err
		}
		for _, ip := range addresses {
			if isBlockedAddress(ip) {
				return nil, errors.New("download host resolved to a non-public address")
			}
		}
		for _, ip := range addresses {
			conn, dialErr := dialer.DialContext(ctx, network, net.JoinHostPort(ip.String(), port))
			if dialErr == nil {
				return conn, nil
			}
		}
		return nil, errors.New("download connection failed")
	}
}

// open validates rawURL, performs a GET, and returns a 2xx response.
func (c *Client) open(ctx context.Context, rawURL string) (*http.Response, error) {
	if err := c.policy(ctx, rawURL); err != nil {
		return nil, err
	}
	request, err := http.NewRequestWithContext(ctx, http.MethodGet, rawURL, nil)
	if err != nil {
		return nil, invalidf("download URL is invalid")
	}
	request.Header.Set("User-Agent", c.userAgent)
	response, err := c.http.Do(request)
	if err != nil {
		if ctxErr := ctx.Err(); ctxErr != nil {
			return nil, ctxErr
		}
		return nil, failedf("request to " + redactURL(rawURL) + " failed")
	}
	if response.StatusCode < 200 || response.StatusCode > 299 {
		_ = response.Body.Close()
		return nil, failedf(fmt.Sprintf("HTTP %d from %s", response.StatusCode, redactURL(rawURL)))
	}
	return response, nil
}

func (c *Client) getJSON(ctx context.Context, rawURL string, target any) error {
	response, err := c.open(ctx, rawURL)
	if err != nil {
		return err
	}
	defer response.Body.Close()
	if err := json.NewDecoder(io.LimitReader(response.Body, maxMetadataBytes)).Decode(target); err != nil {
		return failedf("invalid JSON from " + redactURL(rawURL))
	}
	return nil
}

// downloadFile writes rawURL to target through a temporary file in the same
// directory. It verifies size and SHA-1 before renaming, and it never leaves
// a partial file at target.
func (c *Client) downloadFile(ctx context.Context, rawURL, target string, expectedSize int64, expectedSHA1 string, report func(done, total int64)) error {
	if expectedSize > c.maxDownloadBytes {
		return failedf("server core exceeds the download size limit")
	}
	response, err := c.open(ctx, rawURL)
	if err != nil {
		return err
	}
	defer response.Body.Close()
	if response.ContentLength > c.maxDownloadBytes {
		return failedf("server core exceeds the download size limit")
	}
	if expectedSize > 0 && response.ContentLength > 0 && response.ContentLength != expectedSize {
		return failedf("server core size does not match the version metadata")
	}

	temporary, err := os.CreateTemp(filepath.Dir(target), ".mofu-download-*")
	if err != nil {
		return failedf("could not create the download file")
	}
	temporaryPath := temporary.Name()
	committed := false
	defer func() {
		if !committed {
			_ = temporary.Close()
			_ = os.Remove(temporaryPath)
		}
	}()

	total := expectedSize
	if total <= 0 {
		total = response.ContentLength
	}
	digest := sha1.New()
	progress := &progressWriter{total: total, report: report}
	written, err := io.Copy(io.MultiWriter(temporary, digest, progress),
		io.LimitReader(response.Body, c.maxDownloadBytes+1))
	if err != nil {
		if ctxErr := ctx.Err(); ctxErr != nil {
			return ctxErr
		}
		return failedf("download was interrupted")
	}
	progress.finish()
	if written > c.maxDownloadBytes {
		return failedf("server core exceeds the download size limit")
	}
	if expectedSize > 0 && written != expectedSize {
		return failedf("server core size does not match the version metadata")
	}
	if expectedSHA1 != "" && !strings.EqualFold(hex.EncodeToString(digest.Sum(nil)), expectedSHA1) {
		return failedf("server core failed SHA-1 verification")
	}
	if err := temporary.Sync(); err != nil {
		return failedf("could not flush the download file")
	}
	if err := temporary.Close(); err != nil {
		return failedf("could not close the download file")
	}
	_ = os.Chmod(temporaryPath, 0o644)
	if err := os.Rename(temporaryPath, target); err != nil {
		return failedf("could not move the download into place")
	}
	committed = true
	return nil
}

type progressWriter struct {
	total    int64
	done     int64
	reported int64
	report   func(done, total int64)
}

func (w *progressWriter) Write(payload []byte) (int, error) {
	w.done += int64(len(payload))
	if w.report != nil && w.done-w.reported >= progressStepBytes {
		w.reported = w.done
		w.report(w.done, w.total)
	}
	return len(payload), nil
}

func (w *progressWriter) finish() {
	if w.report != nil && w.done != w.reported {
		w.reported = w.done
		w.report(w.done, w.total)
	}
}
