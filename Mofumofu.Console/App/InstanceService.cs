using MofuCore.Config;
using MofuCore.Download.Minecraft;
using MofuCore.Java;
using MofuCore.Launch;
using MofuCore.Tools;

namespace Mofumofu.Console;

public sealed record InstanceItem(
    string Id,
    string Loader,
    string MinecraftVersion,
    string Directory,
    int ModCount,
    long SizeBytes)
{
    public string Display => $"{Id}  ·  {(Loader == "Vanilla" ? "原版" : Loader)} {MinecraftVersion}";
}

/// <summary>实例扫描、启动参数组装与启动。</summary>
public sealed class InstanceService
{
    private readonly AccountService _accounts;
    private readonly TuiSettings _settings;
    private readonly VersionLoader _loader = new();

    public InstanceService(AccountService accounts, TuiSettings settings)
    {
        _accounts = accounts;
        _settings = settings;
    }

    public string MinecraftDirectory => YamlRead.GameDirectory;

    public IReadOnlyList<InstanceItem> Scan()
    {
        string root = MinecraftDirectory;
        string versions = Path.Combine(root, "versions");
        List<InstanceItem> result = [];
        if (!Directory.Exists(versions))
        {
            return result;
        }

        foreach (string directory in Directory.GetDirectories(versions))
        {
            string id = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(id) ||
                !File.Exists(Path.Combine(directory, id + ".json")))
            {
                continue;
            }

            LaunchProfile? profile = _loader.TryLoad(root, id);
            if (profile is null)
            {
                continue;
            }

            string probe = (profile.InheritsFrom + " " + profile.SourceId + " " + id).ToLowerInvariant();
            string loader = probe.Contains("neoforge") ? "NeoForge"
                : probe.Contains("quilt") ? "Quilt"
                : probe.Contains("fabric") ? "Fabric"
                : probe.Contains("forge") ? "Forge"
                : probe.Contains("optifine") ? "OptiFine"
                : "Vanilla";
            string minecraftVersion = string.IsNullOrWhiteSpace(profile.InheritsFrom)
                ? id
                : profile.InheritsFrom;

            int modCount = 0;
            try
            {
                string mods = Path.Combine(directory, "mods");
                if (Directory.Exists(mods))
                {
                    modCount = Directory.EnumerateFiles(mods, "*.jar").Count();
                }
            }
            catch (Exception)
            {
            }

            long size = 0;
            try
            {
                size = new DirectoryInfo(directory)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(file => file.Length);
            }
            catch (Exception)
            {
            }

            result.Add(new InstanceItem(id, loader, minecraftVersion, directory, modCount, size));
        }

        return result
            .OrderByDescending(item => Directory.GetLastWriteTimeUtc(item.Directory))
            .ToList();
    }

    public async Task<MinecraftLaunchResult> LaunchAsync(
        string instanceId,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        string root = MinecraftDirectory;
        string versionDirectory = Path.Combine(root, "versions", instanceId);
        if (!File.Exists(Path.Combine(versionDirectory, instanceId + ".json")))
        {
            throw new InvalidOperationException("实例不存在或版本 JSON 缺失：" + instanceId);
        }

        GlobalLaunchSettings global = YamlRead.LoadGlobalLaunchSettings();
        InstanceJavaConfig instanceJava = YamlRead.LoadInstanceJavaConfig(versionDirectory);
        LaunchProfile profile = await _loader.LoadAsync(root, instanceId, cancellationToken).ConfigureAwait(false);

        string gameDirectory = root;
        if (YamlRead.DefaultVersionIsolation)
        {
            gameDirectory = versionDirectory;
            try
            {
                Directory.CreateDirectory(gameDirectory);
                foreach (string sub in new[] { "mods", "saves", "resourcepacks", "config" })
                {
                    Directory.CreateDirectory(Path.Combine(gameDirectory, sub));
                }
            }
            catch (Exception)
            {
                gameDirectory = root;
            }
        }

        if (_settings.VerifyBeforeLaunch)
        {
            progress.Report("正在校验游戏文件…");
            MinecraftDownload download = new(root);
            await download.VerifyAndRepairAsync(instanceId, true,
                new Progress<string>(message => progress.Report(message)), cancellationToken).ConfigureAwait(false);
        }

        progress.Report("正在准备启动前还原点…");
        try
        {
            if (RewindSystem.LoadSettings().RescueEnabled)
            {
                await RewindSystem.CreateLaunchSnapshotAsync(gameDirectory, RewindKind.Instance, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            progress.Report("还原点创建失败（忽略）：" + exception.Message);
        }

        string accountKey = _settings.SelectedAccount;
        if (string.IsNullOrWhiteSpace(accountKey))
        {
            throw new InvalidOperationException("还没有选择账号，请到「账号」页添加离线账号或登录正版。");
        }

        progress.Report("正在解析账号…");
        IMinecraftAccount account = await _accounts.ResolveAsync(accountKey, cancellationToken).ConfigureAwait(false);

        int modCount = 0;
        try
        {
            string mods = Path.Combine(gameDirectory, "mods");
            if (Directory.Exists(mods))
            {
                modCount = Directory.EnumerateFiles(mods, "*.jar").Count();
            }
        }
        catch (Exception)
        {
        }

        bool hasLoader = !string.IsNullOrWhiteSpace(profile.InheritsFrom);

        MemoryAllocationResult memory = MemoryAllocation.Resolve(global, instanceJava, modCount, hasLoader);
        string javaExecutable = string.IsNullOrWhiteSpace(instanceJava.JavaExecutable)
            ? global.JavaExecutable
            : instanceJava.JavaExecutable;

        MinecraftLaunchOptions options = new()
        {
            MinecraftDirectory = root,
            GameDirectory = gameDirectory,
            VersionId = instanceId,
            Account = account,
            JavaExecutable = javaExecutable,
            JavaRuntimeDirectory = JavaDownload.GetRuntimeDirectory(root),
            MinimumMemoryMb = memory.MinimumMb,
            MaximumMemoryMb = memory.MaximumMb,
            WindowWidth = global.WindowWidth,
            WindowHeight = global.WindowHeight,
            ProcessPriority = global.ProcessPriority,
            WrapperCommand = global.WrapperCommand,
            LaunchFullscreen = global.LaunchFullscreen,
            JvmTuning = global.JvmTuning,
            AllowUnsafeVersionJvmArguments = global.AllowUnsafeVersionJvmArguments,
            AdditionalJvmArguments = [.. global.AdditionalJvmArguments, .. instanceJava.AdditionalJvmArguments],
            AdditionalGameArguments = [.. global.AdditionalGameArguments, .. instanceJava.AdditionalGameArguments],
            LogCallback = message => progress.Report(message),
            GameOutputCallback = (line, _) => progress.Report(line),
        };

        foreach ((string key, string value) in ParseEnvironment(global.AdditionalEnvironmentVariables))
        {
            options.EnvironmentVariables[key] = value;
        }

        progress.Report($"正在启动 {instanceId}（Java {profile.RequiredJavaMajorVersion?.ToString() ?? "自动"}，内存 {memory.MaximumMb} MB）…");
        return await GameLaunch.Shared.LaunchAsync(options, cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<(string Key, string Value)> ParseEnvironment(IEnumerable<string> entries)
    {
        foreach (string entry in entries)
        {
            int index = entry.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            yield return (entry[..index].Trim(), entry[(index + 1)..].Trim());
        }
    }
}
