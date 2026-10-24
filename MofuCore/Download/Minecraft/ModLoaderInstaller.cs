using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MofuCore.Config;
using MofuCore.Java;

namespace MofuCore.Download.Minecraft;

/// <summary>加载器版本目录、安装入口与安装器公共流程。</summary>
public static class ModLoaderInstaller
{
    public static string DisplayName(ModLoaderKind kind) => kind switch
    {
        ModLoaderKind.Fabric => "Fabric",
        ModLoaderKind.Quilt => "Quilt",
        ModLoaderKind.Forge => "Forge",
        ModLoaderKind.NeoForge => "NeoForge",
        ModLoaderKind.OptiFine => "OptiFine",
        _ => "原版",
    };

    /// <summary>获取指定 Minecraft 版本下可用的加载器版本（按稳定版优先、版本号降序）。</summary>
    public static Task<IReadOnlyList<ModLoaderVersionInfo>> GetVersionsAsync(
        ModLoaderKind kind,
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        return kind switch
        {
            ModLoaderKind.Fabric => FabricDownload.GetVersionsAsync(minecraftVersion, cancellationToken),
            ModLoaderKind.Quilt => QuiltLoaderDownload.GetVersionsAsync(minecraftVersion, cancellationToken),
            ModLoaderKind.Forge => ForgeDownload.GetVersionsAsync(minecraftVersion, cancellationToken),
            ModLoaderKind.NeoForge => NeoforgeLoaderDownload.GetVersionsAsync(minecraftVersion, cancellationToken),
            ModLoaderKind.OptiFine => OptifineInstaller.GetVersionsAsync(minecraftVersion, cancellationToken),
            _ => Task.FromResult<IReadOnlyList<ModLoaderVersionInfo>>([]),
        };
    }

    /// <summary>安装加载器；原版依赖会自动补装。返回 versions/ 下实际使用的目录名。</summary>
    public static Task<string> InstallAsync(
        ModLoaderVersionInfo version,
        string gameDirectory,
        string? instanceName = null,
        IProgress<ModLoaderInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        string name = string.IsNullOrWhiteSpace(instanceName)
            ? DefaultInstanceName(version.Kind, version.MinecraftVersion, version.LoaderVersion)
            : instanceName.Trim();

        return version.Kind switch
        {
            ModLoaderKind.Fabric => FabricDownload.InstallAsync(version, gameDirectory, name, progress, cancellationToken),
            ModLoaderKind.Quilt => QuiltLoaderDownload.InstallAsync(version, gameDirectory, name, progress, cancellationToken),
            ModLoaderKind.Forge => ForgeDownload.InstallAsync(version, gameDirectory, name, progress, cancellationToken),
            ModLoaderKind.NeoForge => NeoforgeLoaderDownload.InstallAsync(version, gameDirectory, name, progress, cancellationToken),
            ModLoaderKind.OptiFine => OptifineInstaller.InstallAsync(version, gameDirectory, name, progress, cancellationToken),
            _ => throw new InvalidOperationException("原版没有加载器可以安装。"),
        };
    }

    /// <summary>按元数据直接构造安装描述（整合包安装、外部实例修复用）。</summary>
    public static ModLoaderVersionInfo CreateVersion(
        ModLoaderKind kind,
        string minecraftVersion,
        string loaderVersion)
    {
        string mc = (minecraftVersion ?? string.Empty).Trim();
        string loader = (loaderVersion ?? string.Empty).Trim();
        if (mc.Length == 0 || loader.Length == 0)
        {
            throw new InvalidOperationException("Minecraft 版本与加载器版本都不能为空。");
        }

        return kind switch
        {
            ModLoaderKind.Fabric => new ModLoaderVersionInfo(
                kind, mc, loader, $"Fabric Loader {loader}", true,
                $"{FabricDownload.MetaRoot}{Uri.EscapeDataString(mc)}/{Uri.EscapeDataString(loader)}/profile/json"),
            ModLoaderKind.Quilt => new ModLoaderVersionInfo(
                kind, mc, loader, $"Quilt Loader {loader}", true,
                $"{QuiltLoaderDownload.MetaRoot}{Uri.EscapeDataString(mc)}/{Uri.EscapeDataString(loader)}/profile/json"),
            ModLoaderKind.Forge => new ModLoaderVersionInfo(
                kind, mc, loader, $"Forge {loader}", true,
                ForgeDownload.InstallerUrl(loader, mc)),
            ModLoaderKind.NeoForge => new ModLoaderVersionInfo(
                kind, mc, loader, $"NeoForge {loader}", true,
                NeoforgeLoaderDownload.InstallerUrl(loader)),
            ModLoaderKind.OptiFine => new ModLoaderVersionInfo(
                kind, mc, loader, $"OptiFine {loader}", true,
                OptifineInstaller.AdLoadUrl(loader, mc)),
            _ => throw new InvalidOperationException("原版不需要加载器描述。"),
        };
    }

    /// <summary>默认实例目录名（与各加载器 meta 里的 id 形态一致）。</summary>
    public static string DefaultInstanceName(ModLoaderKind kind, string minecraftVersion, string loaderVersion)
    {
        string mc = (minecraftVersion ?? string.Empty).Trim();
        string loader = (loaderVersion ?? string.Empty).Trim();
        if (loader.Length == 0)
        {
            return kind switch
            {
                ModLoaderKind.Fabric => $"fabric-loader-{mc}",
                ModLoaderKind.Quilt => $"quilt-loader-{mc}",
                ModLoaderKind.Forge => $"forge-{mc}",
                ModLoaderKind.NeoForge => $"neoforge-{mc}",
                ModLoaderKind.OptiFine => $"OptiFine_{mc}",
                _ => mc,
            };
        }

        return kind switch
        {
            ModLoaderKind.Fabric => $"fabric-loader-{loader}-{mc}",
            ModLoaderKind.Quilt => $"quilt-loader-{loader}-{mc}",
            ModLoaderKind.Forge => loader.StartsWith(mc + "-", StringComparison.OrdinalIgnoreCase)
                ? $"forge-{loader}"
                : $"forge-{mc}-{loader}",
            ModLoaderKind.NeoForge => $"neoforge-{loader}",
            ModLoaderKind.OptiFine => loader.StartsWith("OptiFine_", StringComparison.OrdinalIgnoreCase)
                ? loader
                : $"OptiFine_{loader}",
            _ => mc,
        };
    }

/// <summary>加载器版本排序：稳定版优先，其次按版本号数字降序。</summary>
    internal static IReadOnlyList<ModLoaderVersionInfo> SortLoaderVersions(IEnumerable<ModLoaderVersionInfo> versions)
    {
        return versions
            .OrderByDescending(version => version.IsStable)
            .ThenByDescending(version => version.LoaderVersion, Comparer<string>.Create(CompareLoaderVersion))
            .ToList();
    }

    private static int CompareLoaderVersion(string? left, string? right)
    {
        string[] a = Split(left);
        string[] b = Split(right);
        int length = Math.Max(a.Length, b.Length);
        for (int index = 0; index < length; index++)
        {
            string partA = index < a.Length ? a[index] : "0";
            string partB = index < b.Length ? b[index] : "0";

            if (long.TryParse(partA, out long numberA) && long.TryParse(partB, out long numberB))
            {
                if (numberA != numberB) return numberA.CompareTo(numberB);
                continue;
            }

            int text = string.Compare(partA, partB, StringComparison.OrdinalIgnoreCase);
            if (text != 0) return text;
        }
        return 0;
    }

    private static string[] Split(string? value) =>
        (value ?? string.Empty)
            .Split(['.', '-', '_', '+'], StringSplitOptions.RemoveEmptyEntries);

private const string LoaderTemporarySuffix = ".mofu-download";
    private static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan InstallerDownloadTimeout = TimeSpan.FromMinutes(2);

    /// <summary>原版版本 JSON 不存在时静默安装；百分比 0-45。</summary>
    public static async Task EnsureVanillaAsync(
        string gameDirectory,
        string minecraftVersion,
        IProgress<ModLoaderInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        string root = NormalizeGameDirectory(gameDirectory);
        string mc = FabricDownload.RequireMinecraftVersion(minecraftVersion);
        string jsonPath = Path.Combine(root, "versions", mc, mc + ".json");
        if (File.Exists(jsonPath))
        {
            Report(progress, 3, "检查原版", $"已找到 Minecraft {mc}");
            return;
        }

        Report(progress, 3, "准备原版", $"未安装 Minecraft {mc}，正在自动补装…");
        string? metadataUrl = await MinecraftDownload
            .FindVersionMetadataUrlAsync(mc, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(metadataUrl))
        {
            throw new InvalidOperationException($"版本清单中没有 Minecraft {mc} 的元数据地址。");
        }

        MinecraftDownload download = new(root);
        IProgress<MinecraftInstallProgress>? mapped = progress is null
            ? null
            : new Progress<MinecraftInstallProgress>(update => Report(
                progress,
                3 + Math.Clamp(update.Percentage, 0, 100) * 0.42,
                "安装原版 Minecraft",
                string.IsNullOrWhiteSpace(update.Detail)
                    ? update.StageName
                    : $"{update.StageName} · {update.Detail}",
                update.BytesPerSecond,
                update.CompletedBytes,
                update.TotalBytes));

        await download.InstallAsync(mc, metadataUrl, mapped, cancellationToken).ConfigureAwait(false);
        Report(progress, 45, "原版安装完成", $"Minecraft {mc} 已就绪");
    }

    /// <summary>把加载器 profile JSON 写成自包含的版本目录（id 对齐到实例名）。</summary>
    public static async Task WriteProfileVersionAsync(
        string gameDirectory,
        string instanceName,
        string json,
        IProgress<ModLoaderInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        string root = NormalizeGameDirectory(gameDirectory);
        string id = ValidateInstanceName(instanceName);
        string directory = Path.Combine(root, "versions", id);
        Directory.CreateDirectory(directory);

        string finalJson = RewriteVersionId(json, id);
        string target = Path.Combine(directory, id + ".json");
        string temporary = target + LoaderTemporarySuffix;
        try
        {
            await File.WriteAllTextAsync(temporary, finalJson, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, target, true);
        }
        finally
        {
            DeleteLoaderFileIfExists(temporary);
        }

        Report(progress, 100, "安装完成", $"{id} 已写入 versions/{id}");
    }

    /// <summary>下载安装器 JAR，运行它，并把生成的版本目录对齐到实例名。</summary>
    public static async Task<string> InstallWithInstallerAsync(
        ModLoaderVersionInfo version,
        string gameDirectory,
        string instanceName,
        IProgress<ModLoaderInstallProgress>? progress,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? installSources = null,
        IReadOnlyList<string>? directInstallSources = null)
    {
        await EnsureVanillaAsync(
            gameDirectory, version.MinecraftVersion, progress, cancellationToken).ConfigureAwait(false);

        string root = NormalizeGameDirectory(gameDirectory);
        string id = ValidateInstanceName(instanceName);
        if (string.Equals(id, version.MinecraftVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"实例名称不能与原版版本号相同（{version.MinecraftVersion}），请换一个名称。");
        }

        Report(progress, 48, "下载安装器", $"正在下载 {ModLoaderInstaller.DisplayName(version.Kind)} 安装器…");
        byte[] installerBytes = await DownloadInstallerAsync(
            installSources is { Count: > 0 } ? installSources : [version.InstallSource],
            directInstallSources,
            cancellationToken).ConfigureAwait(false);
        if (installerBytes.Length == 0)
        {
            throw new InvalidOperationException("加载器安装器下载结果为空。");
        }

        string temporaryDirectory = Path.Combine(Path.GetTempPath(), "mofu-loader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        string installerPath = Path.Combine(temporaryDirectory, "installer.jar");
        try
        {
            await File.WriteAllBytesAsync(installerPath, installerBytes, cancellationToken).ConfigureAwait(false);
            return await RunInstallerAndAlignAsync(
                root, version, id, installerPath, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(temporaryDirectory);
        }
    }

    private static async Task<byte[]> DownloadInstallerAsync(
        IReadOnlyList<string> sources,
        IReadOnlyList<string>? directSources,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (string source in sources)
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            try
            {
                byte[] bytes = await DownloadSourceProvider.GetBytesAsync(
                    source, InstallerDownloadTimeout, cancellationToken).ConfigureAwait(false);
                if (IsZip(bytes)) return bytes;
                lastError = new InvalidOperationException("安装器下载内容不是有效的 JAR 文件。");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                lastError = e;
            }
        }

        // 镜像不可用或重定向链卡住时，直连官方 Maven（不被下载源重写）
        if (directSources is not null)
        {
            foreach (string source in directSources)
            {
                if (string.IsNullOrWhiteSpace(source)) continue;
                try
                {
                    byte[] bytes = await DownloadSourceProvider.GetBytesDirectAsync(
                        source, InstallerDownloadTimeout, cancellationToken).ConfigureAwait(false);
                    if (IsZip(bytes)) return bytes;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    lastError = e;
                }
            }
        }

        throw new InvalidOperationException("加载器安装器下载失败。", lastError);
    }

    private static bool IsZip(byte[] bytes) =>
        bytes.Length > 2 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K';

    /// <summary>运行已经下载到本地的安装器（OptiFine 需要先解析引导页，单独调用）。</summary>
    public static async Task<string> RunInstallerAndAlignAsync(
        string gameDirectory,
        ModLoaderVersionInfo version,
        string instanceName,
        string installerPath,
        IProgress<ModLoaderInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        string root = NormalizeGameDirectory(gameDirectory);
        string id = ValidateInstanceName(instanceName);
        if (string.Equals(id, version.MinecraftVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"实例名称不能与原版版本号相同（{version.MinecraftVersion}），请换一个名称。");
        }

        string versionsDirectory = Path.Combine(root, "versions");
        Directory.CreateDirectory(versionsDirectory);
        EnsureLauncherProfiles(root);

        string javaExecutable = await ResolveJavaAsync(
            root, version.MinecraftVersion, version.Kind, progress, cancellationToken).ConfigureAwait(false);

        HashSet<string> before = SnapshotDirectories(versionsDirectory);
        Report(progress, 68, "运行安装器", $"正在运行 {ModLoaderInstaller.DisplayName(version.Kind)} 安装器…");
        string output = await RunInstallerAttemptsAsync(
            javaExecutable, installerPath, root, version.Kind, cancellationToken).ConfigureAwait(false);

        Report(progress, 90, "整理版本目录", "正在把安装器产物对齐到实例目录…");
        string sourceDirectory = DetectInstallerDirectory(
            versionsDirectory, before, version.MinecraftVersion, id, version.Kind);
        AlignInstanceDirectory(sourceDirectory, Path.Combine(versionsDirectory, id), id);
        VerifyRuntimeArtifact(root, version.Kind);

        string targetJson = Path.Combine(versionsDirectory, id, id + ".json");
        if (!File.Exists(targetJson))
        {
            string tail = Tail(output, 1600);
            throw new InvalidOperationException(
                $"{ModLoaderInstaller.DisplayName(version.Kind)} 安装器结束，但没有生成版本配置 {id}.json。" +
                (tail.Length > 0 ? $"安装器输出：{tail}" : string.Empty));
        }

        Report(progress, 100, "安装完成", $"{id} 已就绪");
        return id;
    }

    public static void Report(
        IProgress<ModLoaderInstallProgress>? progress,
        double percentage,
        string stage,
        string detail,
        double bytesPerSecond = 0,
        long completedBytes = 0,
        long totalBytes = 0)
    {
        progress?.Report(new ModLoaderInstallProgress(
            1, stage, detail, Math.Clamp(percentage, 0, 100), bytesPerSecond, completedBytes, totalBytes));
    }

    // ---------------------------------------------------------------- Java

    private static async Task<string> ResolveJavaAsync(
        string gameDirectory,
        string minecraftVersion,
        ModLoaderKind kind,
        IProgress<ModLoaderInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        string configured = string.Empty;
        try
        {
            configured = YamlRead.JavaExecutable.Trim();
        }
        catch (Exception)
        {
            // 配置不可读时继续自动探测
        }

        int requiredMajor = await ReadRequiredJavaMajorAsync(gameDirectory, minecraftVersion, cancellationToken)
            .ConfigureAwait(false);

        // 安装器对 Java 版本敏感：只接受与原版 javaVersion 精确匹配的运行时，
        // 避免用 Java 25 去跑要求 Java 21 的 Forge/NeoForge 安装器。
        string? java = await JavaPathSearch.FindExactMatchJavaAsync(
            configuredPath: configured.Length > 0 ? configured : null,
            requiredMajorVersion: requiredMajor,
            runtimeDirectory: JavaDownload.GetRuntimeDirectory(gameDirectory),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(java))
        {
            return java;
        }

        Report(progress, 58, "准备 Java", $"未找到 Java {requiredMajor}，正在自动下载…");
        InstalledJavaRuntime? installed = await JavaDownload.InstallForMajorVersionAsync(
            requiredMajor, gameDirectory, null, cancellationToken).ConfigureAwait(false);
        if (installed is null && requiredMajor != 21)
        {
            installed = await JavaDownload.InstallForMajorVersionAsync(
                21, gameDirectory, null, cancellationToken).ConfigureAwait(false);
        }

        if (installed is null || string.IsNullOrWhiteSpace(installed.JavaExecutablePath))
        {
            throw new InvalidOperationException(
                $"{ModLoaderInstaller.DisplayName(kind)} 安装器需要 Java，但本机没有可用的运行时，自动下载也失败了。");
        }

        return installed.JavaExecutablePath;
    }

    private static async Task<int> ReadRequiredJavaMajorAsync(
        string gameDirectory,
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        string jsonPath = Path.Combine(gameDirectory, "versions", minecraftVersion, minecraftVersion + ".json");
        if (!File.Exists(jsonPath)) return 21;

        try
        {
            string json = await File.ReadAllTextAsync(jsonPath, cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("javaVersion", out JsonElement javaVersion) &&
                javaVersion.TryGetProperty("majorVersion", out JsonElement major) &&
                major.TryGetInt32(out int value) && value > 0)
            {
                return value;
            }
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            // 读不到就按现代版本处理
        }

        return 21;
    }

    // ---------------------------------------------------------------- 安装器进程

    private static async Task<string> RunInstallerAttemptsAsync(
        string javaExecutable,
        string installerPath,
        string gameDirectory,
        ModLoaderKind kind,
        CancellationToken cancellationToken)
    {
        List<string[]> attempts = BuildInstallerAttempts(installerPath, gameDirectory, kind);
        string lastOutput = string.Empty;
        Exception? lastError = null;

        foreach (string[] arguments in attempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (int exitCode, string output) = await RunProcessAsync(
                javaExecutable, arguments, gameDirectory, cancellationToken).ConfigureAwait(false);
            lastOutput = output;

            if (exitCode == 0)
            {
                return output;
            }

            lastError = new InvalidOperationException(
                $"{ModLoaderInstaller.DisplayName(kind)} 安装器退出码 {exitCode}：{Tail(output, 1600)}");
            if (!IsUnrecognizedOption(output))
            {
                // 非参数不兼容的失败（下载失败 / 校验失败 / 崩溃）重试没有意义
                throw lastError;
            }
        }

        throw new InvalidOperationException(
            $"{ModLoaderInstaller.DisplayName(kind)} 安装器运行失败：{Tail(lastOutput, 1600)}", lastError);
    }

    private static List<string[]> BuildInstallerAttempts(
        string installerPath,
        string gameDirectory,
        ModLoaderKind kind)
    {
        List<string[]> attempts = [];
        if (kind == ModLoaderKind.OptiFine)
        {
            attempts.Add(["-Djava.awt.headless=true", "-cp", installerPath, "optifine.Installer", "--installGame", gameDirectory]);
            attempts.Add(["-Djava.awt.headless=true", "-cp", installerPath, "optifine.Installer", "--install", gameDirectory]);
            return attempts;
        }

        List<string[]> mirrors = BuildMirrorArguments();
        foreach (string installArgument in new[] { "--installClient", "--install-client" })
        {
            foreach (string[] mirror in mirrors)
            {
                List<string> arguments = ["-Djava.awt.headless=true", "-jar", installerPath, installArgument, gameDirectory];
                arguments.AddRange(mirror);
                attempts.Add([.. arguments]);
            }
        }

        return attempts;
    }

    private static List<string[]> BuildMirrorArguments()
    {
        List<string[]> result = [];
        List<string> mirrors = [];
        AddMirror(mirrors, DownloadSourceProvider.Active.Maven);
        DownloadSource? fallback = DownloadSourceProvider.Fallback;
        if (fallback is not null) AddMirror(mirrors, fallback.Maven);

        if (mirrors.Count > 0)
        {
            result.Add(["--mirror", .. mirrors]);
        }
        result.Add([]);
        return result;
    }

    private static void AddMirror(List<string> mirrors, string maven)
    {
        string value = (maven ?? string.Empty).Trim();
        if (value.Length == 0) return;
        if (string.Equals(value.TrimEnd('/'), DownloadSources.Official.Maven.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (string.Equals(value.TrimEnd('/'), "https://maven.neoforged.net/releases",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!mirrors.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            mirrors.Add(value);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        StringBuilder output = new();
        object gate = new();
        void Append(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (gate)
            {
                output.AppendLine(line);
                if (output.Length > 32_000)
                {
                    output.Remove(0, output.Length - 24_000);
                }
            }
        }

        process.OutputDataReceived += (_, args) => Append(args.Data);
        process.ErrorDataReceived += (_, args) => Append(args.Data);
        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动加载器安装器进程。");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(InstallerTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 进程可能已经退出
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("加载器安装器运行超时（10 分钟）。");
        }

        // WaitForExitAsync 可能先于异步输出回调结束；再等一次把尾部输出冲刷完
        process.WaitForExit();
        lock (gate)
        {
            return (process.ExitCode, output.ToString());
        }
    }

    private static bool IsUnrecognizedOption(string output)
    {
        string text = output.ToLowerInvariant();
        return text.Contains("is not a recognized option", StringComparison.Ordinal) ||
               text.Contains("unrecognized option", StringComparison.Ordinal) ||
               text.Contains("unknown option", StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 目录对齐

    private static HashSet<string> SnapshotDirectories(string versionsDirectory)
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string directory in Directory.GetDirectories(versionsDirectory))
            {
                result.Add(Path.GetFileName(directory));
            }
        }
        catch (Exception)
        {
            // 目录不可读时按空集处理
        }

        return result;
    }

    private static string DetectInstallerDirectory(
        string versionsDirectory,
        HashSet<string> before,
        string minecraftVersion,
        string instanceName,
        ModLoaderKind kind)
    {
        string marker = kind switch
        {
            ModLoaderKind.NeoForge => "neoforge",
            ModLoaderKind.OptiFine => "optifine",
            _ => "forge",
        };

        List<string> directories;
        try
        {
            directories = Directory.GetDirectories(versionsDirectory)
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .ToList();
        }
        catch (Exception e)
        {
            throw new InvalidOperationException("无法读取 versions 目录：" + e.Message, e);
        }

        foreach (string directory in directories)
        {
            string name = Path.GetFileName(directory);
            if (string.Equals(name, minecraftVersion, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(name, instanceName, StringComparison.OrdinalIgnoreCase)) continue;
            if (before.Contains(name)) continue;
            if (!name.Contains(marker, StringComparison.OrdinalIgnoreCase)) continue;
            return directory;
        }

        // 重装场景：安装器可能直接写进了实例目录
        string target = Path.Combine(versionsDirectory, instanceName);
        if (Directory.Exists(target) && Directory.EnumerateFiles(target, "*.json").Any())
        {
            return target;
        }

        throw new InvalidOperationException(
            $"{ModLoaderInstaller.DisplayName(kind)} 安装器结束，但没有发现新的 versions/{marker}* 目录。" +
            "请检查安装器输出确认 Java 版本与网络。");
    }

    private static void AlignInstanceDirectory(string sourceDirectory, string targetDirectory, string instanceName)
    {
        string sourceName = Path.GetFileName(sourceDirectory);
        if (string.Equals(
                Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            NormalizeVersionDirectory(targetDirectory, sourceName, instanceName);
            return;
        }

        if (Directory.Exists(targetDirectory))
        {
            MergeInstallerOutput(sourceDirectory, targetDirectory, instanceName);
            return;
        }

        Directory.Move(sourceDirectory, targetDirectory);
        NormalizeVersionDirectory(targetDirectory, sourceName, instanceName);
    }

    private static void NormalizeVersionDirectory(string directory, string oldName, string instanceName)
    {
        string targetJson = Path.Combine(directory, instanceName + ".json");
        string? sourceJson = Directory.EnumerateFiles(directory, "*.json")
            .FirstOrDefault(path => !string.Equals(
                Path.GetFileName(path), instanceName + ".json", StringComparison.OrdinalIgnoreCase));

        if (sourceJson is not null)
        {
            string json = File.ReadAllText(sourceJson);
            string rewritten = RewriteVersionId(json, instanceName);
            File.WriteAllText(targetJson, rewritten, new UTF8Encoding(false));
            if (!string.Equals(sourceJson, targetJson, StringComparison.OrdinalIgnoreCase))
            {
                DeleteLoaderFileIfExists(sourceJson);
            }
        }

        string oldJar = Path.Combine(directory, oldName + ".jar");
        string newJar = Path.Combine(directory, instanceName + ".jar");
        if (!string.Equals(oldJar, newJar, StringComparison.OrdinalIgnoreCase) && File.Exists(oldJar))
        {
            File.Move(oldJar, newJar, true);
        }
    }

    private static void MergeInstallerOutput(string sourceDirectory, string targetDirectory, string instanceName)
    {
        string? sourceJson = Directory.EnumerateFiles(sourceDirectory, "*.json").FirstOrDefault();
        if (sourceJson is not null)
        {
            string json = File.ReadAllText(sourceJson);
            File.WriteAllText(
                Path.Combine(targetDirectory, instanceName + ".json"),
                RewriteVersionId(json, instanceName),
                new UTF8Encoding(false));
        }

        string sourceName = Path.GetFileName(sourceDirectory);
        string sourceJar = Path.Combine(sourceDirectory, sourceName + ".jar");
        string targetJar = Path.Combine(targetDirectory, instanceName + ".jar");
        if (File.Exists(sourceJar) && !File.Exists(targetJar))
        {
            File.Copy(sourceJar, targetJar, true);
        }

        // 只清理安装器生成的 json/jar 空壳，绝不碰用户已有实例内容
        foreach (string file in Directory.EnumerateFiles(sourceDirectory))
        {
            string extension = Path.GetExtension(file);
            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jar", StringComparison.OrdinalIgnoreCase))
            {
                DeleteLoaderFileIfExists(file);
            }
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(sourceDirectory).Any())
            {
                Directory.Delete(sourceDirectory);
            }
        }
        catch (Exception)
        {
            // 清理失败不影响安装
        }
    }

    private static void VerifyRuntimeArtifact(string gameDirectory, ModLoaderKind kind)
    {
        if (kind == ModLoaderKind.OptiFine) return;

        string libraries = Path.Combine(gameDirectory, "libraries");
        if (kind == ModLoaderKind.NeoForge)
        {
            string patched = Path.Combine(libraries, "net", "neoforged", "minecraft-client-patched");
            if (FindFile(patched, "*.jar") is null)
            {
                throw new InvalidOperationException(
                    "NeoForge 安装器没有生成运行时客户端（minecraft-client-patched），安装不完整。");
            }
            return;
        }

        string forgeClient = Path.Combine(libraries, "net", "minecraft", "client");
        if (FindFile(forgeClient, "*-srg.jar") is null)
        {
            throw new InvalidOperationException(
                "Forge 安装器没有生成 SRG 重映射客户端（*-srg.jar），安装不完整。");
        }
    }

    private static string? FindFile(string root, string pattern)
    {
        if (!Directory.Exists(root)) return null;
        try
        {
            return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- 公共辅助

    private static void EnsureLauncherProfiles(string minecraftRoot)
    {
        string profilePath = Path.Combine(minecraftRoot, "launcher_profiles.json");
        if (File.Exists(profilePath))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(profilePath));
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("profiles", out _))
                {
                    return;
                }
            }
            catch (Exception)
            {
                // 内容损坏时重写一份最小模板
            }
        }

        const string template = """
                                {
                                  "profiles": {},
                                  "selectedProfile": "(Default)",
                                  "clientToken": "mofumofu",
                                  "authenticationDatabase": {}
                                }
                                """;
        try
        {
            File.WriteAllText(profilePath, template, new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // 部分安装器版本不强制检查，写失败不阻塞安装
        }
    }

    private static string RewriteVersionId(string json, string instanceName)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(json);
            if (node is not JsonObject root) return json;

            root["id"] = instanceName;
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static string ValidateInstanceName(string instanceName)
    {
        string name = (instanceName ?? string.Empty).Trim();
        MinecraftDownload.ValidateVersionId(name);
        return name;
    }

    private static string NormalizeGameDirectory(string gameDirectory)
    {
        string root = (gameDirectory ?? string.Empty).Trim();
        if (root.Length == 0)
        {
            throw new InvalidOperationException("Minecraft 目录不能为空。");
        }
        return Path.GetFullPath(root);
    }

    private static string Tail(string value, int maximum)
    {
        string text = (value ?? string.Empty).Trim();
        return text.Length <= maximum ? text : text[^maximum..];
    }

    private static void DeleteLoaderFileIfExists(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // 清理失败不影响安装
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch (Exception)
        {
            // 临时目录清理失败不影响结果
        }
    }
}
