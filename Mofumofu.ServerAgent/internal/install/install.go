// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package install

import (
	"bytes"
	"context"
	"errors"
	"io/fs"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"sort"
	"strconv"
	"strings"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/config"
)

const (
	defaultPort       = 25565
	defaultMaxPlayers = 20
	defaultMOTD       = "Mofumofu Server"
	installerTailSize = 64 << 10
)

// Request describes one install. JavaPath comes from the Agent config only.
type Request struct {
	RootDirectory    string
	InstanceID       string
	Core             Core
	MinecraftVersion string
	CoreVersion      string
	AcceptEULA       bool
	Port             int
	MaxPlayers       int
	MOTD             string
	JavaPath         string
}

// Result describes the files written by a successful install.
type Result struct {
	ServerDirectory  string
	CoreJarPath      string
	StartScriptPath  string
	MinecraftVersion string
	CoreVersion      string
}

// ReportFunc receives a 0-100 percentage and a short stage message.
type ReportFunc func(percent float64, message string)

// CheckTarget returns a conflict unless <root>/<instanceID> is absent or an
// empty directory. It is advisory; Install repeats the check authoritatively.
func CheckTarget(root, instanceID string) error {
	if err := config.ValidateInstanceID(instanceID); err != nil {
		return invalidf("instance ID is invalid")
	}
	target := filepath.Join(root, instanceID)
	info, err := os.Lstat(target)
	if errors.Is(err, fs.ErrNotExist) {
		return nil
	}
	if err != nil {
		return failedf("could not inspect the instance directory")
	}
	if !info.IsDir() || info.Mode()&fs.ModeSymlink != 0 {
		return conflictf("the instance path is not a directory")
	}
	entries, err := os.ReadDir(target)
	if err != nil {
		return failedf("could not inspect the instance directory")
	}
	if len(entries) > 0 {
		return conflictf("the instance directory is not empty")
	}
	return nil
}

// Install creates <RootDirectory>/<InstanceID> and writes a complete server
// layout. server.json is written last, so an interrupted install is not
// loadable. Any failure removes the files this call created.
func (c *Client) Install(ctx context.Context, request Request, report ReportFunc) (Result, error) {
	if report == nil {
		report = func(float64, string) {}
	}
	if !request.AcceptEULA {
		return Result{}, invalidf("the Minecraft EULA must be accepted")
	}
	if request.RootDirectory == "" {
		return Result{}, invalidf("the install root directory is not configured")
	}
	if err := config.ValidateInstanceID(request.InstanceID); err != nil {
		return Result{}, invalidf("instance ID is invalid")
	}
	if _, err := ParseCore(string(request.Core)); err != nil {
		return Result{}, err
	}
	if err := ValidateVersionID(request.MinecraftVersion, "minecraftVersion"); err != nil {
		return Result{}, err
	}
	if request.CoreVersion != "" {
		if err := ValidateVersionID(request.CoreVersion, "coreVersion"); err != nil {
			return Result{}, err
		}
	}
	port := request.Port
	if port <= 0 || port > 65535 {
		port = defaultPort
	}
	maxPlayers := request.MaxPlayers
	if maxPlayers <= 0 {
		maxPlayers = defaultMaxPlayers
	}
	motd := request.MOTD
	if strings.TrimSpace(motd) == "" {
		motd = defaultMOTD
	}

	root, err := prepareRoot(request.RootDirectory)
	if err != nil {
		return Result{}, err
	}
	target, created, err := claimInstanceDirectory(root, request.InstanceID)
	if err != nil {
		return Result{}, err
	}
	succeeded := false
	defer func() {
		if !succeeded {
			cleanupInstanceDirectory(target, created)
		}
	}()

	result := Result{
		ServerDirectory:  target,
		MinecraftVersion: request.MinecraftVersion,
		CoreVersion:      request.CoreVersion,
	}
	javaPath := resolveJavaPath(request.JavaPath)
	report(1, "Downloading server core")
	if err := c.installCore(ctx, request, target, javaPath, &result, report); err != nil {
		return Result{}, err
	}

	report(90, "Writing server files")
	if err := WriteEULA(target); err != nil {
		return Result{}, err
	}
	if err := WriteInitialProperties(target, port, maxPlayers, motd); err != nil {
		return Result{}, err
	}
	scriptPath, err := WriteStartScript(target, request.Core, result.CoreVersion, filepath.Base(result.CoreJarPath), javaPath)
	if err != nil {
		return Result{}, err
	}
	result.StartScriptPath = scriptPath
	if err := WriteServerConfig(target, ServerConfig{
		ID:            request.InstanceID,
		Name:          request.InstanceID,
		Core:          string(request.Core),
		CoreVersion:   result.CoreVersion,
		MCVersion:     request.MinecraftVersion,
		Port:          port,
		MaxPlayers:    maxPlayers,
		JavaPath:      request.JavaPath,
		ExtraJavaArgs: []string{},
	}); err != nil {
		return Result{}, err
	}
	succeeded = true
	report(100, "Server installed")
	return result, nil
}

func (c *Client) installCore(ctx context.Context, request Request, target, javaPath string, result *Result, report ReportFunc) error {
	switch request.Core {
	case CoreVanilla:
		return c.installVanilla(ctx, request.MinecraftVersion, target, result, report)
	case CorePaper:
		return c.installPaper(ctx, request, target, result, report)
	case CoreFabric:
		return c.installFabric(ctx, request, target, result, report)
	case CoreNeoForge:
		return c.installNeoForge(ctx, request, target, javaPath, result, report)
	default:
		return invalidf("core must be vanilla, paper, fabric, or neoforge")
	}
}

func (c *Client) installVanilla(ctx context.Context, minecraftVersion, target string, result *Result, report ReportFunc) error {
	var manifest mojangManifest
	if err := c.getJSON(ctx, c.endpoints.MojangManifest, &manifest); err != nil {
		return err
	}
	metadataURL := ""
	for _, version := range manifest.Versions {
		if strings.EqualFold(version.ID, minecraftVersion) {
			metadataURL = version.URL
			break
		}
	}
	if metadataURL == "" {
		return invalidf("Minecraft version was not found")
	}
	var metadata struct {
		Downloads struct {
			Server *struct {
				URL  string `json:"url"`
				SHA1 string `json:"sha1"`
				Size int64  `json:"size"`
			} `json:"server"`
		} `json:"downloads"`
	}
	if err := c.getJSON(ctx, metadataURL, &metadata); err != nil {
		return err
	}
	serverDownload := metadata.Downloads.Server
	if serverDownload == nil || serverDownload.URL == "" {
		return failedf("version metadata has no server download")
	}
	jar := filepath.Join(target, ServerJarName)
	if err := c.downloadFile(ctx, serverDownload.URL, jar, serverDownload.Size, serverDownload.SHA1, downloadReporter(report, "Downloading server.jar", 1, 80)); err != nil {
		return err
	}
	result.CoreJarPath = jar
	return nil
}

func (c *Client) installPaper(ctx context.Context, request Request, target string, result *Result, report ReportFunc) error {
	buildRef := "latest"
	if result.CoreVersion != "" {
		buildRef = result.CoreVersion
	}
	var build paperBuild
	if err := c.getJSON(ctx, c.endpoints.PaperAPI+"/projects/paper/versions/"+request.MinecraftVersion+"/builds/"+buildRef, &build); err != nil {
		return err
	}
	download, ok := choosePaperDownload(build.Downloads)
	if !ok {
		return failedf("Paper build has no download")
	}
	if result.CoreVersion == "" {
		result.CoreVersion = strconv.Itoa(build.ID)
	}
	name := strings.TrimSpace(download.Name)
	if name == "" {
		name = "paper.jar"
	}
	jar := filepath.Join(target, SanitizeFileName(name))
	if err := c.downloadFile(ctx, download.URL, jar, 0, "", downloadReporter(report, "Downloading "+filepath.Base(jar), 1, 80)); err != nil {
		return err
	}
	result.CoreJarPath = jar
	return nil
}

func choosePaperDownload(downloads map[string]paperDownload) (paperDownload, bool) {
	if preferred, ok := downloads["server:default"]; ok && preferred.URL != "" {
		return preferred, true
	}
	keys := make([]string, 0, len(downloads))
	for key := range downloads {
		keys = append(keys, key)
	}
	sort.Strings(keys)
	for _, key := range keys {
		if downloads[key].URL != "" {
			return downloads[key], true
		}
	}
	return paperDownload{}, false
}

func (c *Client) installFabric(ctx context.Context, request Request, target string, result *Result, report ReportFunc) error {
	var loaders []fabricLoader
	if err := c.getJSON(ctx, c.endpoints.FabricMeta+"/versions/loader/"+request.MinecraftVersion, &loaders); err != nil {
		return err
	}
	if len(loaders) == 0 || loaders[0].Loader.Version == "" {
		return failedf("Fabric has no loader for this Minecraft version")
	}
	firstLoader := loaders[0].Loader.Version
	var installers []struct {
		Version string `json:"version"`
	}
	if err := c.getJSON(ctx, c.endpoints.FabricMeta+"/versions/installer", &installers); err != nil {
		return err
	}
	if len(installers) == 0 || installers[0].Version == "" {
		return failedf("Fabric has no installer version")
	}
	if result.CoreVersion == "" {
		result.CoreVersion = firstLoader
	}
	if err := ValidateVersionID(result.CoreVersion, "coreVersion"); err != nil {
		return err
	}
	if err := ValidateVersionID(installers[0].Version, "installer version"); err != nil {
		return failedf("Fabric returned an invalid installer version")
	}
	url := c.endpoints.FabricMeta + "/versions/loader/" + request.MinecraftVersion + "/" +
		result.CoreVersion + "/" + installers[0].Version + "/server/jar"
	jar := filepath.Join(target, "fabric-server.jar")
	if err := c.downloadFile(ctx, url, jar, 0, "", downloadReporter(report, "Downloading fabric-server.jar", 1, 80)); err != nil {
		return err
	}
	result.CoreJarPath = jar
	return nil
}

func (c *Client) installNeoForge(ctx context.Context, request Request, target, javaPath string, result *Result, report ReportFunc) error {
	if result.CoreVersion == "" {
		prefix, err := NeoForgeVersionPrefix(request.MinecraftVersion)
		if err != nil {
			return err
		}
		versions, err := c.neoForgeVersions(ctx)
		if err != nil {
			return err
		}
		latest := ""
		for _, version := range versions {
			if strings.HasPrefix(version, prefix) {
				latest = version
			}
		}
		if latest == "" {
			return invalidf("NeoForge has no release for this Minecraft version")
		}
		result.CoreVersion = latest
	}
	if err := ValidateVersionID(result.CoreVersion, "coreVersion"); err != nil {
		return err
	}
	version := result.CoreVersion
	installerPath := filepath.Join(target, "neoforge-installer.jar")
	installerURL := c.endpoints.NeoForgeMaven + "/releases/net/neoforged/neoforge/" + version +
		"/neoforge-" + version + "-installer.jar"
	if err := c.downloadFile(ctx, installerURL, installerPath, 0, "", downloadReporter(report, "Downloading the NeoForge installer", 1, 70)); err != nil {
		return err
	}

	report(75, "Running the NeoForge installer (this can take several minutes)")
	if err := runNeoForgeInstaller(ctx, javaPath, target); err != nil {
		return err
	}

	argsDirectory := filepath.Join(target, "libraries", "net", "neoforged", "neoforge", version)
	argsFile := ""
	for _, name := range []string{"win_args.txt", "unix_args.txt"} {
		candidate := filepath.Join(argsDirectory, name)
		if info, err := os.Stat(candidate); err == nil && !info.IsDir() {
			argsFile = candidate
			break
		}
	}
	if argsFile == "" {
		return failedf("NeoForge installation finished but no launch argument file was found")
	}
	_ = os.Remove(installerPath)
	result.CoreJarPath = argsFile
	return nil
}

func runNeoForgeInstaller(ctx context.Context, javaPath, directory string) error {
	runContext, cancel := context.WithTimeout(ctx, NeoForgeInstallerTimeout)
	defer cancel()
	output := &tailBuffer{limit: installerTailSize}
	command := exec.CommandContext(runContext, javaPath, "-jar", "neoforge-installer.jar", "--installServer")
	command.Dir = directory
	command.Stdout = output
	command.Stderr = output
	if err := command.Run(); err != nil {
		if ctxErr := ctx.Err(); ctxErr != nil {
			return ctxErr
		}
		if errors.Is(runContext.Err(), context.DeadlineExceeded) {
			return failedf("NeoForge installer timed out")
		}
		var exitErr *exec.ExitError
		if errors.As(err, &exitErr) {
			return failedf("NeoForge installer failed:\n" + tailLines(output.String(), 20))
		}
		return failedf("NeoForge installer could not be started")
	}
	return nil
}

// tailBuffer keeps the most recent bytes of installer output.
type tailBuffer struct {
	buffer bytes.Buffer
	limit  int
}

func (t *tailBuffer) Write(payload []byte) (int, error) {
	t.buffer.Write(payload)
	if extra := t.buffer.Len() - t.limit; extra > 0 {
		kept := append([]byte(nil), t.buffer.Bytes()[extra:]...)
		t.buffer.Reset()
		t.buffer.Write(kept)
	}
	return len(payload), nil
}

func (t *tailBuffer) String() string {
	return t.buffer.String()
}

func tailLines(text string, count int) string {
	lines := strings.Split(strings.TrimSpace(text), "\n")
	if len(lines) > count {
		lines = lines[len(lines)-count:]
	}
	return strings.Join(lines, "\n")
}

func downloadReporter(report ReportFunc, label string, low, high float64) func(done, total int64) {
	return func(done, total int64) {
		if total <= 0 {
			return
		}
		if done > total {
			done = total
		}
		report(low+(high-low)*float64(done)/float64(total), label)
	}
}

func prepareRoot(root string) (string, error) {
	absolute, err := filepath.Abs(root)
	if err != nil {
		return "", failedf("install root directory is invalid")
	}
	if err := os.MkdirAll(absolute, 0o750); err != nil {
		return "", failedf("could not create the install root directory")
	}
	resolved, err := filepath.EvalSymlinks(absolute)
	if err != nil {
		return "", failedf("could not resolve the install root directory")
	}
	return resolved, nil
}

func claimInstanceDirectory(root, instanceID string) (string, bool, error) {
	target := filepath.Join(root, instanceID)
	if relative, err := filepath.Rel(root, target); err != nil || relative != instanceID {
		return "", false, forbiddenf("the instance directory escapes the install root")
	}
	err := os.Mkdir(target, 0o750)
	if err == nil {
		return target, true, nil
	}
	if !errors.Is(err, fs.ErrExist) {
		return "", false, failedf("could not create the instance directory")
	}
	if err := CheckTarget(root, instanceID); err != nil {
		return "", false, err
	}
	return target, false, nil
}

func cleanupInstanceDirectory(target string, created bool) {
	if created {
		_ = os.RemoveAll(target)
		return
	}
	entries, err := os.ReadDir(target)
	if err != nil {
		return
	}
	for _, entry := range entries {
		_ = os.RemoveAll(filepath.Join(target, entry.Name()))
	}
}

func resolveJavaPath(configured string) string {
	if strings.TrimSpace(configured) != "" {
		return configured
	}
	if home := os.Getenv("JAVA_HOME"); home != "" {
		name := "java"
		if runtime.GOOS == "windows" {
			name = "java.exe"
		}
		candidate := filepath.Join(home, "bin", name)
		if info, err := os.Stat(candidate); err == nil && !info.IsDir() {
			return candidate
		}
	}
	return "java"
}
