// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package install

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"slices"
	"strings"
	"testing"
)

const (
	fakeNeoForgeEnv        = "MOFUMOFU_FAKE_NEOFORGE_INSTALLER"
	fakeNeoForgeVersionEnv = "MOFUMOFU_FAKE_NEOFORGE_VERSION"
)

// TestMain lets the test binary act as a Java executable for the NeoForge
// installer path, so tests exercise the real exec and working-directory logic.
func TestMain(m *testing.M) {
	if os.Getenv(fakeNeoForgeEnv) == "1" {
		os.Exit(runFakeNeoForgeInstaller())
	}
	os.Exit(m.Run())
}

func runFakeNeoForgeInstaller() int {
	if !slices.Equal(os.Args[1:], []string{"-jar", "neoforge-installer.jar", "--installServer"}) {
		return 2
	}
	if _, err := os.Stat("neoforge-installer.jar"); err != nil {
		return 3
	}
	directory := filepath.Join("libraries", "net", "neoforged", "neoforge", os.Getenv(fakeNeoForgeVersionEnv))
	if err := os.MkdirAll(directory, 0o750); err != nil {
		return 4
	}
	for _, name := range []string{"win_args.txt", "unix_args.txt"} {
		if err := os.WriteFile(filepath.Join(directory, name), []byte("-Dfake=true\n"), 0o644); err != nil {
			return 5
		}
	}
	return 0
}

func (u *fakeUpstream) serveVanilla(version string, jar []byte) {
	u.handleJSON("/mojang/version_manifest_v2.json", fmt.Sprintf(
		`{"latest":{"release":%q,"snapshot":""},"versions":[{"id":%q,"type":"release","url":%q}]}`,
		version, version, u.base+"/mojang/meta/"+version+".json"))
	u.handleJSON("/mojang/meta/"+version+".json", fmt.Sprintf(
		`{"downloads":{"server":{"url":%q,"sha1":%q,"size":%d}}}`,
		u.base+"/files/server.jar", sha1Hex(jar), len(jar)))
	u.handleBytes("/files/server.jar", jar)
}

func readText(t *testing.T, path string) string {
	t.Helper()
	payload, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	return string(payload)
}

func startScriptName() string {
	if runtime.GOOS == "windows" {
		return "start.bat"
	}
	return "start.sh"
}

func TestInstallVanillaWritesServerLayout(t *testing.T) {
	jar := []byte("fake vanilla server")
	upstream := newFakeUpstream(t, nil)
	upstream.serveVanilla("1.21.4", jar)
	root := t.TempDir()

	result, err := upstream.client.Install(context.Background(), Request{
		RootDirectory:    root,
		InstanceID:       "survival",
		Core:             CoreVanilla,
		MinecraftVersion: "1.21.4",
		AcceptEULA:       true,
		Port:             25566,
		MaxPlayers:       12,
		MOTD:             "Hello\r\nWorld",
		JavaPath:         "java-custom",
	}, nil)
	if err != nil {
		t.Fatalf("Install() error = %v", err)
	}

	resolvedRoot, err := filepath.EvalSymlinks(root)
	if err != nil {
		t.Fatal(err)
	}
	directory := filepath.Join(resolvedRoot, "survival")
	if result.ServerDirectory != directory {
		t.Fatalf("ServerDirectory = %q, want %q", result.ServerDirectory, directory)
	}
	if got := readText(t, filepath.Join(directory, ServerJarName)); got != string(jar) {
		t.Fatalf("server.jar = %q", got)
	}
	if got := readText(t, filepath.Join(directory, "eula.txt")); !strings.Contains(got, "eula=true") {
		t.Fatalf("eula.txt = %q", got)
	}
	properties := readText(t, filepath.Join(directory, "server.properties"))
	for _, line := range []string{"server-port=25566", "max-players=12", "motd=HelloWorld", "online-mode=true"} {
		if !strings.Contains(properties, line+"\n") {
			t.Fatalf("server.properties missing %q:\n%s", line, properties)
		}
	}
	script := readText(t, filepath.Join(directory, startScriptName()))
	if !strings.Contains(script, `"java-custom" -Xms2G -Xmx4G -jar server.jar nogui`) {
		t.Fatalf("start script = %q", script)
	}
	if result.StartScriptPath != filepath.Join(directory, startScriptName()) {
		t.Fatalf("StartScriptPath = %q", result.StartScriptPath)
	}
	if runtime.GOOS != "windows" {
		info, err := os.Stat(filepath.Join(directory, "start.sh"))
		if err != nil {
			t.Fatal(err)
		}
		if info.Mode().Perm() != 0o755 {
			t.Fatalf("start.sh mode = %v", info.Mode().Perm())
		}
	}

	var config ServerConfig
	if err := json.Unmarshal([]byte(readText(t, filepath.Join(directory, "server.json"))), &config); err != nil {
		t.Fatal(err)
	}
	if config.ID != "survival" || config.Core != "vanilla" || config.MCVersion != "1.21.4" ||
		config.Port != 25566 || config.MaxPlayers != 12 || config.JavaPath != "java-custom" {
		t.Fatalf("server.json = %+v", config)
	}
}

func TestInstallPaperAndFabricDownloadTheirArtifacts(t *testing.T) {
	upstream := newFakeUpstream(t, nil)
	paperJar := []byte("paper build")
	fabricJar := []byte("fabric server")
	upstream.handleJSON("/paper/v3/projects/paper/versions/1.21.4/builds/latest", fmt.Sprintf(`{
		"id": 232,
		"downloads": {"server:default": {"name": "../paper-1.21.4-232.jar", "url": %q}}
	}`, upstream.base+"/files/paper.jar"))
	upstream.handleBytes("/files/paper.jar", paperJar)
	upstream.handleJSON("/fabric/v2/versions/loader/1.21.4", `[{"loader": {"version": "0.16.9"}}]`)
	upstream.handleJSON("/fabric/v2/versions/installer", `[{"version": "1.0.1"}]`)
	upstream.handleBytes("/fabric/v2/versions/loader/1.21.4/0.16.9/1.0.1/server/jar", fabricJar)
	root := t.TempDir()
	ctx := context.Background()

	paper, err := upstream.client.Install(ctx, Request{RootDirectory: root, InstanceID: "paper1", Core: CorePaper,
		MinecraftVersion: "1.21.4", AcceptEULA: true}, nil)
	if err != nil {
		t.Fatalf("paper install error = %v", err)
	}
	if paper.CoreVersion != "232" || filepath.Base(paper.CoreJarPath) != "paper-1.21.4-232.jar" {
		t.Fatalf("paper result = %+v", paper)
	}
	if got := readText(t, paper.CoreJarPath); got != string(paperJar) {
		t.Fatalf("paper jar = %q", got)
	}

	fabric, err := upstream.client.Install(ctx, Request{RootDirectory: root, InstanceID: "fabric1", Core: CoreFabric,
		MinecraftVersion: "1.21.4", AcceptEULA: true}, nil)
	if err != nil {
		t.Fatalf("fabric install error = %v", err)
	}
	if fabric.CoreVersion != "0.16.9" || filepath.Base(fabric.CoreJarPath) != "fabric-server.jar" {
		t.Fatalf("fabric result = %+v", fabric)
	}
	if got := readText(t, fabric.CoreJarPath); got != string(fabricJar) {
		t.Fatalf("fabric jar = %q", got)
	}
}

func TestInstallNeoForgeRunsInstallerAndUsesArgsFile(t *testing.T) {
	t.Setenv(fakeNeoForgeEnv, "1")
	t.Setenv(fakeNeoForgeVersionEnv, "21.4.156")
	installerJar := []byte("pretend neoforge installer")
	upstream := newFakeUpstream(t, nil)
	upstream.handleJSON("/neoforge/api/maven/versions/releases/net/neoforged/neoforge",
		`{"versions":["21.4.100","21.4.156","21.1.5"]}`)
	upstream.handleBytes("/neoforge/releases/net/neoforged/neoforge/21.4.156/neoforge-21.4.156-installer.jar", installerJar)
	javaPath, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	root := t.TempDir()

	result, err := upstream.client.Install(context.Background(), Request{
		RootDirectory:    root,
		InstanceID:       "neo",
		Core:             CoreNeoForge,
		MinecraftVersion: "1.21.4",
		AcceptEULA:       true,
		JavaPath:         javaPath,
	}, nil)
	if err != nil {
		t.Fatalf("neoforge install error = %v", err)
	}
	if result.CoreVersion != "21.4.156" {
		t.Fatalf("CoreVersion = %q, want the latest matching build", result.CoreVersion)
	}
	argsName := "unix_args.txt"
	if runtime.GOOS == "windows" {
		argsName = "win_args.txt"
	}
	if filepath.Base(result.CoreJarPath) != argsName {
		t.Fatalf("CoreJarPath = %q", result.CoreJarPath)
	}
	script := readText(t, filepath.Join(result.ServerDirectory, startScriptName()))
	if !strings.Contains(script, "@libraries/net/neoforged/neoforge/21.4.156/"+argsName+" nogui") {
		t.Fatalf("start script = %q", script)
	}
	if _, err := os.Stat(filepath.Join(result.ServerDirectory, "neoforge-installer.jar")); !os.IsNotExist(err) {
		t.Fatalf("installer jar was not removed: %v", err)
	}
}

func TestInstallRequiresEULAAndSafeInstanceIDs(t *testing.T) {
	upstream := newFakeUpstream(t, nil)
	upstream.serveVanilla("1.21.4", []byte("jar"))
	parent := t.TempDir()
	root := filepath.Join(parent, "installs")

	if _, err := upstream.client.Install(context.Background(), Request{RootDirectory: root, InstanceID: "survival",
		Core: CoreVanilla, MinecraftVersion: "1.21.4"}, nil); err == nil {
		t.Fatal("Install ran without EULA acceptance")
	}
	for _, id := range []string{"", ".", "..", "../escape", "a/b", `a\b`, "x..y/../z"} {
		if _, err := upstream.client.Install(context.Background(), Request{RootDirectory: root, InstanceID: id,
			Core: CoreVanilla, MinecraftVersion: "1.21.4", AcceptEULA: true}, nil); err == nil {
			t.Fatalf("Install accepted instance ID %q", id)
		}
	}
	if _, err := os.Stat(filepath.Join(parent, "escape")); !os.IsNotExist(err) {
		t.Fatalf("path traversal created a sibling directory: %v", err)
	}
}

func TestInstallLeavesExistingNonEmptyDirectoryUntouched(t *testing.T) {
	upstream := newFakeUpstream(t, nil)
	upstream.serveVanilla("1.21.4", []byte("jar"))
	root := t.TempDir()
	existing := filepath.Join(root, "survival")
	if err := os.Mkdir(existing, 0o750); err != nil {
		t.Fatal(err)
	}
	keep := filepath.Join(existing, "keep.txt")
	if err := os.WriteFile(keep, []byte("operator data"), 0o600); err != nil {
		t.Fatal(err)
	}

	_, err := upstream.client.Install(context.Background(), Request{RootDirectory: root, InstanceID: "survival",
		Core: CoreVanilla, MinecraftVersion: "1.21.4", AcceptEULA: true}, nil)
	if err == nil {
		t.Fatal("Install overwrote a non-empty directory")
	}
	if got := readText(t, keep); got != "operator data" {
		t.Fatalf("existing file changed: %q", got)
	}
	if err := CheckTarget(root, "survival"); err == nil {
		t.Fatal("CheckTarget accepted a non-empty directory")
	}
	if err := CheckTarget(root, "fresh"); err != nil {
		t.Fatalf("CheckTarget rejected a missing directory: %v", err)
	}
}

func TestInstallRollsBackFilesCreatedAfterAFailure(t *testing.T) {
	upstream := newFakeUpstream(t, nil)
	jar := []byte("jar")
	upstream.handleJSON("/mojang/version_manifest_v2.json", fmt.Sprintf(
		`{"latest":{"release":"1.21.4","snapshot":""},"versions":[{"id":"1.21.4","type":"release","url":%q}]}`,
		upstream.base+"/mojang/meta/1.21.4.json"))
	upstream.handleJSON("/mojang/meta/1.21.4.json", fmt.Sprintf(
		`{"downloads":{"server":{"url":%q,"sha1":%q,"size":%d}}}`,
		upstream.base+"/files/server.jar", strings.Repeat("0", 40), len(jar)))
	upstream.handleBytes("/files/server.jar", jar)
	root := t.TempDir()

	_, err := upstream.client.Install(context.Background(), Request{RootDirectory: root, InstanceID: "broken",
		Core: CoreVanilla, MinecraftVersion: "1.21.4", AcceptEULA: true}, nil)
	if err == nil || !strings.Contains(err.Error(), "SHA-1") {
		t.Fatalf("Install() error = %v", err)
	}
	if _, statErr := os.Stat(filepath.Join(root, "broken")); !os.IsNotExist(statErr) {
		t.Fatalf("failed install left the instance directory behind: %v", statErr)
	}
}

func TestWriteInitialPropertiesKeepsExistingKeysAndSanitizes(t *testing.T) {
	directory := t.TempDir()
	existing := "#comment\r\nmotd=Old\r\nserver-port=1000\r\nserver-port=2000\r\nnot a key\r\nlevel-name=world\n"
	if err := os.WriteFile(filepath.Join(directory, "server.properties"), []byte(existing), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := WriteInitialProperties(directory, 25570, 8, "Line\nBreak"); err != nil {
		t.Fatal(err)
	}
	want := "#Minecraft server properties\n" +
		"motd=Old\n" +
		"server-port=2000\n" +
		"level-name=world\n" +
		"max-players=8\n" +
		"online-mode=true\n"
	if got := readText(t, filepath.Join(directory, "server.properties")); got != want {
		t.Fatalf("server.properties =\n%q\nwant\n%q", got, want)
	}
}

func TestWriteStartScriptRejectsUnsafeJavaPath(t *testing.T) {
	directory := t.TempDir()
	if _, err := WriteStartScript(directory, CoreVanilla, "", ServerJarName, "java & calc"); err == nil {
		t.Fatal("WriteStartScript accepted a Java path with a shell metacharacter")
	}
	if _, err := os.Stat(filepath.Join(directory, startScriptName())); !os.IsNotExist(err) {
		t.Fatalf("unsafe script was written: %v", err)
	}
}

func TestWriteEULAWritesAcceptedState(t *testing.T) {
	directory := t.TempDir()
	if err := WriteEULA(directory); err != nil {
		t.Fatal(err)
	}
	content := readText(t, filepath.Join(directory, "eula.txt"))
	if !strings.Contains(content, "eula=true\n") || !strings.Contains(content, EulaURL) {
		t.Fatalf("eula.txt = %q", content)
	}
}
