// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package install

import (
	"context"
	"reflect"
	"testing"
)

func registerVersionRoutes(upstream *fakeUpstream) {
	upstream.handleJSON("/mojang/version_manifest_v2.json", `{
		"latest": {"release": "26.1", "snapshot": "26.1-snapshot-1"},
		"versions": [
			{"id": "26.1", "type": "release", "url": "https://unused.invalid/a.json"},
			{"id": "1.21.4-pre1", "type": "snapshot", "url": "https://unused.invalid/b.json"},
			{"id": "1.21.4", "type": "release", "url": "https://unused.invalid/c.json"},
			{"id": "1.20.6", "type": "release", "url": "https://unused.invalid/d.json"},
			{"id": "1.12", "type": "release", "url": "https://unused.invalid/e.json"}
		]
	}`)
	upstream.handleJSON("/paper/v3/projects/paper", `{
		"project": {"id": "paper"},
		"versions": {"26.1": ["26.1"], "1.21": ["1.21.4"], "1.20": ["1.20.6"]}
	}`)
	upstream.handleJSON("/paper/v3/projects/paper/versions/1.21.4/builds", `[
		{"id": 232, "downloads": {}},
		{"id": 231, "downloads": {}}
	]`)
	upstream.handleJSON("/fabric/v2/versions/game", `[
		{"version": "1.21.4", "stable": true},
		{"version": "1.20.6", "stable": false},
		{"version": "1.12", "stable": true}
	]`)
	upstream.handleJSON("/fabric/v2/versions/loader/1.21.4", `[
		{"loader": {"version": "0.16.9"}},
		{"loader": {"version": "0.16.8"}}
	]`)
	upstream.handleJSON("/neoforge/api/maven/versions/releases/net/neoforged/neoforge", `{
		"versions": ["21.4.100", "21.1.5", "21.4.156", "26.1.2", "26.1.0.0-alpha.1"]
	}`)
}

func TestListMinecraftVersionsFiltersByCore(t *testing.T) {
	upstream := newFakeUpstream(t, nil)
	registerVersionRoutes(upstream)
	ctx := context.Background()

	cases := map[Core][]string{
		CoreVanilla:  {"26.1", "1.21.4", "1.20.6", "1.12"},
		CorePaper:    {"26.1", "1.21.4", "1.20.6"},
		CoreFabric:   {"1.21.4", "1.12"},
		CoreNeoForge: {"26.1", "1.21.4"},
	}
	for core, want := range cases {
		got, err := upstream.client.ListMinecraftVersions(ctx, core)
		if err != nil {
			t.Fatalf("ListMinecraftVersions(%s) error = %v", core, err)
		}
		if !reflect.DeepEqual(got, want) {
			t.Fatalf("ListMinecraftVersions(%s) = %v, want %v", core, got, want)
		}
	}
}

func TestListCoreVersionsParsesUpstreamOrder(t *testing.T) {
	upstream := newFakeUpstream(t, nil)
	registerVersionRoutes(upstream)
	ctx := context.Background()

	paper, err := upstream.client.ListCoreVersions(ctx, CorePaper, "1.21.4")
	if err != nil || !reflect.DeepEqual(paper, []string{"232", "231"}) {
		t.Fatalf("paper builds = %v, %v", paper, err)
	}
	fabric, err := upstream.client.ListCoreVersions(ctx, CoreFabric, "1.21.4")
	if err != nil || !reflect.DeepEqual(fabric, []string{"0.16.9", "0.16.8"}) {
		t.Fatalf("fabric loaders = %v, %v", fabric, err)
	}
	neoforge, err := upstream.client.ListCoreVersions(ctx, CoreNeoForge, "1.21.4")
	if err != nil || !reflect.DeepEqual(neoforge, []string{"21.4.156", "21.4.100"}) {
		t.Fatalf("neoforge versions = %v, %v", neoforge, err)
	}
	vanilla, err := upstream.client.ListCoreVersions(ctx, CoreVanilla, "1.21.4")
	if err != nil || !reflect.DeepEqual(vanilla, []string{"1.21.4"}) {
		t.Fatalf("vanilla versions = %v, %v", vanilla, err)
	}
}

func TestListCoreVersionsRejectsUnsafeVersionIDs(t *testing.T) {
	upstream := newFakeUpstream(t, nil)
	if _, err := upstream.client.ListCoreVersions(context.Background(), CorePaper, "1.21/../../x"); err == nil {
		t.Fatal("ListCoreVersions accepted a path-like Minecraft version")
	}
}

func TestParseCoreAcceptsOnlySupportedCores(t *testing.T) {
	for _, value := range []string{"vanilla", "paper", "fabric", "neoforge"} {
		if _, err := ParseCore(value); err != nil {
			t.Fatalf("ParseCore(%q) = %v", value, err)
		}
	}
	for _, value := range []string{"Paper", "forge", "", "vanilla "} {
		if _, err := ParseCore(value); err == nil {
			t.Fatalf("ParseCore(%q) accepted an unsupported core", value)
		}
	}
}

func TestChoosePaperDownloadPrefersServerDefault(t *testing.T) {
	downloads := map[string]paperDownload{
		"zz":             {Name: "other.jar", URL: "https://a.example/other.jar"},
		"server:default": {Name: "paper.jar", URL: "https://a.example/paper.jar"},
		"empty":          {Name: "empty.jar"},
	}
	got, ok := choosePaperDownload(downloads)
	if !ok || got.Name != "paper.jar" {
		t.Fatalf("choosePaperDownload() = %+v, %v", got, ok)
	}
	if _, ok := choosePaperDownload(map[string]paperDownload{"x": {Name: "x.jar"}}); ok {
		t.Fatal("choosePaperDownload accepted a download without a URL")
	}
}
