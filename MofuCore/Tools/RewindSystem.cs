using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MofuCore.Config;

namespace MofuCore.Tools;

/// <summary>
/// Rewind 快照 / 时间线回滚系统：把任意目标目录（世界存档或实例游戏目录）的多次状态串成时间线。
/// 采用文件级内容寻址（类似 Git）：按 SHA-256 去重存进全局 blobs，每个快照是一份
/// 「相对路径 → 数据块摘要」清单；回滚时把数据块复制回目标目录，快照本身不可变。
/// 仓库位于 {DataRoot}/Mofumofu/rewind（blobs/ 与 repos/&lt;repoKey&gt;/manifests/）。
/// </summary>
public class RewindSystem
{
    public const string RootDirectoryName = "rewind";
    private const string BlobsDirectoryName = "blobs";
    private const string ReposDirectoryName = "repos";
    private const string ManifestsDirectoryName = "manifests";
    private const string SessionLockName = "session.lock";
    private const string RestoreSuffix = ".mofu-restore-";
    private const string BackupSuffix = ".mofu-rollback-backup-";
    private const int CopyBufferSize = 128 * 1024;
    private const int SummaryRecentColors = 12;

    public const int DefaultMaximumAutoSnapshots = 10;
    public const long DefaultMaximumTotalBytes = 4L << 30;

    private static readonly string[] InstanceIgnoredTopDirectories =
        ["libraries", "assets", "logs", "crash-reports", "cache"];

    private static readonly HashSet<string> AllowedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "primary", "secondary", "success", "warning", "danger",
    };

    private static readonly JsonSerializerOptions WriteJsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions ReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly object SyncRoot = new();
    private static readonly object BlobPinLock = new();
    private static readonly object GcLock = new();
    private static readonly Dictionary<string, SemaphoreSlim> RepoLocks = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> BlobPins = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, RewindScheduler> Schedulers = new(StringComparer.Ordinal);

    private static string __dataRoot = AppContext.BaseDirectory;

    /// <summary>启动器数据根目录（默认跟随程序目录）；快照仓库位于 {DataRoot}/Mofumofu/rewind。</summary>
    public static string DataRoot
    {
        get => __dataRoot;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                __dataRoot = value.Trim();
            }
        }
    }

    /// <summary>Rewind 仓库根目录。</summary>
    public static string RewindDirectory => Path.Combine(__dataRoot, "Mofumofu", RootDirectoryName);

    /// <summary>全局内容寻址数据块目录。</summary>
    public static string BlobsDirectory => Path.Combine(RewindDirectory, BlobsDirectoryName);

    /// <summary>各目标清单仓库的父目录。</summary>
    public static string ReposRootDirectory => Path.Combine(RewindDirectory, ReposDirectoryName);

    /// <summary>列出某个目录（世界存档或实例游戏目录）的全部快照，新的在前。</summary>
    public static IReadOnlyList<RewindSnapshot> ListSnapshots(string targetDirectory)
    {
        return ListSnapshotsAsync(targetDirectory).GetAwaiter().GetResult();
    }

    /// <summary>异步列出快照，新的在前。</summary>
    public static Task<IReadOnlyList<RewindSnapshot>> ListSnapshotsAsync(
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalized = NormalizeTargetDirectory(targetDirectory);
            return (IReadOnlyList<RewindSnapshot>)ListSnapshotsLocked(RepoDirectory(normalized));
        }, cancellationToken);
    }

    /// <summary>按天分组的时间线（新的在前）。</summary>
    public static RewindTimeline GetTimeline(string targetDirectory)
    {
        return BuildTimeline(ListSnapshots(targetDirectory));
    }

    /// <summary>异步获取按天分组的时间线。</summary>
    public static async Task<RewindTimeline> GetTimelineAsync(
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<RewindSnapshot> snapshots = await ListSnapshotsAsync(targetDirectory, cancellationToken).ConfigureAwait(false);
        return BuildTimeline(snapshots);
    }

    /// <summary>
    /// 把快照列表按天分组（今天 / 昨天 / X 月 X 日 / XXXX 年 X 月 X 日）。
    /// </summary>
    public static RewindTimeline BuildTimeline(IEnumerable<RewindSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        List<RewindSnapshot> ordered = snapshots
            .OrderByDescending(snapshot => snapshot.CreatedAt)
            .ThenByDescending(snapshot => snapshot.Id, StringComparer.Ordinal)
            .ToList();

        DateTime today = DateTime.Now.Date;
        RewindTimeline timeline = new()
        {
            SnapshotCount = ordered.Count,
            TotalSize = ordered.Sum(snapshot => snapshot.TotalSize),
            AddedSize = ordered.Sum(snapshot => snapshot.AddedSize),
            LatestAt = ordered.Count > 0 ? ordered[0].CreatedAt : null,
            OldestAt = ordered.Count > 0 ? ordered[^1].CreatedAt : null,
        };

        List<RewindDayGroup> days = [];
        foreach (IGrouping<DateTime, RewindSnapshot> group in ordered.GroupBy(snapshot => snapshot.CreatedAt.LocalDateTime.Date))
        {
            days.Add(new RewindDayGroup
            {
                Date = group.Key,
                Label = DayLabel(group.Key, today),
                Count = group.Count(),
                TotalSize = group.Sum(snapshot => snapshot.TotalSize),
                AddedSize = group.Sum(snapshot => snapshot.AddedSize),
                Snapshots = group.ToList(),
            });
        }

        timeline.Days = days;
        return timeline;
    }

    /// <summary>
    /// 给目标目录当前状态创建一个手动快照。
    /// </summary>
    public static Task<RewindSnapshot> CreateSnapshotAsync(
        string targetDirectory,
        RewindKind kind = RewindKind.Save,
        string? label = null,
        string? color = null,
        CancellationToken cancellationToken = default)
    {
        return CreateSnapshotWithReasonAsync(
            targetDirectory, kind, label, color, RewindReasons.Manual, DefaultRetention(), cancellationToken);
    }

    /// <summary>
    /// 启动前的"还原点"快照：内容与最近一份快照一致时跳过（连续启动不堆相同的还原点）。
    /// 跳过或目录不存在时返回 null。
    /// </summary>
    public static async Task<RewindSnapshot?> CreateLaunchSnapshotAsync(
        string targetDirectory,
        RewindKind kind = RewindKind.Save,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            return null;
        }

        if (!ShouldCreateLaunchSnapshot(targetDirectory, kind, out string normalized))
        {
            return null;
        }

        return await CreateSnapshotWithReasonAsync(
            normalized, kind, "启动前自动快照", string.Empty, RewindReasons.BeforeLaunch, DefaultRetention(), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 定时快照（由 <see cref="RewindScheduler"/> 调用）。
    /// </summary>
    public static Task<RewindSnapshot> CreateScheduledSnapshotAsync(
        string targetDirectory,
        RewindKind kind = RewindKind.Save,
        CancellationToken cancellationToken = default)
    {
        return CreateSnapshotWithReasonAsync(
            targetDirectory, kind, "定时自动快照", string.Empty, RewindReasons.Scheduled, DefaultRetention(), cancellationToken);
    }

    private static async Task<RewindSnapshot> CreateSnapshotWithReasonAsync(
        string targetDirectory,
        RewindKind kind,
        string? label,
        string? color,
        string reason,
        RewindRetention retention,
        CancellationToken cancellationToken)
    {
        string normalized = NormalizeTargetDirectory(targetDirectory);
        SemaphoreSlim gate = RepoGate(normalized);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CreateSnapshotLockedAsync(kind, normalized, label, reason, color, retention, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 把目标目录回滚到指定快照。回滚前自动创建安全快照（返回值里带上，便于反悔）。
    /// </summary>
    public static async Task<RewindRollbackResult> RollbackAsync(
        string targetDirectory,
        string snapshotId,
        RewindKind kind = RewindKind.Save,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            throw new ArgumentException("快照 id 不能为空。", nameof(snapshotId));
        }

        string normalized = NormalizeTargetDirectory(targetDirectory);
        SemaphoreSlim gate = RepoGate(normalized);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string repo = RepoDirectory(normalized);
            RewindSnapshotManifest target = LoadManifest(repo, snapshotId);

            // 回滚前先给当前状态打安全快照，失败/后悔都能还原
            RewindSnapshot safety = await CreateSnapshotLockedAsync(
                kind, normalized, "回滚前自动快照", RewindReasons.BeforeRollback, string.Empty, DefaultRetention(), cancellationToken)
                .ConfigureAwait(false);

            try
            {
                await RestoreSnapshotAtomicallyAsync(kind, normalized, target, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    $"回滚失败：{e.Message}（已保留回滚前快照 {safety.Id}）", e);
            }

            return new RewindRollbackResult
            {
                TargetDirectory = normalized,
                SafetySnapshot = safety,
                RestoredSnapshot = ToSnapshot(target),
            };
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 删除指定快照，并回收不再被任何快照引用的数据块。快照不存在时返回 true（幂等）。
    /// </summary>
    public static async Task<bool> DeleteSnapshotAsync(
        string targetDirectory,
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            return false;
        }

        string normalized = NormalizeTargetDirectory(targetDirectory);
        SemaphoreSlim gate = RepoGate(normalized);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string manifestPath = Path.Combine(RepoDirectory(normalized), ManifestsDirectoryName, snapshotId + ".json");
            if (!File.Exists(manifestPath))
            {
                return true;
            }

            File.Delete(manifestPath);
            GarbageCollectBlobs();
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 删除快照的同步版本。
    /// </summary>
    public static bool DeleteSnapshot(string targetDirectory, string snapshotId)
    {
        return DeleteSnapshotAsync(targetDirectory, snapshotId).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 更新快照的标记颜色（语义色键：primary/secondary/success/warning/danger；空串恢复默认色）。
    /// </summary>
    public static async Task<bool> SetSnapshotColorAsync(
        string targetDirectory,
        string snapshotId,
        string? color,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            return false;
        }

        string normalized = NormalizeTargetDirectory(targetDirectory);
        SemaphoreSlim gate = RepoGate(normalized);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string repo = RepoDirectory(normalized);
            RewindSnapshotManifest manifest = LoadManifest(repo, snapshotId);
            manifest.Color = NormalizeColor(color);
            WriteManifest(repo, manifest);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 按保留策略清理指定目标的快照，返回删除数量。
    /// 带用户标记（备注或颜色）的快照永不自动删除。
    /// </summary>
    public static async Task<int> PruneAsync(
        string targetDirectory,
        RewindRetention? retention = null,
        CancellationToken cancellationToken = default)
    {
        string normalized = NormalizeTargetDirectory(targetDirectory);
        SemaphoreSlim gate = RepoGate(normalized);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string repo = RepoDirectory(normalized);
            if (!Directory.Exists(Path.Combine(repo, ManifestsDirectoryName)))
            {
                return 0;
            }

            return PruneLocked(repo, string.Empty, retention ?? DefaultRetention());
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 跨全部仓库的 Rewind 汇总（主页小组件用）：快照数、数据块占用、预算、最近颜色。
    /// </summary>
    public static RewindSummary GetSummary()
    {
        return GetSummaryAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// 异步计算 Rewind 全局汇总。
    /// </summary>
    public static Task<RewindSummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            RewindSummary summary = new() { BudgetBytes = DefaultMaximumTotalBytes };
            List<(DateTimeOffset CreatedAt, string Color)> recents = [];

            if (Directory.Exists(ReposRootDirectory))
            {
                foreach (string repoDirectory in Directory.GetDirectories(ReposRootDirectory))
                {
                    foreach (RewindSnapshotManifest manifest in LoadManifests(repoDirectory))
                    {
                        summary.SnapshotCount++;
                        if (summary.LastSnapshotAt is null || manifest.CreatedAt > summary.LastSnapshotAt)
                        {
                            summary.LastSnapshotAt = manifest.CreatedAt;
                        }

                        recents.Add((manifest.CreatedAt, manifest.Color));
                    }
                }
            }

            summary.BlobBytes = DirectorySize(BlobsDirectory);
            summary.RecentColors = recents
                .OrderByDescending(item => item.CreatedAt)
                .Take(SummaryRecentColors)
                .Select(item => item.Color)
                .ToList();
            return summary;
        }, cancellationToken);
    }

    /// <summary>
    /// 列出实例 saves/ 下的世界（按最后修改时间降序），并附带各自的快照统计。
    /// </summary>
    public static IReadOnlyList<RewindWorldInfo> ListWorlds(string instanceDirectory)
    {
        List<RewindWorldInfo> worlds = [];
        if (string.IsNullOrWhiteSpace(instanceDirectory))
        {
            return worlds;
        }

        string savesDirectory = Path.Combine(instanceDirectory.Trim(), "saves");
        if (!Directory.Exists(savesDirectory))
        {
            return worlds;
        }

        foreach (string worldDirectory in Directory.GetDirectories(savesDirectory))
        {
            string name = Path.GetFileName(worldDirectory);
            if (name.StartsWith('.'))
            {
                continue;
            }

            DateTimeOffset lastModified = Directory.GetLastWriteTimeUtc(worldDirectory);
            string levelData = Path.Combine(worldDirectory, "level.dat");
            if (File.Exists(levelData))
            {
                DateTimeOffset levelModified = File.GetLastWriteTimeUtc(levelData);
                if (levelModified > lastModified)
                {
                    lastModified = levelModified;
                }
            }

            IReadOnlyList<RewindSnapshot> snapshots;
            try
            {
                snapshots = ListSnapshots(worldDirectory);
            }
            catch (Exception)
            {
                snapshots = [];
            }

            worlds.Add(new RewindWorldInfo
            {
                Name = name,
                DirectoryPath = worldDirectory,
                LastModified = lastModified,
                SnapshotCount = snapshots.Count,
                LatestSnapshotAt = snapshots.Count > 0 ? snapshots[0].CreatedAt : null,
            });
        }

        return worlds
            .OrderByDescending(world => world.LastModified)
            .ToList();
    }

    /// <summary>
    /// 启动某个目标的定时快照（同目标重复启动会替换旧调度器）。
    /// interval 为空时用 settings 里的间隔。
    /// </summary>
    public static RewindScheduler StartScheduler(
        string targetDirectory,
        TimeSpan? interval = null,
        RewindKind kind = RewindKind.Save)
    {
        string normalized = NormalizeTargetDirectory(targetDirectory);
        TimeSpan actualInterval = interval ?? TimeSpan.FromSeconds(LoadSettings().AutoSnapshotIntervalSeconds);
        if (actualInterval <= TimeSpan.Zero)
        {
            actualInterval = TimeSpan.FromMinutes(15);
        }

        lock (SyncRoot)
        {
            string key = RepoKey(normalized);
            if (Schedulers.Remove(key, out RewindScheduler? existing))
            {
                existing.Dispose();
            }

            RewindScheduler scheduler = new(normalized, kind, actualInterval);
            Schedulers[key] = scheduler;
            scheduler.Start();
            return scheduler;
        }
    }

    /// <summary>停止全部定时快照调度器。</summary>
    public static void StopAllSchedulers()
    {
        lock (SyncRoot)
        {
            foreach (RewindScheduler scheduler in Schedulers.Values)
            {
                scheduler.Dispose();
            }

            Schedulers.Clear();
        }
    }

    /// <summary>读取 Rewind 设置（config.yaml 的 rewind* 键）。</summary>
    public static RewindSettings LoadSettings()
    {
        RewindSettings settings = new()
        {
            AutoSnapshotEnabled = ReadBool("rewindAutoSnapshotEnabled", false),
            AutoSnapshotIntervalSeconds = Math.Clamp(ReadInt("rewindAutoSnapshotIntervalSeconds", 900), 30, 86400),
            MaximumAutoSnapshots = Math.Clamp(ReadInt("rewindMaxAutoSnapshots", DefaultMaximumAutoSnapshots), 1, 500),
            KeepLastCount = Math.Max(0, ReadInt("rewindKeepLastCount", 0)),
            KeepDays = Math.Max(0, ReadInt("rewindKeepDays", 0)),
        };

        if (long.TryParse(YamlRead.GetValue(YamlDomain.Config, "rewindMaximumBytes"), out long maximumBytes)
            && maximumBytes >= 64L * 1024 * 1024)
        {
            settings.MaximumBytes = maximumBytes;
        }

        return settings;
    }

    /// <summary>保存 Rewind 设置（单次事务写入 config.yaml）。</summary>
    public static bool SaveSettings(RewindSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return YamlRead.UpdateInTransaction(YamlDomain.Config, document =>
        {
            document["rewindAutoSnapshotEnabled"] = settings.AutoSnapshotEnabled ? "True" : "False";
            document["rewindAutoSnapshotIntervalSeconds"] = Math.Clamp(settings.AutoSnapshotIntervalSeconds, 30, 86400).ToString();
            document["rewindMaxAutoSnapshots"] = Math.Clamp(settings.MaximumAutoSnapshots, 1, 500).ToString();
            document["rewindKeepLastCount"] = Math.Max(0, settings.KeepLastCount).ToString();
            document["rewindKeepDays"] = Math.Max(0, settings.KeepDays).ToString();
            document["rewindMaximumBytes"] = Math.Max(64L * 1024 * 1024, settings.MaximumBytes).ToString();
            return true;
        });
    }

    /// <summary>默认保留策略。</summary>
    public static RewindRetention DefaultRetention()
    {
        return new RewindRetention
        {
            MaximumAutoSnapshots = DefaultMaximumAutoSnapshots,
            MaximumBytes = DefaultMaximumTotalBytes,
        };
    }

    // ---- 快照创建 ----

    private static async Task<RewindSnapshot> CreateSnapshotLockedAsync(
        RewindKind kind,
        string directory,
        string? label,
        string reason,
        string? color,
        RewindRetention retention,
        CancellationToken cancellationToken)
    {
        string repo = RepoDirectory(directory);
        string manifestsDirectory = Path.Combine(repo, ManifestsDirectoryName);
        Directory.CreateDirectory(BlobsDirectory);
        Directory.CreateDirectory(manifestsDirectory);

        RewindSnapshotManifest? previous = LatestManifest(repo);
        Dictionary<string, string> previousHashes = new(StringComparer.Ordinal);
        if (previous is not null)
        {
            foreach (RewindSnapshotFile file in previous.Files)
            {
                previousHashes[file.Path] = file.Hash;
            }
        }

        List<RewindSnapshotFile> files = [];
        HashSet<string> createdBlobs = new(StringComparer.Ordinal);
        List<Action> blobPins = [];
        long totalSize = 0;
        long addedSize = 0;
        int addedFiles = 0;

        try
        {
            foreach ((string fullPath, string relativePath) in EnumerateFiles(directory, kind))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileInfo info = new(fullPath);
                // 变更检测只看内容哈希：同尺寸同 mtime 的改动也必须能发现
                string hash = await HashFileAsync(fullPath, cancellationToken).ConfigureAwait(false);

                // 跨仓库 GC 只持单仓库锁：blob 已落盘但清单未写时先钉住，避免被别处回收
                blobPins.Add(PinBlob(hash));
                if (!BlobExists(hash))
                {
                    await StoreBlobAsync(fullPath, hash, cancellationToken).ConfigureAwait(false);
                    if (createdBlobs.Add(hash))
                    {
                        addedSize += info.Length;
                    }
                }
                else
                {
                    createdBlobs.Add(hash);
                }

                if (!previousHashes.TryGetValue(relativePath, out string? previousHash)
                    || !string.Equals(previousHash, hash, StringComparison.Ordinal))
                {
                    addedFiles++;
                }

                totalSize += info.Length;
                files.Add(new RewindSnapshotFile
                {
                    Path = relativePath,
                    Hash = hash,
                    Size = info.Length,
                    ModTime = ToUnixNanoseconds(info.LastWriteTimeUtc),
                });
            }

            files.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));

            RewindSnapshotManifest manifest = new()
            {
                Id = NewSnapshotId(manifestsDirectory),
                CreatedAt = DateTimeOffset.Now,
                Label = label?.Trim() ?? string.Empty,
                Reason = reason,
                Color = NormalizeColor(color),
                Kind = kind == RewindKind.Instance ? "instance" : "save",
                FileCount = files.Count,
                TotalSize = totalSize,
                AddedSize = addedSize,
                AddedFiles = addedFiles,
                WorldName = Path.GetFileName(directory),
                Files = files,
            };
            WriteManifest(repo, manifest);

            PruneLocked(repo, manifest.Id, retention);
            return ToSnapshot(manifest);
        }
        finally
        {
            // 清单落盘后才解除钉住：期间任何仓库的 GC 都不会回收这些数据块
            foreach (Action unpin in blobPins)
            {
                unpin();
            }
        }
    }

    private static IEnumerable<(string FullPath, string RelativePath)> EnumerateFiles(string root, RewindKind kind)
    {
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            string[] subdirectories;
            string[] files;
            try
            {
                subdirectories = Directory.GetDirectories(directory);
                files = Directory.GetFiles(directory);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (string subdirectory in subdirectories)
            {
                string relative = ToRelativePath(root, subdirectory);
                if (!string.IsNullOrEmpty(relative) && ShouldIgnore(kind, relative))
                {
                    continue;
                }

                pending.Push(subdirectory);
            }

            foreach (string file in files)
            {
                string relative = ToRelativePath(root, file);
                if (string.IsNullOrEmpty(relative) || ShouldIgnore(kind, relative))
                {
                    continue;
                }

                yield return (file, relative);
            }
        }
    }

    private static bool ShouldIgnore(RewindKind kind, string relativePath)
    {
        string[] segments = relativePath.Split('/');
        if (segments.Any(segment => string.Equals(segment, SessionLockName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (kind != RewindKind.Instance)
        {
            return false;
        }

        if (InstanceIgnoredTopDirectories.Any(name => string.Equals(name, segments[0], StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // versions/<id>/natives[-<hash>]/ 每次启动重新解压，跳过
        return segments.Any(segment =>
            string.Equals(segment, "natives", StringComparison.OrdinalIgnoreCase)
            || segment.StartsWith("natives-", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldCreateLaunchSnapshot(string targetDirectory, RewindKind kind, out string normalized)
    {
        normalized = string.Empty;
        if (!Directory.Exists(targetDirectory))
        {
            return false;
        }

        try
        {
            normalized = NormalizeTargetDirectory(targetDirectory);
        }
        catch (Exception)
        {
            return false;
        }

        RewindSnapshotManifest? latest = LatestManifest(RepoDirectory(normalized));
        if (latest is null)
        {
            return true;
        }

        // 基准取"最近快照里记录的文件 mtime 最大值"，与文件系统同源同粒度
        DateTimeOffset baseline = LaunchSnapshotBaseline(latest);
        return DirectoryModifiedAfter(normalized, kind, baseline);
    }

    private static DateTimeOffset LaunchSnapshotBaseline(RewindSnapshotManifest manifest)
    {
        long newest = 0;
        foreach (RewindSnapshotFile file in manifest.Files)
        {
            if (file.ModTime > newest)
            {
                newest = file.ModTime;
            }
        }

        if (newest <= 0)
        {
            return manifest.CreatedAt;
        }

        DateTimeOffset moment = FromUnixNanoseconds(newest);
        return moment > manifest.CreatedAt ? manifest.CreatedAt : moment;
    }

    private static bool DirectoryModifiedAfter(string directory, RewindKind kind, DateTimeOffset moment)
    {
        foreach ((string fullPath, _) in EnumerateFiles(directory, kind))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(fullPath) > moment.UtcDateTime)
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // 读不到的项按没变化处理
            }
        }

        return false;
    }

    // ---- 回滚 ----

    private static async Task RestoreSnapshotAtomicallyAsync(
        RewindKind kind,
        string targetDirectory,
        RewindSnapshotManifest manifest,
        CancellationToken cancellationToken)
    {
        string parent = Path.GetDirectoryName(targetDirectory)
                        ?? throw new InvalidOperationException("目标目录没有父目录：" + targetDirectory);
        string baseName = Path.GetFileName(targetDirectory);
        string staging = Path.Combine(parent, RestoreSuffix + baseName);
        string backup = Path.Combine(parent, BackupSuffix + baseName);
        HashSet<string> preserved = kind == RewindKind.Instance
            ? new HashSet<string>(InstanceIgnoredTopDirectories, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 上次中断留下的残骸：目标是"目标目录没了、备份还在"，数据都在，让用户手动恢复
        if (!Directory.Exists(targetDirectory) && Directory.Exists(backup))
        {
            throw new InvalidOperationException(
                $"检测到上次回滚在换名途中中断：{baseName} 缺失，原内容在 {backup}，快照内容与保留目录在 {staging}，请手动恢复后重试");
        }

        SalvagePreservedDirectories(staging, targetDirectory, preserved);

        Directory.CreateDirectory(staging);
        List<string> movedPreserved = [];
        bool stagingAlive = true;
        try
        {
            await MaterializeSnapshotAsync(staging, manifest, cancellationToken).ConfigureAwait(false);

            // 快照不包含的顶层目录先搬进临时目录，换名后随新目录一起归位
            foreach (string name in preserved)
            {
                string source = Path.Combine(targetDirectory, name);
                if (!Directory.Exists(source))
                {
                    continue;
                }

                string destination = Path.Combine(staging, name);
                if (Directory.Exists(destination))
                {
                    Directory.Delete(destination, true);
                }

                Directory.Move(source, destination);
                movedPreserved.Add(name);
            }

            // 旧备份（上次中断残留）先清掉，否则 Windows 上改名会失败
            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, true);
            }

            Directory.Move(targetDirectory, backup);
            try
            {
                Directory.Move(staging, targetDirectory);
            }
            catch (Exception e)
            {
                // 第二步失败：把原目录改回来，保持"回滚没发生"的状态
                try
                {
                    Directory.Move(backup, targetDirectory);
                }
                catch (Exception restoreError)
                {
                    throw new InvalidOperationException(
                        $"{e.Message}（原目录在 {backup}，保留目录在 {staging}，请手动改回 {targetDirectory}）", restoreError);
                }

                MovePreservedBack(staging, targetDirectory, movedPreserved);
                throw;
            }

            stagingAlive = false;
            Directory.Delete(backup, true);
        }
        catch
        {
            MovePreservedBack(staging, targetDirectory, movedPreserved);
            throw;
        }
        finally
        {
            if (stagingAlive && Directory.Exists(staging))
            {
                try
                {
                    Directory.Delete(staging, true);
                }
                catch (Exception)
                {
                    // 清理失败不影响回滚结果
                }
            }
        }
    }

    private static async Task MaterializeSnapshotAsync(
        string stagingDirectory,
        RewindSnapshotManifest manifest,
        CancellationToken cancellationToken)
    {
        foreach (RewindSnapshotFile file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string source = BlobPath(file.Hash);
            if (!File.Exists(source))
            {
                throw new InvalidOperationException("快照数据块缺失：" + file.Hash);
            }

            string destination = ResolveWithin(stagingDirectory, file.Path);
            string? directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await CopyFileAsync(source, destination, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void SalvagePreservedDirectories(string staging, string targetDirectory, HashSet<string> preserved)
    {
        if (!Directory.Exists(staging))
        {
            return;
        }

        try
        {
            foreach (string entry in Directory.GetDirectories(staging))
            {
                if (!preserved.Contains(Path.GetFileName(entry)))
                {
                    continue;
                }

                string destination = Path.Combine(targetDirectory, Path.GetFileName(entry));
                if (!Directory.Exists(destination))
                {
                    Directory.Move(entry, destination);
                }
            }
        }
        catch (Exception)
        {
            // 恢复失败时保持原样，交给下面的整体清理
        }
        finally
        {
            try
            {
                Directory.Delete(staging, true);
            }
            catch (Exception)
            {
                // 清理失败不影响后续流程
            }
        }
    }

    private static void MovePreservedBack(string staging, string targetDirectory, List<string> moved)
    {
        foreach (string name in moved)
        {
            string source = Path.Combine(staging, name);
            string destination = Path.Combine(targetDirectory, name);
            if (Directory.Exists(destination))
            {
                continue;
            }

            try
            {
                Directory.Move(source, destination);
            }
            catch (Exception)
            {
                // 保留目录都是可再生内容，搬不回去也不影响数据安全
            }
        }
    }

    // ---- 保留策略 ----

    private static int PruneLocked(string repo, string keepId, RewindRetention retention)
    {
        List<RewindSnapshotManifest> manifests = LoadManifests(repo);
        if (manifests.Count == 0)
        {
            return 0;
        }

        manifests.Sort(CompareNewestFirst);
        HashSet<string> remove = new(StringComparer.Ordinal);

        // 1. 自动快照（回滚前/启动前/定时）只保留最近 N 个
        int autoKept = 0;
        foreach (RewindSnapshotManifest manifest in manifests)
        {
            if (!RewindReasons.IsAutomatic(manifest.Reason))
            {
                continue;
            }

            autoKept++;
            if (string.Equals(manifest.Id, keepId, StringComparison.Ordinal))
            {
                continue;
            }

            if (autoKept > Math.Max(1, retention.MaximumAutoSnapshots))
            {
                remove.Add(manifest.Id);
            }
        }

        // 2. 保留最近 N 份（显式策略）
        if (retention.KeepLastCount > 0)
        {
            for (int index = retention.KeepLastCount; index < manifests.Count; index++)
            {
                RewindSnapshotManifest manifest = manifests[index];
                if (string.Equals(manifest.Id, keepId, StringComparison.Ordinal) || IsProtected(manifest))
                {
                    continue;
                }

                remove.Add(manifest.Id);
            }
        }

        // 3. 只保留最近 N 天（显式策略）
        if (retention.KeepDays > 0)
        {
            DateTimeOffset cutoff = DateTimeOffset.Now.AddDays(-retention.KeepDays);
            foreach (RewindSnapshotManifest manifest in manifests)
            {
                if (manifest.CreatedAt >= cutoff
                    || string.Equals(manifest.Id, keepId, StringComparison.Ordinal)
                    || IsProtected(manifest))
                {
                    continue;
                }

                remove.Add(manifest.Id);
            }
        }

        // 4. 体积预算：从最旧的开始淘汰，先自动快照、再未标记的手动快照
        long totalBytes = 0;
        foreach (RewindSnapshotManifest manifest in manifests)
        {
            if (!remove.Contains(manifest.Id))
            {
                totalBytes += manifest.AddedSize;
            }
        }

        long budget = Math.Max(0, retention.MaximumBytes);
        if (totalBytes > budget)
        {
            foreach (bool automaticPass in new[] { true, false })
            {
                for (int index = manifests.Count - 1; index >= 0 && totalBytes > budget; index--)
                {
                    RewindSnapshotManifest manifest = manifests[index];
                    if (remove.Contains(manifest.Id)
                        || string.Equals(manifest.Id, keepId, StringComparison.Ordinal)
                        || IsProtected(manifest))
                    {
                        continue;
                    }

                    if (RewindReasons.IsAutomatic(manifest.Reason) != automaticPass)
                    {
                        continue;
                    }

                    remove.Add(manifest.Id);
                    totalBytes -= manifest.AddedSize;
                }
            }
        }

        if (remove.Count == 0)
        {
            return 0;
        }

        int removed = 0;
        foreach (string id in remove)
        {
            string manifestPath = Path.Combine(repo, ManifestsDirectoryName, id + ".json");
            try
            {
                File.Delete(manifestPath);
                removed++;
            }
            catch (Exception e)
            {
                System.Diagnostics.Debug.WriteLine("[Rewind] 删除快照清单失败: " + e.Message);
            }
        }

        if (removed > 0)
        {
            GarbageCollectBlobs();
        }

        return removed;
    }

    private static bool IsProtected(RewindSnapshotManifest manifest)
    {
        // 带用户标记（备注或颜色）的快照永不自动删除
        return !string.IsNullOrWhiteSpace(manifest.Label) || !string.IsNullOrWhiteSpace(manifest.Color);
    }

    private static int CompareNewestFirst(RewindSnapshotManifest left, RewindSnapshotManifest right)
    {
        int byTime = right.CreatedAt.CompareTo(left.CreatedAt);
        return byTime != 0 ? byTime : string.CompareOrdinal(right.Id, left.Id);
    }

    // ---- 数据块 ----

    private static Action PinBlob(string hash)
    {
        lock (BlobPinLock)
        {
            BlobPins.TryGetValue(hash, out int count);
            BlobPins[hash] = count + 1;
        }

        return () =>
        {
            lock (BlobPinLock)
            {
                if (BlobPins.TryGetValue(hash, out int count))
                {
                    if (count <= 1)
                    {
                        BlobPins.Remove(hash);
                    }
                    else
                    {
                        BlobPins[hash] = count - 1;
                    }
                }
            }
        };
    }

    private static bool IsBlobPinned(string hash)
    {
        lock (BlobPinLock)
        {
            return BlobPins.TryGetValue(hash, out int count) && count > 0;
        }
    }

    private static string BlobPath(string hash)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("快照清单中的摘要非法：" + hash);
        }

        return Path.Combine(BlobsDirectory, hash[..2], hash);
    }

    private static bool BlobExists(string hash)
    {
        return hash.Length == 64 && File.Exists(Path.Combine(BlobsDirectory, hash[..2], hash));
    }

    private static async Task StoreBlobAsync(string sourcePath, string hash, CancellationToken cancellationToken)
    {
        string bucket = Path.Combine(BlobsDirectory, hash[..2]);
        Directory.CreateDirectory(bucket);
        string target = Path.Combine(bucket, hash);
        if (File.Exists(target))
        {
            return;
        }

        string temporary = Path.Combine(bucket, hash + ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true))
            await using (FileStream destination = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true))
            {
                await source.CopyToAsync(destination, CopyBufferSize, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporary, target);
            }
            catch (IOException)
            {
                // 并发写入时目标可能刚被另一个仓库创建：同名即同内容，视为成功
                if (!File.Exists(target))
                {
                    throw;
                }
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (Exception)
            {
                // 临时文件清理失败不影响结果
            }
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using FileStream sourceStream = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true);
        await using FileStream destinationStream = new(destination, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true);
        await sourceStream.CopyToAsync(destinationStream, CopyBufferSize, cancellationToken).ConfigureAwait(false);
    }

    private static void GarbageCollectBlobs()
    {
        lock (GcLock)
        {
            HashSet<string> referenced = new(StringComparer.Ordinal);
            if (Directory.Exists(ReposRootDirectory))
            {
                foreach (string repoDirectory in Directory.GetDirectories(ReposRootDirectory))
                {
                    foreach (RewindSnapshotManifest manifest in LoadManifests(repoDirectory))
                    {
                        foreach (RewindSnapshotFile file in manifest.Files)
                        {
                            referenced.Add(file.Hash);
                        }
                    }
                }
            }

            if (!Directory.Exists(BlobsDirectory))
            {
                return;
            }

            foreach (string bucket in Directory.GetDirectories(BlobsDirectory))
            {
                foreach (string blob in Directory.GetFiles(bucket))
                {
                    string name = Path.GetFileName(blob);
                    if (referenced.Contains(name) || IsBlobPinned(name))
                    {
                        continue;
                    }

                    try
                    {
                        File.Delete(blob);
                    }
                    catch (Exception)
                    {
                        // 单个数据块删不掉时留到下次回收
                    }
                }

                try
                {
                    if (Directory.GetFileSystemEntries(bucket).Length == 0)
                    {
                        Directory.Delete(bucket);
                    }
                }
                catch (Exception)
                {
                    // 空目录删不掉不影响结果
                }
            }
        }
    }

    // ---- 清单读写 ----

    private static List<RewindSnapshotManifest> LoadManifests(string repo)
    {
        List<RewindSnapshotManifest> result = [];
        string directory = Path.Combine(repo, ManifestsDirectoryName);
        if (!Directory.Exists(directory))
        {
            return result;
        }

        foreach (string path in Directory.GetFiles(directory, "*.json"))
        {
            try
            {
                RewindSnapshotManifest? manifest = JsonSerializer.Deserialize<RewindSnapshotManifest>(
                    File.ReadAllText(path, Encoding.UTF8), ReadJsonOptions);
                if (manifest is null)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(manifest.Id))
                {
                    manifest.Id = Path.GetFileNameWithoutExtension(path);
                }

                result.Add(manifest);
            }
            catch (Exception)
            {
                // 单个清单损坏时跳过，不让整个列表失败
            }
        }

        return result;
    }

    private static RewindSnapshotManifest? LatestManifest(string repo)
    {
        List<RewindSnapshotManifest> manifests = LoadManifests(repo);
        if (manifests.Count == 0)
        {
            return null;
        }

        RewindSnapshotManifest latest = manifests[0];
        foreach (RewindSnapshotManifest manifest in manifests)
        {
            if (string.CompareOrdinal(manifest.Id, latest.Id) > 0)
            {
                latest = manifest;
            }
        }

        return latest;
    }

    private static RewindSnapshotManifest LoadManifest(string repo, string snapshotId)
    {
        string path = Path.Combine(repo, ManifestsDirectoryName, snapshotId + ".json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("快照不存在：" + snapshotId, path);
        }

        try
        {
            RewindSnapshotManifest manifest = JsonSerializer.Deserialize<RewindSnapshotManifest>(
                File.ReadAllText(path, Encoding.UTF8), ReadJsonOptions)
                ?? throw new InvalidDataException("快照清单为空。");
            if (string.IsNullOrEmpty(manifest.Id))
            {
                manifest.Id = snapshotId;
            }

            return manifest;
        }
        catch (JsonException e)
        {
            // 坏清单必须报错：零值清单会让回滚把目标目录里的文件全部删掉
            throw new InvalidDataException($"快照清单损坏（{snapshotId}）：{e.Message}", e);
        }
    }

    private static void WriteManifest(string repo, RewindSnapshotManifest manifest)
    {
        string directory = Path.Combine(repo, ManifestsDirectoryName);
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, manifest.Id + ".json");
        string temporary = target + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, WriteJsonOptions), new UTF8Encoding(false));
        File.Move(temporary, target, overwrite: true);
    }

    private static string NewSnapshotId(string manifestsDirectory)
    {
        string baseId = DateTime.Now.ToString("yyyyMMdd-HHmmss.fff");
        string candidate = baseId;
        for (int index = 1; ; index++)
        {
            if (!File.Exists(Path.Combine(manifestsDirectory, candidate + ".json")))
            {
                return candidate;
            }

            candidate = $"{baseId}-{index:00}";
        }
    }

    private static IReadOnlyList<RewindSnapshot> ListSnapshotsLocked(string repo)
    {
        List<RewindSnapshotManifest> manifests = LoadManifests(repo);
        manifests.Sort(CompareNewestFirst);
        return manifests.Select(ToSnapshot).ToList();
    }

    private static RewindSnapshot ToSnapshot(RewindSnapshotManifest manifest)
    {
        return new RewindSnapshot
        {
            Id = manifest.Id,
            CreatedAt = manifest.CreatedAt,
            Label = manifest.Label,
            Reason = manifest.Reason,
            Color = manifest.Color,
            Kind = string.Equals(manifest.Kind, "instance", StringComparison.OrdinalIgnoreCase)
                ? RewindKind.Instance
                : RewindKind.Save,
            FileCount = manifest.FileCount,
            TotalSize = manifest.TotalSize,
            AddedSize = manifest.AddedSize,
            AddedFiles = manifest.AddedFiles,
            WorldName = manifest.WorldName,
        };
    }

    // ---- 路径与工具 ----

    private static string NormalizeTargetDirectory(string targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new ArgumentException("目标目录不能为空。", nameof(targetDirectory));
        }

        string normalized = Path.GetFullPath(targetDirectory.Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException(
                $"目标目录不存在：{targetDirectory}（若刚执行过回滚，请检查目标旁边是否有 {BackupSuffix} 开头的目录）");
        }

        return normalized;
    }

    private static string RepoKey(string normalizedDirectory)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedDirectory.ToLowerInvariant()));
        return Convert.ToHexString(hash).ToLowerInvariant()[..24];
    }

    private static string RepoDirectory(string normalizedDirectory)
    {
        return Path.Combine(ReposRootDirectory, RepoKey(normalizedDirectory));
    }

    private static SemaphoreSlim RepoGate(string normalizedDirectory)
    {
        string key = RepoKey(normalizedDirectory);
        lock (SyncRoot)
        {
            if (!RepoLocks.TryGetValue(key, out SemaphoreSlim? gate))
            {
                gate = new SemaphoreSlim(1, 1);
                RepoLocks[key] = gate;
            }

            return gate;
        }
    }

    private static string ResolveWithin(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidDataException("快照清单中的路径为空。");
        }

        string rootFull = Path.GetFullPath(root);
        string normalizedRelative = relativePath.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar);
        string combined = Path.GetFullPath(Path.Combine(rootFull, normalizedRelative));
        if (!PathWithinDirectory(rootFull, combined))
        {
            throw new InvalidDataException($"快照路径越出目标目录：{relativePath}");
        }

        return combined;
    }

    private static bool PathWithinDirectory(string root, string path)
    {
        string rootFull = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string pathFull = Path.GetFullPath(path);
        if (string.Equals(pathFull, rootFull, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return pathFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string ToRelativePath(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return relative.Replace('\\', '/');
    }

    private static string NormalizeColor(string? color)
    {
        string normalized = (color ?? string.Empty).Trim().ToLowerInvariant();
        if (AllowedColors.Contains(normalized)) return normalized;

        // 允许 #RRGGBB 自定义标记色（启动器主题色板与快照标记共用一套色值）
        if (normalized.Length == 7 && normalized[0] == '#' && normalized[1..].All(Uri.IsHexDigit))
        {
            return normalized;
        }

        return string.Empty;
    }

    private static string DayLabel(DateTime date, DateTime today)
    {
        if (date == today)
        {
            return "今天";
        }

        if (date == today.AddDays(-1))
        {
            return "昨天";
        }

        return date.Year == today.Year
            ? $"{date.Month} 月 {date.Day} 日"
            : $"{date.Year} 年 {date.Month} 月 {date.Day} 日";
    }

    private static long DirectorySize(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        long total = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception)
                {
                    // 读不到的项跳过
                }
            }
        }
        catch (Exception)
        {
            // 目录不可读时返回已统计的部分
        }

        return total;
    }

    private static long ToUnixNanoseconds(DateTime utcDateTime)
    {
        return (utcDateTime.ToUniversalTime().Ticks - DateTimeOffset.UnixEpoch.UtcTicks) * 100L;
    }

    private static DateTimeOffset FromUnixNanoseconds(long nanoseconds)
    {
        return DateTimeOffset.UnixEpoch.AddTicks(nanoseconds / 100L);
    }

    private static bool ReadBool(string key, bool fallback)
    {
        string value = YamlRead.GetValue(YamlDomain.Config, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return bool.TryParse(value.Trim(), out bool result) ? result : fallback;
    }

    private static int ReadInt(string key, int fallback)
    {
        return int.TryParse(YamlRead.GetValue(YamlDomain.Config, key), out int value) ? value : fallback;
    }
}

/// <summary>快照目标类型。</summary>
public enum RewindKind
{
    /// <summary>单个世界存档目录（saves/&lt;世界&gt;）。</summary>
    Save,

    /// <summary>实例游戏目录（.minecraft 式根目录，跳过 libraries/assets 等可再生目录）。</summary>
    Instance,
}

/// <summary>快照来源（触发方式）。</summary>
public static class RewindReasons
{
    public const string Manual = "manual";
    public const string BeforeRollback = "before-rollback";
    public const string BeforeLaunch = "before-launch";
    public const string Scheduled = "scheduled";

    /// <summary>系统自动创建的来源（共享自动快照保留上限与淘汰优先级）。</summary>
    public static bool IsAutomatic(string? reason)
    {
        return reason is BeforeRollback or BeforeLaunch or Scheduled;
    }

    /// <summary>来源的中文名称。</summary>
    public static string Label(string? reason)
    {
        return reason switch
        {
            BeforeRollback => "回滚前自动",
            BeforeLaunch => "启动前自动",
            Scheduled => "定时自动",
            _ => "手动",
        };
    }
}

/// <summary>时间线上的单个快照。</summary>
public class RewindSnapshot
{
    public string Id { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public string Label { get; set; } = string.Empty;

    /// <summary>来源：manual / before-rollback / before-launch / scheduled。</summary>
    public string Reason { get; set; } = RewindReasons.Manual;

    /// <summary>用户标记的语义色键（空串表示未标记）。</summary>
    public string Color { get; set; } = string.Empty;

    public RewindKind Kind { get; set; }

    public int FileCount { get; set; }

    /// <summary>快照内全部文件原始大小之和。</summary>
    public long TotalSize { get; set; }

    /// <summary>相对上一快照新写入的数据块字节（近似新增占盘）。</summary>
    public long AddedSize { get; set; }

    public int AddedFiles { get; set; }

    /// <summary>目标目录名（展示用）。</summary>
    public string WorldName { get; set; } = string.Empty;

    /// <summary>回滚前自动创建的安全快照。</summary>
    public bool IsSafetySnapshot => Reason == RewindReasons.BeforeRollback;

    /// <summary>系统自动创建的快照（回滚前 / 启动前 / 定时）。</summary>
    public bool IsAutomatic => RewindReasons.IsAutomatic(Reason);

    /// <summary>来源中文名称。</summary>
    public string ReasonLabel => RewindReasons.Label(Reason);

    public string TotalSizeDisplay => MemoryRead.FormatBytes(TotalSize);

    public string AddedSizeDisplay => MemoryRead.FormatBytes(AddedSize);
}

/// <summary>清单里的单个文件。</summary>
public class RewindSnapshotFile
{
    /// <summary>相对目标目录的路径（正斜杠）。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>SHA-256 十六进制摘要（内容寻址键）。</summary>
    public string Hash { get; set; } = string.Empty;

    public long Size { get; set; }

    /// <summary>文件最后修改时间（Unix 纳秒）；启动前变更检测的基准。</summary>
    public long ModTime { get; set; }
}

/// <summary>快照清单的存储模型（repos/&lt;repoKey&gt;/manifests/&lt;id&gt;.json）。</summary>
public class RewindSnapshotManifest
{
    public string Id { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public string Label { get; set; } = string.Empty;

    public string Reason { get; set; } = RewindReasons.Manual;

    public string Color { get; set; } = string.Empty;

    /// <summary>目标类型："save" 或 "instance"。</summary>
    public string Kind { get; set; } = "save";

    public int FileCount { get; set; }

    public long TotalSize { get; set; }

    public long AddedSize { get; set; }

    public int AddedFiles { get; set; }

    public string WorldName { get; set; } = string.Empty;

    public List<RewindSnapshotFile> Files { get; set; } = [];
}

/// <summary>按天分组的时间线。</summary>
public class RewindTimeline
{
    public int SnapshotCount { get; set; }

    public long TotalSize { get; set; }

    public long AddedSize { get; set; }

    public DateTimeOffset? LatestAt { get; set; }

    public DateTimeOffset? OldestAt { get; set; }

    /// <summary>按天分组（新的在前）。</summary>
    public List<RewindDayGroup> Days { get; set; } = [];
}

/// <summary>时间线里的一天。</summary>
public class RewindDayGroup
{
    public DateTime Date { get; set; }

    /// <summary>今天 / 昨天 / X 月 X 日 / XXXX 年 X 月 X 日。</summary>
    public string Label { get; set; } = string.Empty;

    public int Count { get; set; }

    public long TotalSize { get; set; }

    public long AddedSize { get; set; }

    public List<RewindSnapshot> Snapshots { get; set; } = [];
}

/// <summary>Rewind 仓库的全局汇总（主页小组件用）。</summary>
public class RewindSummary
{
    /// <summary>全部仓库的快照总数。</summary>
    public int SnapshotCount { get; set; }

    /// <summary>数据块实际占用（全局共享，同一块只算一次）。</summary>
    public long BlobBytes { get; set; }

    /// <summary>数据块预算（超过后按"先自动、后手动"淘汰最旧快照）。</summary>
    public long BudgetBytes { get; set; }

    public DateTimeOffset? LastSnapshotAt { get; set; }

    /// <summary>最近若干次快照的标记颜色（新的在前，空串表示未标记）。</summary>
    public List<string> RecentColors { get; set; } = [];
}

/// <summary>保留策略。</summary>
public class RewindRetention
{
    /// <summary>自动快照（回滚前/启动前/定时）保留上限。</summary>
    public int MaximumAutoSnapshots { get; set; } = RewindSystem.DefaultMaximumAutoSnapshots;

    /// <summary>单仓库数据块预算（字节）。</summary>
    public long MaximumBytes { get; set; }

    /// <summary>只保留最近 N 份（0 = 不限；带用户标记的快照永不自动删除）。</summary>
    public int KeepLastCount { get; set; }

    /// <summary>只保留最近 N 天（0 = 不限；带用户标记的快照永不自动删除）。</summary>
    public int KeepDays { get; set; }
}

/// <summary>Rewind 设置（config.yaml 的 rewind* 键）。</summary>
public class RewindSettings
{
    public bool AutoSnapshotEnabled { get; set; }

    /// <summary>定时快照间隔（秒，30~86400）。</summary>
    public int AutoSnapshotIntervalSeconds { get; set; } = 900;

    public int MaximumAutoSnapshots { get; set; } = RewindSystem.DefaultMaximumAutoSnapshots;

    public long MaximumBytes { get; set; } = RewindSystem.DefaultMaximumTotalBytes;

    public int KeepLastCount { get; set; }

    public int KeepDays { get; set; }

    public RewindRetention ToRetention()
    {
        return new RewindRetention
        {
            MaximumAutoSnapshots = Math.Max(1, MaximumAutoSnapshots),
            MaximumBytes = Math.Max(64L * 1024 * 1024, MaximumBytes),
            KeepLastCount = Math.Max(0, KeepLastCount),
            KeepDays = Math.Max(0, KeepDays),
        };
    }
}

/// <summary>回滚结果。</summary>
public class RewindRollbackResult
{
    public string TargetDirectory { get; set; } = string.Empty;

    /// <summary>本次回滚被恢复到的快照。</summary>
    public RewindSnapshot RestoredSnapshot { get; set; } = new();

    /// <summary>回滚前自动创建的安全快照（可用于反悔）。</summary>
    public RewindSnapshot SafetySnapshot { get; set; } = new();
}

/// <summary>实例 saves/ 下的一个世界。</summary>
public class RewindWorldInfo
{
    public string Name { get; set; } = string.Empty;

    public string DirectoryPath { get; set; } = string.Empty;

    public DateTimeOffset LastModified { get; set; }

    public int SnapshotCount { get; set; }

    public DateTimeOffset? LatestSnapshotAt { get; set; }
}

/// <summary>
/// 定时快照调度器：每 Interval 给目标目录创建一份定时快照。
/// </summary>
public sealed class RewindScheduler : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _loop;

    internal RewindScheduler(string targetDirectory, RewindKind kind, TimeSpan interval)
    {
        TargetDirectory = targetDirectory;
        Kind = kind;
        Interval = interval;
    }

    public string TargetDirectory { get; }

    public RewindKind Kind { get; }

    public TimeSpan Interval { get; }

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>每次创建成功后触发。</summary>
    public event Action<RewindSnapshot>? SnapshotCreated;

    /// <summary>创建失败时触发（不会中断调度）。</summary>
    public event Action<Exception>? Error;

    internal void Start()
    {
        _loop ??= Task.Run(LoopAsync);
    }

    /// <summary>停止调度并等待当前循环退出。</summary>
    public async Task StopAsync()
    {
        _cancellation.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常停止
            }
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
    }

    private async Task LoopAsync()
    {
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                await Task.Delay(Interval, _cancellation.Token).ConfigureAwait(false);
                try
                {
                    RewindSnapshot snapshot = await RewindSystem
                        .CreateScheduledSnapshotAsync(TargetDirectory, Kind, _cancellation.Token)
                        .ConfigureAwait(false);
                    SnapshotCreated?.Invoke(snapshot);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    Error?.Invoke(e);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 停止时取消 Task.Delay
        }
    }
}
