// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package server

import (
	"context"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"runtime"
	"sort"
	"strconv"
	"strings"
	"time"
)

var javaVersionPattern = regexp.MustCompile(`version\s+"?(\d+)(?:\.(\d+))?`)
var digitRunPattern = regexp.MustCompile(`\d+`)

func RequiredJavaMajor(version string) *int {
	parts := digitRunPattern.FindAllString(version, -1)
	if len(parts) == 0 {
		return nil
	}
	major, _ := strconv.Atoi(parts[0])
	minor := 0
	patch := 0
	if len(parts) > 1 {
		minor, _ = strconv.Atoi(parts[1])
	}
	if len(parts) > 2 {
		patch, _ = strconv.Atoi(parts[2])
	}
	required := 0
	switch {
	case major == 1 && minor >= 21:
		required = 21
	case major == 1 && minor == 20 && patch >= 5:
		required = 21
	case major == 1 && minor == 20:
		required = 17
	case major == 1 && minor >= 18:
		required = 17
	case major == 1 && minor == 17:
		required = 16
	case major == 1:
		required = 8
	case major >= 26:
		required = 25
	default:
		return nil
	}
	return &required
}

type javaCandidate struct {
	path      string
	preferred bool
	order     int
	major     int
}

// ResolveJava mirrors the Linux-relevant MofuCore candidate order and selection
// rules. extraCandidates represents launcher-managed/Yaml runtimes when known.
func ResolveJava(ctx context.Context, configured, mcVersion string, environ []string, extraCandidates []string) (string, error) {
	var candidates []javaCandidate
	add := func(path string, preferred bool) {
		if strings.TrimSpace(path) == "" {
			return
		}
		if strings.ContainsAny(path, `/\`) && !filepath.IsAbs(path) {
			path = filepath.Clean(path)
		}
		if executable(path) {
			candidates = append(candidates, javaCandidate{path: path, preferred: preferred, order: len(candidates)})
		}
	}
	add(configured, true)
	for _, entry := range environ {
		if strings.HasPrefix(entry, "MOFUMOFU_JAVA=") {
			add(strings.TrimPrefix(entry, "MOFUMOFU_JAVA="), true)
		}
	}
	for _, path := range extraCandidates {
		add(path, false)
	}
	for _, entry := range environ {
		for _, name := range []string{"JAVA_HOME=", "JDK_HOME="} {
			if strings.HasPrefix(entry, name) {
				add(filepath.Join(strings.TrimPrefix(entry, name), "bin", "java"), false)
			}
		}
	}
	for _, root := range []string{"/usr/lib/jvm", "/opt"} {
		_ = filepath.WalkDir(root, func(path string, entry os.DirEntry, err error) error {
			if err != nil {
				return nil
			}
			if entry.IsDir() {
				return nil
			}
			if entry.Name() == "java" && executable(path) {
				add(path, false)
			}
			return nil
		})
	}
	if path, err := exec.LookPath("java"); err == nil {
		add(path, false)
	}
	for _, path := range []string{"/usr/bin/java", "/usr/local/bin/java", "/opt/java/bin/java", "/opt/java/openjdk/bin/java"} {
		add(path, false)
	}
	if len(candidates) == 0 {
		return "", fmt.Errorf("no Java executable was found")
	}
	required := RequiredJavaMajor(mcVersion)
	if required == nil {
		return candidates[0].path, nil
	}
	for index := range candidates {
		major, err := probeJavaMajor(ctx, candidates[index].path)
		if err != nil {
			continue
		}
		candidates[index].major = major
	}
	usable := candidates[:0]
	for _, candidate := range candidates {
		if candidate.major > 0 {
			usable = append(usable, candidate)
		}
	}
	sort.SliceStable(usable, func(i, j int) bool {
		if usable[i].major != usable[j].major {
			return usable[i].major < usable[j].major
		}
		if usable[i].preferred != usable[j].preferred {
			return usable[i].preferred
		}
		return usable[i].order < usable[j].order
	})
	for _, candidate := range usable {
		if candidate.major == *required {
			return candidate.path, nil
		}
	}
	for _, candidate := range usable {
		if candidate.preferred && candidate.major >= *required {
			return candidate.path, nil
		}
	}
	for _, candidate := range usable {
		if candidate.major >= *required {
			return candidate.path, nil
		}
	}
	return "", fmt.Errorf("Java %d or newer is required for Minecraft %s", *required, mcVersion)
}

func probeJavaMajor(ctx context.Context, path string) (int, error) {
	probeCtx, cancel := context.WithTimeout(ctx, 3*time.Second)
	defer cancel()
	command := exec.CommandContext(probeCtx, path, "-version")
	output, err := command.CombinedOutput()
	if err != nil {
		return 0, err
	}
	match := javaVersionPattern.FindStringSubmatch(string(output))
	if len(match) < 2 {
		return 0, fmt.Errorf("java version was not recognized")
	}
	major, _ := strconv.Atoi(match[1])
	if major == 1 && len(match) > 2 && match[2] != "" {
		major, _ = strconv.Atoi(match[2])
	}
	return major, nil
}

func executable(path string) bool {
	info, err := os.Stat(path)
	if err != nil || info.IsDir() {
		return false
	}
	return runtime.GOOS == "windows" || info.Mode().Perm()&0o111 != 0
}

// BuildJavaArguments returns the JVM arguments for an instance directory.
func BuildJavaArguments(directory string, config ServerConfig) ([]string, error) {
	args := make([]string, 0, len(config.ExtraJavaArgs)+4)
	if config.MemoryMB > 0 {
		args = append(args, fmt.Sprintf("-Xmx%dM", config.MemoryMB))
	}
	if config.MemoryMinMB > 0 {
		args = append(args, fmt.Sprintf("-Xms%dM", config.MemoryMinMB))
	}
	args = append(args, config.ExtraJavaArgs...)
	switch config.Core {
	case CoreVanilla:
		args = append(args, "-jar", "server.jar", "nogui")
	case CorePaper:
		jar := "paper.jar"
		if info, err := os.Stat(filepath.Join(directory, "paper.jar")); err != nil || info.IsDir() {
			matches, _ := filepath.Glob(filepath.Join(directory, "paper*.jar"))
			sort.SliceStable(matches, func(i, j int) bool {
				left, _ := os.Stat(matches[i])
				right, _ := os.Stat(matches[j])
				if left != nil && right != nil && left.Size() != right.Size() {
					return left.Size() > right.Size()
				}
				return matches[i] < matches[j]
			})
			if len(matches) > 0 {
				jar = filepath.Base(matches[0])
			}
		}
		args = append(args, "-jar", jar, "nogui")
	case CoreFabric:
		args = append(args, "-jar", "fabric-server.jar", "nogui")
	case CoreNeoForge:
		base := filepath.Join("libraries", "net", "neoforged", "neoforge", config.CoreVersion)
		for _, name := range []string{"win_args.txt", "unix_args.txt"} {
			if info, err := os.Stat(filepath.Join(directory, filepath.FromSlash(base), name)); err == nil && !info.IsDir() {
				return []string{"@" + filepath.Join(filepath.FromSlash(base), name)}, nil
			}
		}
		return nil, fmt.Errorf("NeoForge argument file was not found")
	default:
		return nil, fmt.Errorf("unsupported server core %q", config.Core)
	}
	return args, nil
}
