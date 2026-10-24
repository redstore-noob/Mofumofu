using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MofuCore.Config;
using MofuCore.Download.Minecraft;
using MofuCore.Java;
using Mofumofu.Launcher.Controls;
using Mofumofu.Launcher.Services;
using Mofumofu.Launcher.Services.Wallpaper;
using SukiUI.Controls;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views;

/// <summary>设置页里一行 Java 运行时（ItemsControl 的数据项）。</summary>
public sealed class SettingsJavaRow
{
    public string Path { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;

    public bool HasVersion => Version.Length > 0;

    public bool IsDefault { get; init; }

    public bool CanSetDefault => !IsDefault;
}

/// <summary>
/// 设置页：左侧条目导航（游戏 / 通用 / 界面 / 下载与网络 / 关于）+ 右侧设置内容。
/// 设置项写真实配置：MofuCore 的 config.yaml / game.yaml、LocalSettings、DownloadSourceProvider。
/// </summary>
public partial class SettingsView : UserControl
{
    private static readonly string[] CloseActions = ["tray", "exit", "ask"];

    private bool _loading = true;
    private int _activeTab;
    private TextBox? _searchBox;
    private StackPanel[] _tabPanels = Array.Empty<StackPanel>();
    private Button[] _tabButtons = Array.Empty<Button>();
    private readonly List<Border> _accentSwatches = new();

    public SettingsView()
    {
        InitializeComponent();

        _searchBox = SearchBox;
        _tabPanels = new[] { GamePanel, GeneralPanel, AppearancePanel, NetworkPanel, AboutPanel };
        _tabButtons = new[] { TabGame, TabGeneral, TabAppearance, TabNetwork, TabAbout };

        LoadSettings();
        _loading = false;
        ApplySearch(string.Empty);

        AttachedToVisualTree += (_, _) =>
        {
            RefreshGameDirectory();
            RefreshJavaList();
            Dispatcher.UIThread.Post(async () =>
            {
                RequestAppearanceApply();
                if (LocalSettings.Current.AccentFollowBackground)
                {
                    await ApplyAccentFromWallpaperAsync(silent: true);
                    UpdateAccentSelection();
                }
            });
        };
    }

    // ==================== 初始化 ====================

    private void LoadSettings()
    {
        var settings = LocalSettings.Current;

        ThemeCombo.SelectedIndex = settings.Theme switch
        {
            "light" => 1,
            "system" => 2,
            _ => 0,
        };

        BuildAccentSwatches();
        UpdateAccentSelection();

        AccentFollowToggle.IsChecked = settings.AccentFollowBackground;
        IconPaletteCombo.SelectedIndex = string.Equals(settings.IconPalette, "theme", StringComparison.OrdinalIgnoreCase)
            ? 0
            : 1;
        PanelBlurToggle.IsChecked = settings.PanelBlur;
        PremiumVisualsToggle.IsChecked = settings.PremiumVisuals;
        WindowOpacitySlider.Value = settings.WindowOpacity;
        BackgroundOpacitySlider.Value = settings.BackgroundOpacity;
        WindowOpacityValue.Text = $"{settings.WindowOpacity}%";
        BackgroundOpacityValue.Text = $"{settings.BackgroundOpacity}%";
        SquareCornerToggle.IsChecked = settings.SquareCorners;

        CloseActionCombo.SelectedIndex = Math.Max(0, Array.IndexOf(CloseActions, settings.CloseAction));
        SingleInstanceToggle.IsChecked = settings.SingleInstance;

        LanguageCards.ItemsSource = L.Languages;
        foreach (var card in LanguageCards.Items.OfType<Button>())
        {
            card.Classes.Set("active",
                string.Equals(card.Tag as string, L.CurrentLanguage, StringComparison.OrdinalIgnoreCase));
        }

        StartBarPlacementCombo.SelectedIndex = settings.StartBarPlacement switch
        {
            "left" => 1,
            "right" => 2,
            "top" => 3,
            _ => 0,
        };

        StartBarAutoHideToggle.IsChecked = settings.StartBarAutoHide;

        var memoryMb = ReadMaximumMemoryMb();
        MaxMemorySlider.Value = memoryMb;
        MaxMemoryValue.Text = $"{memoryMb} MB";

        RefreshJavaList();

        DownloadSourceCombo.SelectedIndex = SourceIndex(settings.DownloadSource);
        DownloadFallbackCombo.SelectedIndex = FallbackIndex(settings.DownloadFallbackSource);
        CustomSourceNameBox.Text = settings.CustomDownloadSourceName;
        CustomSourceUrlBox.Text = settings.CustomDownloadSourceUrl;
        CustomSourceRow.IsVisible = DownloadSourceSettings.IsCustom(settings.DownloadSource) ||
                                    DownloadSourceSettings.IsCustom(settings.DownloadFallbackSource);
        DownloadSourceSettings.Apply(settings);

        var parallel = Math.Clamp(settings.ParallelDownloads, 1, 128);
        ParallelSlider.Value = parallel;
        ParallelValue.Text = L.T("Settings.Network.Download.ParallelValue", "{0} 线程", parallel);
        DownloadSettings.ParallelDownloads = parallel;

        CurseForgeKeyBox.Text = settings.CurseForgeApiKey;
        AutoDependenciesToggle.IsChecked = settings.AutoInstallDependencies;

        RefreshAboutVersions();
    }

    private static void SaveAndToast(string message)
    {
        LocalSettings.Save();
        Shell.Toast(message);
    }

    // ==================== 强调色 / 语言 / 主题色映射 ====================

    private static string AccentKey(string key) =>
        "Settings.Appearance.Accent." + (key.Length == 0 ? key : char.ToUpperInvariant(key[0]) + key[1..]);

    private static string AccentLabel(string key)
    {
        if (ThemeManager.IsCustomAccent(key))
        {
            return L.T("Settings.Appearance.Accent.Custom", "自定义");
        }

        var fallback = key;
        foreach (var option in ThemeManager.AccentOptions)
        {
            if (option.Key != key) continue;
            fallback = option.Name;
            break;
        }

        return L.T(AccentKey(key), fallback);
    }

    // ==================== 窗口外观钩子 ====================

    /// <summary>
    /// 外观设置改完请主窗口立刻重刷（MainWindow 公开方法 ApplyAppearanceSettings）；
    /// 钩子还没接好时静默跳过，设置本身已经落盘。
    /// </summary>
    private static void RequestAppearanceApply()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        if (desktop.MainWindow is not { } window) return;

        try
        {
            window.GetType()
                .GetMethod("ApplyAppearanceSettings", BindingFlags.Public | BindingFlags.Instance)
                ?.Invoke(window, null);
        }
        catch
        {
            // 钩子未就绪 / 反射失败时忽略
        }
    }

    // ==================== 分区切换 ====================

    private void SelectTab(int index)
    {
        _activeTab = index;

        for (var i = 0; i < _tabButtons.Length; i++)
        {
            _tabButtons[i].Classes.Set("active", i == index);
        }

        if (_searchBox is not null && !string.IsNullOrWhiteSpace(_searchBox.Text))
        {
            // 切换分区时清空搜索，回到正常浏览状态
            _searchBox.Text = string.Empty;
        }
        else
        {
            ApplySearch(string.Empty);
        }

        ContentScroll.Offset = new Avalonia.Vector(0, 0);
        UiAnimations.FadeSlideIn(_tabPanels[index], 0.99, 6, 170);
    }

    private void OnTabGameClick(object? sender, RoutedEventArgs e) => SelectTab(0);
    private void OnTabGeneralClick(object? sender, RoutedEventArgs e) => SelectTab(1);
    private void OnTabAppearanceClick(object? sender, RoutedEventArgs e) => SelectTab(2);
    private void OnTabNetworkClick(object? sender, RoutedEventArgs e) => SelectTab(3);
    private void OnTabAboutClick(object? sender, RoutedEventArgs e) => SelectTab(4);

    // ==================== 搜索过滤 ====================

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (sender is TextBox textBox) ApplySearch(textBox.Text);
    }

    private void ApplySearch(string? raw)
    {
        var query = (raw ?? string.Empty).Trim();
        var searching = query.Length > 0;
        var anyVisible = false;
        var matchCount = 0;

        for (var i = 0; i < _tabPanels.Length; i++)
        {
            var panel = _tabPanels[i];
            var tabVisible = false;

            var groups = panel.GetLogicalDescendants()
                .OfType<Control>()
                .Where(c => c.Classes.Contains("setting-group"))
                .ToList();

            foreach (var group in groups)
            {
                var groupMatch = searching && Matches(group, query);
                var anyRowVisible = false;

                foreach (var row in group.GetLogicalDescendants()
                             .OfType<Control>()
                             .Where(c => c.Classes.Contains("setting-row")))
                {
                    var rowMatch = !searching || groupMatch || Matches(row, query);
                    row.IsVisible = rowMatch;
                    if (!rowMatch) continue;

                    anyRowVisible = true;
                    matchCount++;
                }

                var groupVisible = !searching || groupMatch || anyRowVisible;
                group.IsVisible = groupVisible;
                if (groupVisible) tabVisible = true;
            }

            panel.IsVisible = searching ? tabVisible : i == _activeTab;
            anyVisible |= panel.IsVisible;

            if (i < _tabButtons.Length)
            {
                // 搜索时清掉选中态、压暗没有命中的左侧条目
                _tabButtons[i].Classes.Set("active", !searching && i == _activeTab);
                _tabButtons[i].Classes.Set("dim", searching && !tabVisible);
            }
        }

        var headerWasVisible = SearchHeader.IsVisible;
        SearchHeader.IsVisible = searching;
        if (searching)
        {
            SearchSummary.Text = anyVisible
                ? L.T("Settings.Search.Summary", "在全部设置中找到 {0} 项与「{1}」相关的选项", matchCount, query)
                : L.T("Settings.Search.Empty", "没有找到与「{0}」相关的设置项，换个关键词试试。", query);

            if (!headerWasVisible)
            {
                UiAnimations.FadeSlideIn(SearchHeader, 0.99, 4, 140);
                foreach (var panel in _tabPanels)
                {
                    if (panel.IsVisible) UiAnimations.FadeSlideIn(panel, 0.995, 5, 160);
                }
            }
        }

        ContentScroll.Offset = new Avalonia.Vector(0, 0);
    }

    /// <summary>在控件自身 Tag 和其下所有 TextBlock 文本里做忽略大小写的包含匹配。</summary>
    private static bool Matches(Control control, string query)
    {
        if (control.Tag is string tag &&
            tag.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var textBlock in control.GetLogicalDescendants().OfType<TextBlock>())
        {
            var text = textBlock.Text;
            if (!string.IsNullOrEmpty(text) &&
                text.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // ==================== 游戏：目录 ====================

    /// <summary>未配置时使用的默认游戏目录：启动器目录下的 .minecraft。</summary>
    private static string DefaultGameDirectory => Path.Combine(AppContext.BaseDirectory, ".minecraft");

    private static string CurrentGameDirectory()
    {
        var configured = YamlRead.GameDirectory;
        return string.IsNullOrWhiteSpace(configured) ? DefaultGameDirectory : configured;
    }

    private void RefreshGameDirectory()
    {
        var path = CurrentGameDirectory();
        GameDirPathText.Text = path;
        VersionIsolationToggle.IsChecked = YamlRead.DefaultVersionIsolation;
        try
        {
            if (!Directory.Exists(path))
            {
                GameDirStatusText.Text = L.T("Settings.Game.Directory.MissingHint",
                    "目录不存在，打开或启动游戏时会自动创建");
                return;
            }

            var versionsPath = Path.Combine(path, "versions");
            var versions = Directory.Exists(versionsPath) ? Directory.GetDirectories(versionsPath).Length : 0;
            GameDirStatusText.Text = L.T("Settings.Game.Directory.ExistsHint", "已存在 · {0} 个版本", versions);
        }
        catch (Exception ex)
        {
            GameDirStatusText.Text = L.T("Settings.Game.Directory.Invalid", "目录不可用：{0}", ex.Message);
        }
    }

    private async void OnChangeGameDirClick(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            Shell.Toast(L.T("Settings.Game.Directory.Invalid", "目录不可用：{0}",
                L.T("Common.NotFound", "未找到")));
            return;
        }

        try
        {
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = L.T("Settings.Game.Directory.PickTitle", "选择游戏目录（.minecraft）"),
                AllowMultiple = false,
            });

            if (folders.Count == 0) return;

            var path = folders[0].Path.LocalPath;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                Shell.Toast(L.T("Settings.Game.Directory.Invalid", "目录不可用：{0}", path));
                return;
            }

            if (!YamlRead.SaveGameDirectory(path))
            {
                Shell.Toast(L.T("Settings.Game.Directory.SaveFailed", "保存游戏目录失败"));
                return;
            }

            RefreshGameDirectory();
            RefreshJavaList();
            Shell.Toast(L.T("Settings.Toast.GameDirChanged", "已更改游戏目录：{0}", path));
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Settings.Game.Directory.Invalid", "目录不可用：{0}", ex.Message));
        }
    }

    private void OnOpenGameDirClick(object? sender, RoutedEventArgs e)
    {
        var path = CurrentGameDirectory();

        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            RefreshGameDirectory();
            Shell.Toast(L.T("Settings.Toast.GameDirOpening", "已打开游戏目录"));
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Settings.Game.Directory.OpenFailed", "打开目录失败：{0}", ex.Message));
        }
    }

    private void OnResetGameDirClick(object? sender, RoutedEventArgs e)
    {
        YamlRead.ClearGameDirectory();
        RefreshGameDirectory();
        RefreshJavaList();
        Shell.Toast(L.T("Settings.Toast.GameDirReset", "已恢复默认游戏目录"));
    }

    private void OnVersionIsolationChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        bool enabled = VersionIsolationToggle.IsChecked == true;
        if (!YamlRead.SaveDefaultVersionIsolation(enabled))
        {
            Shell.Toast(L.T("Settings.Toast.SaveFailed", "保存失败"));
            return;
        }

        SaveAndToast(enabled
            ? L.T("Settings.Toast.IsolationOn", "已保存：版本隔离开启，各实例的 mods 与存档相互独立")
            : L.T("Settings.Toast.IsolationOff", "已保存：版本隔离关闭，所有实例共享 mods 与存档"));
    }

    // ==================== 游戏：Java ====================

    private void RefreshJavaList()
    {
        var saved = YamlRead.GetJavaPaths();
        var rows = new List<SettingsJavaRow>(saved.Count);

        for (var i = 0; i < saved.Count; i++)
        {
            var item = saved[i];
            if (string.IsNullOrWhiteSpace(item.Path)) continue;

            rows.Add(new SettingsJavaRow
            {
                Path = item.Path,
                Version = string.Equals(item.Version, "unknown", StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : item.Version,
                Detail = i == 0
                    ? L.T("Settings.Game.Java.DefaultDetail", "默认 Java · 启动时优先使用")
                    : L.T("Settings.Game.Java.SavedDetail", "已保存"),
                IsDefault = i == 0,
            });
        }

        JavaList.ItemsSource = rows;
        JavaStatusText.Text = rows.Count == 0
            ? L.T("Settings.Game.Java.Empty", "未配置 Java：点「自动检索」扫描本机，或「添加」选择 java.exe")
            : L.T("Settings.Game.Java.Detected", "已保存 {0} 个 Java 运行时", rows.Count);
    }

    private static string JavaVersionText(JavaRuntime runtime)
    {
        if (runtime.MajorVersion is { } major) return major.ToString(CultureInfo.InvariantCulture);
        return runtime.VersionString.Length > 0 ? runtime.VersionString : "unknown";
    }

    private async void OnAutoDetectJavaClick(object? sender, RoutedEventArgs e)
    {
        if (JavaDetectButton is not null) JavaDetectButton.IsEnabled = false;
        JavaStatusText.Text = L.T("Settings.Game.Java.Detecting", "正在检索本机 Java 运行时…");

        try
        {
            var runtimeDirectory = Path.Combine(CurrentGameDirectory(), "runtime");
            IReadOnlyList<JavaRuntime> runtimes =
                await Task.Run(() => JavaPathSearch.FindAllJavaAsync(runtimeDirectory));

            var hadPrimary = YamlRead.GetJavaPaths().Count > 0;
            foreach (var runtime in runtimes)
            {
                if (string.IsNullOrWhiteSpace(runtime.JavaPath)) continue;
                YamlRead.AddJava(runtime.JavaPath, JavaVersionText(runtime));
            }

            if (!hadPrimary)
            {
                var first = runtimes.FirstOrDefault(runtime => !string.IsNullOrWhiteSpace(runtime.JavaPath));
                if (first is not null)
                {
                    YamlRead.SetPrimaryJava(first.JavaPath);
                }
            }

            RefreshJavaList();
            Shell.Toast(L.T("Settings.Toast.JavaDetected", "自动检索完成：发现 {0} 个 Java 运行时", runtimes.Count));
        }
        catch (Exception ex)
        {
            JavaStatusText.Text = L.T("Settings.Game.Java.DetectFailed", "Java 检索失败：{0}", ex.Message);
            Shell.Toast(L.T("Settings.Game.Java.DetectFailed", "Java 检索失败：{0}", ex.Message));
        }
        finally
        {
            if (JavaDetectButton is not null) JavaDetectButton.IsEnabled = true;
        }
    }

    private async void OnAddJavaClick(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L.T("Settings.Game.Java.PickTitle", "选择 java 可执行文件"),
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(L.T("Settings.Game.Java.PickFileType", "Java 可执行文件"))
                    {
                        Patterns = new[] { "java.exe", "java" },
                    },
                },
            });

            if (files.Count == 0) return;

            var path = files[0].Path.LocalPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                Shell.Toast(L.T("Settings.Game.Java.PickInvalid", "所选文件不是可用的 java 可执行文件"));
                return;
            }

            var runtime = await Task.Run(() => JavaPathSearch.Detect(path));
            var version = runtime is null ? "unknown" : JavaVersionText(runtime);
            var hadPrimary = YamlRead.GetJavaPaths().Count > 0;

            if (!YamlRead.AddJava(path, version))
            {
                Shell.Toast(L.T("Settings.Game.Java.SaveFailed", "保存 Java 运行时失败"));
                return;
            }

            if (!hadPrimary)
            {
                YamlRead.SetPrimaryJava(path);
            }

            RefreshJavaList();
            Shell.Toast(L.T("Settings.Toast.JavaAdded", "已添加 Java 运行时：{0}", path));
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Settings.Game.Java.DetectFailed", "Java 检索失败：{0}", ex.Message));
        }
    }

    private void OnSetDefaultJavaClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;

        if (!YamlRead.SetPrimaryJava(path))
        {
            Shell.Toast(L.T("Settings.Game.Java.SaveFailed", "保存 Java 运行时失败"));
            return;
        }

        RefreshJavaList();
        Shell.Toast(L.T("Settings.Toast.JavaDefaultSet", "已设为默认 Java 运行时"));
    }

    private void OnRemoveJavaClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;

        if (!YamlRead.RemoveJava(path))
        {
            Shell.Toast(L.T("Settings.Game.Java.RemoveFailed", "该 Java 不在已保存列表中"));
            return;
        }

        RefreshJavaList();
        Shell.Toast(L.T("Settings.Toast.JavaRemoved", "已移除 Java 运行时"));
    }

    // ==================== 游戏：内存 ====================

    private static int ReadMaximumMemoryMb()
    {
        try
        {
            return Math.Clamp(YamlRead.LoadGlobalLaunchSettings().MaximumMemoryMb, 2048, 16384);
        }
        catch
        {
            return 8192;
        }
    }

    private void OnMaxMemoryChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        var mb = (int)Math.Round(e.NewValue);
        MaxMemoryValue.Text = $"{mb} MB";
        if (_loading) return;

        try
        {
            var launch = YamlRead.LoadGlobalLaunchSettings();
            launch.MaximumMemoryMb = mb;
            if (!YamlRead.SaveGlobalLaunchSettings(launch))
            {
                Shell.Toast(L.T("Settings.Game.Memory.SaveFailed", "保存最大内存失败"));
                return;
            }
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Settings.Game.Memory.SaveFailed", "保存最大内存失败：{0}", ex.Message));
            return;
        }

        Shell.Toast(L.T("Settings.Toast.MemoryMax", "已保存：最大内存 {0} MB", mb));
    }

    // ==================== 通用：窗口 / 单实例 / S 模式 / 语言 ====================

    private void OnCloseActionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        var index = CloseActionCombo.SelectedIndex;
        var action = index >= 0 && index < CloseActions.Length ? CloseActions[index] : "exit";
        LocalSettings.Current.CloseAction = action;
        LocalSettings.Save();

        var label = action switch
        {
            "tray" => L.T("Settings.General.Window.CloseActionMinimize", "最小化到系统托盘"),
            "ask" => L.T("Settings.General.Window.CloseActionAsk", "每次询问"),
            _ => L.T("Settings.General.Window.CloseActionExit", "退出启动器"),
        };

        Shell.Toast(L.T("Settings.Toast.CloseAction", "已保存：关闭窗口时「{0}」", label));
    }

    private void OnSingleInstanceChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        LocalSettings.Current.SingleInstance = SingleInstanceToggle.IsChecked == true;
        SaveAndToast(SingleInstanceToggle.IsChecked == true
            ? L.T("Settings.Toast.SingleInstanceOn", "已保存：单实例运行（重启后生效）")
            : L.T("Settings.Toast.SingleInstanceOff", "已保存：允许多实例运行（重启后生效）"));
    }

    private void OnLanguageCardClick(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not Button { Tag: string code }) return;

        // SetLanguage 在发通知之后才写 LocalSettings，先写一次，重建出来的页面选中项才正确
        LocalSettings.Current.Language = code;
        L.SetLanguage(code);
        Shell.Toast(L.T("Settings.General.Language.Changed", "已切换界面语言"));
    }

    // ==================== 界面：主题 / 外观 ====================

    private void OnIconPaletteChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        var colorful = IconPaletteCombo.SelectedIndex != 0;
        LocalSettings.Current.IconPalette = colorful ? "colorful" : "theme";
        LocalSettings.Save();

        IconParkIcon.UseColorfulPalette = colorful;
        Shell.Toast(colorful
            ? L.T("Settings.Toast.IconPaletteColorful", "图标已切换为多彩配色")
            : L.T("Settings.Toast.IconPaletteTheme", "图标已切换为跟随主题配色"));
    }

    private void OnPremiumVisualsChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var enabled = PremiumVisualsToggle.IsChecked == true;
        LocalSettings.Current.PremiumVisuals = enabled;
        LocalSettings.Save();

        GlassEffects.SetPremiumVisuals(enabled);
        Shell.Toast(enabled
            ? L.T("Settings.Toast.PremiumVisualsOn", "已开启卓越视效")
            : L.T("Settings.Toast.PremiumVisualsOff", "已关闭卓越视效"));
    }

    private void OnThemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        var theme = ThemeCombo.SelectedIndex switch
        {
            1 => "light",
            2 => "system",
            _ => "dark",
        };

        LocalSettings.Current.Theme = theme;
        LocalSettings.Save();
        ThemeManager.Apply(theme, LocalSettings.Current.AccentColor);

        var label = theme switch
        {
            "light" => L.T("Settings.Appearance.Theme.Light", "浅色"),
            "system" => L.T("Settings.Appearance.Theme.System", "跟随系统"),
            _ => L.T("Settings.Appearance.Theme.Dark", "深色"),
        };

        Shell.Toast(L.T("Settings.Toast.Theme", "已保存：UI 主题切换为「{0}」", label));
    }

    // ==================== 主题色色板 / 自定义颜色 ====================

    private void BuildAccentSwatches()
    {
        AccentSwatchHost.Children.Clear();
        _accentSwatches.Clear();

        foreach (var option in ThemeManager.AccentOptions)
        {
            var swatch = new Border
            {
                Background = new SolidColorBrush(Color.Parse(option.Hex)),
                Tag = option.Key,
            };
            swatch.Classes.Add("accent-swatch");
            ToolTip.SetTip(swatch, AccentLabel(option.Key));
            swatch.PointerPressed += OnAccentSwatchPressed;
            AccentSwatchHost.Children.Add(swatch);
            _accentSwatches.Add(swatch);
        }
    }

    private void UpdateAccentSelection()
    {
        var current = ThemeManager.NormalizeAccentKey(LocalSettings.Current.AccentColor);
        foreach (var swatch in _accentSwatches)
        {
            swatch.Classes.Set("selected",
                string.Equals(swatch.Tag as string, current, StringComparison.Ordinal));
        }

        var custom = ThemeManager.IsCustomAccent(current);
        CustomAccentButton.Classes.Set("selected", custom);
        CustomAccentDot.Background = new SolidColorBrush(custom
            ? ThemeManager.AccentColor(current)
            : ThemeTokens.Color("NekoDimBrush"));
        ToolTip.SetTip(CustomAccentButton, custom
            ? L.T("Settings.Appearance.Accent.CustomCurrent", "当前自定义色：{0}", current.ToUpperInvariant())
            : L.T("Settings.Appearance.Accent.Custom", "自定义"));
    }

    private void OnAccentSwatchPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_loading) return;
        if (sender is not Border { Tag: string key }) return;

        ApplyAccent(key);
    }

    private void OnCustomAccentClick(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        ShowCustomAccentDialog();
    }

    private void ApplyAccent(string key)
    {
        var normalized = ThemeManager.NormalizeAccentKey(key);

        // 手动选色即视为退出「跟随壁纸」，否则下次进入页面又会被壁纸主色覆盖
        if (AccentFollowToggle.IsChecked == true)
        {
            _loading = true;
            AccentFollowToggle.IsChecked = false;
            _loading = false;
            LocalSettings.Current.AccentFollowBackground = false;
        }

        LocalSettings.Current.AccentColor = normalized;
        LocalSettings.Save();
        ThemeManager.Apply(LocalSettings.Current.Theme, normalized);
        UpdateAccentSelection();

        Shell.Toast(ThemeManager.IsCustomAccent(normalized)
            ? L.T("Settings.Toast.AccentCustom", "已保存：自定义主题色 {0}", normalized.ToUpperInvariant())
            : L.T("Settings.Toast.Accent", "已保存：主题色切换为「{0}」", AccentLabel(normalized)));
    }

    private void ShowCustomAccentDialog()
    {
        var start = ThemeManager.AccentColor(LocalSettings.Current.AccentColor);

        var wheel = new ColorWheel
        {
            Width = 340,
            Height = 300,
            Color = start,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var hexBox = new TextBox
        {
            Text = ToHex(start),
            Width = 128,
            Height = 34,
            MaxLength = 9,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
        };
        var redBox = ColorChannelBox(start.R);
        var greenBox = ColorChannelBox(start.G);
        var blueBox = ColorChannelBox(start.B);

        var syncingFields = false;
        void SyncFields(Color color)
        {
            if (syncingFields) return;

            syncingFields = true;
            try
            {
                hexBox.Text = ToHex(color);
                redBox.Text = color.R.ToString();
                greenBox.Text = color.G.ToString();
                blueBox.Text = color.B.ToString();
            }
            finally
            {
                syncingFields = false;
            }
        }

        wheel.ColorChanged += SyncFields;

        void CommitChannelFields()
        {
            if (syncingFields) return;

            var color = Color.FromArgb(255,
                ParseChannel(redBox.Text) ?? wheel.Color.R,
                ParseChannel(greenBox.Text) ?? wheel.Color.G,
                ParseChannel(blueBox.Text) ?? wheel.Color.B);

            syncingFields = true;
            try
            {
                wheel.Color = color;
                hexBox.Text = ToHex(color);
            }
            finally
            {
                syncingFields = false;
            }
        }

        hexBox.TextChanged += (_, _) =>
        {
            if (syncingFields) return;
            if (!Color.TryParse((hexBox.Text ?? string.Empty).Trim(), out var parsed)) return;

            var color = Color.FromArgb(255, parsed.R, parsed.G, parsed.B);
            syncingFields = true;
            try
            {
                wheel.Color = color;
                redBox.Text = color.R.ToString();
                greenBox.Text = color.G.ToString();
                blueBox.Text = color.B.ToString();
            }
            finally
            {
                syncingFields = false;
            }
        };

        foreach (var box in new[] { redBox, greenBox, blueBox })
        {
            box.LostFocus += (_, _) => CommitChannelFields();
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;

                CommitChannelFields();
                e.Handled = true;
            };
        }

        var recentPanel = new WrapPanel { Orientation = Orientation.Horizontal };
        void RefreshRecent()
        {
            recentPanel.Children.Clear();
            var any = false;
            foreach (var hex in LocalSettings.Current.RecentAccentColors)
            {
                if (!Color.TryParse(hex, out var color)) continue;

                any = true;
                recentPanel.Children.Add(ColorDot(color, 26, () => wheel.Color = color));
            }

            if (!any)
            {
                recentPanel.Children.Add(new TextBlock
                {
                    Text = L.T("Settings.Appearance.Accent.NoRecent", "还没有用过自定义颜色"),
                    Classes = { "caption" },
                });
            }
        }
        RefreshRecent();

        var presetRows = new StackPanel { Spacing = 9 };
        foreach (var option in ThemeManager.AccentOptions)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var shade in PaletteShades(Color.Parse(option.Hex)))
            {
                row.Children.Add(ColorDot(shade, 22, () => wheel.Color = shade));
            }

            presetRows.Children.Add(row);
        }

        var presets = new Expander
        {
            Header = L.T("Settings.Appearance.Accent.Presets", "预设色板"),
            IsExpanded = true,
            Content = presetRows,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var leftCard = new Border
        {
            Background = ThemeTokens.Brush("NekoCardSoftBrush"),
            BorderBrush = ThemeTokens.Brush("NekoBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(18),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    wheel,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 12,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Children =
                        {
                            FieldBlock("HEX", hexBox),
                            FieldBlock("R", redBox),
                            FieldBlock("G", greenBox),
                            FieldBlock("B", blueBox),
                        },
                    },
                },
            },
        };

        var rightCard = new Border
        {
            Background = ThemeTokens.Brush("NekoCardSoftBrush"),
            BorderBrush = ThemeTokens.Brush("NekoBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(18),
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = L.T("Settings.Appearance.Accent.Recent", "最近使用"),
                        FontSize = 13,
                        FontWeight = FontWeight.SemiBold,
                    },
                    recentPanel,
                    presets,
                },
            },
        };

        var body = new Grid
        {
            Width = 720,
            ColumnDefinitions = new ColumnDefinitions("380,*"),
            ColumnSpacing = 16,
        };
        body.Children.Add(leftCard);
        Grid.SetColumn(rightCard, 1);
        body.Children.Add(rightCard);

        var apply = new Button
        {
            Content = L.T("Common.Apply", "应用"),
            Classes = { "primary" },
            Height = 34,
            Padding = new Thickness(20, 0, 20, 0),
        };
        var cancel = new Button
        {
            Content = L.T("Common.Cancel", "取消"),
            Classes = { "ghost" },
            Height = 34,
            Padding = new Thickness(20, 0, 20, 0),
        };

        apply.Click += (_, _) =>
        {
            var color = wheel.Color;
            var hex = ToHex(Color.FromArgb(255, color.R, color.G, color.B));
            RememberRecentColor(hex);
            CloseActiveDialog(apply);
            ApplyAccent(hex);
        };
        cancel.Click += (_, _) => CloseActiveDialog(cancel);

        var content = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = L.T("Settings.Appearance.Accent.PickerHint",
                        "拖动色相环、圆盘与明度条，或直接输入 HEX / RGB。"),
                    Classes = { "caption" },
                    TextWrapping = TextWrapping.Wrap,
                },
                body,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, apply },
                },
            },
        };

        Shell.Dialog(content, L.T("Settings.Appearance.Accent.PickerTitle", "自定义主题色"));
    }

    private static TextBox ColorChannelBox(byte value) => new()
    {
        Text = value.ToString(),
        Width = 58,
        Height = 34,
        MaxLength = 3,
        TextAlignment = TextAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static byte? ParseChannel(string? text) =>
        byte.TryParse((text ?? string.Empty).Trim(), out var value) ? value : null;

    private static Control FieldBlock(string label, Control box)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Classes = { "caption" },
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        stack.Children.Add(box);
        return stack;
    }

    private static Border ColorDot(Color color, double size, Action onPick)
    {
        var dot = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = new SolidColorBrush(color),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeTokens.Brush("NekoBorderBrush"),
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(0, 0, 4, 4),
        };
        dot.PointerPressed += (_, _) => onPick();
        return dot;
    }

    /// <summary>把一个预设色展开成同色系的 5 档深浅（浅 → 深）。</summary>
    private static IEnumerable<Color> PaletteShades(Color baseColor)
    {
        var (hue, _, _) = ColorWheel.ToHsv(baseColor);
        double[] values = [0.97, 0.86, 0.72, 0.58, 0.44];
        double[] sats = [0.42, 0.62, 0.80, 0.90, 0.98];

        for (var i = 0; i < values.Length; i++)
        {
            yield return ColorWheel.FromHsv(hue, sats[i], values[i]);
        }
    }

    private static void RememberRecentColor(string hex)
    {
        var recent = LocalSettings.Current.RecentAccentColors;
        recent.RemoveAll(item => string.Equals(item, hex, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, hex);
        while (recent.Count > 8) recent.RemoveAt(recent.Count - 1);
        LocalSettings.Save();
    }

    /// <summary>弹窗内容里的按钮：取它所在的 SukiDialogHost 关掉当前弹窗。</summary>
    private static void CloseActiveDialog(object? source)
    {
        if (source is Visual visual)
        {
            visual.FindAncestorOfType<SukiDialogHost>(true)?.Manager?.DismissDialog();
        }
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private async void OnAccentFollowChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var enabled = AccentFollowToggle.IsChecked == true;
        LocalSettings.Current.AccentFollowBackground = enabled;
        LocalSettings.Save();

        if (!enabled)
        {
            ThemeManager.Apply(LocalSettings.Current.Theme, LocalSettings.Current.AccentColor);
            Shell.Toast(L.T("Settings.Toast.AccentFollowOff", "已恢复手动主题色"));
            return;
        }

        await ApplyAccentFromWallpaperAsync(silent: false);
    }

    /// <summary>从当前壁纸预览图取平均色，映射到最接近的主题色后应用。</summary>
    public static async Task<bool> ApplyAccentFromWallpaperAsync(bool silent)
    {
        var preview = WallpaperService.Instance.Settings.Images
            .FirstOrDefault(WallpaperService.IsImageFile) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(preview) || !File.Exists(preview))
        {
            if (!silent)
            {
                Shell.Toast(L.T("Settings.Toast.AccentFollowFailed", "未选择壁纸或预览图不可用，无法取色"));
            }

            return false;
        }

        var average = await Task.Run(() => AverageColor(preview));
        if (average is null)
        {
            if (!silent)
            {
                Shell.Toast(L.T("Settings.Toast.AccentFollowFailed", "未选择壁纸或预览图不可用，无法取色"));
            }

            return false;
        }

        var best = LocalSettings.Current.AccentColor;
        var bestDistance = double.MaxValue;
        foreach (var option in ThemeManager.AccentOptions)
        {
            // 借 ThemeManager 应用一次，再从令牌读回该主题色的真实颜色（避免在视图里复制一份调色板）
            ThemeManager.Apply(LocalSettings.Current.Theme, option.Key);
            var distance = ColorDistance(ThemeTokens.Color("NekoAccentBrush"), average.Value);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = option.Key;
        }

        ThemeManager.Apply(LocalSettings.Current.Theme, best);
        if (!silent)
        {
            Shell.Toast(L.T("Settings.Toast.AccentFollowApplied", "已按壁纸主色切换主题色：{0}", AccentLabel(best)));
        }

        return true;
    }

    private static Color? AverageColor(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using Bitmap bitmap = Bitmap.DecodeToWidth(stream, 24);
            var width = bitmap.PixelSize.Width;
            var height = bitmap.PixelSize.Height;
            if (width <= 0 || height <= 0) return null;

            var stride = width * 4;
            var buffer = new byte[stride * height];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(),
                    buffer.Length, stride);
            }
            finally
            {
                handle.Free();
            }

            long red = 0, green = 0, blue = 0, count = 0;
            for (var i = 0; i + 3 < buffer.Length; i += 4)
            {
                if (buffer[i + 3] < 16) continue;
                blue += buffer[i];
                green += buffer[i + 1];
                red += buffer[i + 2];
                count++;
            }

            if (count == 0) return null;

            return Color.FromRgb((byte)(red / count), (byte)(green / count), (byte)(blue / count));
        }
        catch
        {
            return null;
        }
    }

    private static double ColorDistance(Color a, Color b)
    {
        double dr = a.R - b.R;
        double dg = a.G - b.G;
        double db = a.B - b.B;
        return dr * dr + dg * dg * 1.5 + db * db * 0.8;
    }

    private void OnPanelBlurChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        LocalSettings.Current.PanelBlur = PanelBlurToggle.IsChecked == true;
        LocalSettings.Save();
        GlassEffects.SetEnabled(LocalSettings.Current.PanelBlur);
        RequestAppearanceApply();
        Shell.Toast(PanelBlurToggle.IsChecked == true
            ? L.T("Settings.Toast.BlurOn", "已开启面板毛玻璃")
            : L.T("Settings.Toast.BlurOff", "已关闭面板毛玻璃"));
    }

    private void OnWindowOpacityChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        WindowOpacityValue.Text = $"{e.NewValue:0}%";
        if (_loading) return;

        LocalSettings.Current.WindowOpacity = (int)Math.Round(e.NewValue);
        LocalSettings.Save();
        RequestAppearanceApply();
        Shell.Toast(L.T("Settings.Toast.WindowOpacity", "已保存：窗口不透明度 {0}%",
            (int)Math.Round(e.NewValue)));
    }

    private void OnBackgroundOpacityChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        BackgroundOpacityValue.Text = $"{e.NewValue:0}%";
        if (_loading) return;

        LocalSettings.Current.BackgroundOpacity = (int)Math.Round(e.NewValue);
        LocalSettings.Save();
        RequestAppearanceApply();
        Shell.Toast(L.T("Settings.Toast.BackgroundOpacity", "已保存：背景不透明度 {0}%",
            (int)Math.Round(e.NewValue)));
    }

    private void OnSquareCornersChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        LocalSettings.Current.SquareCorners = SquareCornerToggle.IsChecked == true;
        LocalSettings.Save();
        RequestAppearanceApply();
        Shell.Toast(SquareCornerToggle.IsChecked == true
            ? L.T("Settings.Toast.SquareOn", "已开启直角模式")
            : L.T("Settings.Toast.SquareOff", "已关闭直角模式"));
    }

    private void OnStartBarPlacementChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        LocalSettings.Current.StartBarPlacement = StartBarPlacementCombo.SelectedIndex switch
        {
            1 => "left",
            2 => "right",
            3 => "top",
            _ => "bottom",
        };

        var label = StartBarPlacementCombo.SelectedIndex switch
        {
            1 => L.T("Settings.Appearance.StartBar.Left", "左侧"),
            2 => L.T("Settings.Appearance.StartBar.Right", "右侧"),
            3 => L.T("Settings.Appearance.StartBar.Top", "顶部"),
            _ => L.T("Settings.Appearance.StartBar.Bottom", "底部"),
        };

        SaveAndToast(L.T("Settings.Toast.StartBarPlacement", "已保存：开始栏位置「{0}」，重启后生效", label));
    }

    private void OnStartBarAutoHideChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        LocalSettings.Current.StartBarAutoHide = StartBarAutoHideToggle.IsChecked == true;
        SaveAndToast(StartBarAutoHideToggle.IsChecked == true
            ? L.T("Settings.Toast.StartBarAutoHideOn", "已保存：开始栏自动隐藏")
            : L.T("Settings.Toast.StartBarAutoHideOff", "已保存：开始栏不再自动隐藏"));
    }

    // ==================== 下载与网络 ====================

    private static int SourceIndex(string? key) => key?.Trim().ToLowerInvariant() switch
    {
        DownloadSourceSettings.Official => 1,
        DownloadSourceSettings.Custom => 2,
        _ => 0,
    };

    private static int FallbackIndex(string? key) => key?.Trim().ToLowerInvariant() switch
    {
        DownloadSourceSettings.Bmcl => 1,
        DownloadSourceSettings.Official => 2,
        DownloadSourceSettings.Custom => 3,
        _ => 0,
    };

    private static string SourceKeyAt(int index) => index switch
    {
        1 => DownloadSourceSettings.Official,
        2 => DownloadSourceSettings.Custom,
        _ => DownloadSourceSettings.Bmcl,
    };

    private static string FallbackKeyAt(int index) => index switch
    {
        1 => DownloadSourceSettings.Bmcl,
        2 => DownloadSourceSettings.Official,
        3 => DownloadSourceSettings.Custom,
        _ => DownloadSourceSettings.None,
    };

    private void UpdateCustomSourceRowVisibility()
    {
        CustomSourceRow.IsVisible = DownloadSourceSettings.IsCustom(LocalSettings.Current.DownloadSource) ||
                                    DownloadSourceSettings.IsCustom(LocalSettings.Current.DownloadFallbackSource);
    }

    private void OnDownloadSourceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        string key = SourceKeyAt(DownloadSourceCombo.SelectedIndex);
        LocalSettings.Current.DownloadSource = key;
        LocalSettings.Save();
        UpdateCustomSourceRowVisibility();
        DownloadSourceSettings.Apply(LocalSettings.Current);

        string label = key switch
        {
            DownloadSourceSettings.Official => L.T("Settings.Network.Download.SourceOfficial", "官方源（Mojang）"),
            DownloadSourceSettings.Custom => L.T("Settings.Network.Download.SourceCustom", "自定义源"),
            _ => L.T("Settings.Network.Download.SourceMirror", "镜像源（BMCLAPI）"),
        };

        Shell.Toast(L.T("Settings.Toast.DownloadSource", "已保存：下载源「{0}」", label));

        if (DownloadSourceSettings.IsCustom(key) && DownloadSourceSettings.CreateCustom(LocalSettings.Current) is null)
        {
            Shell.Toast(L.T("Settings.Toast.DownloadSourceInvalid", "自定义源地址无效，需要 HTTPS 地址"));
        }
    }

    private void OnDownloadFallbackChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        string key = FallbackKeyAt(DownloadFallbackCombo.SelectedIndex);
        LocalSettings.Current.DownloadFallbackSource = key;
        LocalSettings.Save();
        UpdateCustomSourceRowVisibility();
        DownloadSourceSettings.Apply(LocalSettings.Current);
        Shell.Toast(L.T("Settings.Toast.DownloadFallback", "已更新回退下载源"));
    }

    private void OnCustomSourceChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        LocalSettings.Current.CustomDownloadSourceName = CustomSourceNameBox.Text?.Trim() ?? string.Empty;
        LocalSettings.Current.CustomDownloadSourceUrl = CustomSourceUrlBox.Text?.Trim() ?? string.Empty;
        LocalSettings.Save();
        DownloadSourceSettings.Apply(LocalSettings.Current);

        if (DownloadSourceSettings.CreateCustom(LocalSettings.Current) is null &&
            (DownloadSourceSettings.IsCustom(LocalSettings.Current.DownloadSource) ||
             DownloadSourceSettings.IsCustom(LocalSettings.Current.DownloadFallbackSource)))
        {
            Shell.Toast(L.T("Settings.Toast.DownloadSourceInvalid", "自定义源地址无效，需要 HTTPS 地址"));
        }
    }

    private static string FormatLatency(int milliseconds) =>
        milliseconds > 0
            ? $"{milliseconds} ms"
            : L.T("Settings.Network.Download.SpeedTestFailed", "失败");

    private async void OnTestDownloadSourcesClick(object? sender, RoutedEventArgs e)
    {
        SourceTestButton.IsEnabled = false;
        SourceLatencyText.Text = L.T("Settings.Network.Download.SpeedTesting", "测速中…");

        try
        {
            var results = await DownloadSourceProvider.MeasureLatenciesAsync();
            List<string> parts = results
                .Select(result => $"{result.Name}  {FormatLatency(result.LatencyMs)}")
                .ToList();

            DownloadSource? custom = DownloadSourceSettings.CreateCustom(LocalSettings.Current);
            if (custom is not null)
            {
                int latency = await DownloadSourceProvider.MeasureLatencyAsync(custom);
                parts.Add($"{custom.Name}  {FormatLatency(latency)}");
            }

            SourceLatencyText.Text = string.Join("   ·   ", parts);
        }
        catch (Exception error)
        {
            SourceLatencyText.Text = error.Message;
        }
        finally
        {
            SourceTestButton.IsEnabled = true;
        }
    }

    private async void OnApplyFastestSourceClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            IReadOnlyList<SourceLatency> results = await DownloadSourceProvider.MeasureLatenciesAsync();
            SourceLatency? fastest = results
                .Where(result => result.LatencyMs > 0)
                .OrderBy(result => result.LatencyMs)
                .FirstOrDefault();

            if (fastest is null)
            {
                Shell.Toast(L.T("Settings.Toast.DownloadSourceFastestFailed", "测速失败，未切换下载源"));
                return;
            }

            string key = string.Equals(fastest.Name, DownloadSources.Official.Name, StringComparison.Ordinal)
                ? DownloadSourceSettings.Official
                : DownloadSourceSettings.Bmcl;

            LocalSettings.Current.DownloadSource = key;
            LocalSettings.Save();
            DownloadSourceSettings.Apply(LocalSettings.Current);

            _loading = true;
            DownloadSourceCombo.SelectedIndex = SourceIndex(key);
            _loading = false;
            UpdateCustomSourceRowVisibility();

            Shell.Toast(L.T("Settings.Toast.DownloadSourceFastest", "已切换到最快源「{0}」（{1} ms）",
                fastest.Name, fastest.LatencyMs));
        }
        catch (Exception error)
        {
            Shell.Toast(error.Message);
        }
    }

    private void OnParallelChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        var parallel = (int)Math.Round(e.NewValue);
        ParallelValue.Text = L.T("Settings.Network.Download.ParallelValue", "{0} 线程", parallel);
        if (_loading) return;

        LocalSettings.Current.ParallelDownloads = parallel;
        LocalSettings.Save();
        DownloadSettings.ParallelDownloads = parallel;
        Shell.Toast(L.T("Settings.Toast.Parallel", "已保存：并行下载数 {0}", parallel));
    }

    private void OnCurseForgeKeyLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var key = (CurseForgeKeyBox.Text ?? string.Empty).Trim();
        LocalSettings.Current.CurseForgeApiKey = key;
        LocalSettings.Save();
        Shell.Toast(key.Length == 0
            ? L.T("Settings.Toast.CurseForgeEmpty", "已保存：未配置 CurseForge API Key")
            : L.T("Settings.Toast.CurseForgeSet", "已保存：CurseForge API Key（仅保存在本机）"));
    }

    private void OnAutoDependenciesChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        LocalSettings.Current.AutoInstallDependencies = AutoDependenciesToggle.IsChecked == true;
        SaveAndToast(AutoDependenciesToggle.IsChecked == true
            ? L.T("Settings.Toast.AutoDepsOn", "已保存：安装模组时自动补齐前置")
            : L.T("Settings.Toast.AutoDepsOff", "已保存：不再自动安装前置模组"));
    }

    // ==================== 关于 ====================

    private static string LauncherVersion()
    {
        var assembly = typeof(SettingsView).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational)) return informational;
        return assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    }

    private static string LauncherFileVersion() =>
        typeof(SettingsView).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private static string ComponentVersion(Type type)
    {
        var version = type.Assembly.GetName().Version;
        return version is null ? "?" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static string UpdateSummary()
    {
        var version = LauncherVersion();
        return L.T("Settings.About.UpdateLatest", "当前已是最新版本 {0}", version);
    }

    private void RefreshAboutVersions()
    {
        var version = LauncherVersion();
        AboutVersionText.Text = L.T("Settings.About.VersionLine", "版本 {0}", version);
        AboutUpdateHint.Text = L.T("Settings.About.UpdateHint", "当前版本 {0}（文件版本 {1}）", version,
            LauncherFileVersion());
        _loading = true;
        AutoCheckUpdateToggle.IsChecked = LocalSettings.Current.AutoCheckUpdates;
        _loading = false;
    }

    private bool _checkingUpdate;
    private GitHubUpdater.ReleaseInfo? _pendingUpdateRelease;

    private void OnAutoCheckUpdateChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        LocalSettings.Current.AutoCheckUpdates = AutoCheckUpdateToggle.IsChecked == true;
        LocalSettings.Save();
    }

    private async void OnCheckUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (_checkingUpdate) return;
        _checkingUpdate = true;
        AboutUpdateButton.IsEnabled = false;
        AboutUpdateHint.Text = L.T("Settings.About.Checking", "正在检查更新…");
        try
        {
            GitHubUpdater.ReleaseInfo release = await GitHubUpdater.GetLatestAsync().ConfigureAwait(true);
            if (!GitHubUpdater.IsNewer(release.Tag))
            {
                string current = GitHubUpdater.CurrentVersion().ToString(3);
                AboutUpdateHint.Text = L.T("Settings.About.UpdateLatest", "当前已是最新版本 {0}", current);
                Shell.Toast(L.T("Settings.About.UpdateLatest", "当前已是最新版本 {0}", current));
                return;
            }

            _pendingUpdateRelease = release;
            ShowUpdateFoundDialog(release);
        }
        catch (Exception error)
        {
            AboutUpdateHint.Text = L.T("Settings.About.UpdateHint", "当前版本 {0}（文件版本 {1}）",
                LauncherVersion(), LauncherFileVersion());
            Shell.Toast(L.T("Settings.About.UpdateFailed", "检查更新失败：{0}", error.Message));
        }
        finally
        {
            _checkingUpdate = false;
            AboutUpdateButton.IsEnabled = true;
        }
    }

    private void ShowUpdateFoundDialog(GitHubUpdater.ReleaseInfo release)
    {
        string notes = string.IsNullOrWhiteSpace(release.Notes)
            ? L.T("Settings.About.Update.NotesEmpty", "该版本没有附更新说明。")
            : release.Notes.Length > 600 ? release.Notes[..600] + "…" : release.Notes;

        var content = new StackPanel { Spacing = 12, Width = 420 };
        content.Children.Add(new TextBlock
        {
            Text = L.T("Settings.About.Update.CurrentLine", "当前版本 {0} → 新版本 {1}",
                GitHubUpdater.CurrentVersion().ToString(3), release.Tag.TrimStart('v', 'V')),
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new ScrollViewer
        {
            MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock { Text = notes, FontSize = 12.5, TextWrapping = TextWrapping.Wrap },
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancel = new Button { Content = L.T("Common.Cancel", "取消"), Classes = { "ghost" }, Height = 30 };
        cancel.Click += (_, _) => DismissDialog(cancel);
        var install = new Button
        {
            Content = release.AssetUrl is null
                ? L.T("Settings.About.Update.OpenRelease", "打开发布页")
                : L.T("Settings.About.Update.DownloadNow", "立即更新"),
            Classes = { "primary" },
            Height = 30,
        };
        install.Click += (_, _) =>
        {
            DismissDialog(install);
            if (release.AssetUrl is null)
            {
                OpenLinkBrowser.OpenLink($"https://github.com/{GitHubUpdater.Repo}/releases/latest");
                return;
            }

            StartUpdateDownload(release);
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(install);
        content.Children.Add(buttons);
        Shell.Dialog(content, L.T("Settings.About.UpdateFound", "发现新版本 {0}", release.Tag.TrimStart('v', 'V')));
    }

    private async void StartUpdateDownload(GitHubUpdater.ReleaseInfo release)
    {
        AboutUpdateButton.IsEnabled = false;
        var progress = new Progress<double>(percent =>
            AboutUpdateHint.Text = L.T("Settings.About.Update.Downloading", "下载中 {0:0}%", percent));
        try
        {
            string path = await GitHubUpdater.DownloadAsync(release, progress).ConfigureAwait(true);
            AboutUpdateHint.Text = L.T("Settings.About.Update.Downloaded", "更新包已下载");
            ShowInstallConfirmDialog(path);
        }
        catch (Exception error)
        {
            AboutUpdateHint.Text = L.T("Settings.About.UpdateHint", "当前版本 {0}（文件版本 {1}）",
                LauncherVersion(), LauncherFileVersion());
            Shell.Toast(L.T("Settings.About.UpdateFailed", "更新失败：{0}", error.Message));
        }
        finally
        {
            AboutUpdateButton.IsEnabled = true;
        }
    }

    private void ShowInstallConfirmDialog(string downloadedPath)
    {
        var content = new StackPanel { Spacing = 12, Width = 380 };
        content.Children.Add(new TextBlock
        {
            Text = L.T("Settings.About.Update.InstallConfirm",
                "更新包下载完成。重启启动器并安装？安装会自动重启应用。"),
            TextWrapping = TextWrapping.Wrap,
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancel = new Button { Content = L.T("Common.Cancel", "取消"), Classes = { "ghost" }, Height = 30 };
        cancel.Click += (_, _) => DismissDialog(cancel);
        var restart = new Button
        {
            Content = L.T("Settings.About.Update.InstallNow", "重启并安装"),
            Classes = { "primary" },
            Height = 30,
        };
        restart.Click += (_, _) =>
        {
            DismissDialog(restart);
            try
            {
                GitHubUpdater.Apply(downloadedPath); // 内部会重启并退出当前进程
            }
            catch (Exception error)
            {
                Shell.Toast(L.T("Settings.About.UpdateFailed", "更新失败：{0}", error.Message));
            }
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(restart);
        content.Children.Add(buttons);
        Shell.Dialog(content, L.T("Settings.About.Update.InstallTitle", "安装更新"));
    }

    private static void DismissDialog(Avalonia.Controls.Control control) =>
        control.FindAncestorOfType<SukiDialogHost>(true)?.Manager?.DismissDialog();

    private void OnOpenProjectHomeClick(object? sender, RoutedEventArgs e)
    {
        // 用 OpenLinkBrowser 打开项目主页（Windows shell / Linux xdg-open）
        var result = OpenLinkBrowser.OpenLink("https://github.com/redstore-noob/Mofumofu");
        Shell.Toast(result == "success"
            ? L.T("Settings.About.HomeOpened", "已在浏览器打开项目主页")
            : L.T("Settings.About.HomeOpenFailed", "打开项目主页失败：{0}", result));
    }

    private void OnOpenProjectSiteClick(object? sender, RoutedEventArgs e)
    {
        // 宣传页：同一个 OpenLinkBrowser
        var result = OpenLinkBrowser.OpenLink("https://redstore-noob.github.io/");
        Shell.Toast(result == "success"
            ? L.T("Settings.About.SiteOpened", "已在浏览器打开宣传页")
            : L.T("Settings.About.HomeOpenFailed", "打开项目主页失败：{0}", result));
    }

    private void OnShowLicenseClick(object? sender, RoutedEventArgs e)
    {
        const string licenseBody =
            "Mofumofu Launcher / Mofumofu.Launcher\n" +
            "MIT License\n\n" +
            "Copyright (c) 2026 Mofumofu\n\n" +
            "Permission is hereby granted, free of charge, to any person obtaining a copy\n" +
            "of this software and associated documentation files (the \"Software\"), to deal\n" +
            "in the Software without restriction, including without limitation the rights\n" +
            "to use, copy, modify, merge, publish, distribute, sublicense, and/or sell\n" +
            "copies of the Software, and to permit persons to whom the Software is\n" +
            "furnished to do so, subject to the following conditions:\n\n" +
            "The above copyright notice and this permission notice shall be included in all\n" +
            "copies or substantial portions of the Software.\n\n" +
            "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR\n" +
            "IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,\n" +
            "FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE\n" +
            "AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER\n" +
            "LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,\n" +
            "OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE\n" +
            "SOFTWARE.\n\n";

        var licenseText = licenseBody + L.T("Settings.About.LicenseThirdParty",
            "第三方组件许可：\n" +
            "· Avalonia UI 12.1.4 — MIT License\n" +
            "· SukiUI 7 — MIT License\n" +
            "· .NET 10 Runtime — MIT License\n" +
            "· Material Design Icons — Apache License 2.0\n" +
            "· Inter 字体 — SIL Open Font License 1.1");

        Shell.Dialog(new ScrollViewer
        {
            MaxHeight = 420,
            Content = new TextBlock
            {
                Text = licenseText,
                FontSize = 11.5,
                FontFamily = new FontFamily("Consolas, Microsoft YaHei"),
                TextWrapping = TextWrapping.Wrap,
            },
        });
    }
}
