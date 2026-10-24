using Mofumofu.Launcher.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MofuCore.Config;
using MofuCore.Download.Minecraft;
using MofuCore.Java;
using MofuCore.Launch;
using Mofumofu.Launcher.Mock;
using Mofumofu.Launcher.Models;
using Mofumofu.Launcher.Services;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views;

/// <summary>把 MockData 里的 avares:// 图标字符串转换成 Image 可用的位图。</summary>
public sealed class InstanceIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return new Bitmap(AssetLoader.Open(new Uri(path)));
        }
        catch
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>「内容」标签页里的一行（真实实例目录扫描结果）。</summary>
public sealed class ContentEntry
{
    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>磁盘上的真实路径（停用/导入/图标用）。</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>图标来源：本地文件路径（ResourceIconCache 支持）。</summary>
    public string? IconPath { get; set; }
}

/// <summary>Java 设置里的一行（来自 MofuCore.Java.JavaPathSearch 的检测结果）。</summary>
public sealed class InstalledJavaEntry
{
    public string DisplayName { get; init; } = string.Empty;

    public string JavaPath { get; init; } = string.Empty;

    /// <summary>主版本号（无则版本字符串）。</summary>
    public string Version { get; init; } = string.Empty;

    public bool IsDefault { get; init; }

    /// <summary>仅来自已保存列表、本次未扫描到的路径。</summary>
    public bool IsSavedOnly { get; init; }
}

public partial class InstancesView : UserControl
{
    private readonly List<Button> _tabButtons = new();
    private readonly List<Control> _tabPanels = new();

    private InstanceInfo? _current;
    private bool _ready;
    private bool _suppressSelection;
    private int _activeTabIndex;
    private int _lastInstanceIndex;
    private List<InstalledJavaEntry> _installedJava = new();
    private CancellationTokenSource? _javaCts;
    private List<InstanceInfo> _instances = [];
    private bool _installing;
    private string _gameDirectory = string.Empty;

    public InstancesView()
    {
        InitializeComponent();

        _gameDirectory = EnsureGameDirectory();
        bool local = ScanInstances();

        ScanStatusText.Text = L.T("Instances.Scan.Status", "已检测到 {0} 个实例 · {1} 个账户 · 最后扫描 {2}",
            _instances.Count, CountAccounts(),
            local ? L.T("Instances.Scan.JustNow", "刚刚") : L.T("Instances.Scan.NotScanned", "尚未扫描"));
        DiskUsageText.Text = L.T("Instances.Scan.DiskUsage", "磁盘占用 {0}", FormatSize(GetDirectorySize(_gameDirectory)));
        int initialMemoryMb = YamlRead.LoadGlobalLaunchSettings().MaximumMemoryMb;
        MemoryValueText.Text = L.T("Instances.Launch.MemoryValue", "{0} MB · {1} GB", initialMemoryMb, initialMemoryMb / 1024.0);
        JavaListStatus.Text = L.T("Instances.Java.Detecting", "正在检测本机 Java 运行时…");
        GameDirCombo.Items.Clear();
        GameDirCombo.Items.Add(new ComboBoxItem { Content = _gameDirectory });
        GameDirCombo.SelectedIndex = 0;

        _tabButtons.AddRange(new[] { TabOverview, TabLaunch, TabJava, TabContent, TabGame, TabManage });
        _tabPanels.AddRange(new Control[]
        {
            OverviewPanel, LaunchSettingsPanel, JavaPanel,
            ContentPanel, GameSettingsPanel, ManagePanel,
        });

        WorldsList.ItemsSource = Array.Empty<WorldInfo>();

        InstanceList.ItemsSource = _instances;
        InstanceCountText.Text = $"{_instances.Count} / {_instances.Count}";

        InstanceInfo? selected = null;
        string selectedId = LocalSettings.Current.SelectedInstanceId;
        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            selected = _instances.FirstOrDefault(instance =>
                string.Equals(instance.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        }
        selected ??= _instances.FirstOrDefault();

        if (selected is not null)
        {
            _suppressSelection = true;
            InstanceList.SelectedItem = selected;
            _suppressSelection = false;
            _lastInstanceIndex = IndexOfInstance(selected);
            ShowInstance(selected);
        }

        _ready = true;
    }

    // ---------------- 初始化 / 真实实例扫描 ----------------

    private static string EnsureGameDirectory()
    {
        try
        {
            string directory = (YamlRead.GameDirectory ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = Path.GetFullPath(".minecraft");
                YamlRead.SaveGameDirectory(directory);
            }

            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(Path.Combine(directory, "versions"));
            return directory;
        }
        catch (Exception)
        {
            return Path.GetFullPath(".minecraft");
        }
    }

    /// <summary>从 versions/*.json 扫描本机实例。</summary>
    private bool ScanInstances()
    {
        _instances = [];
        string versionsRoot = Path.Combine(_gameDirectory, "versions");

        if (Directory.Exists(versionsRoot))
        {
            GlobalLaunchSettings launch = YamlRead.LoadGlobalLaunchSettings();
            string defaultJava = SafeDefaultJava();
            string[] directories;
            try
            {
                directories = Directory.GetDirectories(versionsRoot);
            }
            catch (Exception)
            {
                return false;
            }

            VersionLoader versionLoader = new();
            foreach (string directory in directories)
            {
                string id = Path.GetFileName(directory);
                string jsonPath = Path.Combine(directory, id + ".json");
                if (!File.Exists(jsonPath)) continue;

                try
                {
                    // TryLoad：继承链断裂/JSON 损坏时降级为部分档案，实例仍进列表而不是消失
                    LaunchProfile? profile = versionLoader.TryLoad(_gameDirectory, id);
                    if (profile is null) continue;
                    string version = string.IsNullOrWhiteSpace(profile.SourceId) ? id : profile.SourceId;
                    string? inherits = string.IsNullOrWhiteSpace(profile.InheritsFrom) ? null : profile.InheritsFrom;
                    string type = string.IsNullOrWhiteSpace(profile.VersionType) ? "release" : profile.VersionType;

                    string loader = DetectLoader(inherits, id);
                    string displayVersion = string.IsNullOrWhiteSpace(inherits) ? version : inherits!;
                    string icon = InstanceIconFor(loader, displayVersion, type);
                    string lastPlayed = File.GetLastWriteTime(jsonPath).ToString("yyyy-MM-dd HH:mm");

                    _instances.Add(new InstanceInfo(
                        id,
                        id,
                        displayVersion,
                        loader,
                        icon,
                        lastPlayed,
                        L.T("Instances.Detail.Unknown", "未记录"),
                        string.IsNullOrWhiteSpace(defaultJava) ? string.Empty : defaultJava,
                        launch.MaximumMemoryMb,
                        CountFiles(ContentDirectoryFor(id, "mods")),
                        CountDirectories(Path.Combine(InstanceRoot(id), "saves")) + CountDirectories(Path.Combine(_gameDirectory, "saves"))));
                }
                catch (Exception)
                {
                    // 单个版本 JSON 坏掉不影响其它实例
                }
            }
        }

        return _instances.Count > 0;
    }

    private string InstanceRoot(string id)
    {
        return YamlRead.DefaultVersionIsolation
            ? Path.Combine(_gameDirectory, "versions", id)
            : _gameDirectory;
    }

    private string ContentDirectoryFor(string id, string name)
    {
        string isolated = Path.Combine(InstanceRoot(id), name);
        if (Directory.Exists(isolated)) return isolated;
        return Path.Combine(_gameDirectory, name);
    }

    private static string DetectLoader(string? inheritsFrom, string id)
    {
        string probe = (inheritsFrom ?? string.Empty) + " " + id;
        if (probe.Contains("fabric", StringComparison.OrdinalIgnoreCase)) return "Fabric";
        if (probe.Contains("quilt", StringComparison.OrdinalIgnoreCase)) return "Quilt";
        if (probe.Contains("neoforge", StringComparison.OrdinalIgnoreCase)) return "NeoForge";
        if (probe.Contains("forge", StringComparison.OrdinalIgnoreCase)) return "Forge";
        if (probe.Contains("liteloader", StringComparison.OrdinalIgnoreCase)) return "LiteLoader";
        return "Vanilla";
    }

    private static string InstanceIconFor(string loader, string version, string type)
    {
        string file = loader switch
        {
            "Fabric" => "fabric.png",
            "Quilt" => "fabric.png",
            "Forge" => "forge.png",
            "NeoForge" => "neoforge.png",
            "LiteLoader" => "liteloader.png",
            _ => type == "snapshot" ? "snapshot_version.png"
                : version.StartsWith("1.7", StringComparison.Ordinal) ? "old_version.png"
                : "vanilla.png",
        };
        return "avares://Mofumofu.Launcher/Assets/instance-icons/" + file;
    }

    private static int CountFiles(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly).Length
                : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static int CountDirectories(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.GetDirectories(directory).Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.#} KB",
        _ => $"{bytes} B",
    };

    private static long GetDirectorySize(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return 0;
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(file =>
            {
                try
                {
                    return new FileInfo(file).Length;
                }
                catch (Exception)
                {
                    return 0L;
                }
            });
        }
        catch (Exception)
        {
            return 0;
        }
    }

    // ---------------- 内容列表（真实扫描实例目录） ----------------

    private void LoadContentLists(InstanceInfo info)
    {
        List<ContentEntry> mods = ScanContent(info, "mods");
        ModsList.ItemsSource = mods;
        ModContentCount.Text = L.T("Instances.Count.Units", "{0} 个", mods.Count);

        List<ContentEntry> packs = ScanContent(info, "resourcepacks");
        ResourcePacksList.ItemsSource = packs;
        PackContentCount.Text = L.T("Instances.Count.Units", "{0} 个", packs.Count);

        List<ContentEntry> shaders = ScanContent(info, "shaderpacks");
        ShadersList.ItemsSource = shaders;
        ShaderContentCount.Text = L.T("Instances.Count.Units", "{0} 个", shaders.Count);

        List<ContentEntry> worlds = ScanSaves(info);
        WorldsContentList.ItemsSource = worlds;
        WorldContentCount.Text = L.T("Instances.Count.Units", "{0} 个", worlds.Count);
        WorldsList.ItemsSource = worlds
            .Select(world => new WorldInfo(world.Name, info.Version, string.Empty, "IconFluentGlobe", world.FilePath))
            .ToList();
        WorldCountText.Text = L.T("Instances.Worlds.Count", "共 {0} 个世界", worlds.Count);
    }

    private List<ContentEntry> ScanContent(InstanceInfo info, string directoryName)
    {
        List<ContentEntry> result = [];
        string directory = ContentDirectoryFor(info.Id, directoryName);
        if (!Directory.Exists(directory)) return result;

        foreach (string file in Directory.GetFiles(directory))
        {
            string fileName = Path.GetFileName(file);
            string lower = fileName.ToLowerInvariant();
            if (!lower.EndsWith(".jar") && !lower.EndsWith(".jar.disabled")
                && !lower.EndsWith(".zip") && !lower.EndsWith(".zip.disabled"))
            {
                continue;
            }

            long size = 0;
            try
            {
                size = new FileInfo(file).Length;
            }
            catch (Exception)
            {
                // 忽略不可读文件
            }

            result.Add(new ContentEntry
            {
                Name = fileName,
                Version = FormatSize(size),
                Enabled = !lower.EndsWith(".disabled"),
                FilePath = file,
                IconPath = file,
            });
        }

        result.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private List<ContentEntry> ScanSaves(InstanceInfo info)
    {
        List<ContentEntry> result = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in new[]
                 {
                     Path.Combine(InstanceRoot(info.Id), "saves"),
                     Path.Combine(_gameDirectory, "saves"),
                 })
        {
            if (!Directory.Exists(directory)) continue;
            foreach (string save in Directory.GetDirectories(directory))
            {
                string name = Path.GetFileName(save);
                if (!seen.Add(name)) continue;
                string icon = Path.Combine(save, "icon.png");
                result.Add(new ContentEntry
                {
                    Name = name,
                    // 存档行的说明位放最后修改时间（level.dat 与目录取较新者），比空着有用
                    Version = File.GetLastWriteTime(Path.Combine(save, "level.dat")) is { } levelTime &&
                              File.Exists(Path.Combine(save, "level.dat"))
                        ? levelTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                        : Directory.GetLastWriteTime(save).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    Enabled = true,
                    FilePath = save,
                    IconPath = File.Exists(icon) ? icon : null,
                });
            }
        }

        result.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private void OnContentIconLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Image { Tag: ContentEntry entry } image) return;
        if (string.IsNullOrWhiteSpace(entry.IconPath) || !File.Exists(entry.IconPath)) return;

        string key = $"instance:{_current?.Id}:{Path.GetFileName(entry.FilePath)}";
        _ = ResourceIconCache.LoadAsync(entry.IconPath, key).ContinueWith(task =>
        {
            if (!task.IsCompletedSuccessfully || task.Result is null) return;
            Dispatcher.UIThread.Post(() => image.Source = task.Result);
        }, TaskScheduler.Default);
    }

    private void OnContentToggled(object? sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not ToggleSwitch toggle || toggle.DataContext is not ContentEntry entry) return;
        if (string.IsNullOrWhiteSpace(entry.FilePath) || !File.Exists(entry.FilePath)) return;
        bool desired = toggle.IsChecked == true;
        if (desired == entry.Enabled) return;

        try
        {
            string target = desired
                ? entry.FilePath[..^".disabled".Length]
                : entry.FilePath + ".disabled";
            if (File.Exists(target)) throw new InvalidOperationException(
                L.T("Instances.Content.Exists", "同名文件已存在：{0}", Path.GetFileName(target)));
            File.Move(entry.FilePath, target);
            entry.FilePath = target;
            entry.Enabled = desired;
            Shell.Toast(desired
                ? L.T("Instances.Content.Enabled", "已启用 {0}", Path.GetFileName(target))
                : L.T("Instances.Content.Disabled", "已停用 {0}", Path.GetFileName(entry.FilePath)));
        }
        catch (Exception ex)
        {
            toggle.IsChecked = entry.Enabled;
            Shell.Toast(L.T("Instances.Content.ToggleFailed", "切换启用状态失败：{0}", ex.Message));
        }
    }

    private void ShowInstance(InstanceInfo info)
    {
        _current = info;
        LocalSettings.Current.SelectedInstanceId = info.Id;
        LocalSettings.Save();

        DetailIcon.Source = LoadBitmap(info.Icon);
        DetailName.Text = info.Name;
        DetailSub.Text = $"{info.Version} · {info.Loader}";
        DetailMemoryValue.Text = L.T("Instances.Detail.MemoryValue", "{0} MB", info.MemoryMb);
        DetailJavaValue.Text = JavaLabel(info.JavaPath);
        DetailLastPlayedValue.Text = info.LastPlayed;

        StatPlaytime.Text = info.Playtime;
        StatMods.Text = L.T("Instances.Count.Units", "{0} 个", info.ModCount);
        StatWorlds.Text = L.T("Instances.Count.Units", "{0} 个", info.WorldCount);
        StatLastPlayed.Text = info.LastPlayed;

        var requiredJava = MofuCore.Server.ServerManage.RequiredJavaMajorVersion(info.Version);
        JavaRequirementText.Text = L.T("Instances.Java.RequirementCurrent",
            "当前实例要求 Java {0}（{1}）。",
            requiredJava?.ToString(CultureInfo.InvariantCulture) ?? "?",
            $"{info.Version} · {info.Loader}");

        MemorySlider.Value = info.MemoryMb;
        JavaPathBox.Text = info.JavaPath;

        LoadContentLists(info);
        LoadOptions(info);
    }

    // ---------------- options.txt 真实读写 ----------------

    private string? OptionsPath(InstanceInfo info)
    {
        string isolated = Path.Combine(InstanceRoot(info.Id), "options.txt");
        if (File.Exists(isolated)) return isolated;
        string shared = Path.Combine(_gameDirectory, "options.txt");
        return File.Exists(shared) ? shared : isolated;
    }

    private static Dictionary<string, string> ReadOptions(string? path)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return values;

        try
        {
            foreach (string line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
                int index = line.IndexOf(':');
                if (index <= 0) continue;
                values[line[..index].Trim()] = line[(index + 1)..].Trim();
            }
        }
        catch (Exception)
        {
            // 读取失败时保持默认值
        }

        return values;
    }

    private static int ReadInt(Dictionary<string, string> values, string key, int fallback) =>
        values.TryGetValue(key, out string? text) && int.TryParse(text, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int number)
            ? number
            : fallback;

    private static double ReadDouble(Dictionary<string, string> values, string key, double fallback) =>
        values.TryGetValue(key, out string? text) && double.TryParse(text, NumberStyles.Float,
            CultureInfo.InvariantCulture, out double number)
            ? number
            : fallback;

    private void LoadOptions(InstanceInfo info)
    {
        Dictionary<string, string> options = ReadOptions(OptionsPath(info));
        RenderDistanceSlider.Value = Math.Clamp(ReadInt(options, "renderDistance", 12), 2, 32);
        SimulationDistanceSlider.Value = Math.Clamp(ReadInt(options, "simulationDistance", 10), 2, 32);
        FovSlider.Value = Math.Clamp(ReadInt(options, "fov", 70), 30, 110);
        SensitivitySlider.Value = Math.Clamp(
            Math.Round(ReadDouble(options, "mouseSensitivity", 0.5) * 100), 0, 100);
        GuiScaleCombo.SelectedIndex = Math.Clamp(ReadInt(options, "guiScale", 1), 0, 4);

        int maxFps = ReadInt(options, "maxFps", 60);
        MaxFpsCombo.SelectedIndex = maxFps switch
        {
            0 => 0,
            <= 60 => 1,
            <= 120 => 2,
            <= 144 => 3,
            _ => 4,
        };
    }

    private void SaveOptions(InstanceInfo info)
    {
        string? path = OptionsPath(info);
        if (string.IsNullOrWhiteSpace(path)) return;

        Dictionary<string, string> desired = new(StringComparer.OrdinalIgnoreCase)
        {
            ["renderDistance"] = ((int)Math.Round(RenderDistanceSlider.Value)).ToString(CultureInfo.InvariantCulture),
            ["simulationDistance"] = ((int)Math.Round(SimulationDistanceSlider.Value)).ToString(CultureInfo.InvariantCulture),
            ["fov"] = ((int)Math.Round(FovSlider.Value)).ToString(CultureInfo.InvariantCulture),
            ["mouseSensitivity"] = (Math.Round(SensitivitySlider.Value) / 100.0)
                .ToString("0.00", CultureInfo.InvariantCulture),
            ["guiScale"] = Math.Max(0, GuiScaleCombo.SelectedIndex).ToString(CultureInfo.InvariantCulture),
            ["maxFps"] = (MaxFpsCombo.SelectedIndex switch
            {
                0 => 0,
                1 => 60,
                2 => 120,
                3 => 144,
                _ => 240,
            }).ToString(CultureInfo.InvariantCulture),
        };

        List<string> output = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path))
        {
            foreach (string line in File.ReadAllLines(path))
            {
                int index = line.IndexOf(':');
                string key = index > 0 ? line[..index].Trim() : string.Empty;
                if (index > 0 && desired.TryGetValue(key, out string? value))
                {
                    output.Add(key + ":" + value);
                    seen.Add(key);
                }
                else
                {
                    output.Add(line);
                }
            }
        }

        foreach (var (key, value) in desired)
        {
            if (seen.Add(key)) output.Add(key + ":" + value);
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, string.Join(Environment.NewLine, output) + Environment.NewLine);
    }

    private static Bitmap? LoadBitmap(string uri)
    {
        try
        {
            return new Bitmap(AssetLoader.Open(new Uri(uri)));
        }
        catch
        {
            return null;
        }
    }

    private static string JavaLabel(string path)
    {
        if (path.Contains("temurin-21", StringComparison.OrdinalIgnoreCase)) return "Temurin 21";
        if (path.Contains("temurin-17", StringComparison.OrdinalIgnoreCase)) return "Temurin 17";
        if (path.Contains("jre8", StringComparison.OrdinalIgnoreCase)) return "JRE 8";
        return L.T("Instances.Detail.JavaCustom", "自定义 Java");
    }

    private object? FindRes(string key) =>
        (this.TryFindResource(key, out var local) ? local : null)
        ?? (Application.Current is { } app && app.TryFindResource(key, out var global) ? global : null);

    // ---------------- 启动状态 ----------------

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        GameState.Changed += SyncLaunchState;
        SyncLaunchState();
        _ = LoadJavaAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        GameState.Changed -= SyncLaunchState;
        CancelCts(ref _javaCts);
        base.OnDetachedFromVisualTree(e);
    }

    private static void CancelCts(ref CancellationTokenSource? source)
    {
        var current = source;
        source = null;
        if (current is null) return;
        try
        {
            current.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放
        }
    }

    private void SyncLaunchState()
    {
        if (DetailLaunchText is null) return;

        DetailLaunchText.Text = GameState.IsRunning
            ? L.T("Instances.Detail.Stop", "停止游戏")
            : L.T("Instances.Detail.Launch", "启动游戏");
        DetailLaunchIcon.Kind = GameState.IsRunning ? "IconStop" : "IconPlay";
    }

    private void OnLaunchClick(object? sender, RoutedEventArgs e)
    {
        // 与主窗口开始栏同一套真实启动路径：停 → 取消后台启动；启 → 交给启动控制台跑 MofuCore
        if (GameState.IsRunning)
        {
            GameState.Stop();
            LaunchProgress.Shared.Cancel();
            return;
        }

        if (!GameState.HasSelectedVersion)
        {
            Shell.Toast(L.T("Shell.Toast.NoVersion", "请先在下载页下载或选择一个游戏版本"));
            return;
        }

        GameState.Start();
        Shell.Go("launchconsole");
    }

    // ---------------- 实例列表 ----------------

    private void OnInstanceSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        if (InstanceList.SelectedItem is not InstanceInfo info) return;

        var index = IndexOfInstance(info);
        var direction = index >= _lastInstanceIndex ? 1 : -1;
        if (index >= 0) _lastInstanceIndex = index;

        ShowInstance(info);
        AnimateDetailSwitch(direction);
    }

    private int IndexOfInstance(InstanceInfo info)
    {
        for (var i = 0; i < _instances.Count; i++)
        {
            if (ReferenceEquals(_instances[i], info)) return i;
        }
        return -1;
    }

    private void OnInstanceSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_ready) return;

        var query = InstanceSearchBox.Text?.Trim() ?? string.Empty;
        var list = string.IsNullOrEmpty(query)
            ? _instances.ToList()
            : _instances
                .Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || i.Loader.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || i.Version.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

        var keep = _current;
        InstanceList.ItemsSource = list;
        InstanceCountText.Text = $"{list.Count} / {_instances.Count}";
        InstanceEmptyHint.IsVisible = list.Count == 0;
        AnimateListRefresh(InstanceList);

        if (list.Count == 0)
        {
            _suppressSelection = true;
            InstanceList.SelectedItem = null;
            _suppressSelection = false;
            return;
        }

        var target = keep is not null && list.Contains(keep) ? keep : list[0];
        if (!ReferenceEquals(target, _current))
        {
            var index = IndexOfInstance(target);
            var direction = index >= _lastInstanceIndex ? 1 : -1;
            if (index >= 0) _lastInstanceIndex = index;
            ShowInstance(target);
            AnimateDetailSwitch(direction);
        }

        _suppressSelection = true;
        InstanceList.SelectedItem = target;
        _suppressSelection = false;
    }

    // ---------------- 页头 / 工具栏 ----------------

    private void RefreshInstanceList(string? selectId = null)
    {
        ScanInstances();
        InstanceList.ItemsSource = null;
        InstanceList.ItemsSource = _instances;
        InstanceCountText.Text = $"{_instances.Count} / {_instances.Count}";

        InstanceInfo? selected = null;
        string targetId = selectId ?? _current?.Id ?? LocalSettings.Current.SelectedInstanceId;
        if (!string.IsNullOrWhiteSpace(targetId))
        {
            selected = _instances.FirstOrDefault(instance =>
                string.Equals(instance.Id, targetId, StringComparison.OrdinalIgnoreCase));
        }
        selected ??= _instances.FirstOrDefault();

        _suppressSelection = true;
        InstanceList.SelectedItem = selected;
        _suppressSelection = false;
        if (selected is not null)
        {
            _lastInstanceIndex = IndexOfInstance(selected);
            ShowInstance(selected);
        }

        bool local = Directory.Exists(Path.Combine(_gameDirectory, "versions"));
        ScanStatusText.Text = L.T("Instances.Scan.Status", "已检测到 {0} 个实例 · {1} 个账户 · 最后扫描 {2}",
            _instances.Count, CountAccounts(), L.T("Instances.Scan.JustNow", "刚刚"));
        DiskUsageText.Text = L.T("Instances.Scan.DiskUsage", "磁盘占用 {0}", FormatSize(GetDirectorySize(_gameDirectory)));
        _ = local;
    }

    /// <summary>真实账户数：Microsoft + Authlib 皮肤站凭据 + 离线账户（与账户页同一数据源）。</summary>
    private static int CountAccounts()
    {
        try
        {
            MofuCore.Account.MinecraftTokenHandle handle = new();
            return handle.LoadMicrosoftAccounts().Count
                   + handle.LoadAuthlibCredentials().Count
                   + handle.LoadOfflineAccounts().Count;
        }
        catch
        {
            return 0;
        }
    }

    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is not { } storage)
            {
                Shell.Toast(L.T("Instances.Import.NoPicker", "当前环境无法打开文件夹选择器"));
                return;
            }

            IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = L.T("Instances.Import.PickFolder", "选择其他启动器的 .minecraft 目录"),
                    AllowMultiple = false,
                });
            if (folders.Count == 0) return;

            string source = folders[0].TryGetLocalPath() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(source)) return;

            string? versionsRoot = Directory.Exists(Path.Combine(source, "versions"))
                ? Path.Combine(source, "versions")
                : Directory.Exists(Path.Combine(source, ".minecraft", "versions"))
                    ? Path.Combine(source, ".minecraft", "versions")
                    : null;
            if (versionsRoot is null)
            {
                Shell.Toast(L.T("Instances.Import.NoVersions", "所选目录下没有 versions 文件夹"));
                return;
            }

            string targetRoot = Path.Combine(_gameDirectory, "versions");
            Directory.CreateDirectory(targetRoot);
            int imported = 0;
            int skipped = 0;
            foreach (string directory in Directory.GetDirectories(versionsRoot))
            {
                string name = Path.GetFileName(directory);
                if (!File.Exists(Path.Combine(directory, name + ".json")))
                {
                    skipped++;
                    continue;
                }

                string destination = Path.Combine(targetRoot, name);
                if (Directory.Exists(destination))
                {
                    skipped++;
                    continue;
                }

                CopyDirectory(directory, destination);
                imported++;
            }

            RefreshInstanceList();
            Shell.Toast(L.T("Instances.Import.Done",
                "已导入 {0} 个实例（跳过 {1} 个已存在/无效）", imported, skipped));
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Instances.Import.Failed", "导入实例失败：{0}", ex.Message));
        }
    }

    private void OnRescanClick(object? sender, RoutedEventArgs e)
    {
        RefreshInstanceList();
        Shell.Toast(L.T("Instances.Toast.Rescanned", "已重新扫描游戏目录，{0} 个实例就绪", _instances.Count));
    }

    private void OnOpenGameDirClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_gameDirectory);
            Process.Start(new ProcessStartInfo(_gameDirectory) { UseShellExecute = true });
            Shell.Toast(L.T("Instances.Toast.OpenGameDir", "打开游戏目录 {0}", _gameDirectory));
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Instances.Toast.OpenGameDirFailed", "打开游戏目录失败：{0}", ex.Message));
        }
    }

    private void OnRewindClick(object? sender, RoutedEventArgs e)
    {
        Shell.Dialog(RewindDialogContent.Build(_current?.Name ?? L.T("Instances.Current", "当前实例")), "Rewind");
    }

    private void OnGameDirChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;

        var dir = (GameDirCombo.SelectedItem as ComboBoxItem)?.Content as string ?? string.Empty;
        if (string.IsNullOrWhiteSpace(dir)) return;
        if (string.Equals(dir, _gameDirectory, StringComparison.OrdinalIgnoreCase)) return;

        _gameDirectory = dir;
        YamlRead.SaveGameDirectory(dir);
        RefreshInstanceList();
        Shell.Toast(L.T("Instances.Toast.DirChanged", "已切换游戏目录到 {0}", dir));
    }

    private async void OnNewInstanceClick(object? sender, RoutedEventArgs e)
    {
        if (_installing) return;
        _installing = true;
        try
        {
            Shell.Toast(L.T("Instances.Create.LoadingVersions", "正在获取可安装的 Minecraft 版本…"));
            IReadOnlyList<MinecraftVersion> versions = await MinecraftDownload.GetVersionsAsync();
            List<MinecraftVersion> candidates = versions
                .Where(version => version.Type is "release" or "snapshot")
                .Take(80)
                .ToList();
            if (candidates.Count == 0)
            {
                Shell.Toast(L.T("Instances.Create.NoVersions", "没有获取到可安装的 Minecraft 版本"));
                return;
            }

            var versionCombo = new ComboBox
            {
                ItemsSource = candidates.Select(version => version.Id).ToList(),
                SelectedIndex = 0,
                Height = 34,
            };
            var loaderCombo = new ComboBox
            {
                ItemsSource = new List<string>
                {
                    L.T("Download.Minecraft.Vanilla", "原版"),
                    ModLoaderInstaller.DisplayName(ModLoaderKind.Fabric),
                    ModLoaderInstaller.DisplayName(ModLoaderKind.Quilt),
                    ModLoaderInstaller.DisplayName(ModLoaderKind.Forge),
                    ModLoaderInstaller.DisplayName(ModLoaderKind.NeoForge),
                    ModLoaderInstaller.DisplayName(ModLoaderKind.OptiFine),
                },
                SelectedIndex = 0,
                Height = 34,
            };
            var nameBox = new TextBox
            {
                Height = 34,
                PlaceholderText = L.T("Instances.Create.NameHint", "实例名称（留空自动生成）"),
            };
            var statusText = new TextBlock
            {
                FontSize = 11.5,
                Foreground = ThemeTokens.Brush("NekoMutedBrush"),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            };
            var progressBar = new ProgressBar
            {
                Height = 6,
                Minimum = 0,
                Maximum = 100,
                IsVisible = false,
            };
            var installButton = new Button
            {
                Content = L.T("Instances.Create.Install", "安装实例"),
                Classes = { "primary" },
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            };

            var panel = new StackPanel { Spacing = 8, Width = 380 };
            panel.Children.Add(new TextBlock
            {
                Text = L.T("Instances.Create.Version", "Minecraft 版本"),
                Classes = { "field-label" },
            });
            panel.Children.Add(versionCombo);
            panel.Children.Add(new TextBlock
            {
                Text = L.T("Instances.Create.Loader", "加载器"),
                Classes = { "field-label" },
                Margin = new Thickness(0, 6, 0, 0),
            });
            panel.Children.Add(loaderCombo);
            panel.Children.Add(new TextBlock
            {
                Text = L.T("Instances.Create.Name", "实例名称"),
                Classes = { "field-label" },
                Margin = new Thickness(0, 6, 0, 0),
            });
            panel.Children.Add(nameBox);
            panel.Children.Add(installButton);
            panel.Children.Add(progressBar);
            panel.Children.Add(statusText);

            installButton.Click += async (_, _) =>
            {
                if (installButton.IsEnabled is false) return;
                installButton.IsEnabled = false;
                progressBar.IsVisible = true;
                DownloadTaskHandle? installTask = null;
                try
                {
                    string versionId = versionCombo.SelectedItem as string ?? candidates[0].Id;
                    int loaderIndex = Math.Max(0, loaderCombo.SelectedIndex);
                    ModLoaderKind loaderKind = loaderIndex switch
                    {
                        1 => ModLoaderKind.Fabric,
                        2 => ModLoaderKind.Quilt,
                        3 => ModLoaderKind.Forge,
                        4 => ModLoaderKind.NeoForge,
                        5 => ModLoaderKind.OptiFine,
                        _ => ModLoaderKind.Vanilla,
                    };
                    string loaderName = ModLoaderInstaller.DisplayName(loaderKind);
                    string name = (nameBox.Text ?? string.Empty).Trim();
                    bool autoName = name.Length == 0;
                    if (autoName)
                    {
                        name = loaderKind == ModLoaderKind.Vanilla
                            ? versionId
                            : ModLoaderInstaller.DefaultInstanceName(loaderKind, versionId, string.Empty);
                    }

                    if (loaderKind == ModLoaderKind.Vanilla)
                    {
                        // 原版安装器按版本号落盘，实例名无法单独指定
                        name = versionId;
                    }

                    MinecraftDownload.ValidateVersionId(name);
                    if (loaderKind != ModLoaderKind.Vanilla &&
                        string.Equals(name, versionId, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            L.T("Instances.Create.SameName", "实例名称不能与原版版本号相同"));
                    }

                    string instanceDirectory = Path.Combine(_gameDirectory, "versions", name);
                    if (loaderKind == ModLoaderKind.Vanilla && Directory.Exists(instanceDirectory))
                    {
                        throw new InvalidOperationException(
                            L.T("Instances.Create.Exists", "实例「{0}」已存在", name));
                    }

                    if (loaderKind == ModLoaderKind.Vanilla)
                    {
                        MinecraftVersion target = candidates.First(version => version.Id == versionId);
                        var download = new MinecraftDownload(_gameDirectory);
                        installTask = DownloadTaskService.Start(
                            $"Minecraft {versionId}",
                            L.T("Download.Version.Preparing", "准备下载"), canPause: true);
                        var installProgress = new Progress<MinecraftInstallProgress>(value =>
                        {
                            installTask.Report(value.Percentage,
                                string.IsNullOrWhiteSpace(value.Detail) ? value.StageName : $"{value.StageName} · {value.Detail}",
                                value.BytesPerSecond > 0 ? MofuCore.Tools.MemoryRead.FormatBytes((long)value.BytesPerSecond) + "/s" : null,
                                value.TotalBytes > 0
                                    ? $"{MofuCore.Tools.MemoryRead.FormatBytes(value.CompletedBytes)} / {MofuCore.Tools.MemoryRead.FormatBytes(value.TotalBytes)}"
                                    : null);
                            Dispatcher.UIThread.Post(() => progressBar.Value = value.Percentage);
                        });

                        await download.InstallAsync(target, installProgress, installTask.Token);
                        installTask.Complete();
                    }
                    else
                    {
                        statusText.Text = L.T("Instances.Create.InstallingLoader",
                            "正在准备 {0}…", loaderName);
                        IReadOnlyList<ModLoaderVersionInfo> loaderVersions = await ModLoaderInstaller
                            .GetVersionsAsync(loaderKind, versionId);
                        ModLoaderVersionInfo? loaderVersion = loaderVersions
                            .FirstOrDefault(version => version.IsStable) ?? loaderVersions.FirstOrDefault();
                        if (loaderVersion is null)
                        {
                            throw new InvalidOperationException(
                                L.T("Instances.Create.NoLoader", "没有找到适配 {0} 的 {1} 版本", versionId, loaderName));
                        }

                        if (autoName)
                        {
                            name = ModLoaderInstaller.DefaultInstanceName(
                                loaderKind, versionId, loaderVersion.LoaderVersion);
                        }

                        instanceDirectory = Path.Combine(_gameDirectory, "versions", name);
                        if (Directory.Exists(instanceDirectory))
                        {
                            throw new InvalidOperationException(
                                L.T("Instances.Create.Exists", "实例「{0}」已存在", name));
                        }

                        installTask = DownloadTaskService.Start(
                            $"Minecraft {versionId} · {ModLoaderInstaller.DisplayName(loaderKind)} {loaderVersion.LoaderVersion}",
                            L.T("Download.Version.Preparing", "准备下载"), canPause: true);
                        var installProgress = new Progress<ModLoaderInstallProgress>(value =>
                        {
                            installTask.Report(value.Percentage,
                                string.IsNullOrWhiteSpace(value.Detail) ? value.StageName : $"{value.StageName} · {value.Detail}",
                                value.BytesPerSecond > 0 ? MofuCore.Tools.MemoryRead.FormatBytes((long)value.BytesPerSecond) + "/s" : null,
                                value.TotalBytes > 0
                                    ? $"{MofuCore.Tools.MemoryRead.FormatBytes(value.CompletedBytes)} / {MofuCore.Tools.MemoryRead.FormatBytes(value.TotalBytes)}"
                                    : null);
                            Dispatcher.UIThread.Post(() => progressBar.Value = value.Percentage);
                        });

                        await ModLoaderInstaller.InstallAsync(
                            loaderVersion, _gameDirectory, name, installProgress, installTask.Token);
                        installTask.Complete();
                    }

                    statusText.Text = L.T("Instances.Create.Done", "安装完成");
                    RefreshInstanceList();
                    Shell.Toast(L.T("Instances.Create.Done", "安装完成"));
                }
                catch (OperationCanceledException)
                {
                    installTask?.MarkCancelled();
                    statusText.Text = L.T("Instances.Create.Cancelled", "已取消安装");
                }
                catch (Exception ex)
                {
                    installTask?.Fail(ex.Message);
                    statusText.Text = L.T("Instances.Create.Failed", "安装失败：{0}", ex.Message);
                }
                finally
                {
                    installButton.IsEnabled = true;
                    progressBar.IsVisible = false;
                }
            };

            Shell.Dialog(panel, L.T("Instances.Create.Title", "新建实例"));
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Instances.Create.Failed", "安装失败：{0}", ex.Message));
        }
        finally
        {
            _installing = false;
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
    }

    // ---------------- 子标签 ----------------

    private void OnTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tab }) return;

        var next = -1;
        for (var i = 0; i < _tabButtons.Count; i++)
        {
            if (string.Equals((string?)_tabButtons[i].Tag, tab, StringComparison.Ordinal))
            {
                next = i;
                break;
            }
        }
        if (next < 0 || next == _activeTabIndex) return;

        var direction = next >= _activeTabIndex ? 1 : -1;
        _activeTabIndex = next;

        for (var i = 0; i < _tabButtons.Count; i++)
        {
            var active = i == next;
            _tabButtons[i].Classes.Set("active", active);
            _tabPanels[i].IsVisible = active;
        }

        // 切换标签后回到顶部，避免上一个长页面的滚动位置带过来
        DetailScroll.Offset = default;
        AnimateTabSwitch(_tabPanels[next], direction);
    }

    // ---------------- 启动设置 ----------------

    private void OnMemoryChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (MemoryValueText is null) return;

        var mb = (int)Math.Round(e.NewValue);
        MemoryValueText.Text = L.T("Instances.Launch.MemoryValue", "{0} MB · {1} GB", mb, mb / 1024.0);
        if (DetailMemoryValue is not null)
        {
            DetailMemoryValue.Text = L.T("Instances.Detail.MemoryValue", "{0} MB", mb);
        }
    }

    private void OnResetLaunchSettingsClick(object? sender, RoutedEventArgs e)
    {
        GlobalLaunchSettings settings = YamlRead.LoadGlobalLaunchSettings();
        MemorySlider.Value = settings.MaximumMemoryMb;
        JavaPathBox.Text = string.Empty;
        JvmArgsBox.Text = "-XX:+UseG1GC -XX:+AlwaysPreTouch";
        WindowSizeCombo.SelectedIndex = 2;
        FullscreenToggle.IsChecked = settings.LaunchFullscreen;
        AutoLoginToggle.IsChecked = true;
        Shell.Toast(L.T("Instances.Toast.LaunchReset", "已恢复默认启动设置"));
    }

    private void OnSaveLaunchSettingsClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            GlobalLaunchSettings settings = YamlRead.LoadGlobalLaunchSettings();
            int mb = (int)Math.Round(MemorySlider.Value);
            settings.MaximumMemoryMb = mb;
            settings.MinimumMemoryMb = Math.Min(
                settings.MinimumMemoryMb > 0 ? settings.MinimumMemoryMb : 2048, mb);
            settings.LaunchFullscreen = FullscreenToggle.IsChecked == true;
            settings.JavaExecutable = (JavaPathBox.Text ?? string.Empty).Trim();
            settings.AdditionalJvmArguments = (JvmArgsBox.Text ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            if (!YamlRead.SaveGlobalLaunchSettings(settings))
            {
                Shell.Toast(L.T("Instances.Toast.LaunchSaveFailed", "保存启动设置失败"));
                return;
            }

            if (_current is not null)
            {
                _current = _current with
                {
                    MemoryMb = mb,
                    JavaPath = settings.JavaExecutable,
                };
                DetailMemoryValue.Text = L.T("Instances.Detail.MemoryValue", "{0} MB", mb);
                DetailJavaValue.Text = JavaLabel(settings.JavaExecutable);
            }

            Shell.Toast(L.T("Instances.Toast.LaunchSaved", "已保存 {0} 的启动设置 · 内存 {1} MB",
                _current?.Name ?? L.T("Instances.Current", "当前实例"), mb));
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Instances.Toast.LaunchSaveFailed", "保存启动设置失败") + "：" + ex.Message);
        }
    }

    // ---------------- Java 设置 ----------------

    private async Task LoadJavaAsync()
    {
        CancelCts(ref _javaCts);
        _javaCts = new CancellationTokenSource();
        var token = _javaCts.Token;

        try
        {
            string gameDirectory = SafeGameDirectory();
            string? runtimeDirectory = gameDirectory.Length == 0
                ? null
                : Path.Combine(gameDirectory, "runtime");
            IReadOnlyList<JavaRuntime> runtimes = await JavaPathSearch.FindAllJavaAsync(runtimeDirectory, token);
            if (token.IsCancellationRequested) return;

            string defaultJava = SafeDefaultJava();
            List<InstalledJavaEntry> entries = runtimes
                .Select(runtime => new InstalledJavaEntry
                {
                    DisplayName = JavaDisplayName(runtime),
                    JavaPath = runtime.JavaPath,
                    Version = runtime.MajorVersion?.ToString(CultureInfo.InvariantCulture) ?? runtime.VersionString,
                    IsDefault = string.Equals(runtime.JavaPath, defaultJava, StringComparison.OrdinalIgnoreCase),
                })
                .ToList();

            // 已保存但本次未扫描到的路径也要列出来，保证「使用 / 删除」能管理已保存列表
            foreach (JavaPathItem item in YamlRead.GetJavaPaths())
            {
                if (string.IsNullOrWhiteSpace(item.Path)
                    || entries.Any(entry => string.Equals(entry.JavaPath, item.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                entries.Add(new InstalledJavaEntry
                {
                    DisplayName = L.T("Instances.Java.SavedJava", "已保存 · Java {0}", item.Version),
                    JavaPath = item.Path,
                    Version = item.Version,
                    IsDefault = string.Equals(item.Path, defaultJava, StringComparison.OrdinalIgnoreCase),
                    IsSavedOnly = true,
                });
            }

            Dispatcher.UIThread.Post(() =>
            {
                _installedJava = entries;
                InstalledJavaList.ItemsSource = entries;
                JavaListStatus.Text = entries.Count == 0
                    ? L.T("Instances.Java.NoRuntimes", "未检测到本机 Java 运行时，请先安装或手动添加。")
                    : L.T("Instances.Java.Detected", "已检测到 {0} 个 Java 运行时（默认 Java 会用于启动所有实例）", entries.Count);
            });
        }
        catch (OperationCanceledException)
        {
            // 页面卸载 / 重新检测
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => JavaListStatus.Text =
                L.T("Instances.Java.DetectFailed", "Java 检测失败：{0}", ex.Message));
        }
    }

    private static string JavaDisplayName(JavaRuntime runtime)
    {
        string version = runtime.VersionString.Length > 0
            ? runtime.VersionString
            : runtime.MajorVersion?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        return $"{runtime.VendorName} {version}".Trim();
    }

    private async void OnAddJavaClick(object? sender, RoutedEventArgs e)
    {
        if (_installing) return;
        _installing = true;
        try
        {
            var majorCombo = new ComboBox
            {
                ItemsSource = new List<string> { "8", "17", "21" },
                SelectedIndex = 2,
                Height = 34,
            };
            var statusText = new TextBlock
            {
                FontSize = 11.5,
                Foreground = ThemeTokens.Brush("NekoMutedBrush"),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            };
            var progressBar = new ProgressBar
            {
                Height = 6,
                Minimum = 0,
                Maximum = 100,
                IsVisible = false,
            };
            var installButton = new Button
            {
                Content = L.T("Instances.Java.Download", "下载并启用"),
                Classes = { "primary" },
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            };

            var panel = new StackPanel { Spacing = 8, Width = 340 };
            panel.Children.Add(new TextBlock
            {
                Text = L.T("Instances.Java.AddTitle", "选择要安装的 Java 主版本"),
                Classes = { "field-label" },
            });
            panel.Children.Add(majorCombo);
            panel.Children.Add(installButton);
            panel.Children.Add(progressBar);
            panel.Children.Add(statusText);

            installButton.Click += async (_, _) =>
            {
                if (installButton.IsEnabled is false) return;
                installButton.IsEnabled = false;
                progressBar.IsVisible = true;
                DownloadTaskHandle? installTask = null;
                try
                {
                    if (!int.TryParse(majorCombo.SelectedItem as string, out int major)) major = 21;
                    installTask = DownloadTaskService.Start(
                        $"{L.T("Download.Java.Runtime", "Java 运行时")} {major}",
                        L.T("Download.Java.Preparing", "正在准备下载…"));
                    var progress = new Progress<JavaRuntimeInstallProgress>(value =>
                    {
                        installTask.Report(value.Percent, value.Phase);
                        Dispatcher.UIThread.Post(() =>
                        {
                            progressBar.Value = value.Percent;
                            statusText.Text = value.Phase;
                        });
                    });

                    InstalledJavaRuntime? runtime = await JavaDownload.InstallForMajorVersionAsync(
                        major, _gameDirectory, progress, installTask.Token);
                    if (runtime is null)
                    {
                        throw new InvalidOperationException(
                            L.T("Instances.Java.NoRuntime", "Mojang 没有提供 Java {0} 的运行时", major));
                    }

                    YamlRead.SaveJava(runtime.JavaExecutablePath, runtime.VersionName);
                    installTask.Complete();
                    await LoadJavaAsync();
                    if (JavaPathBox is not null) JavaPathBox.Text = runtime.JavaExecutablePath;
                    statusText.Text = L.T("Instances.Java.Installed", "已安装 Java {0}", runtime.VersionName);
                    Shell.Toast(L.T("Instances.Java.Installed", "已安装 Java {0}", runtime.VersionName));
                }
                catch (OperationCanceledException)
                {
                    installTask?.MarkCancelled();
                    statusText.Text = L.T("Instances.Create.Cancelled", "已取消安装");
                }
                catch (Exception ex)
                {
                    installTask?.Fail(ex.Message);
                    statusText.Text = ex.Message;
                }
                finally
                {
                    installButton.IsEnabled = true;
                    progressBar.IsVisible = false;
                }
            };

            Shell.Dialog(panel, L.T("Instances.Java.AddTitle", "选择要安装的 Java 主版本"));
        }
        finally
        {
            _installing = false;
        }
    }

    private void OnUseJavaClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string javaPath } || string.IsNullOrWhiteSpace(javaPath)) return;

        try
        {
            InstalledJavaEntry? entry = _installedJava.FirstOrDefault(item =>
                string.Equals(item.JavaPath, javaPath, StringComparison.OrdinalIgnoreCase));
            string version = string.IsNullOrWhiteSpace(entry?.Version) ? "unknown" : entry.Version;
            if (!YamlRead.SaveJava(javaPath, version))
            {
                Shell.Toast(L.T("Instances.Toast.SaveDefaultJavaFailed", "保存默认 Java 失败"));
                return;
            }

            if (DetailJavaValue is not null)
            {
                DetailJavaValue.Text = entry?.DisplayName ?? L.T("Instances.Detail.JavaCustom", "自定义 Java");
            }
            if (JavaPathBox is not null) JavaPathBox.Text = javaPath;
            _ = LoadJavaAsync();
            Shell.Toast(L.T("Instances.Toast.SetDefaultJava", "已将 {0} 设为默认 Java",
                entry?.DisplayName ?? javaPath));
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Instances.Toast.SetDefaultJavaFailed", "设置默认 Java 失败：{0}", ex.Message));
        }
    }

    private void OnDeleteJavaClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string javaPath } || string.IsNullOrWhiteSpace(javaPath)) return;

        try
        {
            bool removed = YamlRead.RemoveJava(javaPath);
            if (removed)
            {
                _ = LoadJavaAsync();
                Shell.Toast(L.T("Instances.Toast.JavaRemoved", "已从列表移除该 Java（不会删除磁盘上的文件）"));
            }
            else
            {
                Shell.Toast(L.T("Instances.Toast.JavaNotInList", "该 Java 不在已保存列表中"));
            }
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Instances.Toast.JavaRemoveFailed", "移除 Java 失败：{0}", ex.Message));
        }
    }

    private static string SafeGameDirectory()
    {
        try
        {
            string directory = YamlRead.GameDirectory;
            return string.IsNullOrWhiteSpace(directory) ? string.Empty : directory.Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SafeDefaultJava()
    {
        try
        {
            return YamlRead.JavaExecutable;
        }
        catch
        {
            return string.Empty;
        }
    }

    // ---------------- 内容 ----------------

    private void OnAddContentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string group }) return;
        Shell.Toast(L.T("Instances.Toast.SearchContent", "在下载大厅搜索并安装{0}", L.T(group, group)));
        Shell.Go("download");
    }

    private void OnEnterWorldClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name } || _current is null) return;
        string saves = ContentDirectoryFor(_current.Id, "saves");
        string target = Path.Combine(saves, name);
        try
        {
            if (!Directory.Exists(target))
            {
                Shell.Toast(L.T("Instances.Worlds.Missing", "世界目录不存在：{0}", target));
                return;
            }

            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            Shell.Toast(L.T("Instances.Worlds.Opened", "已打开世界目录：{0}", name));
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Instances.Worlds.OpenFailed", "打开世界目录失败：{0}", ex.Message));
        }
    }

    // ---------------- 存档操作（Rewind / 备份 / 导出 / 导入） ----------------

    /// <summary>
    /// 行数据统一解析：内容页行绑定 <see cref="ContentEntry"/>，概览页行绑定 <see cref="WorldInfo"/>。
    /// 返回（世界名, 世界目录）。
    /// </summary>
    private static (string Name, string Directory)? ResolveWorldRow(object? dataContext)
    {
        return dataContext switch
        {
            ContentEntry entry when !string.IsNullOrWhiteSpace(entry.FilePath) => (entry.Name, entry.FilePath),
            WorldInfo world when !string.IsNullOrWhiteSpace(world.FilePath) => (world.Name, world.FilePath),
            _ => null,
        };
    }

    private void OnWorldRewindClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || ResolveWorldRow(button.DataContext) is not { } world) return;
        // 显式传目录：版本隔离实例的存档在 versions/<id>/saves 下，按名字解析会找不到
        Shell.Dialog(RewindDialogContent.Build(world.Name, null, world.Directory), "Rewind");
    }

    private void OnWorldOpenClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || ResolveWorldRow(button.DataContext) is not { } world) return;
        try
        {
            if (!Directory.Exists(world.Directory))
            {
                Shell.Toast(L.T("Instances.Worlds.Missing", "世界目录不存在：{0}", world.Directory));
                return;
            }

            Process.Start(new ProcessStartInfo(world.Directory) { UseShellExecute = true });
            Shell.Toast(L.T("Instances.Worlds.Opened", "已打开世界目录：{0}", world.Name));
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Instances.Worlds.OpenFailed", "打开世界目录失败：{0}", ex.Message));
        }
    }

    private void OnBackupWorldClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || ResolveWorldRow(button.DataContext) is not { } world) return;
        try
        {
            if (!Directory.Exists(world.Directory))
            {
                Shell.Toast(L.T("Instances.Worlds.Missing", "世界目录不存在：{0}", world.Directory));
                return;
            }

            string savesRoot = Path.GetDirectoryName(
                world.Directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!;
            string stamp = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string target = Path.Combine(savesRoot, $"{world.Name} copy {stamp}");
            for (int suffix = 2; Directory.Exists(target); suffix++)
            {
                target = Path.Combine(savesRoot, $"{world.Name} copy {stamp} ({suffix})");
            }

            CopyDirectoryRecursive(world.Directory, target);
            Shell.Toast(L.T("Instances.Worlds.Backup.Done", "已备份：{0}", Path.GetFileName(target)));
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Instances.Worlds.Backup.Failed", "备份失败：{0}", ex.Message));
        }
    }

    private async void OnExportWorldClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || ResolveWorldRow(button.DataContext) is not { } world) return;
        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null || !Directory.Exists(world.Directory)) return;
        try
        {
            Avalonia.Platform.Storage.IStorageFile? target =
                await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = L.T("Instances.Worlds.Export.Title", "导出存档为 ZIP"),
                SuggestedFileName = SanitizeFileName(world.Name),
                DefaultExtension = "zip",
                FileTypeChoices = [new FilePickerFileType("ZIP") { Patterns = ["*.zip"] }],
            });
            if (target is null) return;

            string zipPath = target.Path.LocalPath;
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(world.Directory, zipPath, CompressionLevel.Optimal,
                includeBaseDirectory: false);
            Shell.Toast(L.T("Instances.Worlds.Export.Done", "已导出：{0}", Path.GetFileName(zipPath)));
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Instances.Worlds.Export.Failed", "导出失败：{0}", ex.Message));
        }
    }

    private async void OnImportWorldClick(object? sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        try
        {
            IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    AllowMultiple = false,
                    Title = L.T("Instances.Worlds.Import.Title", "选择要导入的存档 ZIP"),
                    FileTypeFilter = [new FilePickerFileType("ZIP") { Patterns = ["*.zip"] }],
                });
            if (files.Count == 0) return;

            string savesRoot = ContentDirectoryFor(_current.Id, "saves");
            string worldName = ImportWorldFromZip(files[0].Path.LocalPath, savesRoot);
            Shell.Toast(L.T("Instances.Worlds.Import.Done", "已导入存档：{0}", worldName));
            if (_current is not null) LoadContentLists(_current);
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Instances.Worlds.Import.Failed", "导入存档失败：{0}", ex.Message));
        }
    }

    /// <summary>
    /// 从 zip 导入世界：兼容两种打包方式——level.dat 在压缩包根目录，
    /// 或整个世界在压缩包里唯一的子文件夹中。返回导入后的世界名。
    /// </summary>
    private string ImportWorldFromZip(string zipPath, string savesRoot)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "mofumofu-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporary);
            ZipFile.ExtractToDirectory(zipPath, temporary, overwriteFiles: false);

            // level.dat 在哪一层，世界根就在哪一层
            string worldRoot;
            string worldName;
            if (File.Exists(Path.Combine(temporary, "level.dat")))
            {
                worldRoot = temporary;
                worldName = Path.GetFileNameWithoutExtension(zipPath);
            }
            else
            {
                string? candidate = Directory.GetDirectories(temporary).FirstOrDefault(directory =>
                    File.Exists(Path.Combine(directory, "level.dat")));
                if (candidate is null)
                {
                    throw new InvalidOperationException(L.T("Instances.Worlds.Import.NoLevel",
                        "ZIP 里没有找到 level.dat（需要放在压缩包根目录或唯一子文件夹内）"));
                }

                worldRoot = candidate;
                worldName = Path.GetFileName(candidate);
            }

            Directory.CreateDirectory(savesRoot);
            worldName = SanitizeFileName(worldName);
            if (worldName.Length == 0) worldName = "world";

            string target = Path.Combine(savesRoot, worldName);
            for (int suffix = 2; Directory.Exists(target); suffix++)
            {
                target = Path.Combine(savesRoot, $"{worldName} ({suffix})");
            }

            CopyDirectoryRecursive(worldRoot, target);
            return Path.GetFileName(target);
        }
        finally
        {
            try
            {
                Directory.Delete(temporary, true);
            }
            catch
            {
                // 临时目录清理失败不影响导入结果
            }
        }
    }

    private static void CopyDirectoryRecursive(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = file[(source.Length + 1)..];
            string destination = Path.Combine(target, relative);
            string? destinationDirectory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            File.Copy(file, destination, overwrite: true);
        }
    }

    private static string SanitizeFileName(string name)
    {
        string cleaned = string.Concat(
            name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        return cleaned.Trim();
    }

    // ---------------- 游戏设置 ----------------

    private void OnOptionSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (RenderDistanceValue is null) return;

        if (ReferenceEquals(sender, RenderDistanceSlider))
        {
            RenderDistanceValue.Text = L.T("Instances.Game.Chunks", "{0} 区块",
                (int)Math.Round(RenderDistanceSlider.Value));
        }
        else if (ReferenceEquals(sender, SimulationDistanceSlider))
        {
            SimulationDistanceValue.Text = L.T("Instances.Game.Chunks", "{0} 区块",
                (int)Math.Round(SimulationDistanceSlider.Value));
        }
        else if (ReferenceEquals(sender, FovSlider))
        {
            FovValue.Text = $"{(int)Math.Round(FovSlider.Value)}°";
        }
        else if (ReferenceEquals(sender, SensitivitySlider))
        {
            SensitivityValue.Text = $"{(int)Math.Round(SensitivitySlider.Value)}%";
        }
    }

    private void OnResetGameSettingsClick(object? sender, RoutedEventArgs e)
    {
        RenderDistanceSlider.Value = 12;
        SimulationDistanceSlider.Value = 10;
        FovSlider.Value = 70;
        SensitivitySlider.Value = 50;
        GuiScaleCombo.SelectedIndex = 1;
        MaxFpsCombo.SelectedIndex = 1;

        if (_current is not null)
        {
            try
            {
                SaveOptions(_current);
            }
            catch (Exception ex)
            {
                Shell.Toast(L.T("Instances.Toast.GameSaveFailed", "保存 options.txt 失败：{0}", ex.Message));
                return;
            }
        }

        Shell.Toast(L.T("Instances.Toast.GameReset", "已还原 options.txt 默认值"));
    }

    private void OnSaveGameSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (_current is null) return;

        try
        {
            SaveOptions(_current);
            Shell.Toast(L.T("Instances.Toast.GameSaved", "已保存 {0} 的 options.txt", _current.Name));
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Instances.Toast.GameSaveFailed", "保存 options.txt 失败：{0}", ex.Message));
        }
    }

    // ---------------- 管理 ----------------

    private void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        if (_current is null) return;

        try
        {
            string directory = InstanceRoot(_current.Id);
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            Shell.Toast(L.T("Instances.Toast.OpenInstanceDir", "打开实例目录 {0}", directory));
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Instances.Toast.OpenInstanceDirFailed", "打开实例目录失败：{0}", ex.Message));
        }
    }

    private void OnRenameInstanceClick(object? sender, RoutedEventArgs e)
    {
        if (_current is null) return;

        var nameBox = new TextBox { Text = _current.Id, Height = 34 };
        var statusText = new TextBlock
        {
            FontSize = 11.5,
            Foreground = ThemeTokens.Brush("NekoMutedBrush"),
        };
        var confirm = new Button
        {
            Content = L.T("Instances.Manage.Rename", "重命名实例"),
            Classes = { "primary" },
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        };
        var panel = new StackPanel { Spacing = 8, Width = 340 };
        panel.Children.Add(new TextBlock
        {
            Text = L.T("Instances.Manage.NewName", "新的实例名称"),
            Classes = { "field-label" },
        });
        panel.Children.Add(nameBox);
        panel.Children.Add(confirm);
        panel.Children.Add(statusText);

        confirm.Click += (_, _) =>
        {
            if (_current is null) return;
            try
            {
                string newName = (nameBox.Text ?? string.Empty).Trim();
                if (newName.Length == 0 || newName == _current.Id) return;
                string currentDirectory = Path.Combine(_gameDirectory, "versions", _current.Id);
                string targetDirectory = Path.Combine(_gameDirectory, "versions", newName);
                if (!Directory.Exists(currentDirectory))
                {
                    throw new DirectoryNotFoundException(currentDirectory);
                }
                if (Directory.Exists(targetDirectory))
                {
                    throw new InvalidOperationException(
                        L.T("Instances.Create.Exists", "实例「{0}」已存在", newName));
                }

                Directory.Move(currentDirectory, targetDirectory);
                string oldJson = Path.Combine(targetDirectory, _current.Id + ".json");
                string newJson = Path.Combine(targetDirectory, newName + ".json");
                if (File.Exists(oldJson)) File.Move(oldJson, newJson);
                RefreshInstanceList(newName);
                statusText.Text = L.T("Instances.Toast.Rename", "重命名实例「{0}」", newName);
                Shell.Toast(L.T("Instances.Toast.Rename", "重命名实例「{0}」", newName));
            }
            catch (Exception ex)
            {
                statusText.Text = ex.Message;
            }
        };

        Shell.Dialog(panel, L.T("Instances.Manage.Rename", "重命名实例"));
    }

    private void OnDuplicateInstanceClick(object? sender, RoutedEventArgs e)
    {
        if (_current is null) return;

        var nameBox = new TextBox { Text = _current.Id + "-copy", Height = 34 };
        var statusText = new TextBlock
        {
            FontSize = 11.5,
            Foreground = ThemeTokens.Brush("NekoMutedBrush"),
        };
        var confirm = new Button
        {
            Content = L.T("Instances.Manage.Duplicate", "复制实例"),
            Classes = { "primary" },
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        };
        var panel = new StackPanel { Spacing = 8, Width = 340 };
        panel.Children.Add(new TextBlock
        {
            Text = L.T("Instances.Manage.NewName", "新的实例名称"),
            Classes = { "field-label" },
        });
        panel.Children.Add(nameBox);
        panel.Children.Add(confirm);
        panel.Children.Add(statusText);

        confirm.Click += (_, _) =>
        {
            if (_current is null) return;
            try
            {
                string newName = (nameBox.Text ?? string.Empty).Trim();
                if (newName.Length == 0) return;
                string source = Path.Combine(_gameDirectory, "versions", _current.Id);
                string target = Path.Combine(_gameDirectory, "versions", newName);
                if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
                if (Directory.Exists(target))
                {
                    throw new InvalidOperationException(
                        L.T("Instances.Create.Exists", "实例「{0}」已存在", newName));
                }

                CopyDirectory(source, target);
                string sourceJson = Path.Combine(target, _current.Id + ".json");
                string targetJson = Path.Combine(target, newName + ".json");
                if (File.Exists(sourceJson)) File.Move(sourceJson, targetJson);
                RefreshInstanceList(newName);
                statusText.Text = L.T("Instances.Toast.Duplicate", "已复制实例「{0}」", newName);
                Shell.Toast(L.T("Instances.Toast.Duplicate", "已复制实例「{0}」", newName));
            }
            catch (Exception ex)
            {
                statusText.Text = ex.Message;
            }
        };

        Shell.Dialog(panel, L.T("Instances.Manage.Duplicate", "复制实例"));
    }

    private void OnExportModpackClick(object? sender, RoutedEventArgs e)
    {
        Shell.GoTool("modpack");
    }

    private void OnDeleteInstanceClick(object? sender, RoutedEventArgs e)
    {
        if (_current is null) return;

        InstanceInfo target = _current;
        var statusText = new TextBlock
        {
            FontSize = 11.5,
            Foreground = ThemeTokens.Brush("NekoMutedBrush"),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        var confirm = new Button
        {
            Content = L.T("Instances.Manage.Delete", "删除实例"),
            Classes = { "danger" },
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        };
        var panel = new StackPanel { Spacing = 10, Width = 360 };
        panel.Children.Add(new TextBlock
        {
            Text = L.T("Instances.Manage.DangerHint",
                "删除实例会移除 mods、config 与存档目录，此操作不可撤销。"),
            Classes = { "muted" },
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        panel.Children.Add(confirm);
        panel.Children.Add(statusText);

        confirm.Click += (_, _) =>
        {
            try
            {
                string directory = Path.Combine(_gameDirectory, "versions", target.Id);
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }

                statusText.Text = L.T("Instances.Toast.Deleted", "已删除实例「{0}」", target.Name);
                RefreshInstanceList();
                Shell.Toast(L.T("Instances.Toast.Deleted", "已删除实例「{0}」", target.Name));
            }
            catch (Exception ex)
            {
                statusText.Text = ex.Message;
            }
        };

        Shell.Dialog(panel, L.T("Instances.Manage.Delete", "删除实例"));
    }

    // ---------------- 过渡动画 ----------------

    private static readonly TimeSpan TabSwitchDuration = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan DetailSwitchDuration = TimeSpan.FromMilliseconds(340);
    private static readonly TimeSpan ListRefreshDuration = TimeSpan.FromMilliseconds(160);

    /// <summary>标签页横向滑入（前进从右、后退从左）。</summary>
    private void AnimateTabSwitch(Control? panel, int direction)
    {
        ResetMotion(panel);
        if (!UiAnimations.Enabled || panel is null) return;

        panel.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TabSwitchDuration,
                Easing = new CubicEaseOut(),
            },
            new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = TabSwitchDuration,
                Easing = new CubicEaseOut(),
            },
        };

        panel.Opacity = 0;
        panel.RenderTransform = TransformOperations.Parse(
            $"translateX({(direction >= 0 ? 22 : -22).ToString(CultureInfo.InvariantCulture)}px)");
        Dispatcher.UIThread.Post(() =>
        {
            panel.Opacity = 1;
            panel.RenderTransform = TransformOperations.Parse("translateX(0px)");
        }, DispatcherPriority.Render);
    }

    /// <summary>切换实例时的详情面板过渡：轻微横移 + 缩放 + 淡入。</summary>
    private void AnimateDetailSwitch(int direction)
    {
        ResetMotion(DetailPanel);
        if (!UiAnimations.Enabled) return;

        DetailPanel.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = DetailSwitchDuration,
                Easing = new CubicEaseOut(),
            },
            new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = DetailSwitchDuration,
                Easing = new CubicEaseOut(),
            },
        };

        var offset = (direction >= 0 ? 30 : -30).ToString(CultureInfo.InvariantCulture);
        DetailPanel.Opacity = 0;
        DetailPanel.RenderTransform = TransformOperations.Parse($"translateX({offset}px) scale(0.985)");
        Dispatcher.UIThread.Post(() =>
        {
            DetailPanel.Opacity = 1;
            DetailPanel.RenderTransform = TransformOperations.Parse("translateX(0px) scale(1)");
        }, DispatcherPriority.Render);
    }

    /// <summary>搜索/筛选后列表淡入，避免条目瞬间跳变。</summary>
    private void AnimateListRefresh(Control? target)
    {
        ResetMotion(target);
        if (!UiAnimations.Enabled || target is null) return;

        target.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = ListRefreshDuration,
                Easing = new CubicEaseOut(),
            },
        };
        target.Opacity = 0;
        Dispatcher.UIThread.Post(() => target.Opacity = 1, DispatcherPriority.Render);
    }

    /// <summary>把控件恢复为静态可见状态（动画取消时避免停在透明）。</summary>
    private static void ResetMotion(Control? target)
    {
        if (target is null) return;
        target.Opacity = 1;
        target.RenderTransform = TransformOperations.Parse("translateX(0px) scale(1)");
    }
}
