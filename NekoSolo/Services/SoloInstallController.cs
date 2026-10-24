using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace NekoSolo.Services;

public enum InstallStep
{
    Check = 0,
    Verify = 1,
    Extract = 2,
    Config = 3,
    Minecraft = 4,
    Finish = 5,
}

public enum StepStatus
{
    Pending,
    Running,
    Done,
    Failed,
}

public sealed class InstallStepItem(InstallStep step, string title)
{
    public InstallStep Step { get; } = step;
    public string Title { get; } = title;
    public StepStatus Status { get; set; } = StepStatus.Pending;
    public string Detail { get; set; } = string.Empty;
}

public sealed record InstallOptions(
    string TargetPath,
    bool CreateShortcut,
    bool LaunchAfterInstall,
    bool Portable);

public sealed record InstallResult(
    bool Success,
    bool Cancelled,
    string? Error,
    int FileCount,
    long WrittenBytes,
    TimeSpan Elapsed);

/// <summary>
/// 安装流程状态机：检查包 → 校验 → 释放启动器 → 写配置 → 补全游戏文件 → 完成。
/// UI 只订阅事件，不关心具体步骤实现。
/// </summary>
public sealed class SoloInstallController
{
    private readonly string? _executableOverride;
    private readonly string? _packagePath;

    public SoloInstallController(string? executableOverride = null, string? packagePath = null)
    {
        _executableOverride = executableOverride;
        _packagePath = packagePath;
    }

    public IReadOnlyList<InstallStepItem> Steps { get; } = new List<InstallStepItem>
    {
        new(InstallStep.Check, SoloStrings.T("Step.Check")),
        new(InstallStep.Verify, SoloStrings.T("Step.Verify")),
        new(InstallStep.Extract, SoloStrings.T("Step.Extract")),
        new(InstallStep.Config, SoloStrings.T("Step.Config")),
        new(InstallStep.Minecraft, SoloStrings.T("Step.Minecraft")),
        new(InstallStep.Finish, SoloStrings.T("Step.Finish")),
    };

    public event Action<InstallStepItem>? StepChanged;
    public event Action<double, string, string>? ProgressChanged;
    public event Action<string>? LogAppended;
    public event Action<InstallResult>? Finished;

    public SoloManifest? Manifest { get; private set; }

    public async Task RunAsync(SoloManifest manifest, InstallOptions options, CancellationToken cancellationToken)
    {
        Manifest = manifest;
        var stopwatch = Stopwatch.StartNew();
        int fileCount = 0;
        long writtenBytes = 0;
        string? error = null;
        bool cancelled = false;

        try
        {
            Directory.CreateDirectory(options.TargetPath);

            // 1) 检查安装包
            Begin(InstallStep.Check);
            SoloPayloadInfo payload = default;
            if (!string.IsNullOrWhiteSpace(_packagePath))
            {
                if (!File.Exists(_packagePath))
                {
                    throw new InvalidOperationException("安装包不存在：" + _packagePath);
                }

                payload = new SoloPayloadInfo(_packagePath, 0, new FileInfo(_packagePath).Length, 0);
                Log("package: " + _packagePath);
                Log($"package: {payload.SizeMegabytes:0.0} MB（GitHub Releases 外部安装包）");
                using (Stream package = File.OpenRead(_packagePath))
                {
                    SoloManifest? fromPackage = SoloPayload.ReadManifest(package);
                    if (fromPackage is not null) Manifest = fromPackage;
                }

                End(InstallStep.Check, $"{payload.SizeMegabytes:0.0} MB");
            }
            else
            {
                string executable = _executableOverride
                                    ?? Environment.ProcessPath
                                    ?? throw new InvalidOperationException(SoloStrings.T("Error.NoPayload"));
                Log("executable: " + executable);
                if (!SoloPayload.TryRead(executable, out payload, out string readError))
                {
                    throw new InvalidOperationException(readError);
                }

                Log($"payload: offset={payload.Offset} length={payload.Length} crc=0x{payload.Crc:X8}");
                SoloManifest? fromPayload = SoloPayload.ReadManifest(payload);
                if (fromPayload is not null)
                {
                    Manifest = fromPayload;
                }

                End(InstallStep.Check, $"{payload.SizeMegabytes:0.0} MB");
            }

            // 2) 校验
            Begin(InstallStep.Verify);
            if (!string.IsNullOrWhiteSpace(_packagePath))
            {
                using (Stream package = File.OpenRead(_packagePath))
                using (var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: false))
                {
                    int entries = archive.Entries.Count;
                    if (entries == 0) throw new InvalidOperationException("安装包为空");
                    Log($"zip: OK（{entries} 个条目）");
                }

                Progress(1, SoloStrings.T("Step.Verify"), "zip");
            }
            else
            {
                var verifyProgress = new Progress<double>(value =>
                    Progress(value, SoloStrings.T("Step.Verify"), $"{value * 100:0}%"));
                bool ok = await SoloPayload.VerifyAsync(payload, verifyProgress, cancellationToken).ConfigureAwait(false);
                if (!ok) throw new InvalidOperationException(SoloStrings.F("Error.BadPayload", "CRC32 不匹配"));
                Log("crc32: OK");
            }

            End(InstallStep.Verify);

            // 3) 释放启动器
            Begin(InstallStep.Extract);
            using (Stream payloadStream = OpenPackageStream(payload))
            using (var archive = new ZipArchive(payloadStream, ZipArchiveMode.Read, leaveOpen: false))
            {
                var extractProgress = new Progress<double>(value =>
                    Progress(value, SoloStrings.T("Step.Extract"), string.Empty));
                long count = SoloPayload.ExtractPrefix(payload, archive, "files/", options.TargetPath, Log,
                    extractProgress, cancellationToken);
                if (count == 0)
                {
                    // 兼容旧导出器写 overrides/ 的包
                    count = SoloPayload.ExtractPrefix(payload, archive, "overrides/", options.TargetPath, Log,
                        extractProgress, cancellationToken);
                }

                fileCount += (int)count;
                writtenBytes += archive.Entries
                    .Where(entry => entry.FullName.StartsWith("files/", StringComparison.OrdinalIgnoreCase) ||
                                    entry.FullName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase))
                    .Sum(entry => entry.Length);
                Log($"extract: {count} 个文件 → {options.TargetPath}");
            }

            End(InstallStep.Extract, SoloStrings.F("Progress.Files", fileCount));

            // 4) 写配置
            Begin(InstallStep.Config);
            await WriteConfigurationAsync(manifest, options, cancellationToken).ConfigureAwait(false);

            End(InstallStep.Config);

            // 5) 补全游戏文件
            Begin(InstallStep.Minecraft);
            using (Stream payloadStream = OpenPackageStream(payload))
            using (var archive = new ZipArchive(payloadStream, ZipArchiveMode.Read, leaveOpen: false))
            {
                string dataRoot = Path.Combine(options.TargetPath, "NyaLauncher-data");
                string mcTarget = Path.Combine(dataRoot, "minecraft");
                var mcProgress = new Progress<double>(value =>
                    Progress(value, SoloStrings.T("Step.Minecraft"), string.Empty));
                long count = SoloPayload.ExtractPrefix(payload, archive, "minecraft/", mcTarget, Log, mcProgress,
                    cancellationToken);
                fileCount += (int)count;

                if (count > 0)
                {
                    Log($"minecraft: minecraft/ → {mcTarget} ({count} 个文件)");
                }
                else
                {
                    Log("minecraft: 安装包内未附带游戏文件，将在首次启动时补全");
                }

                var jreProgress = new Progress<double>(value =>
                    Progress(value, SoloStrings.T("Step.Minecraft"), "jre"));
                long jreCount = SoloPayload.ExtractPrefix(payload, archive, "jre/",
                    Path.Combine(dataRoot, "runtime", "jre"), Log, jreProgress, cancellationToken);
                fileCount += (int)jreCount;
                if (jreCount > 0) Log($"java: jre/ → {Path.Combine(dataRoot, "runtime", "jre")} ({jreCount} 个文件)");
            }

            End(InstallStep.Minecraft);

            // 6) 完成
            Begin(InstallStep.Finish);
            await WriteInstallMarkerAsync(manifest, options, stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);

            Progress(1, SoloStrings.T("Step.Finish"), string.Empty);
            End(InstallStep.Finish);
            Log($"done: {stopwatch.Elapsed.TotalSeconds:0.0}s");
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            MarkRunningFailed();
            Log("cancelled");
        }
        catch (Exception ex)
        {
            error = ex.Message;
            MarkRunningFailed();
            Log("error: " + ex.Message);
        }

        Finished?.Invoke(new InstallResult(error is null && !cancelled, cancelled, error, fileCount, writtenBytes,
            stopwatch.Elapsed));
    }

    private Stream OpenPackageStream(SoloPayloadInfo payload) =>
        string.IsNullOrWhiteSpace(_packagePath)
            ? SoloPayload.OpenPayloadStream(payload)
            : File.OpenRead(_packagePath);

    private void Begin(InstallStep step)
    {
        InstallStepItem item = Steps.First(entry => entry.Step == step);
        item.Status = StepStatus.Running;
        StepChanged?.Invoke(item);
        Progress(0, item.Title, string.Empty);
        Log($"== {item.Title} ==");
    }

    private void End(InstallStep step, string detail = "")
    {
        InstallStepItem item = Steps.First(entry => entry.Step == step);
        item.Status = StepStatus.Done;
        item.Detail = detail;
        StepChanged?.Invoke(item);
    }

    private void MarkRunningFailed()
    {
        foreach (InstallStepItem item in Steps.Where(entry => entry.Status == StepStatus.Running))
        {
            item.Status = StepStatus.Failed;
            StepChanged?.Invoke(item);
        }
    }

    private void Progress(double value, string stage, string detail) =>
        ProgressChanged?.Invoke(Math.Clamp(value, 0, 1), stage, detail);

    private void Log(string line) => LogAppended?.Invoke(line);

    private async Task WriteConfigurationAsync(SoloManifest manifest, InstallOptions options,
        CancellationToken cancellationToken)
    {
        string target = options.TargetPath;
        string launcher = FindLauncherExe(target, _executableOverride ?? Environment.ProcessPath) ?? string.Empty;
        var soloConfig = new
        {
            manifest.PackId,
            manifest.PackName,
            manifest.PackVersion,
            manifest.McVersion,
            manifest.LoaderName,
            manifest.LoaderVersion,
            manifest.VersionId,
            manifest.SimpleMode,
            manifest.HasJava,
            manifest.Author,
            manifest.UpdateLink,
            manifest.Silent,
            LauncherExe = launcher,
            Portable = options.Portable,
            InstalledAt = DateTimeOffset.Now,
        };
        string configPath = Path.Combine(target, "solo.json");
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(soloConfig, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }),
            Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        Log("config: " + configPath);

        if (options.Portable)
        {
            string flagPath = Path.Combine(target, "portable.flag");
            await File.WriteAllTextAsync(flagPath, string.Empty, cancellationToken).ConfigureAwait(false);
            Log("config: " + flagPath);
        }

        if (options.CreateShortcut)
        {
            if (launcher.Length > 0)
            {
                Shortcut.CreateDesktopShortcut(manifest.DisplayName, launcher, target, Log);
            }
            else
            {
                Log("shortcut: 未找到启动器可执行文件，已跳过");
            }
        }
    }

    /// <summary>写入安装标记后各个界面都能据此判断「已安装 / 可修复」。</summary>
    public static InstallMarker? ReadMarker(string target)
    {
        try
        {
            string path = Path.Combine(target, ".nekosolo-installed");
            if (!File.Exists(path)) return null;

            Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadLines(path))
            {
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
            }

            return new InstallMarker(
                values.TryGetValue("pack", out string? pack) ? pack : string.Empty,
                values.TryGetValue("version", out string? version) ? version : string.Empty);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>安装目录里真正的启动器 exe（优先 solo.json 里记录的 LauncherExe）。</summary>
    public static string? FindLauncherExe(string target, string? selfPath)
    {
        try
        {
            string soloJson = Path.Combine(target, "solo.json");
            if (File.Exists(soloJson))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(soloJson));
                if (document.RootElement.TryGetProperty("LauncherExe", out JsonElement launcher) &&
                    launcher.ValueKind == JsonValueKind.String &&
                    launcher.GetString() is { Length: > 0 } recorded &&
                    File.Exists(recorded))
                {
                    return recorded;
                }
            }

            string selfName = string.IsNullOrWhiteSpace(selfPath)
                ? string.Empty
                : Path.GetFileName(selfPath);
            return Directory.EnumerateFiles(target, "*.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => !string.Equals(Path.GetFileName(path), selfName,
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task WriteInstallMarkerAsync(SoloManifest manifest, InstallOptions options, TimeSpan elapsed,
        CancellationToken cancellationToken)
    {
        string markerPath = Path.Combine(options.TargetPath, ".nekosolo-installed");
        string text = $"pack={manifest.PackId}\nversion={manifest.VersionDisplay}\nelapsed={elapsed.TotalSeconds:0.0}s\n";
        await File.WriteAllTextAsync(markerPath, text, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>安装目录 .nekosolo-installed 的内容。</summary>
public sealed record InstallMarker(string PackId, string Version);

/// <summary>桌面快捷方式（WScript.Shell COM，走反射以避开 dynamic 依赖）。</summary>
internal static class Shortcut
{
    public static void CreateDesktopShortcut(string name, string targetExe, string workingDirectory,
        Action<string>? log)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                log?.Invoke("shortcut: WScript.Shell 不可用，已跳过");
                return;
            }

            object shell = Activator.CreateInstance(shellType)!;
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string linkPath = Path.Combine(desktop, Sanitize(name) + ".lnk");
            object shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod,
                null, shell, [linkPath])!;
            Type linkType = shortcut.GetType();
            linkType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut,
                [targetExe]);
            linkType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut,
                [workingDirectory]);
            linkType.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, shortcut,
                [targetExe + ",0"]);
            linkType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
            log?.Invoke("shortcut: " + linkPath);
        }
        catch (Exception ex)
        {
            log?.Invoke("shortcut: 创建失败（" + ex.Message + "）");
        }
    }

    private static string Sanitize(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(name) ? "NekoSolo" : name;
    }
}
