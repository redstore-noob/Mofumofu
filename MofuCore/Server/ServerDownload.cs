using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using MofuCore.Download.Minecraft;

using MofuCore.Download;

using MofuCore;

namespace MofuCore.Server;

/// <summary>
/// 目前支持paper，vanilla，fabric，neoforge四种服务器的直接下载与安装。
/// </summary>
public sealed class ServerDownload
{
    /// <summary>服务端核心类型。</summary>
    public enum CoreKind
    {
        Vanilla,
        Paper,
        Fabric,
        NeoForge,
    }

    /// <summary>安装参数。</summary>
    public sealed class InstallOptions
    {
        public CoreKind Core { get; set; } = CoreKind.Vanilla;
        public string MinecraftVersion { get; set; } = string.Empty;
        /// <summary>Paper 构建号 / Fabric Loader 版本 / NeoForge 版本；空串表示最新。</summary>
        public string CoreVersion { get; set; } = string.Empty;
        public string ServerDirectory { get; set; } = string.Empty;
        public bool AcceptEula { get; set; }
        public int Port { get; set; } = 25565;
        public int MaxPlayers { get; set; } = 20;
        public string JavaPath { get; set; } = string.Empty;
        public string Motd { get; set; } = "Mofumofu Server";
    }

    /// <summary>安装结果。</summary>
    public sealed class InstallResult
    {
        public string ServerDirectory { get; set; } = string.Empty;
        public string CoreJarPath { get; set; } = string.Empty;
        public string StartScriptPath { get; set; } = string.Empty;
        public string MinecraftVersion { get; set; } = string.Empty;
        public string CoreVersion { get; set; } = string.Empty;
        public List<string> Warnings { get; } = [];
    }

    /// <summary>安装/下载进度。</summary>
    public sealed record InstallProgress(int Stage, string Detail, long DownloadedBytes = 0, long TotalBytes = 0)
    {
        public double Percentage => TotalBytes <= 0 ? 0 : Math.Clamp(DownloadedBytes * 100.0 / TotalBytes, 0, 100);
    }

    public const string EulaUrl = "https://aka.ms/MinecraftEULA";
    public const string ServerJarName = "server.jar";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly HttpClient Http = CreateClient();
    private static readonly TimeSpan MinecraftVersionCacheTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan CoreVersionCacheTtl = TimeSpan.FromMinutes(30);
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static HttpClient CreateClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MofuCore/" + MofuCoreInfo.Version);
        return client;
    }

    // ---------------------------------------------------------------- 版本列表

    /// <summary>某核心支持的 MC 版本（新→旧，仅正式版）。</summary>
    public static async Task<IReadOnlyList<string>> ListMinecraftVersionsAsync(CoreKind core, CancellationToken cancellationToken = default)
    {
        List<string> releases = await GetMojangReleasesAsync(cancellationToken).ConfigureAwait(false);
        if (core == CoreKind.Vanilla) return releases;

        IReadOnlyDictionary<string, bool> supported = core switch
        {
            CoreKind.Paper => await GetPaperSupportedAsync(cancellationToken).ConfigureAwait(false),
            CoreKind.Fabric => await GetFabricSupportedAsync(cancellationToken).ConfigureAwait(false),
            CoreKind.NeoForge => await GetNeoForgeSupportedAsync(cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException("不支持的服务器核心：" + core),
        };

        List<string> result = [];
        foreach (string version in releases)
        {
            if (supported.TryGetValue(version, out bool isSupported) && isSupported) result.Add(version);
        }
        return result;
    }

    /// <summary>某核心在指定 MC 版本下的全部服务端版本（新→旧）。</summary>
    public static Task<IReadOnlyList<string>> ListCoreVersionsAsync(CoreKind core, string minecraftVersion, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion)) throw new InvalidOperationException("minecraftVersion 不能为空。");
        string key = $"core:{core}:{minecraftVersion}";
        return FetchCachedAsync<IReadOnlyList<string>>(key, CoreVersionCacheTtl, async () =>
        {
            switch (core)
            {
                case CoreKind.Vanilla:
                    return new List<string> { minecraftVersion };

                case CoreKind.Paper:
                {
                    List<PaperBuildPayload>? builds = await HttpGetJsonAsync<List<PaperBuildPayload>>(
                        $"https://fill.papermc.io/v3/projects/paper/versions/{minecraftVersion}/builds", cancellationToken).ConfigureAwait(false);
                    List<string> ids = [];
                    foreach (PaperBuildPayload build in builds ?? []) ids.Add(build.Id.ToString());
                    return ids;
                }

                case CoreKind.Fabric:
                {
                    List<FabricLoaderPayload>? loaders = await HttpGetJsonAsync<List<FabricLoaderPayload>>(
                        $"https://meta.fabricmc.net/v2/versions/loader/{minecraftVersion}", cancellationToken).ConfigureAwait(false);
                    List<string> versions = [];
                    foreach (FabricLoaderPayload loader in loaders ?? [])
                    {
                        if (!string.IsNullOrWhiteSpace(loader.Loader?.Version)) versions.Add(loader.Loader.Version);
                    }
                    return versions;
                }

                case CoreKind.NeoForge:
                {
                    string prefix = NeoForgeVersionPrefix(minecraftVersion);
                    MavenVersionsPayload? versions = await HttpGetJsonAsync<MavenVersionsPayload>(
                        "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge", cancellationToken).ConfigureAwait(false);
                    List<string> matched = [];
                    foreach (string version in versions?.Versions ?? [])
                    {
                        if (version.StartsWith(prefix, StringComparison.Ordinal)) matched.Add(version);
                    }
                    matched.Reverse();
                    return matched;
                }

                default:
                    throw new InvalidOperationException("不支持的服务器核心：" + core);
            }
        }, cancellationToken);
    }

    private static async Task<List<string>> GetMojangReleasesAsync(CancellationToken cancellationToken)
    {
        return await FetchCachedAsync("mojang-releases", MinecraftVersionCacheTtl, async () =>
        {
            MinecraftManifest manifest = new();
            IReadOnlyList<MinecraftVersion> versions = await manifest.GetVersionsAsync(cancellationToken).ConfigureAwait(false);
            List<string> releases = [];
            foreach (MinecraftVersion version in versions)
            {
                if (string.Equals(version.Type, "release", StringComparison.Ordinal)) releases.Add(version.Id);
            }
            return releases;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static Task<IReadOnlyDictionary<string, bool>> GetPaperSupportedAsync(CancellationToken cancellationToken)
    {
        return FetchCachedAsync<IReadOnlyDictionary<string, bool>>("paper-supported", MinecraftVersionCacheTtl, async () =>
        {
            PaperProjectPayload? project = await HttpGetJsonAsync<PaperProjectPayload>(
                "https://fill.papermc.io/v3/projects/paper", cancellationToken).ConfigureAwait(false);
            Dictionary<string, bool> supported = new(StringComparer.OrdinalIgnoreCase);
            if (project?.Versions is not null)
            {
                foreach (List<string> versions in project.Versions.Values)
                {
                    foreach (string version in versions) supported[version] = true;
                }
            }
            return supported;
        }, cancellationToken);
    }

    private static Task<IReadOnlyDictionary<string, bool>> GetFabricSupportedAsync(CancellationToken cancellationToken)
    {
        return FetchCachedAsync<IReadOnlyDictionary<string, bool>>("fabric-supported", MinecraftVersionCacheTtl, async () =>
        {
            List<FabricGamePayload>? games = await HttpGetJsonAsync<List<FabricGamePayload>>(
                "https://meta.fabricmc.net/v2/versions/game", cancellationToken).ConfigureAwait(false);
            Dictionary<string, bool> supported = new(StringComparer.OrdinalIgnoreCase);
            foreach (FabricGamePayload game in games ?? [])
            {
                if (game.Stable && !string.IsNullOrWhiteSpace(game.Version)) supported[game.Version] = true;
            }
            return supported;
        }, cancellationToken);
    }

    private static Task<IReadOnlyDictionary<string, bool>> GetNeoForgeSupportedAsync(CancellationToken cancellationToken)
    {
        return FetchCachedAsync<IReadOnlyDictionary<string, bool>>("neoforge-supported", MinecraftVersionCacheTtl, async () =>
        {
            MavenVersionsPayload? versions = await HttpGetJsonAsync<MavenVersionsPayload>(
                "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge", cancellationToken).ConfigureAwait(false);
            Dictionary<string, bool> supported = new(StringComparer.OrdinalIgnoreCase);
            foreach (string version in versions?.Versions ?? [])
            {
                string[] parts = version.Split('.');
                if (parts.Length < 2) continue;
                string minecraft = parts[0] + "." + parts[1];
                if (!int.TryParse(parts[0], out int major) || major < 26) minecraft = "1." + minecraft;
                supported[minecraft] = true;
            }
            return supported;
        }, cancellationToken);
    }

    /// <summary>MC 版本 → NeoForge maven 版本前缀（"1.21.4" → "21.4."，日期式 "26.1" → "26.1."）。</summary>
    public static string NeoForgeVersionPrefix(string minecraftVersion)
    {
        string trimmed = minecraftVersion.Trim();
        if (trimmed.StartsWith("1.", StringComparison.Ordinal)) trimmed = trimmed[2..];
        if (trimmed.Length == 0 || trimmed.StartsWith('.')) throw new InvalidOperationException("NeoForge 不支持 Minecraft " + minecraftVersion);
        return trimmed + ".";
    }

    private static async Task<T> FetchCachedAsync<T>(string key, TimeSpan ttl, Func<Task<T>> fetch, CancellationToken cancellationToken)
    {
        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out CacheEntry? cached) && DateTimeOffset.UtcNow < cached.Expires && cached.Value is T value)
            {
                return value;
            }
        }

        T fresh = await fetch().ConfigureAwait(false);
        lock (CacheGate)
        {
            Cache[key] = new CacheEntry(fresh!, DateTimeOffset.UtcNow + ttl);
        }
        return fresh;
    }

    // ---------------------------------------------------------------- 安装

    /// <summary>创建服务器目录、下载核心、写 eula.txt / server.properties / 启动脚本。</summary>
    public static async Task<InstallResult> InstallAsync(
        InstallOptions options,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.AcceptEula) throw new InvalidOperationException("未同意 Minecraft EULA，无法创建服务器。");
        if (string.IsNullOrWhiteSpace(options.ServerDirectory)) throw new InvalidOperationException("服务器目录不能为空。");
        if (string.IsNullOrWhiteSpace(options.MinecraftVersion)) throw new InvalidOperationException("Minecraft 版本不能为空。");

        InstallResult result = new()
        {
            ServerDirectory = Path.GetFullPath(options.ServerDirectory),
            MinecraftVersion = options.MinecraftVersion.Trim(),
            CoreVersion = options.CoreVersion.Trim(),
        };
        Directory.CreateDirectory(result.ServerDirectory);

        progress?.Report(new InstallProgress(1, "正在下载服务端核心"));
        switch (options.Core)
        {
            case CoreKind.Vanilla:
                await InstallVanillaAsync(result, cancellationToken).ConfigureAwait(false);
                break;
            case CoreKind.Paper:
                await InstallPaperAsync(result, progress, cancellationToken).ConfigureAwait(false);
                break;
            case CoreKind.Fabric:
                await InstallFabricAsync(result, progress, cancellationToken).ConfigureAwait(false);
                break;
            case CoreKind.NeoForge:
                await InstallNeoForgeAsync(result, options, progress, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException("不支持的服务器核心：" + options.Core);
        }

        progress?.Report(new InstallProgress(2, "正在写入 eula.txt 与 server.properties"));
        await WriteEulaAsync(result.ServerDirectory, options.AcceptEula, cancellationToken).ConfigureAwait(false);
        await WriteInitialPropertiesAsync(result.ServerDirectory, options, cancellationToken).ConfigureAwait(false);

        progress?.Report(new InstallProgress(3, "正在写入启动脚本"));
        result.StartScriptPath = await WriteStartScriptAsync(result, options, cancellationToken).ConfigureAwait(false);

        progress?.Report(new InstallProgress(4, "服务器安装完成"));
        return result;
    }

    /// <summary>仅下载核心 jar（不创建目录布局）。</summary>
    public static async Task<string> DownloadCoreAsync(
        CoreKind core,
        string minecraftVersion,
        string coreVersion,
        string targetDirectory,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        InstallResult result = new()
        {
            ServerDirectory = Path.GetFullPath(targetDirectory),
            MinecraftVersion = minecraftVersion.Trim(),
            CoreVersion = coreVersion.Trim(),
        };
        Directory.CreateDirectory(result.ServerDirectory);
        InstallOptions options = new() { Core = core, MinecraftVersion = minecraftVersion, CoreVersion = coreVersion, ServerDirectory = targetDirectory };
        switch (core)
        {
            case CoreKind.Vanilla:
                await InstallVanillaAsync(result, cancellationToken).ConfigureAwait(false);
                break;
            case CoreKind.Paper:
                await InstallPaperAsync(result, progress, cancellationToken).ConfigureAwait(false);
                break;
            case CoreKind.Fabric:
                await InstallFabricAsync(result, progress, cancellationToken).ConfigureAwait(false);
                break;
            case CoreKind.NeoForge:
                await InstallNeoForgeAsync(result, options, progress, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException("不支持的服务器核心：" + core);
        }
        return result.CoreJarPath;
    }

    // ---------------------------------------------------------------- 各核心下载

    private static async Task InstallVanillaAsync(InstallResult result, CancellationToken cancellationToken)
    {
        MinecraftManifest manifest = new();
        IReadOnlyList<MinecraftVersion> versions = await manifest.GetVersionsAsync(cancellationToken).ConfigureAwait(false);
        string? metadataUrl = null;
        foreach (MinecraftVersion version in versions)
        {
            if (string.Equals(version.Id, result.MinecraftVersion, StringComparison.OrdinalIgnoreCase))
            {
                metadataUrl = version.Url;
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(metadataUrl)) throw new InvalidOperationException($"未找到 Minecraft 版本：{result.MinecraftVersion}");

        VersionMetadataPayload? metadata = await HttpGetJsonAsync<VersionMetadataPayload>(metadataUrl, cancellationToken).ConfigureAwait(false);
        string? serverUrl = metadata?.Downloads?.Server?.Url;
        if (string.IsNullOrWhiteSpace(serverUrl)) throw new InvalidOperationException("版本元数据中没有 server 下载地址。");

        result.CoreJarPath = Path.Combine(result.ServerDirectory, ServerJarName);
        await DownloadFileAsync(serverUrl, result.CoreJarPath, null, cancellationToken).ConfigureAwait(false);
    }

    private static async Task InstallPaperAsync(InstallResult result, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        string buildRef = string.IsNullOrWhiteSpace(result.CoreVersion) ? "latest" : result.CoreVersion;
        PaperBuildPayload? build = await HttpGetJsonAsync<PaperBuildPayload>(
            $"https://fill.papermc.io/v3/projects/paper/versions/{result.MinecraftVersion}/builds/{buildRef}", cancellationToken).ConfigureAwait(false);
        if (build is null || build.Downloads is null || build.Downloads.Count == 0) throw new InvalidOperationException("Paper 构建缺少下载地址。");
        if (string.IsNullOrWhiteSpace(result.CoreVersion)) result.CoreVersion = build.Id.ToString();

        PaperDownloadPayload? download = build.Downloads.Values.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Url));
        if (download is null) throw new InvalidOperationException("Paper 构建缺少下载地址。");

        string jarName = string.IsNullOrWhiteSpace(download.Name) ? "paper.jar" : download.Name;
        result.CoreJarPath = Path.Combine(result.ServerDirectory, jarName);
        await DownloadFileAsync(download.Url, result.CoreJarPath, progress, cancellationToken).ConfigureAwait(false);
    }

    private static async Task InstallFabricAsync(InstallResult result, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        List<FabricLoaderPayload>? loaders = await HttpGetJsonAsync<List<FabricLoaderPayload>>(
            $"https://meta.fabricmc.net/v2/versions/loader/{result.MinecraftVersion}", cancellationToken).ConfigureAwait(false);
        string? firstLoader = loaders is { Count: > 0 } ? loaders[0].Loader?.Version : null;
        if (string.IsNullOrWhiteSpace(firstLoader))
        {
            throw new InvalidOperationException($"Fabric 没有 {result.MinecraftVersion} 的可用加载器。");
        }

        List<FabricInstallerPayload>? installers = await HttpGetJsonAsync<List<FabricInstallerPayload>>(
            "https://meta.fabricmc.net/v2/versions/installer", cancellationToken).ConfigureAwait(false);
        if (installers is null || installers.Count == 0 || string.IsNullOrWhiteSpace(installers[0].Version))
        {
            throw new InvalidOperationException("Fabric 没有可用的安装器版本。");
        }

        if (string.IsNullOrWhiteSpace(result.CoreVersion)) result.CoreVersion = firstLoader;
        string installerVersion = installers[0].Version;
        string url = $"https://meta.fabricmc.net/v2/versions/loader/{result.MinecraftVersion}/{result.CoreVersion}/{installerVersion}/server/jar";
        result.CoreJarPath = Path.Combine(result.ServerDirectory, "fabric-server.jar");
        await DownloadFileAsync(url, result.CoreJarPath, progress, cancellationToken).ConfigureAwait(false);
    }

    private static async Task InstallNeoForgeAsync(
        InstallResult result,
        InstallOptions options,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(result.CoreVersion))
        {
            string prefix = NeoForgeVersionPrefix(result.MinecraftVersion);
            MavenVersionsPayload? versions = await HttpGetJsonAsync<MavenVersionsPayload>(
                "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge", cancellationToken).ConfigureAwait(false);
            string? latest = null;
            foreach (string version in versions?.Versions ?? [])
            {
                if (version.StartsWith(prefix, StringComparison.Ordinal)) latest = version;
            }
            if (string.IsNullOrWhiteSpace(latest)) throw new InvalidOperationException($"NeoForge 没有 Minecraft {result.MinecraftVersion} 的可用版本。");
            result.CoreVersion = latest;
        }

        string installerPath = Path.Combine(result.ServerDirectory, "neoforge-installer.jar");
        string installerUrl = $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{result.CoreVersion}/neoforge-{result.CoreVersion}-installer.jar";
        await DownloadFileAsync(installerUrl, installerPath, progress, cancellationToken).ConfigureAwait(false);

        progress?.Report(new InstallProgress(1, "正在运行 NeoForge 安装器（可能需要数分钟）"));
        string javaPath = ResolveJavaPath(options.JavaPath);
        string output = await RunProcessAsync(javaPath, ["-jar", "neoforge-installer.jar", "--installServer"], result.ServerDirectory, TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);

        string argsDirectory = Path.Combine(result.ServerDirectory, "libraries", "net", "neoforged", "neoforge", result.CoreVersion);
        string argsFile = new[] { "win_args.txt", "unix_args.txt" }
            .Select(name => Path.Combine(argsDirectory, name))
            .FirstOrDefault(File.Exists) ?? throw new InvalidOperationException($"NeoForge 安装完成但未找到启动参数文件。{TailLines(output, 10)}");

        DeleteIfExists(installerPath);
        result.CoreJarPath = argsFile;
    }

    // ---------------------------------------------------------------- 目录布局

    /// <summary>写入 eula.txt（accepted=false 时写 eula=false 并提示）。</summary>
    public static async Task WriteEulaAsync(string serverDirectory, bool accepted, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(serverDirectory);
        string content = $"#By changing the setting below to TRUE you are indicating your agreement to our EULA ({EulaUrl}).\neula={(accepted ? "true" : "false")}\n";
        await File.WriteAllTextAsync(Path.Combine(serverDirectory, "eula.txt"), content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>写入初始 server.properties（已有文件时保留原内容，只补缺失键）。</summary>
    public static async Task WriteInitialPropertiesAsync(string serverDirectory, InstallOptions options, CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(serverDirectory, "server.properties");
        Dictionary<string, string> existing = new(StringComparer.Ordinal);
        if (File.Exists(path))
        {
            foreach (string line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (line.StartsWith('#') || !line.Contains('=')) continue;
                int index = line.IndexOf('=');
                existing[line[..index].Trim()] = line[(index + 1)..];
            }
        }

        existing.TryAdd("server-port", options.Port.ToString());
        existing.TryAdd("max-players", options.MaxPlayers.ToString());
        existing.TryAdd("motd", options.Motd);
        existing.TryAdd("online-mode", "true");

        StringBuilder builder = new();
        builder.Append("#Minecraft server properties\n");
        foreach ((string key, string value) in existing) builder.Append(key).Append('=').Append(value).Append('\n');
        await File.WriteAllTextAsync(path, builder.ToString(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>写入启动脚本（Windows 写 start.bat，非 Windows 写 start.sh）。</summary>
    public static async Task<string> WriteStartScriptAsync(InstallResult result, InstallOptions options, CancellationToken cancellationToken = default)
    {
        string jarName = Path.GetFileName(result.CoreJarPath);
        string launchArguments = options.Core switch
        {
            CoreKind.Vanilla => $"-jar {jarName} nogui",
            CoreKind.Paper => $"-jar {jarName} nogui",
            CoreKind.Fabric => $"-jar {jarName} nogui",
            CoreKind.NeoForge => $"@libraries/net/neoforged/neoforge/{result.CoreVersion}/{(OperatingSystem.IsWindows() ? "win_args.txt" : "unix_args.txt")} nogui",
            _ => $"-jar {jarName} nogui",
        };

        string javaPath = ResolveJavaPath(options.JavaPath);
        string command = $"\"{javaPath}\" -Xms2G -Xmx4G {launchArguments}";

        string scriptPath;
        if (OperatingSystem.IsWindows())
        {
            scriptPath = Path.Combine(result.ServerDirectory, "start.bat");
            string content = "@echo off\r\ncd /d \"%~dp0\"\r\n" + command + "\r\npause\r\n";
            await File.WriteAllTextAsync(scriptPath, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            scriptPath = Path.Combine(result.ServerDirectory, "start.sh");
            string content = "#!/bin/sh\ncd \"$(dirname \"$0\")\"\n" + command + "\n";
            await File.WriteAllTextAsync(scriptPath, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        }
        return scriptPath;
    }

    private static string ResolveJavaPath(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        string? javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            string candidate = Path.Combine(javaHome, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
            if (File.Exists(candidate)) return candidate;
        }
        return "java";
    }

    private static async Task<string> RunProcessAsync(string fileName, string[] arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ProcessStartInfo info = new(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using Process process = Process.Start(info) ?? throw new InvalidOperationException($"无法启动进程：{fileName}");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw new InvalidOperationException("NeoForge 安装器执行超时。");
        }

        string output = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
        if (process.ExitCode != 0) throw new InvalidOperationException($"NeoForge 安装器执行失败：\n{TailLines(output, 20)}");
        return output;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(true);
        }
        catch (Exception)
        {
            // 进程已退出
        }
    }

    private static string TailLines(string text, int count)
    {
        string[] lines = text.Trim().Split('\n');
        return string.Join("\n", lines.Length <= count ? lines : lines[^count..]);
    }

    // ---------------------------------------------------------------- HTTP

    private static async Task<T?> HttpGetJsonAsync<T>(string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)response.StatusCode}：{url}");
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static async Task DownloadFileAsync(string url, string targetPath, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"下载地址不是有效的 HTTPS URL：{url}");
        }
        string? directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        long total;
        using (HttpResponseMessage probe = await Http
                   .SendAsync(new HttpRequestMessage(HttpMethod.Head, url), cancellationToken)
                   .ConfigureAwait(false))
        {
            total = probe.Content.Headers.ContentLength ?? 0;
        }

        long downloaded = 0;
        await FastDownloader.DownloadFileAsync(
            new FastDownloadItem(url, targetPath, total),
            segments: 4,
            onBytes: bytes =>
            {
                long current = Interlocked.Add(ref downloaded, bytes);
                progress?.Report(new InstallProgress(1, $"正在下载 {Path.GetFileName(targetPath)}", current, total));
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // 清理失败不影响主流程
        }
    }

    private sealed record CacheEntry(object Value, DateTimeOffset Expires);

    private sealed class PaperProjectPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("versions")] public Dictionary<string, List<string>>? Versions { get; set; }
    }

    private sealed class PaperBuildPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")] public int Id { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("downloads")] public Dictionary<string, PaperDownloadPayload>? Downloads { get; set; }
    }

    private sealed class PaperDownloadPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    }

    private sealed class FabricGamePayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("stable")] public bool Stable { get; set; }
    }

    private sealed class FabricLoaderEntry
    {
        [System.Text.Json.Serialization.JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    }

    private sealed class FabricLoaderPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("loader")] public FabricLoaderEntry? Loader { get; set; }
    }

    private sealed class FabricInstallerPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    }

    private sealed class MavenVersionsPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("versions")] public List<string>? Versions { get; set; }
    }

    private sealed class VersionMetadataPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("downloads")] public VersionDownloadsPayload? Downloads { get; set; }
    }

    private sealed class VersionDownloadsPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("server")] public DownloadInfoPayload? Server { get; set; }
    }

    private sealed class DownloadInfoPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    }
}
