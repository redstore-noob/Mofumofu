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
    private const string LauncherFileName = "Mofumofu.Launcher.exe";

    private readonly string? _executableOverride;
    private readonly string? _packagePath;
    private readonly Action<string, string> _launch;

    public SoloInstallController(string? executableOverride = null, string? packagePath = null,
        Action<string, string>? launch = null)
    {
        _executableOverride = executableOverride;
        _packagePath = packagePath;
        _launch = launch ?? ((executable, directory) =>
            Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false }));
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
        string stageMarkerPath = string.Empty;
        StreamWriter? stage = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            options = options with { TargetPath = Path.GetFullPath(options.TargetPath) };
            Directory.CreateDirectory(options.TargetPath);

            // 阶段标记：实时记录本次写出的文件；失败/取消时按清单回滚，避免重试时新旧文件混装。
            stageMarkerPath = Path.Combine(options.TargetPath, ".nekosolo-installing");
            stage = PrepareStageMarker(stageMarkerPath, options.TargetPath, Log);
            Action<string> track = fullPath => TrackWritten(stage, options.TargetPath, fullPath);

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
                    long declared = 0;
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (entry.Length < 0 || entry.Length > SoloPayload.EntryMaximumBytes)
                            throw new InvalidOperationException("安装包条目体积超限：" + entry.FullName);
                        declared += entry.Length;
                        if (declared > SoloPayload.ExtractedMaximumBytes)
                            throw new InvalidOperationException("安装包解压后总体积超限");
                    }

                    Log($"zip: OK（{entries} 个条目，{declared} 字节）");
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

            manifest = Manifest!;
            // 校验与全部释放共用同一个 ZipArchive 实例：先统一校验条目路径/体积/完整性，
            // 再连续释放 files/、minecraft/、jre/，关闭「校验一个 archive、释放另一个 archive」的 TOCTOU 窗口。
            using (Stream packageStream = OpenPackageStream(payload))
            using (var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: false))
            {
                // 2) 校验（Begin 已在步骤 1 之后调用）
                // 释放/启动之前统一校验条目路径、体积上限与解压完整性，任一失败即终止（fail-closed）。
                SoloPayload.VerifyArchive(archive, Log);
                // 只接受包含主启动器的完整包，不能把普通 zip 或仅含实例内容的旧包标为安装成功。
                if (!archive.Entries.Any(entry =>
                        string.Equals(entry.FullName, "files/" + LauncherFileName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(entry.FullName, "overrides/" + LauncherFileName, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("安装包缺少 files/Mofumofu.Launcher.exe，请使用新版导出器重新打包。");
                End(InstallStep.Verify);

                // 3) 释放启动器
                Begin(InstallStep.Extract);
                var extractProgress = new Progress<double>(value =>
                    Progress(value, SoloStrings.T("Step.Extract"), string.Empty));
                string extractedPrefix = archive.Entries.Any(entry => string.Equals(entry.FullName,
                    "files/" + LauncherFileName, StringComparison.OrdinalIgnoreCase)) ? "files/" : "overrides/";
                long count = SoloPayload.ExtractPrefix(payload, archive, extractedPrefix, options.TargetPath, Log,
                    extractProgress, cancellationToken, onWritten: track);

                if (count == 0) throw new InvalidOperationException("安装包没有可释放的启动器文件。");
                fileCount += (int)count;
                writtenBytes += archive.Entries
                    .Where(entry => entry.FullName.StartsWith(extractedPrefix, StringComparison.OrdinalIgnoreCase))
                    .Sum(entry => entry.Length);
                Log($"extract: {count} 个文件 → {options.TargetPath}");
                End(InstallStep.Extract, SoloStrings.F("Progress.Files", fileCount));

                // 4) 写配置
                Begin(InstallStep.Config);
                // 先登记会写出的配置/标记文件，写配置中途失败也能一并回滚；桌面快捷方式不在安装目录内，不回滚。
                track(Path.Combine(options.TargetPath, "solo.json"));
                if (options.Portable) track(Path.Combine(options.TargetPath, "portable.flag"));
                await WriteConfigurationAsync(manifest, options, cancellationToken).ConfigureAwait(false);
                End(InstallStep.Config);

                // 5) 补全游戏文件
                Begin(InstallStep.Minecraft);
                string mcTarget = Path.Combine(options.TargetPath, ".minecraft");
                var mcProgress = new Progress<double>(value =>
                    Progress(value, SoloStrings.T("Step.Minecraft"), string.Empty));
                long mcCount = SoloPayload.ExtractPrefix(payload, archive, "minecraft/", mcTarget, Log, mcProgress,
                    cancellationToken, onWritten: track);
                fileCount += (int)mcCount;

                if (mcCount > 0)
                {
                    Log($"minecraft: minecraft/ → {mcTarget} ({mcCount} 个文件)");
                }
                else
                {
                    Log("minecraft: 安装包内未附带游戏文件，将在首次启动时补全");
                }

                var jreProgress = new Progress<double>(value =>
                    Progress(value, SoloStrings.T("Step.Minecraft"), "jre"));
                long jreCount = SoloPayload.ExtractPrefix(payload, archive, "jre/",
                    Path.Combine(mcTarget, "runtime", "jre"), Log, jreProgress, cancellationToken, onWritten: track);
                fileCount += (int)jreCount;
                writtenBytes += archive.Entries.Where(entry =>
                    entry.FullName.StartsWith("minecraft/", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.StartsWith("jre/", StringComparison.OrdinalIgnoreCase)).Sum(entry => entry.Length);
                if (jreCount > 0) Log($"java: jre/ → {Path.Combine(mcTarget, "runtime", "jre")} ({jreCount} 个文件)");
                End(InstallStep.Minecraft);

                // 6) 完成
                Begin(InstallStep.Finish);
                await WriteInstallMarkerAsync(manifest, options, stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);

                Progress(1, SoloStrings.T("Step.Finish"), string.Empty);
                End(InstallStep.Finish);
                Log($"done: {stopwatch.Elapsed.TotalSeconds:0.0}s");
            }

            // 安装完整成功后移除阶段标记；完成标记 .nekosolo-installed 仍只在 Finish 写入。
            FinishStageMarker(stage, stageMarkerPath);
            stage = null;

            if (options.LaunchAfterInstall)
            {
                // 只启动经白名单与包内断言校验过的启动器；找不到时保持安装成功但跳过自动启动。
                string? launcher = FindLauncherExe(options.TargetPath, null);
                if (launcher is null)
                {
                    Log("launch: 未找到合法的启动器，已跳过自动启动");
                }
                else
                {
                    // 启动失败不改变已经完成的安装结果，用户仍可手动重试启动。
                    try { _launch(launcher, options.TargetPath); }
                    catch (Exception ex) { Log("launch: " + ex.Message); }
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            MarkRunningFailed();
            RollbackStage(stage, stageMarkerPath, options.TargetPath, Log);
            stage = null;
            Log("cancelled");
        }
        catch (Exception ex)
        {
            error = ex.Message;
            MarkRunningFailed();
            RollbackStage(stage, stageMarkerPath, options.TargetPath, Log);
            stage = null;
            Log("error: " + ex.Message);
        }
        finally
        {
            stage?.Dispose();
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

    /// <summary>安装目录里真正的启动器 exe（仅接受白名单文件名，且断言位于安装目录内）。</summary>
    public static string? FindLauncherExe(string target, string? selfPath)
    {
        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
            string rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            string expected = Path.Combine(root, LauncherFileName);
            if (File.Exists(expected)) return Path.GetFullPath(expected);
            string soloJson = Path.Combine(root, "solo.json");
            if (File.Exists(soloJson))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(soloJson));
                if (document.RootElement.TryGetProperty("LauncherExe", out JsonElement launcher) &&
                    launcher.ValueKind == JsonValueKind.String &&
                    launcher.GetString() is { Length: > 0 } recorded &&
                    string.Equals(Path.GetFileName(recorded), LauncherFileName, StringComparison.OrdinalIgnoreCase))
                {
                    // 绝对路径不允许仅凭 File.Exists 就执行：先规范化并断言仍落在安装目录内。
                    string full = Path.IsPathRooted(recorded)
                        ? Path.GetFullPath(recorded)
                        : Path.GetFullPath(Path.Combine(root, recorded));
                    if (full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                    {
                        return full;
                    }
                }
            }

            string selfName = string.IsNullOrWhiteSpace(selfPath)
                ? string.Empty
                : Path.GetFileName(selfPath);
            return Directory.EnumerateFiles(root, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(path => string.Equals(Path.GetFileName(path), LauncherFileName,
                    StringComparison.OrdinalIgnoreCase))
                .Select(path => Path.GetFullPath(path))
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
        // .nekosolo-installed 是换行分隔的 key=value 文本：值里的换行/控制字符会伪造记录，
        // 写入前统一剔除（FromJson 已校验，这里对直接构造的 manifest 做二次兜底）。
        string text = $"pack={EscapeMarkerValue(manifest.PackId)}\n" +
                      $"version={EscapeMarkerValue(manifest.VersionDisplay)}\n" +
                      $"elapsed={elapsed.TotalSeconds:0.0}s\n";
        await File.WriteAllTextAsync(markerPath, text, cancellationToken).ConfigureAwait(false);
    }

    // ---------------- 安装阶段标记 / 失败回滚 ----------------

    /// <summary>剔除 marker 值里的控制字符（含换行），防止伪造 key=value 记录。</summary>
    private static string EscapeMarkerValue(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (!char.IsControl(ch)) builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 准备阶段标记：若上次安装异常退出留下标记，先按其记录清理残留文件，避免重试时新旧文件混装；
    /// 随后打开一个实时写入的清单，记录本次写出的文件相对路径。
    /// </summary>
    private static StreamWriter PrepareStageMarker(string markerPath, string target, Action<string> log)
    {
        if (File.Exists(markerPath))
        {
            int cleaned = 0;
            try
            {
                foreach (string relative in File.ReadLines(markerPath).ToArray())
                {
                    if (!string.IsNullOrWhiteSpace(relative) && TryDeleteTracked(target, relative)) cleaned++;
                }
            }
            catch (Exception) { /* 残留清理失败不阻断本次安装 */ }

            if (cleaned > 0) log($"cleanup: 清理上次未完成安装残留的 {cleaned} 个文件");
        }

        return new StreamWriter(markerPath, append: false) { AutoFlush = true };
    }

    /// <summary>把本次写出的文件（仅安装目录内）追加到阶段标记清单。</summary>
    private static void TrackWritten(StreamWriter? stage, string target, string fullPath)
    {
        if (stage is null) return;
        string relative = Path.GetRelativePath(target, fullPath);
        // 拒绝越界/绝对路径：桌面快捷方式等不在安装目录内，不纳入回滚范围。
        if (relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative)) return;
        stage.WriteLine(relative);
    }

    /// <summary>安装成功：关闭并删除阶段标记，保留 Finish 写入的完成标记。</summary>
    private static void FinishStageMarker(StreamWriter? stage, string markerPath)
    {
        stage?.Dispose();
        TryDeleteFile(markerPath);
    }

    /// <summary>失败/取消：按阶段标记清单回滚本次已写出的文件，并删除标记（完成标记本就不会写入）。</summary>
    private static void RollbackStage(StreamWriter? stage, string markerPath, string target, Action<string> log)
    {
        stage?.Dispose(); // 先释放句柄，下面才能安全读取清单
        if (string.IsNullOrEmpty(markerPath)) return;

        int removed = 0;
        try
        {
            if (File.Exists(markerPath))
            {
                foreach (string relative in File.ReadLines(markerPath).ToArray())
                {
                    if (!string.IsNullOrWhiteSpace(relative) && TryDeleteTracked(target, relative)) removed++;
                }
            }
        }
        catch (Exception) { /* 回滚失败不覆盖原始安装异常 */ }

        TryDeleteFile(markerPath);
        if (removed > 0) log($"cleanup: 已回滚 {removed} 个文件");
    }

    /// <summary>删除清单里的一个相对路径文件；先规范化并断言仍落在安装目录内，防清单被篡改后越界删除。</summary>
    private static bool TryDeleteTracked(string target, string relative)
    {
        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
            string full = Path.GetFullPath(Path.Combine(root, relative));
            string prefix = root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            if (!File.Exists(full)) return false;
            File.Delete(full);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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
