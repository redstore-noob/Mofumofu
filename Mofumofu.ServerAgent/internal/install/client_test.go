// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package install

import (
	"context"
	"crypto/sha1"
	"encoding/hex"
	"errors"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// fakeUpstream serves Minecraft provider JSON and downloads from an httptest TLS
// server. Its policy admits only that server, and its lookup fails, so no test
// reaches the real internet or DNS.
type fakeUpstream struct {
	base   string
	mux    *http.ServeMux
	client *Client
}

func newFakeUpstream(t *testing.T, tune func(*Options)) *fakeUpstream {
	t.Helper()
	mux := http.NewServeMux()
	server := httptest.NewTLSServer(mux)
	t.Cleanup(server.Close)
	base := server.URL
	options := Options{
		HTTPClient: server.Client(),
		UserAgent:  "mofumofu-agent/test",
		Endpoints: Endpoints{
			MojangManifest: base + "/mojang/version_manifest_v2.json",
			PaperAPI:       base + "/paper/v3",
			FabricMeta:     base + "/fabric/v2",
			NeoForgeMaven:  base + "/neoforge",
		},
		URLPolicy: func(_ context.Context, rawURL string) error {
			if strings.HasPrefix(rawURL, base+"/") {
				return nil
			}
			return errors.New("test policy: URL is outside the fake upstream")
		},
		Lookup: func(context.Context, string) ([]net.IPAddr, error) {
			return nil, errors.New("test lookup: DNS is disabled")
		},
	}
	if tune != nil {
		tune(&options)
	}
	return &fakeUpstream{base: base, mux: mux, client: NewClient(options)}
}

func (u *fakeUpstream) handleJSON(path, payload string) {
	u.mux.HandleFunc(path, func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(payload))
	})
}

func (u *fakeUpstream) handleBytes(path string, payload []byte) {
	u.mux.HandleFunc(path, func(w http.ResponseWriter, _ *http.Request) {
		_, _ = w.Write(payload)
	})
}

func sha1Hex(payload []byte) string {
	sum := sha1.Sum(payload)
	return hex.EncodeToString(sum[:])
}

func TestPublicHTTPSURLRejectsNonPublicTargets(t *testing.T) {
	ctx := context.Background()
	rejected := []string{
		"http://8.8.8.8/server.jar",
		"ftp://8.8.8.8/server.jar",
		"https://user:secret@8.8.8.8/server.jar",
		"https://localhost/server.jar",
		"https://api.localhost/server.jar",
		"https://127.0.0.1/server.jar",
		"https://10.0.0.5/server.jar",
		"https://192.168.1.10/server.jar",
		"https://172.16.0.1/server.jar",
		"https://169.254.169.254/latest/meta-data",
		"https://100.64.0.1/server.jar",
		"https://0.0.0.0/server.jar",
		"https://224.0.0.1/server.jar",
		"https://[::1]/server.jar",
		"https://[fe80::1]/server.jar",
		"https://[fc00::1]/server.jar",
		"relative/server.jar",
	}
	for _, rawURL := range rejected {
		if err := ValidatePublicHTTPSURL(ctx, rawURL, nil); err == nil {
			t.Fatalf("ValidatePublicHTTPSURL(%q) accepted a non-public target", rawURL)
		}
	}

	lookup := func(_ context.Context, host string) ([]net.IPAddr, error) {
		switch host {
		case "internal.example":
			return []net.IPAddr{{IP: net.ParseIP("10.1.2.3")}}, nil
		case "mixed.example":
			return []net.IPAddr{{IP: net.ParseIP("93.184.216.34")}, {IP: net.ParseIP("127.0.0.1")}}, nil
		case "public.example":
			return []net.IPAddr{{IP: net.ParseIP("93.184.216.34")}}, nil
		default:
			return nil, errors.New("no such host")
		}
	}
	for _, rawURL := range []string{"https://internal.example/x", "https://mixed.example/x", "https://missing.example/x"} {
		if err := ValidatePublicHTTPSURL(ctx, rawURL, lookup); err == nil {
			t.Fatalf("ValidatePublicHTTPSURL(%q) accepted a host that resolves to a non-public address", rawURL)
		}
	}
	for _, rawURL := range []string{"https://public.example/server.jar", "https://8.8.8.8/server.jar"} {
		if err := ValidatePublicHTTPSURL(ctx, rawURL, lookup); err != nil {
			t.Fatalf("ValidatePublicHTTPSURL(%q) = %v", rawURL, err)
		}
	}
}

func TestHardenedTransportRefusesNonPublicDialsBeforeConnecting(t *testing.T) {
	client := NewClient(Options{Lookup: func(_ context.Context, host string) ([]net.IPAddr, error) {
		if host == "rebind.example" {
			return []net.IPAddr{{IP: net.ParseIP("192.168.10.20")}}, nil
		}
		return nil, errors.New("no such host")
	}})
	transport, ok := client.http.Transport.(*http.Transport)
	if !ok || transport.Proxy != nil || transport.DialContext == nil {
		t.Fatalf("default transport is not hardened: %#v", client.http.Transport)
	}
	for _, address := range []string{"127.0.0.1:443", "[::1]:443", "rebind.example:443"} {
		if conn, err := transport.DialContext(context.Background(), "tcp", address); err == nil {
			_ = conn.Close()
			t.Fatalf("dial to %s succeeded", address)
		}
	}
}

func TestPolicyErrorsDoNotEchoURL(t *testing.T) {
	err := ValidatePublicHTTPSURL(context.Background(), "https://user@127.0.0.1/x?token=secret", nil)
	if err == nil || strings.Contains(err.Error(), "token") || strings.Contains(err.Error(), "secret") {
		t.Fatalf("policy error = %v", err)
	}
	if got := redactURL("https://piston.example/a/b?token=secret#frag"); got != "https://piston.example" {
		t.Fatalf("redactURL() = %q", got)
	}
}

func TestDownloadRejectsNonHTTPSWithoutNetwork(t *testing.T) {
	client := NewClient(Options{Lookup: func(context.Context, string) ([]net.IPAddr, error) {
		t.Fatal("lookup must not run for a rejected scheme")
		return nil, nil
	}})
	target := filepath.Join(t.TempDir(), "server.jar")
	err := client.downloadFile(context.Background(), "http://8.8.8.8/server.jar", target, 0, "", nil)
	if err == nil {
		t.Fatal("downloadFile accepted an HTTP URL")
	}
	if _, statErr := os.Stat(target); !os.IsNotExist(statErr) {
		t.Fatalf("rejected download left a target file: %v", statErr)
	}
}

func TestDownloadVerifiesSHA1AndLeavesNoPartialFile(t *testing.T) {
	payload := []byte("pretend this is a server core")
	upstream := newFakeUpstream(t, nil)
	upstream.handleBytes("/core.jar", payload)
	directory := t.TempDir()
	target := filepath.Join(directory, "server.jar")

	if err := upstream.client.downloadFile(context.Background(), upstream.base+"/core.jar", target,
		int64(len(payload)), sha1Hex(payload), nil); err != nil {
		t.Fatalf("valid download failed: %v", err)
	}
	got, err := os.ReadFile(target)
	if err != nil || string(got) != string(payload) {
		t.Fatalf("downloaded content = %q, %v", got, err)
	}

	badTarget := filepath.Join(directory, "bad.jar")
	err = upstream.client.downloadFile(context.Background(), upstream.base+"/core.jar", badTarget,
		int64(len(payload)), strings.Repeat("0", 40), nil)
	if err == nil || !strings.Contains(err.Error(), "SHA-1") {
		t.Fatalf("sha1 mismatch error = %v", err)
	}
	entries, err := os.ReadDir(directory)
	if err != nil {
		t.Fatal(err)
	}
	for _, entry := range entries {
		if entry.Name() == "bad.jar" || strings.HasPrefix(entry.Name(), ".mofu-download-") {
			t.Fatalf("sha1 mismatch left %s behind", entry.Name())
		}
	}
}

func TestDownloadRejectsSizeMismatchAndOversizedCores(t *testing.T) {
	payload := []byte("0123456789abcdef0123456789abcdef")
	upstream := newFakeUpstream(t, func(options *Options) { options.MaxDownloadBytes = 16 })
	upstream.handleBytes("/big.jar", payload)
	directory := t.TempDir()

	if err := upstream.client.downloadFile(context.Background(), upstream.base+"/big.jar",
		filepath.Join(directory, "a.jar"), 0, "", nil); err == nil {
		t.Fatal("download above the size limit was accepted")
	}
	if err := upstream.client.downloadFile(context.Background(), upstream.base+"/big.jar",
		filepath.Join(directory, "b.jar"), 1024, "", nil); err == nil {
		t.Fatal("metadata size above the limit was accepted")
	}

	small := []byte("short")
	upstream.handleBytes("/small.jar", small)
	if err := upstream.client.downloadFile(context.Background(), upstream.base+"/small.jar",
		filepath.Join(directory, "c.jar"), int64(len(small)+1), "", nil); err == nil {
		t.Fatal("download with a mismatched expected size was accepted")
	}
}

func TestRedirectToNonAllowedHostIsRejected(t *testing.T) {
	upstream := newFakeUpstream(t, nil)
	upstream.mux.HandleFunc("/redirect", func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, "https://10.0.0.9/server.jar", http.StatusFound)
	})
	err := upstream.client.downloadFile(context.Background(), upstream.base+"/redirect",
		filepath.Join(t.TempDir(), "server.jar"), 0, "", nil)
	if err == nil {
		t.Fatal("redirect to a non-allowed address was followed")
	}
}

func TestDownloadReportsProgressToCompletion(t *testing.T) {
	payload := make([]byte, 9<<20)
	upstream := newFakeUpstream(t, nil)
	upstream.handleBytes("/progress.jar", payload)
	var last int64
	err := upstream.client.downloadFile(context.Background(), upstream.base+"/progress.jar",
		filepath.Join(t.TempDir(), "progress.jar"), int64(len(payload)), "", func(done, total int64) {
			last = done
			if total != int64(len(payload)) {
				t.Errorf("total = %d", total)
			}
		})
	if err != nil || last != int64(len(payload)) {
		t.Fatalf("progress final = %d, err = %v", last, err)
	}
}
