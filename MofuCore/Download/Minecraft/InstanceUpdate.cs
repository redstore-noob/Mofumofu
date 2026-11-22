using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MofuCore.Launch;

namespace MofuCore.Download.Minecraft;

public sealed record InstanceUpdateProgress(
    string StageName,
    string Detail,
    double Percentage,
    double BytesPerSecond = 0,
    long CompletedBytes = 0,
    long TotalBytes = 0);

public sealed record InstanceUpdateResult(
    string InstanceId,
    string PreviousMinecraftVersion,
    string MinecraftVersion,
    ModLoaderKind LoaderKind,
    string LoaderVersion,
    string SnapshotInstanceId);

/// <summary>新版本先在暂存目录安装并校验，成功后将原版本转为 {实例}-snapshot{版本}；失败或崩溃按提交日志回滚。</summary>
public sealed class InstanceUpdate
{
    private const int CopyBufferSize = 128 * 1024;
    private const string JournalPattern = ".mofu-update-*.journal";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InstanceLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> ActiveTransactions =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _gameDirectory;

    public InstanceUpdate(string minecraftDirectory)
    {
        if (string.IsNullOrWhiteSpace(minecraftDirectory))
        {
            throw new ArgumentException("Minecraft 目录不能为空。", nameof(minecraftDirectory));
        }

        _gameDirectory = Path.GetFullPath(minecraftDirectory.Trim());
    }

    public string GameDirectory => _gameDirectory;

    internal static SemaphoreSlim GetInstanceLock(string gameDirectory, string instanceId)
    {
        string key = Path.GetFullPath(Path.Combine(gameDirectory, "versions", instanceId))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return InstanceLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    }

    internal static string GetCrossProcessLockPath(string gameDirectory, string purpose)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));
        string key = root + "\n" + purpose;
        if (OperatingSystem.IsWindows()) key = key.ToUpperInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
        return Path.Combine(root, ".mofu-locks", hash + ".lock");
    }

    internal static async Task<FileStream?> AcquireCrossProcessLockAsync(
        string path,
        bool wait,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    4096, FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                if (!wait) return null;
                await Task.Delay(120, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static void RecoverPendingUpdates(string minecraftDirectory, string? instanceId = null)
    {
        if (string.IsNullOrWhiteSpace(minecraftDirectory)) return;
        string gameRoot = Path.GetFullPath(minecraftDirectory);
        string versionsRoot = Path.Combine(gameRoot, "versions");
        if (!Directory.Exists(versionsRoot)) return;

        foreach (string journalPath in Directory.GetFiles(
                     versionsRoot, JournalPattern, SearchOption.TopDirectoryOnly))
        {
            if (ActiveTransactions.ContainsKey(Path.GetFullPath(journalPath))) continue;
            UpdateJournal? journal = TryReadJournal(journalPath);
            if (journal is null) continue;
            if (instanceId is not null &&
                !string.Equals(journal.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            SemaphoreSlim instanceLock = GetInstanceLock(gameRoot, journal.InstanceId);
            if (!instanceLock.Wait(0)) continue;
            FileStream? processLock = null;
            try
            {
                string lockPath = GetCrossProcessLockPath(
                    gameRoot, "instance:" + journal.InstanceId);
                processLock = AcquireCrossProcessLockAsync(
                    lockPath, false, CancellationToken.None).GetAwaiter().GetResult();
                if (processLock is null) continue;
                if (ActiveTransactions.ContainsKey(Path.GetFullPath(journalPath))) continue;

                if (!File.Exists(journalPath)) continue;
                journal = TryReadJournal(journalPath);
                if (journal is not null) RecoverJournal(versionsRoot, journalPath, journal);
            }
            finally
            {
                processLock?.Dispose();
                instanceLock.Release();
            }
        }
    }

    public async Task<InstanceUpdateResult> UpdateAsync(
        string instanceId,
        MinecraftVersion target,
        ModLoaderVersionInfo? loader,
        IProgress<InstanceUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        string id = (instanceId ?? string.Empty).Trim();
        MinecraftDownload.ValidateVersionId(id);
        MinecraftDownload.ValidateVersionId(target.Id);

        string versionsRoot = Path.Combine(_gameDirectory, "versions");
        string instanceDirectory = Path.Combine(versionsRoot, id);
        SemaphoreSlim instanceLock = GetInstanceLock(_gameDirectory, id);
        await instanceLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        string transactionId = Guid.NewGuid().ToString("N");
        string stagingId = ".mofu-update-staging-" + transactionId;
        string stagingDirectory = Path.Combine(versionsRoot, stagingId);
        string backupDirectory = Path.Combine(
            versionsRoot, ".mofu-update-backup-" + transactionId);
        string journalPath = Path.Combine(
            versionsRoot, ".mofu-update-" + transactionId + ".journal");
        bool journalCreated = false;
        FileStream? processLock = null;
        try
        {
            string lockPath = GetCrossProcessLockPath(_gameDirectory, "instance:" + id);
            processLock = await AcquireCrossProcessLockAsync(
                lockPath, false, cancellationToken).ConfigureAwait(false);
            if (processLock is null)
            {
                throw new InvalidOperationException("该实例正在另一个启动器进程中运行或更新，请稍后再试。");
            }

            string? previousJournal = Directory.Exists(versionsRoot)
                ? Directory.GetFiles(versionsRoot, JournalPattern, SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(path =>
                    {
                        if (ActiveTransactions.ContainsKey(Path.GetFullPath(path))) return false;
                        UpdateJournal? journal = TryReadJournal(path);
                        return journal is not null && string.Equals(
                            journal.InstanceId, id, StringComparison.OrdinalIgnoreCase);
                    })
                : null;
            if (previousJournal is not null)
            {
                UpdateJournal? pending = TryReadJournal(previousJournal);
                if (pending is not null) RecoverJournal(versionsRoot, previousJournal, pending);
            }

            if (GameLaunch.Shared.IsGameRunning)
            {
                throw new InvalidOperationException("游戏正在运行，不能提交实例更新。");
            }

            Report(progress, 1, "检查实例", "正在读取当前版本与加载器…");
            CurrentInstanceInfo current = await InspectCurrentInstanceAsync(
                instanceDirectory, id, cancellationToken).ConfigureAwait(false);
            ValidateTarget(target, loader, current.LoaderKind);
            EnsureNoReparsePoints(instanceDirectory);

            byte[]? targetMetadata = null;
            if (loader is null)
            {
                Report(progress, 5, "准备版本", $"正在获取 Minecraft {target.Id} 的版本描述…");
                targetMetadata = await DownloadSourceProvider.GetBytesAsync(
                    target.Url, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                if (targetMetadata.Length == 0)
                {
                    throw new InvalidOperationException("目标版本的元数据下载结果为空。");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            ActiveTransactions[Path.GetFullPath(journalPath)] = 1;
            UpdateJournal journal = new(id, stagingDirectory, backupDirectory);
            WriteJournal(journalPath, journal);
            journalCreated = true;

            if (loader is null)
            {
                await InstallVanillaAsync(
                    stagingId, targetMetadata!, progress, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await InstallLoaderAsync(
                    stagingId, loader, progress, cancellationToken).ConfigureAwait(false);
            }

            Report(progress, 68, "恢复用户内容", "正在把存档、模组、配置与其他实例文件复制到暂存实例…");
            await RestoreUserContentAsync(
                instanceDirectory, stagingDirectory, id, progress, cancellationToken).ConfigureAwait(false);

            await VerifyInstallationAsync(
                stagingId, target.Id, loader, true, progress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            Report(progress, 96, "准备提交", "正在对齐暂存实例的配置与文件名…");
            PrepareStagedInstance(stagingDirectory, stagingId, id);

            Directory.Move(instanceDirectory, backupDirectory);
            Directory.Move(stagingDirectory, instanceDirectory);

            await VerifyInstallationAsync(
                id, target.Id, loader, false, progress, cancellationToken).ConfigureAwait(false);

            Report(progress, 98, "保留原版本", "正在把原版本保存为快照实例…");
            string snapshotId = BuildSnapshotId(versionsRoot, id, current.MinecraftVersion);
            journal = journal with { SnapshotId = snapshotId };
            WriteJournal(journalPath, journal);
            PromoteSnapshot(versionsRoot, backupDirectory, id, current.MinecraftVersion, snapshotId);

            journal = journal with { Completed = true };
            WriteJournal(journalPath, journal);
            File.Delete(journalPath);
            journalCreated = false;
            Report(progress, 100, "更新完成", $"Minecraft {target.Id} 已就绪，原版本已保留为 {snapshotId}。");

            return new InstanceUpdateResult(
                id,
                current.MinecraftVersion,
                target.Id,
                current.LoaderKind,
                loader?.LoaderVersion ?? string.Empty,
                snapshotId);
        }
        catch (Exception updateError)
        {
            try
            {
                Report(progress, 100, "恢复原实例", "更新未完成，正在清理暂存内容并恢复原实例…");
            }
            catch (Exception)
            {
            }
            try
            {
                if (journalCreated || File.Exists(journalPath))
                {
                    UpdateJournal journal = ReadJournal(journalPath) ??
                                            new UpdateJournal(id, stagingDirectory, backupDirectory);
                    RecoverJournal(versionsRoot, journalPath, journal);
                }
                else
                {
                    if (Directory.Exists(stagingDirectory))
                    {
                        Directory.Delete(stagingDirectory, true);
                    }
                }
            }
            catch (Exception rollbackError)
            {
                throw new InvalidOperationException(
                    "实例更新失败，且自动恢复原实例失败。" +
                    $"原实例位置：{instanceDirectory}；备份位置：{backupDirectory}；" +
                    $"暂存位置：{stagingDirectory}。请关闭占用文件的程序后手动恢复。" +
                    $"更新错误：{updateError.Message}；恢复错误：{rollbackError.Message}",
                    new AggregateException(updateError, rollbackError));
            }

            if (updateError is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new InvalidOperationException(
                $"实例更新失败，原实例已恢复或保持未改动。原因：{updateError.Message}", updateError);
        }
        finally
        {
            processLock?.Dispose();
            ActiveTransactions.TryRemove(Path.GetFullPath(journalPath), out _);
            instanceLock.Release();
        }
    }

    private async Task<CurrentInstanceInfo> InspectCurrentInstanceAsync(
        string instanceDirectory,
        string instanceId,
        CancellationToken cancellationToken)
    {
        string jsonPath = Path.Combine(instanceDirectory, instanceId + ".json");
        if (!Directory.Exists(instanceDirectory) || !File.Exists(jsonPath))
        {
            throw new InvalidOperationException($"找不到实例配置：{jsonPath}");
        }

        await new VersionLoader().LoadAsync(_gameDirectory, instanceId, cancellationToken)
            .ConfigureAwait(false);

        await using FileStream stream = File.OpenRead(jsonPath);
        using JsonDocument document = await JsonDocument.ParseAsync(
            stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("实例版本配置的根节点不是 JSON 对象。");
        }

        string inheritsFrom = ReadString(root, "inheritsFrom");
        string declaredId = ReadString(root, "id");
        string minecraftVersion = inheritsFrom.Length > 0
            ? inheritsFrom
            : declaredId.Length > 0 ? declaredId : instanceId;
        ModLoaderKind loaderKind = DetectCurrentLoader(root, inheritsFrom);
        return new CurrentInstanceInfo(minecraftVersion, loaderKind);
    }

    private static void ValidateTarget(
        MinecraftVersion target,
        ModLoaderVersionInfo? loader,
        ModLoaderKind currentLoader)
    {
        if (loader is null)
        {
            if (currentLoader != ModLoaderKind.Vanilla)
            {
                throw new InvalidOperationException(
                    $"原实例使用 {ModLoaderInstaller.DisplayName(currentLoader)}，更新时必须选择兼容的加载器版本。");
            }

            if (string.IsNullOrWhiteSpace(target.Url))
            {
                throw new InvalidOperationException("目标版本缺少元数据下载地址。");
            }

            return;
        }

        if (loader.Kind == ModLoaderKind.Vanilla)
        {
            throw new InvalidOperationException("原版更新不应传入加载器描述。");
        }

        if (currentLoader == ModLoaderKind.Vanilla)
        {
            throw new InvalidOperationException("原版实例不能直接迁移为加载器实例，请新建实例。");
        }

        if (loader.Kind != currentLoader)
        {
            throw new InvalidOperationException(
                $"当前实例使用 {ModLoaderInstaller.DisplayName(currentLoader)}，" +
                "实例更新不支持切换加载器类型，请新建实例。");
        }

        if (!string.Equals(loader.MinecraftVersion, target.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"加载器 {loader.DisplayName} 适配 Minecraft {loader.MinecraftVersion}，" +
                $"不能安装到 Minecraft {target.Id}。");
        }

        if (string.IsNullOrWhiteSpace(loader.LoaderVersion) || string.IsNullOrWhiteSpace(loader.InstallSource))
        {
            throw new InvalidOperationException("目标加载器缺少版本号或安装地址。");
        }
    }

    private static ModLoaderKind DetectCurrentLoader(JsonElement root, string inheritsFrom)
    {
        string json = root.GetRawText();
        bool optifine = json.Contains("optifine", StringComparison.OrdinalIgnoreCase);
        bool quilt = json.Contains("org.quiltmc.loader", StringComparison.OrdinalIgnoreCase) ||
                     json.Contains("quilt-loader", StringComparison.OrdinalIgnoreCase);
        bool fabric = json.Contains("net.fabricmc.loader", StringComparison.OrdinalIgnoreCase) ||
                      json.Contains("fabric-loader", StringComparison.OrdinalIgnoreCase);
        bool neoforge = json.Contains("net.neoforged", StringComparison.OrdinalIgnoreCase) ||
                        json.Contains("neoforge-", StringComparison.OrdinalIgnoreCase);
        bool forge = json.Contains("net.minecraftforge", StringComparison.OrdinalIgnoreCase) ||
                     json.Contains("minecraftforge", StringComparison.OrdinalIgnoreCase);

        int knownFamilies = (optifine ? 1 : 0) + (quilt ? 1 : 0) + (fabric ? 1 : 0) +
                            (neoforge ? 1 : 0) + (forge ? 1 : 0);
        if (knownFamilies > 1)
        {
            throw new InvalidOperationException(
                "当前实例包含复合加载器，无法安全判断更新方式；请新建实例后迁移内容。");
        }

        if (quilt) return ModLoaderKind.Quilt;
        if (fabric) return ModLoaderKind.Fabric;
        if (neoforge) return ModLoaderKind.NeoForge;
        if (forge) return ModLoaderKind.Forge;
        if (optifine) return ModLoaderKind.OptiFine;

        if (inheritsFrom.Length > 0 ||
            json.Contains("liteloader", StringComparison.OrdinalIgnoreCase) ||
            json.Contains("net.minecraft.launchwrapper", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "当前实例使用未知或不受支持的加载器，已停止更新以避免把它误改为原版。");
        }

        return ModLoaderKind.Vanilla;
    }

    private static void EnsureNoReparsePoints(string instanceDirectory)
    {
        List<string> pending = [instanceDirectory];
        while (pending.Count > 0)
        {
            int index = pending.Count - 1;
            string directory = pending[index];
            pending.RemoveAt(index);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "实例目录或其中内容使用了符号链接 / Junction。为避免改变链接语义，当前更新已停止：" + directory);
            }

            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "实例包含符号链接 / Junction。为避免丢失链接目标或改变写入位置，当前更新已停止：" + entry);
                }

                if (Directory.Exists(entry)) pending.Add(entry);
            }
        }
    }

    private async Task InstallVanillaAsync(
        string stagingId,
        byte[] metadata,
        IProgress<InstanceUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        MinecraftDownload download = new(_gameDirectory);
        IProgress<MinecraftInstallProgress>? mapped = progress is null
            ? null
            : new Progress<MinecraftInstallProgress>(value => Report(
                progress,
                12 + Math.Clamp(value.Percentage, 0, 100) * 0.56,
                "安装 Minecraft",
                string.IsNullOrWhiteSpace(value.Detail)
                    ? value.StageName
                    : $"{value.StageName} · {value.Detail}",
                value.BytesPerSecond,
                value.CompletedBytes,
                value.TotalBytes));
        await download.InstallFromMetadataBytesAsync(
            stagingId, metadata, mapped, cancellationToken).ConfigureAwait(false);
    }

    private async Task InstallLoaderAsync(
        string stagingId,
        ModLoaderVersionInfo loader,
        IProgress<InstanceUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        IProgress<ModLoaderInstallProgress>? mapped = progress is null
            ? null
            : new Progress<ModLoaderInstallProgress>(value => Report(
                progress,
                12 + Math.Clamp(value.Percentage, 0, 100) * 0.56,
                $"安装 {ModLoaderInstaller.DisplayName(loader.Kind)}",
                string.IsNullOrWhiteSpace(value.Detail)
                    ? value.StageName
                    : $"{value.StageName} · {value.Detail}",
                value.BytesPerSecond,
                value.CompletedBytes,
                value.TotalBytes));
        await ModLoaderInstaller.InstallAsync(
            loader, _gameDirectory, stagingId, mapped, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RestoreUserContentAsync(
        string sourceDirectory,
        string stagingDirectory,
        string instanceId,
        IProgress<InstanceUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        List<string> files = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !ShouldSkipRestore(sourceDirectory, path, instanceId))
            .ToList();
        long totalBytes = files.Sum(path => new FileInfo(path).Length);
        long completedBytes = 0;

        foreach (string directory in Directory.EnumerateDirectories(
                     sourceDirectory, "*", SearchOption.AllDirectories))
        {
            string relativeDirectory = Path.GetRelativePath(sourceDirectory, directory);
            if (ShouldSkipRestore(sourceDirectory, directory, instanceId)) continue;
            Directory.CreateDirectory(Path.Combine(stagingDirectory, relativeDirectory));
            cancellationToken.ThrowIfCancellationRequested();
        }

        foreach (string sourcePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            string destinationPath = Path.Combine(stagingDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            await using (FileStream source = new(
                             sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                             CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (FileStream destination = new(
                             destinationPath, FileMode.Create, FileAccess.Write, FileShare.None,
                             CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[CopyBufferSize];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    completedBytes += read;
                    double fraction = totalBytes > 0 ? completedBytes / (double)totalBytes : 1;
                    Report(progress, 68 + fraction * 17, "恢复用户内容", relativePath,
                        0, completedBytes, totalBytes);
                }
            }

            File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(sourcePath));
        }

        Report(progress, 85, "恢复用户内容", $"已复制 {files.Count} 个文件，原实例仍未改动。",
            0, totalBytes, totalBytes);
    }

    private static bool ShouldSkipRestore(string sourceDirectory, string path, string instanceId)
    {
        string relative = Path.GetRelativePath(sourceDirectory, path);
        string[] segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return true;
        if (segments[0].Equals("natives", StringComparison.OrdinalIgnoreCase)) return true;
        if (segments.Any(segment => segment.EndsWith(".mofu-download", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return segments.Length == 1 &&
               (segments[0].Equals(instanceId + ".json", StringComparison.OrdinalIgnoreCase) ||
                segments[0].Equals(instanceId + ".jar", StringComparison.OrdinalIgnoreCase));
    }

    private async Task VerifyInstallationAsync(
        string instanceId,
        string targetMinecraftVersion,
        ModLoaderVersionInfo? loader,
        bool downloadMissingLibraries,
        IProgress<InstanceUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, 88, "校验启动档案", "正在解析继承链、客户端与依赖库…");
        LaunchProfile profile = await new VersionLoader().LoadAsync(
            _gameDirectory, instanceId, cancellationToken).ConfigureAwait(false);
        if (profile.IsPartial)
        {
            throw new InvalidOperationException("更新后的启动档案不完整。");
        }

        if (loader is not null &&
            !string.Equals(profile.InheritsFrom, targetMinecraftVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"更新后的加载器仍指向 Minecraft {profile.InheritsFrom}，而不是 {targetMinecraftVersion}。");
        }

        MinecraftDownload download = new(_gameDirectory);
        MinecraftVersionJson metadata = await download.LoadVersionJsonAsync(
            instanceId, cancellationToken).ConfigureAwait(false);
        LibraryResolver resolver = new();

        MinecraftDownloadInfo? client = metadata.Downloads?.Client;
        if (client is not null)
        {
            string clientPath = Path.Combine(
                _gameDirectory, "versions", profile.ClientJarVersionId,
                profile.ClientJarVersionId + ".jar");
            if (!await FileMatchesAsync(
                    client.Size, client.Sha1, clientPath, cancellationToken).ConfigureAwait(false))
            {
                if (!downloadMissingLibraries)
                {
                    throw new InvalidOperationException("基础客户端缺失或校验失败：" + clientPath);
                }

                await DownloadFileAsync(
                    client.Url, client.Size, client.Sha1, "Minecraft 客户端",
                    clientPath, cancellationToken).ConfigureAwait(false);
            }
        }

        IReadOnlyList<LibraryDownloadPlan> plans = resolver.ResolveDownloads(
            metadata.Libraries, MinecraftRuleEvaluator.DefaultFeatures());
        int completed = 0;
        foreach (LibraryDownloadPlan plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = GetLibraryPath(plan.RelativePath);
            if (!await FileMatchesAsync(
                    plan.Size, plan.Sha1, path, cancellationToken).ConfigureAwait(false))
            {
                if (!downloadMissingLibraries)
                {
                    throw new InvalidOperationException("更新后的依赖库缺失或校验失败：" + path);
                }

                await DownloadFileAsync(
                    plan.Url, plan.Size, plan.Sha1, plan.DisplayName,
                    path, cancellationToken).ConfigureAwait(false);
            }

            completed++;
            Report(progress, 88 + 7d * completed / Math.Max(1, plans.Count),
                "校验并补全依赖库", plan.DisplayName);
        }

        await resolver.ResolveAsync(
            profile,
            _gameDirectory,
            MofuCore.Launch.MinecraftRuleEvaluator.CreateDefaultFeatures(true),
            cancellationToken).ConfigureAwait(false);
        foreach (LibraryDownloadPlan plan in plans)
        {
            string path = GetLibraryPath(plan.RelativePath);
            if (!await FileMatchesAsync(
                    plan.Size, plan.Sha1, path, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("依赖库补全后仍然缺失或校验失败：" + path);
            }
        }

        if (client is not null)
        {
            string clientPath = Path.Combine(
                _gameDirectory, "versions", profile.ClientJarVersionId,
                profile.ClientJarVersionId + ".jar");
            if (!await FileMatchesAsync(
                    client.Size, client.Sha1, clientPath, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("基础客户端补全后仍然缺失或校验失败：" + clientPath);
            }
        }

        Report(progress, 95, "校验完成", "客户端、全部依赖库与启动配置均已通过大小和哈希校验。");
    }

    private string GetLibraryPath(string relativePath)
    {
        string librariesRoot = Path.GetFullPath(Path.Combine(_gameDirectory, "libraries"));
        string path = Path.GetFullPath(Path.Combine(librariesRoot, relativePath));
        string prefix = librariesRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                        Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("依赖库路径越出了 libraries 目录：" + relativePath);
        }

        return path;
    }

    private static async Task<bool> FileMatchesAsync(
        long expectedSize,
        string? expectedSha1,
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        if (expectedSize > 0 && new FileInfo(path).Length != expectedSize) return false;
        if (string.IsNullOrWhiteSpace(expectedSha1)) return true;

        await using FileStream stream = new(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(
            Convert.ToHexString(hash), expectedSha1, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DownloadFileAsync(
        string url,
        long expectedSize,
        string? expectedSha1,
        string displayName,
        string path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("文件缺少下载地址：" + displayName);
        }

        byte[] bytes = await DownloadSourceProvider.GetBytesAsync(
            url, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        if (expectedSize > 0 && bytes.LongLength != expectedSize)
        {
            throw new InvalidOperationException(
                $"文件大小校验失败：{displayName}（期望 {expectedSize}，实际 {bytes.LongLength}）。");
        }

        if (!string.IsNullOrWhiteSpace(expectedSha1))
        {
            string actual = Convert.ToHexString(SHA1.HashData(bytes));
            if (!string.Equals(actual, expectedSha1, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("文件 SHA-1 校验失败：" + displayName);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".mofu-download";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string BuildSnapshotId(
        string versionsRoot,
        string instanceId,
        string minecraftVersion)
    {
        string suffix = "-snapshot" + minecraftVersion;
        int keep = Math.Max(8, 120 - suffix.Length);
        string stem = instanceId.Length > keep
            ? instanceId[..keep].TrimEnd('.', ' ')
            : instanceId;
        string baseId = stem + suffix;
        MinecraftDownload.ValidateVersionId(baseId);

        string candidate = baseId;
        for (int index = 2; Directory.Exists(Path.Combine(versionsRoot, candidate)); index++)
        {
            candidate = baseId + "-" + index;
        }

        return candidate;
    }

    private static void PromoteSnapshot(
        string versionsRoot,
        string backupDirectory,
        string instanceId,
        string minecraftVersion,
        string snapshotId)
    {
        string snapshotDirectory = Path.Combine(versionsRoot, snapshotId);
        Directory.Move(backupDirectory, snapshotDirectory);
        RenameVersionIdentity(snapshotDirectory, instanceId, snapshotId);
    }

    private static void RenameVersionIdentity(string directory, string oldId, string newId)
    {
        string oldJson = Path.Combine(directory, oldId + ".json");
        string newJson = Path.Combine(directory, newId + ".json");
        if (File.Exists(oldJson))
        {
            JsonNode? parsed = null;
            try
            {
                parsed = JsonNode.Parse(File.ReadAllText(oldJson));
            }
            catch (JsonException)
            {
            }

            if (parsed is JsonObject root)
            {
                string? declaredId = root["id"] is JsonValue value &&
                                     value.TryGetValue(out string? text)
                    ? text
                    : null;
                if (string.IsNullOrWhiteSpace(declaredId) ||
                    string.Equals(declaredId, oldId, StringComparison.OrdinalIgnoreCase))
                {
                    root["id"] = newId;
                }

                WriteVersionJson(newJson, root);
                File.Delete(oldJson);
            }
            else
            {
                File.Move(oldJson, newJson, true);
            }
        }

        string oldJar = Path.Combine(directory, oldId + ".jar");
        if (File.Exists(oldJar))
        {
            File.Move(oldJar, Path.Combine(directory, newId + ".jar"), true);
        }
    }

    private static void RestoreSnapshotIdentity(
        string snapshotDirectory,
        string snapshotId,
        string instanceId)
    {
        string snapshotJson = Path.Combine(snapshotDirectory, snapshotId + ".json");
        string instanceJson = Path.Combine(snapshotDirectory, instanceId + ".json");
        if (File.Exists(snapshotJson))
        {
            bool restored = false;
            try
            {
                JsonNode? parsed = JsonNode.Parse(File.ReadAllText(snapshotJson));
                if (parsed is JsonObject root &&
                    root["id"] is JsonValue value &&
                    value.TryGetValue(out string? declaredId) &&
                    string.Equals(declaredId, snapshotId, StringComparison.OrdinalIgnoreCase))
                {
                    root["id"] = instanceId;
                    WriteVersionJson(instanceJson, root);
                    File.Delete(snapshotJson);
                    restored = true;
                }
            }
            catch (JsonException)
            {
            }

            if (!restored && File.Exists(snapshotJson))
            {
                File.Move(snapshotJson, instanceJson, true);
            }
        }

        string snapshotJar = Path.Combine(snapshotDirectory, snapshotId + ".jar");
        if (File.Exists(snapshotJar))
        {
            File.Move(snapshotJar, Path.Combine(snapshotDirectory, instanceId + ".jar"), true);
        }
    }

    private static void WriteVersionJson(string path, JsonObject root)
    {
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false,
        }), new UTF8Encoding(false));
    }

    private static void PrepareStagedInstance(
        string stagingDirectory,
        string stagingId,
        string instanceId)
    {
        string sourceJson = Path.Combine(stagingDirectory, stagingId + ".json");
        string targetJson = Path.Combine(stagingDirectory, instanceId + ".json");
        if (!File.Exists(sourceJson))
        {
            throw new InvalidOperationException("暂存实例缺少版本配置：" + sourceJson);
        }

        JsonNode? parsed = JsonNode.Parse(File.ReadAllText(sourceJson));
        if (parsed is not JsonObject root)
        {
            throw new InvalidOperationException("暂存实例的版本配置不是 JSON 对象。");
        }

        if (string.Equals(root["id"]?.GetValue<string>(), stagingId, StringComparison.OrdinalIgnoreCase))
        {
            root["id"] = instanceId;
        }

        File.WriteAllText(targetJson, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false,
        }), new UTF8Encoding(false));
        if (!string.Equals(sourceJson, targetJson, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(sourceJson);
        }

        string sourceJar = Path.Combine(stagingDirectory, stagingId + ".jar");
        string targetJar = Path.Combine(stagingDirectory, instanceId + ".jar");
        if (File.Exists(sourceJar))
        {
            File.Move(sourceJar, targetJar, true);
        }
    }

    private static void WriteJournal(string journalPath, UpdateJournal journal)
    {
        string temporary = journalPath + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(journal), new UTF8Encoding(false));
            File.Move(temporary, journalPath, true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (IOException)
            {
            }
        }
    }

    private static UpdateJournal? TryReadJournal(string journalPath)
    {
        try
        {
            return ReadJournal(journalPath);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static UpdateJournal? ReadJournal(string journalPath)
    {
        try
        {
            UpdateJournal? journal = JsonSerializer.Deserialize<UpdateJournal>(
                File.ReadAllText(journalPath));
            if (journal is null || string.IsNullOrWhiteSpace(journal.InstanceId))
            {
                throw new InvalidOperationException("实例更新日志内容无效：" + journalPath);
            }

            return journal;
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException("实例更新日志损坏，未执行任何自动删除：" + journalPath, error);
        }
    }

    /// <summary>按提交日志回滚：备份还在就恢复备份；已转正就按快照还原；完成态只清理日志。</summary>
    private static void RecoverJournal(
        string versionsRoot,
        string journalPath,
        UpdateJournal journal)
    {
        MinecraftDownload.ValidateVersionId(journal.InstanceId);
        string instanceDirectory = Path.Combine(versionsRoot, journal.InstanceId);
        string stagingDirectory = SafeJournalPath(versionsRoot, journal.StagingDirectory);
        string backupDirectory = SafeJournalPath(versionsRoot, journal.BackupDirectory);

        if (journal.Completed)
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, true);
            }

            File.Delete(journalPath);
            return;
        }

        if (Directory.Exists(backupDirectory))
        {
            if (Directory.Exists(instanceDirectory))
            {
                Directory.Delete(instanceDirectory, true);
            }

            Directory.Move(backupDirectory, instanceDirectory);
        }
        else if (journal.SnapshotId is { Length: > 0 } snapshotId)
        {
            MinecraftDownload.ValidateVersionId(snapshotId);
            string snapshotDirectory = SafeJournalPath(
                versionsRoot, Path.Combine(versionsRoot, snapshotId));
            if (Directory.Exists(snapshotDirectory))
            {
                if (Directory.Exists(instanceDirectory))
                {
                    Directory.Delete(instanceDirectory, true);
                }

                RestoreSnapshotIdentity(snapshotDirectory, snapshotId, journal.InstanceId);
                Directory.Move(snapshotDirectory, instanceDirectory);
            }
            else if (!Directory.Exists(instanceDirectory))
            {
                throw new InvalidOperationException(
                    "原实例与快照目录都不存在，自动恢复已停止。暂存内容保留在：" + stagingDirectory);
            }
        }
        else if (!Directory.Exists(instanceDirectory))
        {
            throw new InvalidOperationException(
                "原实例与备份目录都不存在，自动恢复已停止。暂存内容保留在：" + stagingDirectory);
        }

        if (Directory.Exists(stagingDirectory))
        {
            Directory.Delete(stagingDirectory, true);
        }

        File.Delete(journalPath);
    }

    private static string SafeJournalPath(string versionsRoot, string path)
    {
        string root = Path.GetFullPath(versionsRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("实例更新日志包含越界路径，未执行自动删除：" + path);
        }

        return fullPath;
    }

    private static string ReadString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out JsonElement value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;
    }

    private static void Report(
        IProgress<InstanceUpdateProgress>? progress,
        double percentage,
        string stage,
        string detail,
        double bytesPerSecond = 0,
        long completedBytes = 0,
        long totalBytes = 0)
    {
        progress?.Report(new InstanceUpdateProgress(
            stage, detail, Math.Clamp(percentage, 0, 100),
            bytesPerSecond, completedBytes, totalBytes));
    }

    private sealed record CurrentInstanceInfo(string MinecraftVersion, ModLoaderKind LoaderKind);

    private sealed record UpdateJournal(
        string InstanceId,
        string StagingDirectory,
        string BackupDirectory,
        string? SnapshotId = null,
        bool Completed = false);
}
