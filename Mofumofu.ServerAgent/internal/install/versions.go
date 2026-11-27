// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package install

import (
	"context"
	"strconv"
	"strings"
)

// Core identifies a supported server core. Values match server.json "core".
type Core string

const (
	CoreVanilla  Core = "vanilla"
	CorePaper    Core = "paper"
	CoreFabric   Core = "fabric"
	CoreNeoForge Core = "neoforge"
)

// ParseCore returns the Core for value, or an invalid-request error.
func ParseCore(value string) (Core, error) {
	switch Core(value) {
	case CoreVanilla, CorePaper, CoreFabric, CoreNeoForge:
		return Core(value), nil
	default:
		return "", invalidf("core must be vanilla, paper, fabric, or neoforge")
	}
}

type mojangVersion struct {
	ID   string `json:"id"`
	Type string `json:"type"`
	URL  string `json:"url"`
}

type mojangManifest struct {
	Versions []mojangVersion `json:"versions"`
}

type paperDownload struct {
	Name string `json:"name"`
	URL  string `json:"url"`
}

type paperBuild struct {
	ID        int                      `json:"id"`
	Downloads map[string]paperDownload `json:"downloads"`
}

type fabricGame struct {
	Version string `json:"version"`
	Stable  bool   `json:"stable"`
}

type fabricLoader struct {
	Loader struct {
		Version string `json:"version"`
	} `json:"loader"`
}

type mavenVersions struct {
	Versions []string `json:"versions"`
}

// ListMinecraftVersions returns release IDs in manifest order, filtered to the
// versions that the core's upstream supports.
func (c *Client) ListMinecraftVersions(ctx context.Context, core Core) ([]string, error) {
	var manifest mojangManifest
	if err := c.getJSON(ctx, c.endpoints.MojangManifest, &manifest); err != nil {
		return nil, err
	}
	releases := make([]string, 0, len(manifest.Versions))
	for _, version := range manifest.Versions {
		if version.Type == "release" {
			releases = append(releases, version.ID)
		}
	}
	if core == CoreVanilla {
		return releases, nil
	}
	supported, err := c.supportedMinecraftVersions(ctx, core)
	if err != nil {
		return nil, err
	}
	result := make([]string, 0, len(releases))
	for _, release := range releases {
		if supported[release] {
			result = append(result, release)
		}
	}
	return result, nil
}

func (c *Client) supportedMinecraftVersions(ctx context.Context, core Core) (map[string]bool, error) {
	switch core {
	case CorePaper:
		var project struct {
			Versions map[string][]string `json:"versions"`
		}
		if err := c.getJSON(ctx, c.endpoints.PaperAPI+"/projects/paper", &project); err != nil {
			return nil, err
		}
		supported := make(map[string]bool)
		for _, group := range project.Versions {
			for _, version := range group {
				supported[version] = true
			}
		}
		return supported, nil
	case CoreFabric:
		var games []fabricGame
		if err := c.getJSON(ctx, c.endpoints.FabricMeta+"/versions/game", &games); err != nil {
			return nil, err
		}
		supported := make(map[string]bool)
		for _, game := range games {
			if game.Stable && game.Version != "" {
				supported[game.Version] = true
			}
		}
		return supported, nil
	case CoreNeoForge:
		versions, err := c.neoForgeVersions(ctx)
		if err != nil {
			return nil, err
		}
		supported := make(map[string]bool)
		for _, version := range versions {
			parts := strings.Split(version, ".")
			if len(parts) < 2 {
				continue
			}
			minecraft := parts[0] + "." + parts[1]
			if major, err := strconv.Atoi(parts[0]); err != nil || major < 26 {
				minecraft = "1." + minecraft
			}
			supported[minecraft] = true
		}
		return supported, nil
	default:
		return nil, invalidf("core must be vanilla, paper, fabric, or neoforge")
	}
}

func (c *Client) neoForgeVersions(ctx context.Context) ([]string, error) {
	var versions mavenVersions
	if err := c.getJSON(ctx, c.endpoints.NeoForgeMaven+"/api/maven/versions/releases/net/neoforged/neoforge", &versions); err != nil {
		return nil, err
	}
	return versions.Versions, nil
}

// ListCoreVersions returns core build IDs for minecraftVersion, newest first where upstream is ordered.
func (c *Client) ListCoreVersions(ctx context.Context, core Core, minecraftVersion string) ([]string, error) {
	if err := ValidateVersionID(minecraftVersion, "minecraftVersion"); err != nil {
		return nil, err
	}
	switch core {
	case CoreVanilla:
		return []string{minecraftVersion}, nil
	case CorePaper:
		var builds []paperBuild
		if err := c.getJSON(ctx, c.endpoints.PaperAPI+"/projects/paper/versions/"+minecraftVersion+"/builds", &builds); err != nil {
			return nil, err
		}
		ids := make([]string, 0, len(builds))
		for _, build := range builds {
			ids = append(ids, strconv.Itoa(build.ID))
		}
		return ids, nil
	case CoreFabric:
		var loaders []fabricLoader
		if err := c.getJSON(ctx, c.endpoints.FabricMeta+"/versions/loader/"+minecraftVersion, &loaders); err != nil {
			return nil, err
		}
		versions := make([]string, 0, len(loaders))
		for _, loader := range loaders {
			if loader.Loader.Version != "" {
				versions = append(versions, loader.Loader.Version)
			}
		}
		return versions, nil
	case CoreNeoForge:
		prefix, err := NeoForgeVersionPrefix(minecraftVersion)
		if err != nil {
			return nil, err
		}
		versions, err := c.neoForgeVersions(ctx)
		if err != nil {
			return nil, err
		}
		matched := make([]string, 0)
		for _, version := range versions {
			if strings.HasPrefix(version, prefix) {
				matched = append([]string{version}, matched...)
			}
		}
		return matched, nil
	default:
		return nil, invalidf("core must be vanilla, paper, fabric, or neoforge")
	}
}
