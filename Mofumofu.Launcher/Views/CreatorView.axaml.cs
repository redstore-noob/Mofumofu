using Mofumofu.Launcher.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MofuCore.Config;
using MofuCore.Log;
using MofuCore.Download.Modrinth;
using MofuCore.Tools;
using L = Mofumofu.Launcher.Localization.Localization;

using Mofumofu.Launcher.Models;
using Mofumofu.Launcher.Services;
using Mofumofu.Launcher.Web;
using SukiUI.Controls;

namespace Mofumofu.Launcher.Views;

public partial class CreatorView : UserControl
{
    private const int MaxErrorMessages = 3;

    private const int PreviewDebounceMs = 250;
    private const int MaxPreviewMods = 12;

    private static readonly (string Value, string Label)[] ColorModes =
    {
        ("gradient", L.T("Creator.Cube.ColorMode.Gradient", "上下渐变")), ("solid", L.T("Creator.Cube.ColorMode.Solid", "纯色")), ("rainbow", L.T("Creator.Cube.ColorMode.Rainbow", "彩虹")),
    };

    private static readonly (string Value, string Label)[] Textures =
    {
        ("grid", L.T("Creator.Cube.Texture.Grid", "方块边框")), ("noise", L.T("Creator.Cube.Texture.Noise", "噪点石纹")), ("none", L.T("Creator.Cube.Texture.None", "无纹理")),
    };

    private readonly Dictionary<string, Control> _toolPages = new();
    private readonly Dictionary<string, Button> _toolNavButtons = new();
    private string? _currentToolId;
    private readonly List<string> _fontNames = new();

    private WebRegionHost? _cubeHost;
    private bool _cubeAttached;
    private bool _cubeUiReady;
    private bool _themeSubscribed;

    public CreatorView()
    {
        InitializeComponent();

        BuildToolNav();
        LoadSystemFonts();
        InitCubeControls();

        SelectTool(CreatorToolCatalog.Tools[0], animate: false);

        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (!_themeSubscribed)
        {
            ThemeManager.ThemeChanged += OnThemeChanged;
            _themeSubscribed = true;
        }

        if (!_cubeAttached)
        {
            _cubeAttached = true;
            _ = AttachCubeHostAsync();
        }
        else
        {
            PushCubeState();
        }
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_themeSubscribed)
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
            _themeSubscribed = false;
        }

    }

    private void OnThemeChanged()
    {
        Dispatcher.UIThread.Post(PushCubeState);
    }

    private void BuildToolNav()
    {
        ToolNavPanel.Children.Clear();
        _toolNavButtons.Clear();

        foreach (var tool in CreatorToolCatalog.Tools)
        {
            var label = new TextBlock
            {
                Text = L.T($"Creator.Tool.{tool.Id}.Title", tool.Title),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(label, 1);

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            row.Children.Add(new Border
            {
                Classes = { "side-nav-icon" },
                Child = new IconParkIcon
                {
                    Kind = tool.IconKey,
                    Width = 15,
                    Height = 15,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
            row.Children.Add(label);

            var button = new Button
            {
                Classes = { "side-nav" },
                Content = row,
                Tag = tool.Id,
            };
            ToolTip.SetTip(button, L.T($"Creator.Tool.{tool.Id}.Desc", tool.Description));
            button.Click += (_, _) => SelectTool(tool);

            _toolNavButtons[tool.Id] = button;
            ToolNavPanel.Children.Add(button);
        }
    }

    public void OpenToolById(string toolId)
    {
        var tool = CreatorToolCatalog.Tools.FirstOrDefault(item => item.Id == toolId)
                   ?? CreatorToolCatalog.Tools[0];
        SelectTool(tool, animate: false);
    }

    private void SelectTool(CreatorTool tool, bool animate = true)
    {
        var changed = !string.Equals(_currentToolId, tool.Id, StringComparison.Ordinal);
        _currentToolId = tool.Id;

        foreach (var pair in _toolNavButtons)
        {
            pair.Value.Classes.Set("active", string.Equals(pair.Key, tool.Id, StringComparison.Ordinal));
        }

        var isCube = tool.Id == "cube3d";
        CubeTextPanel.IsVisible = isCube;
        ToolBody.IsVisible = !isCube;

        if (isCube)
        {
            // WebView2 是原生层叠窗口，不参与位移/淡入动画
            UiAnimations.Reset(ToolBody);
            ToolBody.Content = null;
            PushCubeState();
            return;
        }

        if (!_toolPages.TryGetValue(tool.Id, out var page))
        {
            page = BuildToolPage(tool);
            _toolPages[tool.Id] = page;
        }

        ToolBody.Content = page;
        UiAnimations.Reset(ToolBody);

        if (animate && changed)
        {
            UiAnimations.FadeSlideIn(ToolBody);
        }
    }

    private async Task AttachCubeHostAsync()
    {
        _cubeHost = new WebRegionHost(CubeTextHost, "cubetext/index.html", "cubetext");
        _cubeHost.MessageReceived += OnCubeMessageReceived;

        await _cubeHost.AttachAsync();

        for (var attempt = 0; attempt < 14; attempt++)
        {
            PushCubeState();
            await Task.Delay(PreviewDebounceMs);
        }
    }

    private void LoadSystemFonts()
    {
        try
        {
            _fontNames.AddRange(FontManager.Current.SystemFonts
                .Select(font => font.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase));
        }
        catch
        {
            // 取不到系统字体时用下面的兜底列表
        }

        if (_fontNames.Count == 0)
        {
            _fontNames.AddRange(new[]
            {
                "Microsoft YaHei", "SimSun", "KaiTi", "SimHei", "Segoe UI", "Arial", "Consolas",
            });
        }

        CubeFontCombo.ItemsSource = _fontNames;

        // 默认优先雅黑系（中文渲染最稳），系统没有就退回列表首项
        var defaultIndex = _fontNames.FindIndex(name =>
            name.Contains("YaHei", StringComparison.OrdinalIgnoreCase));
        if (defaultIndex < 0)
        {
            defaultIndex = _fontNames.FindIndex(name =>
                name.Contains("雅黑", StringComparison.Ordinal));
        }

        CubeFontCombo.SelectedIndex = defaultIndex >= 0 ? defaultIndex : 0;
    }

    private void InitCubeControls()
    {
        CubeColorModeCombo.ItemsSource = ColorModes.Select(mode => mode.Label).ToArray();
        CubeColorModeCombo.SelectedIndex = 0;
        CubeTextureCombo.ItemsSource = Textures.Select(texture => texture.Label).ToArray();
        CubeTextureCombo.SelectedIndex = 0;

        CubeTextInput.TextChanged += (_, _) => PushCubeState();
        CubeFontCombo.SelectionChanged += (_, _) => PushCubeState();

        CubeFontSizeSlider.ValueChanged += (_, _) =>
        {
            CubeFontSizeValue.Text = ((int)CubeFontSizeSlider.Value).ToString(CultureInfo.InvariantCulture);
            PushCubeState();
        };
        CubeResolutionSlider.ValueChanged += (_, _) =>
        {
            CubeResolutionValue.Text = ((int)CubeResolutionSlider.Value).ToString(CultureInfo.InvariantCulture);
            PushCubeState();
        };
        CubeDepthSlider.ValueChanged += (_, _) =>
        {
            CubeDepthValue.Text = CubeDepthSlider.Value.ToString("0.0", CultureInfo.InvariantCulture);
            PushCubeState();
        };
        CubeSpacingSlider.ValueChanged += (_, _) =>
        {
            CubeSpacingValue.Text = CubeSpacingSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
            PushCubeState();
        };

        CubeColorModeCombo.SelectionChanged += (_, _) => PushCubeState();
        CubeTextureCombo.SelectionChanged += (_, _) => PushCubeState();

        CubeBoldCheck.Click += (_, _) => PushCubeState();
        CubeAutoRotateCheck.Click += (_, _) => PushCubeState();
        CubeTransparentCheck.Click += (_, _) => PushCubeState();
        CubeOutlineCheck.Click += (_, _) =>
        {
            CubeOutlineColorText.IsEnabled = CubeOutlineCheck.IsChecked == true;
            PushCubeState();
        };

        foreach (var (box, swatch) in new[]
                 {
                     (CubeColorAText, CubeColorASwatch),
                     (CubeColorBText, CubeColorBSwatch),
                     (CubeOutlineColorText, CubeOutlineSwatch),
                 })
        {
            var target = swatch;
            box.TextChanged += (_, _) =>
            {
                target.Background = TryColorBrush(box.Text);
                PushCubeState();
            };
            target.Background = TryColorBrush(box.Text);
        }

        _cubeUiReady = true;
    }

    private string SelectedFontName() =>
        CubeFontCombo.SelectedItem as string ?? _fontNames.FirstOrDefault() ?? "Microsoft YaHei";

    private string SelectedColorMode() =>
        ColorModes[Math.Clamp(CubeColorModeCombo.SelectedIndex, 0, ColorModes.Length - 1)].Value;

    private string SelectedTexture() =>
        Textures[Math.Clamp(CubeTextureCombo.SelectedIndex, 0, Textures.Length - 1)].Value;

    private object BuildCubeState()
    {
        var surface = ThemeTokens.Color("NekoSurfaceBrush");

        return new
        {
            theme = new
            {
                dark = !ThemeManager.IsLight,
                surface = HexOf(surface),
                text = HexOf(ThemeTokens.Color("NekoTextBrush")),
                muted = HexOf(ThemeTokens.Color("NekoMutedBrush")),
                border = HexOf(ThemeTokens.Color("NekoBorderBrush")),
                accent = HexOf(ThemeTokens.Color("NekoAccentBrush")),
            },
            fonts = _fontNames,
            hud = new
            {
                title = L.T("Creator.Cube.Hud.Title", "3D 文字预览"),
                loading = L.T("Creator.Cube.Hud.Loading", "正在加载 3D 渲染器…"),
                hint = L.T("Creator.Cube.Hud.Hint", "拖拽旋转 · 滚轮缩放 · 双击自动旋转"),
                hintRotate = L.T("Creator.Cube.Hud.HintRotate", "自动旋转：开 · 拖拽可暂停"),
                counter = L.T("Creator.Cube.Hud.Counter", "{0} 个方块"),
            },
            config = new
            {
                text = CubeTextInput.Text ?? string.Empty,
                font = SelectedFontName(),
                bold = CubeBoldCheck.IsChecked == true,
                fontSize = CubeFontSizeSlider.Value,
                resolution = CubeResolutionSlider.Value,
                depth = CubeDepthSlider.Value,
                spacing = CubeSpacingSlider.Value,
                colorMode = SelectedColorMode(),
                colorA = NormalizeColorText(CubeColorAText.Text, "#38BDF8"),
                colorB = NormalizeColorText(CubeColorBText.Text, "#A855F7"),
                texture = SelectedTexture(),
                outline = CubeOutlineCheck.IsChecked == true,
                outlineColor = NormalizeColorText(CubeOutlineColorText.Text, "#0F172A"),
                transparentBackground = CubeTransparentCheck.IsChecked == true,
                autoRotate = CubeAutoRotateCheck.IsChecked == true,
            },
        };
    }

    private void PushCubeState()
    {
        if (!_cubeUiReady || _cubeHost is null) return;

        var surface = ThemeTokens.Color("NekoSurfaceBrush");
        _cubeHost.SetBackground(System.Drawing.Color.FromArgb(255, surface.R, surface.G, surface.B));
        _cubeHost.Post("state", BuildCubeState());
    }

    private void OnCubeResetClick(object? sender, RoutedEventArgs e)
    {
        _cubeHost?.Execute("window.__mofuResetCamera && window.__mofuResetCamera();");
        Shell.Toast(L.T("Creator.Cube.Toast.CameraReset", "已重置 3D 预览视角"));
    }

    private void OnCubeExportClick(object? sender, RoutedEventArgs e)
    {
        if (_cubeHost is null)
        {
            Shell.Toast(L.T("Creator.Cube.Toast.NotReady", "3D 预览还没准备好，稍后再试"));
            return;
        }

        PushCubeState();
        _cubeHost.Execute("window.__mofuExport && window.__mofuExport();");
        Shell.Toast(L.T("Creator.Cube.Toast.Exporting", "正在渲染预览，稍后会弹出保存位置"));
    }

    private async void OnCubeMessageReceived(string message)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "png") return;
            if (!root.TryGetProperty("dataUrl", out var dataUrl)) return;

            var raw = dataUrl.GetString() ?? string.Empty;
            var comma = raw.IndexOf(',');
            if (comma < 0) return;

            var bytes = Convert.FromBase64String(raw[(comma + 1)..]);
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                Shell.Toast(L.T("Creator.Cube.Toast.NoStorage", "保存对话框不可用，无法导出 PNG"));
                return;
            }

            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = L.T("Creator.Cube.Export.Title", "导出 3D 文字"),
                SuggestedFileName = "cube-3d-text.png",
                DefaultExtension = "png",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType(L.T("Creator.Cube.Export.FileType", "PNG 图片")) { Patterns = new[] { "*.png" } },
                },
            });

            if (file is null) return;

            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(bytes);
            Shell.Toast(L.T("Creator.Cube.Toast.Exported", "已导出 PNG：{0}", file.Name));
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Creator.Cube.Toast.ExportFailed", "导出失败：{0}", ex.Message));
        }
    }

    private Control BuildToolPage(CreatorTool tool)
    {
        if (tool.Id == "command")
            return new CommandGeneratorPage();

        var content = new StackPanel
        {
            Margin = new Thickness(2, 13, 10, 24),
            MaxWidth = 1000,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        switch (tool.Id)
        {
            case "catalog":
                return BuildToolCatalogPage();
            case "resourcepack":
                BuildResourcePackPage(content);
                break;
            case "gradient":
                BuildGradientPage(content);
                break;
            case "skin":
                BuildSkinPage(content);
                break;
            case "modpack":
                return BuildModpackPage(solo: false);
            case "solo":
                return BuildModpackPage(solo: true);
            case "plugin":
                BuildPluginPage(content);
                break;
            default:
                return BuildToolCatalogPage();
        }

        return new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    private Control BuildToolCatalogPage()
    {
        var buttons = new List<Control>();
        foreach (var tool in CreatorToolCatalog.Tools.Where(item => item.Id != "catalog"))
        {
            var label = new TextBlock
            {
                Text = L.T($"Creator.Tool.{tool.Id}.Title", tool.Title),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(label, 1);

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            row.Children.Add(new Border
            {
                Classes = { "side-nav-icon" },
                Child = new IconParkIcon
                {
                    Kind = tool.IconKey,
                    Width = 15,
                    Height = 15,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
            row.Children.Add(label);

            var button = new Button
            {
                Classes = { "side-nav" },
                Content = row,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            ToolTip.SetTip(button, L.T($"Creator.Tool.{tool.Id}.Desc", tool.Description));
            button.Click += (_, _) => SelectTool(tool);
            buttons.Add(button);
        }

        var content = new StackPanel
        {
            Margin = new Thickness(2, 13, 10, 24),
            MaxWidth = 1000,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        content.Children.Add(ToolUi.Group("IconFluentGrid",
            L.T("Creator.Tool.Catalog.Section", "全部工具"),
            L.T("Creator.Tool.Catalog.Hint", "选择一个工具开始创作。"),
            buttons.ToArray()));

        return new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    private static readonly (string Version, int Format)[] PackFormats =
    {
        ("1.21.4", 46), ("1.21.1", 34), ("1.20.6", 32), ("1.20.4", 22), ("1.20.1", 15),
        ("1.19.4", 13), ("1.18.2", 9), ("1.16.5", 6), ("1.12.2", 3),
    };

    private void BuildResourcePackPage(StackPanel content)
    {
        var nameBox = Input("my-resource-pack", L.T("Creator.ResourcePack.NamePlaceholder", "例如 MofuPack"));
        nameBox.Width = 260;
        var descBox = Input(L.T("Creator.ResourcePack.DescDefault", "Mofumofu 风格的原版增强材质"), L.T("Creator.ResourcePack.DescHint", "会写进 pack.mcmeta 的 description"));
        descBox.Width = 260;
        var resolution = Choice(L.T("Creator.ResourcePack.Res16", "16x（原版）"), "32x", "64x", "128x");
        resolution.Width = 200;
        var version = Choice(PackFormats.Select(item => item.Version).ToArray());
        version.Width = 200;
        var metaCode = Code(string.Empty);
        var status = Caption(L.T("Creator.ResourcePack.Status.Init", "填写名称与版本后，右侧会实时生成 pack.mcmeta。"));

        void RefreshMeta()
        {
            var format = PackFormats[Math.Clamp(version.SelectedIndex, 0, PackFormats.Length - 1)].Format;
            var name = (nameBox.Text ?? string.Empty).Trim();
            var description = string.IsNullOrWhiteSpace(descBox.Text) ? name : descBox.Text!.Trim();

            metaCode.Text = "{\n" +
                            "  \"pack\": {\n" +
                            $"    \"pack_format\": {format},\n" +
                            $"    \"description\": {JsonSerializer.Serialize(description ?? string.Empty)}\n" +
                            "  }\n" +
                            "}";
        }

        nameBox.TextChanged += (_, _) => RefreshMeta();
        descBox.TextChanged += (_, _) => RefreshMeta();
        version.SelectionChanged += (_, _) => RefreshMeta();
        RefreshMeta();

        var form = ToolUi.Group("IconFluentImage",
            L.T("Creator.ResourcePack.Card.Title", "资源包信息"),
            L.T("Creator.ResourcePack.Card.Hint", "决定 pack.mcmeta 与目录结构"),
            ToolUi.Row(L.T("Creator.ResourcePack.Name", "资源包名称"), nameBox),
            ToolUi.Row(L.T("Creator.ResourcePack.Desc", "描述"),
                L.T("Creator.ResourcePack.DescHint", "会写进 pack.mcmeta 的 description"), descBox),
            ToolUi.Row(L.T("Creator.ResourcePack.Resolution", "贴图分辨率"), resolution),
            ToolUi.Row(L.T("Creator.ResourcePack.Version", "对应游戏版本"), version));

        var files = new StackPanel { Spacing = 5 };
        foreach (var (glyph, name, info) in new[]
                 {
                     ("IconFolder", "assets/minecraft/textures/block", L.T("Creator.ResourcePack.File.Textures", "12 张贴图")),
                     ("IconFolder", "assets/minecraft/models/item", L.T("Creator.ResourcePack.File.Models", "6 个模型")),
                     ("IconFolder", "assets/minecraft/lang", "zh_cn.json · en_us.json"),
                     ("IconFluentImage", "pack.png", L.T("Creator.ResourcePack.File.Icon", "64 × 64 图标")),
                     ("IconFluentDocument", "pack.mcmeta", L.T("Creator.ResourcePack.File.Mcmeta", "自动生成")),
                 })
        {
            files.Children.Add(FileRow(glyph, name, info));
        }

        var metaCard = ToolUi.Group("IconFluentDocument", "pack.mcmeta",
            null,
            ToolUi.SoftBlock(metaCode));

        var filesCard = ToolUi.Group("IconFolder",
            L.T("Creator.ResourcePack.Files.Title", "目录结构"),
            L.T("Creator.ResourcePack.Files.Hint", "生成草稿时写入的示例文件"),
            ToolUi.SoftBlock(files));

        var actionRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var createButton = PrimaryButton(L.T("Creator.ResourcePack.Create", "生成资源包草稿"));
        var importButton = GhostButton(L.T("Creator.ResourcePack.Import", "导入资源包"));
        var openButton = GhostButton(L.T("Creator.ResourcePack.OpenFolder", "打开草稿目录"));
        var exportButton = GhostButton(L.T("Creator.ResourcePack.Export", "导出 .zip"));

        actionRow.Children.Add(openButton);
        actionRow.Children.Add(importButton);
        actionRow.Children.Add(exportButton);
        actionRow.Children.Add(createButton);

        ResourcePackProject? importedProject = null;

        nameBox.TextChanged += (_, _) => importedProject = null;
        descBox.TextChanged += (_, _) => importedProject = null;
        version.SelectionChanged += (_, _) => importedProject = null;
        resolution.SelectionChanged += (_, _) => importedProject = null;

        string CurrentName() => (nameBox.Text ?? string.Empty).Trim();
        string CurrentDescription(string name) =>
            string.IsNullOrWhiteSpace(descBox.Text) ? name : descBox.Text!.Trim();

        ResourcePackProject BuildCurrentProject(string name)
        {
            var accent = ThemeTokens.Color("NekoAccentBrush");
            return BuildResourcePackProject(
                name,
                CurrentDescription(name),
                CurrentPackFormat(version),
                PackTextureSize(resolution),
                accent);
        }

        createButton.Click += async (_, _) =>
        {
            var name = CurrentName();
            if (name.Length == 0)
            {
                Shell.Toast(L.T("Creator.ResourcePack.Toast.NeedName", "请先填写资源包名称"));
                return;
            }

            try
            {
                createButton.IsEnabled = false;
                var stopwatch = Stopwatch.StartNew();
                var project = importedProject ?? BuildCurrentProject(name);
                await ResourcePackMake.SaveDraftAsync(project);
                stopwatch.Stop();

                importedProject = null;
                status.Text = L.T("Creator.ResourcePack.Status.Drafted",
                    "草稿已保存：{0}（{1} 个文件，{2} ms）",
                    ResourcePackMake.DraftFilePath, project.FileCount, stopwatch.ElapsedMilliseconds);
                Shell.Toast(L.T("Creator.ResourcePack.Toast.Drafted",
                    "资源包草稿已保存：{0}", ResourcePackMake.DraftFilePath));
            }
            catch (Exception ex)
            {
                status.Text = L.T("Creator.ResourcePack.Status.Failed", "生成资源包失败：{0}", ex.Message);
                Shell.Toast(L.T("Creator.ResourcePack.Toast.Failed", "生成资源包失败：{0}", ex.Message));
            }
            finally
            {
                createButton.IsEnabled = true;
            }
        };

        openButton.Click += (_, _) => OpenFolder(ResourcePackMake.DraftDirectory);
        importButton.Click += async (_, _) =>
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                Shell.Toast(L.T("Creator.Common.NoStorage", "文件对话框不可用"));
                return;
            }

            var picks = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L.T("Creator.ResourcePack.Import.Title", "选择资源包 .zip"),
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(L.T("Creator.ResourcePack.Export.FileType", "Minecraft 资源包"))
                    {
                        Patterns = new[] { "*.zip" },
                    },
                },
            });

            if (picks.Count == 0) return;

            try
            {
                importButton.IsEnabled = false;
                var stopwatch = Stopwatch.StartNew();
                var project = await ResourcePackMake.ImportAsync(picks[0].Path.LocalPath);
                await ResourcePackMake.SaveDraftAsync(project);
                stopwatch.Stop();

                if (!string.IsNullOrWhiteSpace(project.Name)) nameBox.Text = project.Name;
                importedProject = project;
                status.Text = L.T("Creator.ResourcePack.Status.Imported",
                    "已导入资源包：{0}（{1} 个文件，{2} ms），可直接导出或点「生成资源包草稿」保存",
                    picks[0].Name, project.FileCount, stopwatch.ElapsedMilliseconds);
                Shell.Toast(L.T("Creator.ResourcePack.Toast.Imported",
                    "已导入资源包：{0}（{1} 个文件）", picks[0].Name, project.FileCount));
                AppLog("INFO", $"创作中心导入资源包：{picks[0].Path.LocalPath}（{project.FileCount} 个文件）");
            }
            catch (Exception ex)
            {
                status.Text = L.T("Creator.ResourcePack.Status.ImportFailed", "导入资源包失败：{0}", ex.Message);
                Shell.Toast(status.Text);
            }
            finally
            {
                importButton.IsEnabled = true;
            }
        };

        exportButton.Click += async (_, _) =>
        {
            var name = CurrentName();
            if (name.Length == 0)
            {
                Shell.Toast(L.T("Creator.ResourcePack.Toast.NeedName", "请先填写资源包名称"));
                return;
            }

            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                Shell.Toast(L.T("Creator.Common.NoStorage", "文件对话框不可用"));
                return;
            }

            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = L.T("Creator.ResourcePack.Export.Title", "导出资源包"),
                SuggestedFileName = $"{SanitizeFileName(name)}.zip",
                DefaultExtension = "zip",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType(L.T("Creator.ResourcePack.Export.FileType", "Minecraft 资源包"))
                    {
                        Patterns = new[] { "*.zip" },
                    },
                },
            });

            if (file is null) return;

            try
            {
                exportButton.IsEnabled = false;
                var stopwatch = Stopwatch.StartNew();
                var target = file.Path.LocalPath;
                var project = importedProject ?? BuildCurrentProject(name);
                var reporter = new Progress<string>(phase =>
                    Dispatcher.UIThread.Post(() =>
                        status.Text = L.T("Creator.ResourcePack.Status.Exporting", "正在写入资源包：{0}", phase)));

                var outputPath = await ResourcePackMake.ExportAsync(project, target, reporter);
                stopwatch.Stop();

                importedProject = null;
                var size = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;
                status.Text = L.T("Creator.ResourcePack.Status.Exported",
                    "已导出资源包：{0}（{1} 个文件，{2}，{3} ms）",
                    outputPath, project.FileCount, FormatSize(size), stopwatch.ElapsedMilliseconds);
                Shell.Toast(L.T("Creator.ResourcePack.Toast.Exported", "已导出资源包：{0}", outputPath));
                AppLog("INFO", $"创作中心导出资源包：{outputPath}（{project.FileCount} 个文件）");
            }
            catch (Exception ex)
            {
                status.Text = L.T("Creator.ResourcePack.Status.ExportFailed", "导出资源包失败：{0}", ex.Message);
                Shell.Toast(L.T("Creator.ResourcePack.Toast.ExportFailed", "导出资源包失败：{0}", ex.Message));
            }
            finally
            {
                exportButton.IsEnabled = true;
            }
        };

        var actionGroup = ToolUi.Group("IconFluentBox",
            L.T("Creator.ResourcePack.Actions.Title", "生成与导出"),
            null,
            actionRow,
            status);

        content.Children.Add(form);
        content.Children.Add(metaCard);
        content.Children.Add(filesCard);
        content.Children.Add(actionGroup);
    }

    private static string CreatorDataRoot => Path.Combine(
        MofuCore.Config.YamlRead.DataRoot,
        MofuCore.Config.YamlRead.DataDirectoryName,
        "creator");

    private static string PluginRoot => Path.Combine(CreatorDataRoot, "plugins");
    private static string ModpackOutputRoot => Path.Combine(CreatorDataRoot, "modpacks");
    private static string SoloOutputRoot => Path.Combine(CreatorDataRoot, "solo");

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            Shell.Toast(L.T("Creator.Common.FolderOpened", "已打开目录：{0}", path));
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Creator.Common.FolderFailed", "打开目录失败：{0}", ex.Message));
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.0} MB",
    };

    private static int CurrentPackFormat(ComboBox version) =>
        PackFormats[Math.Clamp(version.SelectedIndex, 0, PackFormats.Length - 1)].Format;

    private static int PackTextureSize(ComboBox resolution) => Math.Clamp(resolution.SelectedIndex, 0, 3) switch
    {
        1 => 32,
        2 => 64,
        3 => 128,
        _ => 16,
    };

    private static string PackSampleModelJson(int textureSize) =>
        "{\n" +
        "  \"parent\": \"minecraft:item/generated\",\n" +
        "  \"textures\": {\n" +
        $"    \"layer0\": \"minecraft:block/mofumofu_sample_{textureSize}x\"\n" +
        "  }\n" +
        "}";

    private static string PackLangJson(string name, string description) =>
        "{\n" +
        $"  \"pack.mofumofu.name\": {JsonSerializer.Serialize(name)},\n" +
        $"  \"pack.mofumofu.description\": {JsonSerializer.Serialize(description)}\n" +
        "}";

    private static byte[] PngBytes(WriteableBitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return stream.ToArray();
    }

    private static ResourcePackProject BuildResourcePackProject(
        string name, string description, int packFormat, int textureSize, Color accent)
    {
        var project = ResourcePackMake.CreateDraft(name);
        ResourcePackMake.WritePackMeta(project, packFormat, description);

        using (var icon = CreatePackIconBitmap(accent, 128))
        {
            ResourcePackMake.SetIcon(project, PngBytes(icon));
        }

        var textureKey = $"assets/minecraft/textures/block/mofumofu_sample_{textureSize}x.png";
        using (var texture = CreateCheckerTexture(accent, textureSize))
        {
            ResourcePackMake.AddTexture(project, textureKey, PngBytes(texture));
        }

        ResourcePackMake.AddModelJson(
            project,
            $"assets/minecraft/models/item/mofumofu_sample_{textureSize}x.json",
            PackSampleModelJson(textureSize));
        ResourcePackMake.AddTextFile(project, "assets/minecraft/lang/zh_cn.json", PackLangJson(name, description));
        ResourcePackMake.AddTextFile(project, "assets/minecraft/lang/en_us.json", PackLangJson(name, description));

        return project;
    }

    private static void AddZipText(ZipArchive archive, string entry, string text, Encoding encoding)
    {
        var handle = archive.CreateEntry(entry, CompressionLevel.Optimal);
        using var stream = handle.Open();
        using var writer = new StreamWriter(stream, encoding);
        writer.Write(text);
    }

    private static void AddZipBytes(ZipArchive archive, string entry, byte[] bytes)
    {
        var handle = archive.CreateEntry(entry, CompressionLevel.Optimal);
        using var stream = handle.Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    private static WriteableBitmap CreatePackIconBitmap(Color accent, int size)
    {
        var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96));
        var dark = ThemeTokens.Blend(accent, Colors.Black, 0.55);
        var light = ThemeTokens.Blend(accent, Colors.White, 0.35);
        using var buffer = bitmap.Lock();
        var row = new byte[size * 4];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var t = (x + y) / (double)Math.Max(1, size * 2 - 2);
                var color = ThemeTokens.Blend(light, dark, t);
                var border = x < 3 || y < 3 || x >= size - 3 || y >= size - 3;
                var diamond = Math.Abs(x - size / 2.0) + Math.Abs(y - size / 2.0) < size * 0.22;

                if (border) color = Colors.White;
                else if (diamond) color = light;

                var offset = x * 4;
                row[offset + 0] = color.B;
                row[offset + 1] = color.G;
                row[offset + 2] = color.R;
                row[offset + 3] = 0xFF;
            }

            Marshal.Copy(row, 0, buffer.Address + y * buffer.RowBytes, row.Length);
        }

        return bitmap;
    }

    private static WriteableBitmap CreateCheckerTexture(Color accent, int size)
    {
        var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96));
        var c1 = ThemeTokens.Blend(accent, Colors.White, 0.3);
        var c2 = ThemeTokens.Blend(accent, Colors.Black, 0.3);
        var cell = Math.Max(2, size / 8);
        using var buffer = bitmap.Lock();
        var row = new byte[size * 4];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var color = ((x / cell) + (y / cell)) % 2 == 0 ? c1 : c2;
                var offset = x * 4;
                row[offset + 0] = color.B;
                row[offset + 1] = color.G;
                row[offset + 2] = color.R;
                row[offset + 3] = 0xFF;
            }

            Marshal.Copy(row, 0, buffer.Address + y * buffer.RowBytes, row.Length);
        }

        return bitmap;
    }

    private void BuildGradientPage(StackPanel content)
    {
        string[] formatLabels =
        {
            L.T("Creator.Gradient.Format.Legacy", "原版 · &#RRGGBB"), L.T("Creator.Gradient.Format.Section", "标准十六进制 · §x§R…"), L.T("Creator.Gradient.Format.Amp", "& 变体 · &x&R&R…"), "MiniMessage · <#RRGGBB>",
        };
        string[] formatValues = { "legacy", "section", "amp", "minimessage" };
        string[] controlLabels = { L.T("Creator.Gradient.Control.Amp", "& （插件常用）"), L.T("Creator.Gradient.Control.Section", "§ （原版标准）") };

        var textBox = Input("Mofumofu", L.T("Creator.Gradient.TextPlaceholder", "输入要着色的文字（支持中文）"));
        textBox.Width = 280;
        var formatCombo = Choice(formatLabels);
        formatCombo.Width = 230;
        var controlCombo = Choice(controlLabels);
        controlCombo.Width = 230;
        var stops = new List<string> { "#38BDF8", "#A855F7", "#F472B6" };
        var stopsHost = new StackPanel { Spacing = 7 };
        var preview = new TextBlock
        {
            Text = "Mofumofu",
            FontSize = 26,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6),
        };
        var output = Code(string.Empty);
        var status = Caption(L.T("Creator.Gradient.Status.Init", "逐字着色：空白字符不参与渐变推进，颜色码会重置样式。"));

        var boldCheck = ToolUi.Switch();
        var italicCheck = ToolUi.Switch();
        var underlineCheck = ToolUi.Switch();
        var strikeCheck = ToolUi.Switch();
        var obfuscatedCheck = ToolUi.Switch();

        var stopsPanel = new StackPanel { Spacing = 8 };
        stopsPanel.Children.Add(stopsHost);

        void RebuildStops()
        {
            stopsHost.Children.Clear();

            for (var index = 0; index < stops.Count; index++)
            {
                var i = index;
                var colorBox = Input(stops[i]);
                colorBox.Width = 110;
                colorBox.FontSize = 12;
                colorBox.Height = 30;

                var swatch = new Border
                {
                    Width = 26,
                    Height = 26,
                    CornerRadius = new CornerRadius(7),
                    BorderThickness = new Thickness(1),
                    BorderBrush = ThemeTokens.Brush("NekoBorderBrush"),
                    Background = TryColorBrush(stops[i]),
                };

                colorBox.TextChanged += (_, _) =>
                {
                    var normalized = NormalizeHex(colorBox.Text ?? string.Empty);
                    if (normalized.Length == 0) return;

                    stops[i] = normalized;
                    swatch.Background = TryColorBrush(normalized);
                    Refresh();
                };

                var remove = new Button
                {
                    Content = L.T("Common.Remove", "移除"),
                    Classes = { "ghost" },
                    Height = 28,
                    FontSize = 11,
                    Padding = new Thickness(10, 0),
                    IsEnabled = stops.Count > 2,
                };
                remove.Click += (_, _) =>
                {
                    if (stops.Count <= 2) return;
                    stops.RemoveAt(i);
                    RebuildStops();
                    Refresh();
                };

                var editor = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                editor.Children.Add(colorBox);
                editor.Children.Add(swatch);
                editor.Children.Add(remove);
                stopsHost.Children.Add(ToolUi.Row(
                    L.T("Creator.Gradient.StopLabel", "色标 {0}", i + 1), editor));
            }
        }

        void Refresh()
        {
            var text = string.IsNullOrEmpty(textBox.Text) ? "Mofumofu" : textBox.Text!;
            var format = formatValues[Math.Clamp(formatCombo.SelectedIndex, 0, formatValues.Length - 1)];
            var control = controlCombo.SelectedIndex == 1 ? '§' : '&';

            preview.Text = text;
            preview.Foreground = BuildGradientBrush(stops);

            output.Text = BuildGradientOutput(
                text,
                stops,
                format,
                control,
                boldCheck.IsChecked == true,
                italicCheck.IsChecked == true,
                underlineCheck.IsChecked == true,
                strikeCheck.IsChecked == true,
                obfuscatedCheck.IsChecked == true);

            status.Text = L.T("Creator.Gradient.Status.Summary", "共 {0} 个色标 · 输出格式：{1}", stops.Count, formatLabels[Math.Clamp(formatCombo.SelectedIndex, 0, formatLabels.Length - 1)]);
        }

        textBox.TextChanged += (_, _) => Refresh();
        formatCombo.SelectionChanged += (_, _) => Refresh();
        controlCombo.SelectionChanged += (_, _) => Refresh();
        foreach (var check in new[] { boldCheck, italicCheck, underlineCheck, strikeCheck, obfuscatedCheck })
        {
            check.Click += (_, _) => Refresh();
        }

        var addStop = GhostButton(L.T("Creator.Gradient.AddStop", "添加色标"));
        addStop.Click += (_, _) =>
        {
            if (stops.Count >= 6)
            {
                Shell.Toast(L.T("Creator.Gradient.Toast.MaxStops", "最多 6 个色标"));
                return;
            }

            stops.Add("#F2AE49");
            RebuildStops();
            Refresh();
        };

        var copy = PrimaryButton(L.T("Creator.Gradient.Copy", "复制颜色码"));
        copy.Click += async (_, _) =>
        {
            await CopyToClipboardAsync(output.Text ?? string.Empty);
            Shell.Toast(L.T("Creator.Gradient.Toast.Copied", "渐变颜色码已复制到剪贴板"));
        };

        RebuildStops();

        var textGroup = ToolUi.Group("IconFluentFont",
            L.T("Creator.Gradient.Card.Title", "渐变设置"), null,
            ToolUi.Row(L.T("Creator.Gradient.Text", "文字内容"), textBox),
            ToolUi.Row(L.T("Creator.Gradient.Format", "输出格式"), formatCombo),
            ToolUi.Row(L.T("Creator.Gradient.Control", "控制符"), controlCombo));

        var stopsGroup = ToolUi.Group("IconFluentPaint",
            L.T("Creator.Gradient.Stops.Title", "渐变色标"),
            L.T("Creator.Gradient.Card.Hint", "最多 6 个色标，按顺序插值"),
            stopsPanel,
            ToolUi.Actions(addStop));

        var styleGroup = ToolUi.Group("IconFluentFont",
            L.T("Creator.Gradient.Styles", "文字样式"), null,
            ToolUi.Row(L.T("Creator.Gradient.Style.Bold", "粗体"), boldCheck),
            ToolUi.Row(L.T("Creator.Gradient.Style.Italic", "斜体"), italicCheck),
            ToolUi.Row(L.T("Creator.Gradient.Style.Underline", "下划线"), underlineCheck),
            ToolUi.Row(L.T("Creator.Gradient.Style.Strike", "删除线"), strikeCheck),
            ToolUi.Row(L.T("Creator.Gradient.Style.Obfuscated", "乱码"), obfuscatedCheck));

        var previewBlock = ToolUi.SoftBlock(preview);
        previewBlock.Margin = new Thickness(0, 0, 0, 8);

        var previewGroup = ToolUi.Group("IconFluentImage",
            L.T("Creator.Gradient.Preview", "预览与输出"), null,
            previewBlock,
            ToolUi.SoftBlock(output),
            ToolUi.Actions(copy),
            status);

        content.Children.Add(textGroup);
        content.Children.Add(stopsGroup);
        content.Children.Add(styleGroup);
        content.Children.Add(previewGroup);
        Refresh();
    }

    private const int SkinSize = 64;
    private const int SkinScale = 4;

    private static readonly string[] SkinPalette =
    {
        "#3B2A1E", "#6A4B33", "#8C6244", "#B48A68", "#D9B08C", "#F2D3B3",
        "#1B2430", "#3A4A5E", "#5C6B7E", "#93A0B4", "#C6D2E2", "#FFFFFF",
        "#4C6B3A", "#6A9B3A", "#4CC38A", "#3FC6C1", "#5A9CF8", "#A855F7",
        "#E5698F", "#E5484D", "#F2AE49", "#C89B3C", "#000000", "#00FFFF",
    };

    private byte[] _skinPixels = Array.Empty<byte>();
    private WriteableBitmap? _skinBitmap;
    private Image? _skinPreview;
    private string _skinBrushColor = "#B48A68";
    private string _skinAccountName = "Mofumofu";
    private bool _skinErase;
    private bool _skinPainting;
    private TextBlock? _skinStatus;

    private static string SkinLibraryDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mofumofu.Launcher",
        "skins");

    private static string CustomSkinRoot => Path.Combine(SkinLibraryDirectory, "custom");

    private void BuildSkinPage(StackPanel content)
    {
        var accounts = Choice(RealAccountNames().ToArray());
        accounts.Width = 220;
        var model = Choice(L.T("Creator.Skin.Model.Classic", "经典（4 格手臂）"), L.T("Creator.Skin.Model.Slim", "纤细（3 格手臂）"));
        model.Width = 220;
        var cape = ToolUi.Switch();
        var status = Caption(L.T("Creator.Skin.Status.Init", "在画布上按住左键绘制，选「橡皮」擦除；导出的是标准 64×64 皮肤 PNG。"));
        _skinStatus = status;
        _skinAccountName = accounts.SelectedItem as string ?? "Mofumofu";
        accounts.SelectionChanged += (_, _) =>
        {
            _skinAccountName = accounts.SelectedItem as string ?? "Mofumofu";
            status.Text = L.T("Creator.Skin.Status.Account", "画布会保存到「{0}」的皮肤库文件。", _skinAccountName);
        };

        _skinPixels = new byte[SkinSize * SkinSize * 4];
        _skinBitmap = new WriteableBitmap(
            new PixelSize(SkinSize, SkinSize), new Vector(96, 96));
        PaintDefaultSkin();
        CommitSkinPixels();

        _skinPreview = new Image
        {
            Source = _skinBitmap,
            Width = SkinSize * SkinScale,
            Height = SkinSize * SkinScale,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Cross),
        };

        RenderOptions.SetBitmapInterpolationMode(_skinPreview, BitmapInterpolationMode.None);

        var canvasBorder = new Border
        {
            Background = ThemeTokens.Brush("NekoFieldSoftBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeTokens.Brush("NekoBorderBrush"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8),
            Child = _skinPreview,
        };

        _skinPreview.PointerPressed += (_, e) =>
        {
            _skinPainting = true;
            e.Pointer.Capture(_skinPreview);
            PaintAt(e.GetPosition(_skinPreview));
        };
        _skinPreview.PointerMoved += (_, e) =>
        {
            if (!_skinPainting) return;
            PaintAt(e.GetPosition(_skinPreview));
        };
        _skinPreview.PointerReleased += (_, e) =>
        {
            _skinPainting = false;
            e.Pointer.Capture(null);
        };

        var paletteGrid = new WrapPanel { Orientation = Orientation.Horizontal };
        var paletteButtons = new List<Border>();

        void SelectColor(string hex)
        {
            _skinBrushColor = hex;
            _skinErase = false;
            foreach (var border in paletteButtons)
            {
                border.BorderThickness = new Thickness(1);
                border.BorderBrush = ThemeTokens.Brush("NekoBorderBrush");
            }
        }

        foreach (var hex in SkinPalette)
        {
            var swatch = new Border
            {
                Width = 24,
                Height = 24,
                Margin = new Thickness(3),
                CornerRadius = new CornerRadius(6),
                Background = TryColorBrush(hex),
                BorderThickness = new Thickness(1),
                BorderBrush = ThemeTokens.Brush("NekoBorderBrush"),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            var color = hex;
            swatch.PointerPressed += (_, _) =>
            {
                SelectColor(color);
                swatch.BorderThickness = new Thickness(2);
                swatch.BorderBrush = ThemeTokens.Brush("NekoAccentBrush");
                status.Text = L.T("Creator.Skin.Brush.Color", "当前画笔：{0}", color);
            };
            paletteButtons.Add(swatch);
            paletteGrid.Children.Add(swatch);
        }

        var eraser = GhostButton(L.T("Creator.Skin.Eraser", "橡皮"));
        eraser.Click += (_, _) =>
        {
            _skinErase = true;
            status.Text = L.T("Creator.Skin.Brush.Eraser", "当前画笔：橡皮（擦除为透明）");
        };
        var clear = GhostButton(L.T("Creator.Skin.Clear", "清空画布"));
        clear.Click += (_, _) =>
        {
            Array.Clear(_skinPixels);
            CommitSkinPixels();
            status.Text = L.T("Creator.Skin.Status.Cleared", "画布已清空。");
        };

        var import = GhostButton(L.T("Creator.Skin.Import", "导入 PNG"));
        import.Click += async (_, _) => await ImportSkinAsync();
        var export = GhostButton(L.T("Creator.Skin.Export", "导出 PNG"));
        export.Click += async (_, _) => await ExportSkinAsync();
        var apply = PrimaryButton(L.T("Creator.Skin.Apply", "应用到当前账号"));
        apply.Click += (_, _) =>
        {
            var name = accounts.SelectedItem as string ?? "Mofumofu";
            var shape = model.SelectedIndex == 1
                ? L.T("Creator.Skin.Shape.Slim", "纤细")
                : L.T("Creator.Skin.Shape.Classic", "经典");

            var path = ApplyCustomSkin(name);
            if (path is null)
            {
                var message = L.T("Creator.Skin.Status.ApplyFailed", "应用自定义皮肤失败，请重试");
                status.Text = message;
                Shell.Toast(message);
                return;
            }

            status.Text = L.T("Creator.Skin.Status.AppliedCustom",
                "已应用自定义皮肤：{0}（{1} · {2}），请在账户页刷新预览", path, name, shape);
            Shell.Toast(L.T("Creator.Skin.Toast.AppliedCustom", "已应用自定义皮肤：{0}", path));
            AppLog("INFO", $"创作中心应用自定义皮肤：{path}");
        };

        var brushRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        brushRow.Children.Add(eraser);
        brushRow.Children.Add(clear);
        brushRow.Children.Add(import);
        brushRow.Children.Add(export);
        brushRow.Children.Add(apply);

        canvasBorder.HorizontalAlignment = HorizontalAlignment.Center;
        paletteGrid.HorizontalAlignment = HorizontalAlignment.Center;
        paletteGrid.Margin = new Thickness(0, 10, 0, 0);

        var accountGroup = ToolUi.Group("IconFluentPerson",
            L.T("Creator.Skin.Card.Title", "账号与模型"),
            null,
            ToolUi.Row(L.T("Creator.Skin.Account", "账号"), accounts),
            ToolUi.Row(L.T("Creator.Skin.Model", "皮肤模型"), model),
            ToolUi.Row(L.T("Creator.Skin.Cape", "显示披风"), cape));

        var canvasGroup = ToolUi.Group("IconFluentImage",
            L.T("Creator.Skin.Canvas.Title", "64 × 64 像素画布"),
            null,
            canvasBorder,
            paletteGrid,
            ToolUi.Row(L.T("Creator.Skin.Tools", "绘图工具"), brushRow),
            status);

        content.Children.Add(accountGroup);
        content.Children.Add(canvasGroup);
    }

    private void PaintDefaultSkin()
    {
        // 默认画一个经典皮肤的头部正面（Steve 风格），避免一进来是空白画布
        void Set(int x, int y, byte r, byte g, byte b, byte a = 255)
        {
            if (x < 0 || y < 0 || x >= SkinSize || y >= SkinSize) return;
            var offset = (y * SkinSize + x) * 4;
            _skinPixels[offset + 0] = b;
            _skinPixels[offset + 1] = g;
            _skinPixels[offset + 2] = r;
            _skinPixels[offset + 3] = a;
        }

        for (var y = 8; y < 16; y++)
        {
            for (var x = 8; x < 16; x++)
            {
                Set(x, y, 0xB4, 0x8A, 0x68);
            }
        }

        for (var x = 8; x < 16; x++)
        {
            for (var y = 8; y < 10; y++)
            {
                Set(x, y, 0x3B, 0x2A, 0x1E);
            }
        }

        foreach (var (x, y) in new[] { (9, 11), (10, 11), (13, 11), (14, 11) })
        {
            Set(x, y, 0x2E, 0x4A, 0x8A);
        }

        for (var x = 10; x < 14; x++)
        {
            Set(x, 13, 0x8C, 0x62, 0x44);
        }

        Set(11, 14, 0x6A, 0x4B, 0x33);
        Set(12, 14, 0x6A, 0x4B, 0x33);
    }

    private void PaintAt(Point position)
    {
        if (_skinPreview is null) return;

        var x = (int)Math.Floor(position.X / SkinScale);
        var y = (int)Math.Floor(position.Y / SkinScale);
        if (x < 0 || y < 0 || x >= SkinSize || y >= SkinSize) return;

        var offset = (y * SkinSize + x) * 4;
        if (_skinErase)
        {
            _skinPixels[offset + 0] = 0;
            _skinPixels[offset + 1] = 0;
            _skinPixels[offset + 2] = 0;
            _skinPixels[offset + 3] = 0;
        }
        else
        {
            var color = Color.Parse(_skinBrushColor);
            _skinPixels[offset + 0] = color.B;
            _skinPixels[offset + 1] = color.G;
            _skinPixels[offset + 2] = color.R;
            _skinPixels[offset + 3] = 0xFF;
        }

        CommitSkinPixels();
    }

    private void CommitSkinPixels()
    {
        if (_skinBitmap is null) return;

        using (var buffer = _skinBitmap.Lock())
        {
            for (var y = 0; y < SkinSize; y++)
            {
                Marshal.Copy(_skinPixels, y * SkinSize * 4,
                    buffer.Address + y * buffer.RowBytes, SkinSize * 4);
            }
        }

        if (_skinPreview is not null)
        {
            var source = _skinPreview.Source;
            _skinPreview.Source = null;
            _skinPreview.Source = source;
            _skinPreview.InvalidateVisual();
        }
    }

    private async Task ImportSkinAsync()
    {
        IStorageProvider storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null)
        {
            return;
        }
        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("Creator.Skin.Import.Title", "选择 64×64 皮肤 PNG"),
            AllowMultiple = false,
            FileTypeFilter = new FilePickerFileType[1]
            {
                new FilePickerFileType(L.T("Creator.Cube.Export.FileType", "PNG 图片"))
                {
                    Patterns = new string[1] { "*.png" }
                }
            }
        });
        if (files.Count == 0)
        {
            return;
        }
        try
        {
            await using Stream stream = await files[0].OpenReadAsync();
            using (Bitmap bitmap = new Bitmap(stream))
            {
                int width = Math.Min(64, bitmap.PixelSize.Width);
                int height = Math.Min(64, bitmap.PixelSize.Height);
                if (height <= 0 || width <= 0)
                {
                    goto end_IL_0203;
                }
                int stride = width * 4;
                byte[] row = new byte[stride];
                if (_skinBitmap == null)
                {
                    goto end_IL_0203;
                }
                Array.Clear(_skinPixels);
                using (var buffer = _skinBitmap.Lock())
                {
                    for (int y = 0; y < height; y++)
                    {
                        GCHandle handle = GCHandle.Alloc(row, GCHandleType.Pinned);
                        try
                        {
                            bitmap.CopyPixels(new PixelRect(0, y, width, 1), handle.AddrOfPinnedObject(), stride, stride);
                        }
                        finally
                        {
                            handle.Free();
                        }
                        Array.Copy(row, 0, _skinPixels, y * 64 * 4, stride);
                        Marshal.Copy(row, 0, buffer.Address + y * buffer.RowBytes, stride);
                    }
                }
                CommitSkinPixels();
                _skinStatus.Text = L.T("Creator.Skin.Status.Imported", "已导入 {0}（{1}×{2}）", files[0].Name, bitmap.PixelSize.Width, bitmap.PixelSize.Height);
                Shell.Toast(L.T("Creator.Skin.Toast.Imported", "皮肤已导入画布"));
                goto end_IL_01f2;
                end_IL_0203:;
            }
            return;
            end_IL_01f2:;
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Creator.Skin.Toast.ImportFailed", "导入失败：{0}", ex.Message));
        }
    }

    private async Task ExportSkinAsync()
    {
        IStorageProvider storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        string libraryPath = SaveSkinToLibrary();
        if (storage == null || _skinBitmap == null)
        {
            if (libraryPath != null)
            {
                if (_skinStatus != null)
                {
                    _skinStatus.Text = L.T("Creator.Skin.Status.Library", "已存入皮肤库：{0}", libraryPath);
                }
                Shell.Toast(L.T("Creator.Skin.Toast.Library", "皮肤已存入皮肤库：{0}", libraryPath));
            }
            return;
        }
        IStorageFile file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.T("Creator.Skin.Export.Title", "导出皮肤"),
            SuggestedFileName = SanitizeFileName(_skinAccountName) + "-skin.png",
            DefaultExtension = "png",
            FileTypeChoices = new FilePickerFileType[1]
            {
                new FilePickerFileType(L.T("Creator.Cube.Export.FileType", "PNG 图片"))
                {
                    Patterns = new string[1] { "*.png" }
                }
            }
        });
        if (file == null)
        {
            if (libraryPath != null)
            {
                if (_skinStatus != null)
                {
                    _skinStatus.Text = L.T("Creator.Skin.Status.Library", "已存入皮肤库：{0}", libraryPath);
                }
                Shell.Toast(L.T("Creator.Skin.Status.Library", "已存入皮肤库：{0}", libraryPath));
            }
            return;
        }
        try
        {
            await using Stream stream = await file.OpenWriteAsync();
            _skinBitmap.Save(stream);
            string message = ((libraryPath == null) ? L.T("Creator.Skin.Status.Exported", "已导出：{0}", file.Name) : L.T("Creator.Skin.Status.ExportedWithLibrary", "已导出：{0} · 皮肤库：{1}", file.Name, libraryPath));
            if (_skinStatus != null)
            {
                _skinStatus.Text = message;
            }
            Shell.Toast(L.T("Creator.Skin.Toast.Exported", "皮肤已导出：{0}", file.Name));
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Creator.Cube.Toast.ExportFailed", "导出失败：{0}", ex.Message));
        }
    }

    private string? SaveSkinToLibrary()
    {
        if (_skinBitmap == null)
        {
            return null;
        }
        try
        {
            Directory.CreateDirectory(SkinLibraryDirectory);
            string text = Path.Combine(SkinLibraryDirectory, $"{SanitizeFileName(_skinAccountName)}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            _skinBitmap.Save(text);
            AppLog("INFO", "创作中心皮肤已存入皮肤库：" + text);
            return text;
        }
        catch
        {
            return null;
        }
    }

    private string? ApplyCustomSkin(string account)
    {
        if (_skinBitmap == null)
        {
            return null;
        }
        try
        {
            Directory.CreateDirectory(CustomSkinRoot);
            string text = Path.Combine(CustomSkinRoot, SanitizeFileName(account) + ".png");
            _skinBitmap.Save(text);
            return text;
        }
        catch (Exception ex)
        {
            AppLog("WARN", "创作中心应用自定义皮肤失败：" + ex.Message);
            return null;
        }
    }

    private Control BuildModpackPage(bool solo)
    {
        List<InstanceInfo> instances = RealInstances();
        string[] items = instances.Select((InstanceInfo instance) => instance.Name).ToArray();
        ComboBox instanceCombo = Choice(items);
        instanceCombo.Width = 240;
        TextBox versionBox = Input("1.0.0", L.T("Creator.Modpack.VersionPlaceholder", "整合包版本号"));
        versionBox.Width = 160;
        ComboBox field = Choice(L.T("Creator.Modpack.Format.Mrpack", ".mrpack（Modrinth）"), L.T("Creator.Modpack.Format.MultiMc", ".zip（MultiMC / Prism）"));
        field.Width = 240;
        ToggleSwitch includeConfig = ToolUi.Switch(isChecked: true);
        ToggleSwitch includeMods = ToolUi.Switch(isChecked: true);
        ToggleSwitch includeJava = ToolUi.Switch(isChecked: true);
        ToggleSwitch silent = ToolUi.Switch();
        TextBox outputBox = Input(solo ? SoloOutputRoot : ModpackOutputRoot);
        outputBox.Width = 300;
        ProgressBar progress = new ProgressBar
        {
            Minimum = 0.0,
            Maximum = 100.0,
            Height = 6.0
        };
        TextBlock status = Caption(solo ? L.T("Creator.Solo.Status.Init", "把启动器、Java 与整合包打进单个 exe，仅支持 Windows。") : L.T("Creator.Modpack.Status.Init", "导出后可分享给朋友或上传到整合包平台。"));
        SelectableTextBlock manifest = Code(string.Empty);
        ScrollViewer manifestScroll = new()
        {
            Content = manifest,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        instanceCombo.SelectionChanged += (object? _, SelectionChangedEventArgs _) =>
        {
            RefreshManifest();
        };
        versionBox.TextChanged += (object? _, TextChangedEventArgs _) =>
        {
            RefreshManifest();
        };
        includeConfig.Click += (object? _, RoutedEventArgs _) =>
        {
            RefreshManifest();
        };
        includeMods.Click += (object? _, RoutedEventArgs _) =>
        {
            RefreshManifest();
        };
        includeJava.Click += (object? _, RoutedEventArgs _) =>
        {
            RefreshManifest();
        };
        silent.Click += (object? _, RoutedEventArgs _) =>
        {
            RefreshManifest();
        };
        RefreshManifest();
        Button button = GhostButton(L.T("Creator.Modpack.Browse", "选择目录"));
        button.Click += async (object? _, RoutedEventArgs _) =>
        {
            IStorageProvider storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage != null)
            {
                IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = L.T("Creator.Modpack.OutputPick.Title", "选择输出目录"),
                    AllowMultiple = false
                });
                if (folders.Count > 0)
                {
                    outputBox.Text = folders[0].Path.LocalPath;
                    Shell.Toast(L.T("Creator.Modpack.Toast.OutputPicked", "已选择输出目录"));
                }
            }
        };
        Button startButton = PrimaryButton(solo ? L.T("Creator.Solo.Start", "开始打包") : L.T("Creator.Modpack.Start", "导出整合包"));
        Button installButton = GhostButton(L.T("Creator.Modpack.Install", "安装 .mrpack"));
        bool busy = false;
        startButton.Click += async (object? _, RoutedEventArgs _) =>
        {
            if (!busy)
            {
                busy = true;
                startButton.IsEnabled = false;
                installButton.IsEnabled = false;
                progress.Value = 0.0;
                status.Text = (solo ? L.T("Creator.Solo.Status.Busy", "正在收集实例文件并写入 NekoSolo 包…") : L.T("Creator.Modpack.Status.Busy", "正在准备整合包导出…"));
                if (instances.Count != 0)
                {
                    int index = Math.Clamp(instanceCombo.SelectedIndex, 0, instances.Count - 1);
                    InstanceInfo instance = instances[index];
                    string version = (string.IsNullOrWhiteSpace(versionBox.Text) ? "1.0.0" : versionBox.Text.Trim());
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    try
                    {
                        if (solo)
                        {
                            IStorageProvider storage = TopLevel.GetTopLevel(this)?.StorageProvider;
                            if (storage == null)
                            {
                                status.Text = L.T("Creator.Common.NoStorage", "文件对话框不可用");
                                Shell.Toast(status.Text);
                            }
                            else
                            {
                                IStorageFile file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
                                {
                                    Title = L.T("Creator.Solo.Export.Title", "生成 NekoSolo 包"),
                                    SuggestedFileName = SanitizeFileName(instance.Name) + "-solo-" + version + ".zip",
                                    DefaultExtension = "zip",
                                    FileTypeChoices = new FilePickerFileType[1]
                                    {
                                        new FilePickerFileType(L.T("Creator.Solo.Export.FileType", "NekoSolo 整合包"))
                                        {
                                            Patterns = new string[1] { "*.zip" }
                                        }
                                    }
                                });
                                if (file == null)
                                {
                                    status.Text = L.T("Creator.Common.Cancelled", "已取消");
                                }
                                else
                                {
                                    (bool Ok, string Path, int Files, long Size, string Message) result = await CreateSoloPackageAsync(instance, version, file.Path.LocalPath, includeJava.IsChecked == true, silent.IsChecked == true, progress, status);
                                    stopwatch.Stop();
                                    if (!result.Ok)
                                    {
                                        status.Text = result.Message;
                                        Shell.Toast(result.Message);
                                    }
                                    else
                                    {
                                        progress.Value = 100.0;
                                        status.Text = L.T("Creator.Solo.Status.Done", "已生成 NekoSolo 包：{0}（{1} 个文件，{2}，{3} ms）", result.Path, result.Files, FormatSize(result.Size), stopwatch.ElapsedMilliseconds);
                                        Shell.Toast(L.T("Creator.Solo.Toast.Done", "NekoSolo 包已生成：{0}", result.Path));
                                        AppLog("INFO", $"创作中心生成 NekoSolo 包：{result.Path}（{result.Files} 个文件）");
                                    }
                                }
                            }
                        }
                        else
                        {
                            (bool Ok, string OutputPath, string Message) outcome = await TryExportModpackWithCoreAsync(instance, version, outputBox.Text, progress, status);
                            stopwatch.Stop();
                            if (!outcome.Ok)
                            {
                                status.Text = L.T("Creator.Modpack.Status.Failed", "导出失败：{0}", outcome.Message);
                                Shell.Toast(status.Text);
                            }
                            else
                            {
                                progress.Value = 100.0;
                                long size = (File.Exists(outcome.OutputPath) ? new FileInfo(outcome.OutputPath).Length : 0);
                                status.Text = L.T("Creator.Modpack.Status.Done", "已导出整合包：{0}（{1}，{2} ms）", outcome.OutputPath, FormatSize(size), stopwatch.ElapsedMilliseconds);
                                Shell.Toast(L.T("Creator.Modpack.Toast.Done", "已导出整合包：{0}", outcome.OutputPath));
                            }
                        }
                        return;
                    }
                    catch (Exception ex)
                    {
                        Exception ex2 = ex;
                        status.Text = L.T("Creator.Common.OperationFailed", "操作失败：{0}", ex2.Message);
                        Shell.Toast(status.Text);
                        AppLog("ERROR", "创作中心操作失败：" + ex2.Message);
                        return;
                    }
                    finally
                    {
                        busy = false;
                        startButton.IsEnabled = true;
                        installButton.IsEnabled = true;
                    }
                }
                status.Text = L.T("Creator.Modpack.NoInstances", "本机还没有 Minecraft 实例。");
                busy = false;
                startButton.IsEnabled = true;
                installButton.IsEnabled = true;
            }
        };
        installButton.Click += async (object? _, RoutedEventArgs _) =>
        {
            if (!busy)
            {
                IStorageProvider storage = TopLevel.GetTopLevel(this)?.StorageProvider;
                if (storage == null)
                {
                    Shell.Toast(L.T("Creator.Common.NoStorage", "文件对话框不可用"));
                }
                else
                {
                    IReadOnlyList<IStorageFile> picks = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                    {
                        Title = L.T("Creator.Modpack.Install.Title", "选择 .mrpack 整合包"),
                        AllowMultiple = false,
                        FileTypeFilter = new FilePickerFileType[1]
                        {
                            new FilePickerFileType(L.T("Creator.Modpack.Install.FileType", "Modrinth 整合包"))
                            {
                                Patterns = new string[1] { "*.mrpack" }
                            }
                        }
                    });
                    if (picks.Count != 0)
                    {
                        string mrpackPath = picks[0].Path.LocalPath;
                        busy = true;
                        startButton.IsEnabled = false;
                        installButton.IsEnabled = false;
                        progress.Value = 0.0;
                        Stopwatch stopwatch = Stopwatch.StartNew();
                        status.Text = L.T("Creator.Modpack.Install.Running", "正在安装 .mrpack…");
                        DownloadTaskHandle task = DownloadTaskService.Start(L.T("Creator.Modpack.Install.TaskTitle", "整合包导入 · {0}", Path.GetFileName(mrpackPath)), L.T("Creator.Modpack.Install.Running", "正在安装 .mrpack…"));
                        try
                        {
                            string gameRoot = YamlRead.EnsureGameDirectory();
                            if (string.IsNullOrWhiteSpace(gameRoot))
                            {
                                task.Fail(L.T("Creator.Modpack.Install.NoGameDir", "尚未配置 .minecraft 目录，请先在「设置 → 游戏」选择游戏目录"));
                                status.Text = L.T("Creator.Modpack.Install.NoGameDir", "尚未配置 .minecraft 目录，请先在「设置 → 游戏」选择游戏目录");
                                Shell.Toast(status.Text);
                            }
                            else
                            {
                                Directory.CreateDirectory(gameRoot);
                                ModpackRequirements requirements = await ModrinthBundleInstall.ReadRequirementsAsync(mrpackPath);
                                if (requirements != null)
                                {
                                    status.Text = L.T("Creator.Modpack.Install.Requirements", "检测到 {0} {1} / Minecraft {2}，开始安装…", requirements.LoaderName, requirements.LoaderVersion, requirements.MinecraftVersion);
                                }
                                Progress<ModpackInstallProgress> reporter = new Progress<ModpackInstallProgress>((ModpackInstallProgress report) =>
                                {
                                    task.Report(Math.Clamp(report.Percentage, 0.0, 100.0), (report.TotalFiles > 0) ? L.T("Creator.Modpack.Install.Progress2", "{0}（{1}/{2} 个文件）", report.Phase, report.CompletedFiles, report.TotalFiles) : report.Phase);
                                    Dispatcher.UIThread.Post(() =>
                                    {
                                        progress.Value = Math.Clamp(report.Percentage, 0.0, 100.0);
                                        status.Text = ((report.TotalFiles > 0) ? L.T("Creator.Modpack.Install.Progress2", "{0}（{1}/{2} 个文件）", report.Phase, report.CompletedFiles, report.TotalFiles) : report.Phase);
                                    });
                                });
                                ModpackInstallOptions options = new ModpackInstallOptions
                                {
                                    InstallLoader = true,
                                    ParallelDownloads = 4,
                                    JavaExecutable = YamlRead.JavaExecutable
                                };
                                ModpackInstallResult result = await ModrinthBundleInstall.InstallAsync(mrpackPath, gameRoot, options, reporter);
                                stopwatch.Stop();
                                if (!result.Success)
                                {
                                    progress.Value = 0.0;
                                    string errors = string.Join("； ", result.Errors.Take(MaxErrorMessages));
                                    task.Fail(errors);
                                    status.Text = L.T("Creator.Modpack.Install.FailedDetail", "安装失败：{0}", errors);
                                    Shell.Toast(status.Text);
                                    AppLog("ERROR", "创作中心安装 .mrpack 失败：" + errors);
                                }
                                else
                                {
                                    task.Complete();
                                    progress.Value = 100.0;
                                    string warnings = ((result.Warnings.Count > 0) ? L.T("Creator.Modpack.Install.Warnings", "（{0} 条提示）", result.Warnings.Count) : string.Empty);
                                    status.Text = L.T("Creator.Modpack.Install.Done", "已安装到 {0}：写入 {1} 个、下载 {2} 个、复用 {3} 个（{4} ms）{5}", gameRoot, result.InstalledFiles, result.DownloadedFiles, result.ReusedFiles, stopwatch.ElapsedMilliseconds, warnings);
                                    Shell.Toast(L.T("Creator.Modpack.Install.Toast", "整合包已安装到 {0}（{1} 个文件）", gameRoot, result.InstalledFiles));
                                    AppLog("INFO", $"创作中心安装 .mrpack：{mrpackPath} → {gameRoot}（写入 {result.InstalledFiles}、下载 {result.DownloadedFiles}、复用 {result.ReusedFiles}）");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Exception ex2 = ex;
                            task.Fail(ex2.Message);
                            status.Text = L.T("Creator.Modpack.Install.Failed", "安装 .mrpack 失败：{0}", ex2.Message);
                            Shell.Toast(status.Text);
                            AppLog("ERROR", "创作中心安装 .mrpack 失败：" + ex2.Message);
                        }
                        finally
                        {
                            busy = false;
                            startButton.IsEnabled = true;
                            installButton.IsEnabled = true;
                        }
                    }
                }
            }
        };
        var outputRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        outputRow.Children.Add(outputBox);
        outputRow.Children.Add(button);

        var rows = new List<Control>
        {
            ToolUi.Row(L.T("Creator.Modpack.Instance", "实例"), instanceCombo),
            ToolUi.Row(L.T("Creator.Modpack.Version", "版本号"), versionBox),
        };
        if (!solo)
        {
            rows.Insert(1, ToolUi.Row(L.T("Creator.Modpack.Format", "格式"), field));
            rows.Add(ToolUi.Row(L.T("Creator.Modpack.Output", "输出目录"), outputRow));
            rows.Add(ToolUi.Row(L.T("Creator.Modpack.IncludeConfig", "包含 config 与 options 覆盖文件"), includeConfig));
            rows.Add(ToolUi.Row(L.T("Creator.Modpack.IncludeMods", "包含模组清单"), includeMods));
        }
        else
        {
            rows.Add(ToolUi.Row(L.T("Creator.Solo.IncludeJava", "内嵌 Java 运行时"), includeJava));
        }
        rows.Add(ToolUi.Row(L.T("Creator.Solo.Silent", "玩家双击后静默启动"), silent));

        var settingsGroup = ToolUi.Group(
            solo ? "IconFluentDocument" : "IconFluentBox",
            solo ? L.T("Creator.Solo.Card.Title", "NekoSolo 设置") : L.T("Creator.Modpack.Card.Title", "整合包设置"),
            L.T("Creator.Modpack.Card.Hint", "按实例导出，可勾选包含内容"),
            rows.ToArray());

        manifestScroll.MaxHeight = 320;
        var manifestGroup = ToolUi.Group("IconFluentDocument",
            solo ? L.T("Creator.Solo.Manifest", "打包清单") : "modrinth.index.json",
            L.T("Creator.Modpack.Manifest", "导出前预览生成内容"),
            ToolUi.SoftBlock(manifestScroll));

        var actionRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        if (!solo) actionRow.Children.Add(installButton);
        actionRow.Children.Add(startButton);

        progress.Margin = new Thickness(0, 6, 0, 0);
        var actionChildren = new List<Control> { progress, status };
        if (!solo)
        {
            actionChildren.Add(Caption(L.T("Creator.Modpack.Install.Hint", "安装 .mrpack：选择本地整合包，把其中的 overrides 解压进上面所选的实例目录。")));
        }
        actionChildren.Add(actionRow);

        var actionGroup = ToolUi.Group("IconDownload",
            solo ? L.T("Creator.Solo.Actions.Title", "打包") : L.T("Creator.Modpack.Actions.Title", "导出与安装"),
            null,
            actionChildren.ToArray());

        return ToolUi.Page(settingsGroup, manifestGroup, actionGroup);
        void RefreshManifest()
        {
            if (instances.Count == 0)
            {
                manifest.Text = L.T("Creator.Modpack.NoInstances", "本机还没有 Minecraft 实例。");
            }
            else
            {
                int index = Math.Clamp(instanceCombo.SelectedIndex, 0, instances.Count - 1);
                InstanceInfo instanceInfo = instances[index];
                string text = (string.IsNullOrWhiteSpace(versionBox.Text) ? "1.0.0" : versionBox.Text.Trim());
                if (solo)
                {
                    string text2 = JsonSerializer.Serialize(BuildSoloManifest(instanceInfo, text, silent.IsChecked == true, includeJava.IsChecked == true), new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });
                    manifest.Text = "# NekoSolo payload layout\n#   manifest.json\n#   icon.png\n#   files/<launcher>\n#   minecraft/versions/<versionId>/<instance files>\n#   jre/<optional Java runtime>\n\n" + text2;
                }
                else
                {
                    StringBuilder stringBuilder = new StringBuilder();
                    stringBuilder.Append("  \"files\": [");
                    if (includeMods.IsChecked == true)
                    {
                        string[] array = RealModFiles(instanceInfo).Take(MaxPreviewMods).ToArray();
                        for (int i = 0; i < array.Length; i++)
                        {
                            stringBuilder.Append((i == 0) ? "\n" : ",\n");
                            StringBuilder stringBuilder2 = stringBuilder;
                            StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(27, 1, stringBuilder2);
                            handler.AppendLiteral("    { \"path\": ");
                            handler.AppendFormatted(JsonSerializer.Serialize("mods/" + array[i]));
                            handler.AppendLiteral(", \"size\": 0 }");
                            stringBuilder2.Append(ref handler);
                        }
                        if (array.Length != 0)
                        {
                            stringBuilder.Append("\n  ");
                        }
                    }
                    stringBuilder.Append(']');
                    manifest.Text = "{\n  \"formatVersion\": 1,\n  \"game\": \"minecraft\",\n  \"versionId\": " + JsonSerializer.Serialize(text) + ",\n  \"name\": " + JsonSerializer.Serialize(instanceInfo.Name) + "\n" + ((includeConfig.IsChecked == true) ? "  ,\"overrides\": \"overrides\",\n" : string.Empty) + stringBuilder?.ToString() + "\n}";
                }
            }
        }
    }

    private async Task<(bool Ok, string OutputPath, string Message)> TryExportModpackWithCoreAsync(InstanceInfo instance, string version, string? outputDirectory, ProgressBar progressBar, TextBlock status)
    {
        try
        {
            string contentDirectory = FindInstanceContentDirectory(instance);
            if (contentDirectory == null)
            {
                return (Ok: false, OutputPath: string.Empty, Message: L.T("Creator.Modpack.Reason.NoInstanceDir", "本机没有对应的 .minecraft/versions 目录"));
            }
            string target = (outputDirectory ?? string.Empty).Trim();
            if (target.Length == 0)
            {
                return (Ok: false, OutputPath: string.Empty, Message: L.T("Creator.Modpack.Reason.NoOutputDir", "输出目录为空"));
            }
            Directory.CreateDirectory(target);
            string outputPath = Path.Combine(target, SanitizeFileName(instance.Name) + "-" + version + ".mrpack");
            Progress<ModpackExportProgress> reporter = new Progress<ModpackExportProgress>((ModpackExportProgress report) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    double value = ((report.Total > 0) ? Math.Clamp((double)report.Current * 100.0 / (double)report.Total, 0.0, 100.0) : 0.0);
                    progressBar.Value = value;
                    status.Text = L.T("Creator.Modpack.Status.Progress", "{0}（{1}/{2}）", report.Phase, report.Current, report.Total);
                });
            });
            ModpackExportOptions modpackExportOptions = new ModpackExportOptions();
            modpackExportOptions.PackName = instance.Name;
            modpackExportOptions.PackVersion = version;
            modpackExportOptions.MinecraftVersion = instance.Version;
            modpackExportOptions.LoaderName = instance.Loader;
            modpackExportOptions.Description = L.T("Creator.Modpack.Description", "{0} · 由 Mofumofu 创作中心导出", instance.Name);
            modpackExportOptions.ResolveModrinthLinks = true;
            modpackExportOptions.IncludedPaths = new List<string>();
            ModpackExportResult result = await ModrinthBundleMake.ExportAsync(modpackExportOptions, contentDirectory, outputPath, reporter).ConfigureAwait(continueOnCapturedContext: true);
            if (string.IsNullOrWhiteSpace(result.OutputPath))
            {
                return (Ok: false, OutputPath: string.Empty, Message: L.T("Creator.Modpack.Reason.NoOutputFile", "打包器没有返回输出文件"));
            }
            AppLog("INFO", "创作中心导出整合包：" + result.OutputPath);
            return (Ok: true, OutputPath: result.OutputPath, Message: string.Empty);
        }
        catch (Exception ex)
        {
            Exception ex2 = ex;
            AppLog("WARN", "创作中心导出整合包失败：" + ex2.Message);
            return (Ok: false, OutputPath: string.Empty, Message: ex2.Message);
        }
    }

    private static Dictionary<string, object?> BuildSoloManifest(InstanceInfo instance, string version, bool silent, bool hasJava)
    {
        string value = SlugOf(instance.Name);
        string selectedAccountName = LocalSettings.Current.SelectedAccountName;
        Dictionary<string, object> dictionary = new Dictionary<string, object>();
        dictionary["format"] = 2;
        dictionary["packId"] = value;
        dictionary["packName"] = instance.Name;
        dictionary["packVersion"] = version;
        dictionary["author"] = (string.IsNullOrWhiteSpace(selectedAccountName) ? "Mofumofu" : selectedAccountName);
        dictionary["description"] = L.T("Creator.Solo.PackDescription", "{0} · NekoSolo 整合包", instance.Name);
        dictionary["mcVersion"] = instance.Version;
        dictionary["loaderName"] = instance.Loader;
        dictionary["loaderVersion"] = string.Empty;
        dictionary["versionId"] = instance.Name;
        dictionary["silent"] = silent;
        dictionary["hasJava"] = hasJava;
        dictionary["iconPath"] = "icon.png";
        dictionary["updateLink"] = string.Empty;
        return dictionary;
    }

    private static string SlugOf(string value)
    {
        StringBuilder stringBuilder = new StringBuilder(value.Length);
        string text = value.ToLowerInvariant();
        foreach (char c in text)
        {
            if (c < '\u0080' && char.IsLetterOrDigit(c))
            {
                stringBuilder.Append(c);
            }
            else if ((c == ' ' || c == '-' || c == '_') ? true : false)
            {
                stringBuilder.Append('-');
            }
        }
        string text2 = stringBuilder.ToString().Trim('-');
        while (text2.Contains("--", StringComparison.Ordinal))
        {
            text2 = text2.Replace("--", "-", StringComparison.Ordinal);
        }
        return (text2.Length == 0) ? "mofumofu-pack" : text2;
    }

    private async Task<(bool Ok, string Path, int Files, long Size, string Message)> CreateSoloPackageAsync(InstanceInfo instance, string version, string targetZipPath, bool hasJava, bool silent, ProgressBar progressBar, TextBlock status)
    {
        string? contentDirectory = FindInstanceContentDirectory(instance);
        if (contentDirectory is null)
            return (false, string.Empty, 0, 0, L.T("Creator.Solo.Status.NoInstanceDir", "找不到实例目录，无法生成 NekoSolo 包"));
        try
        {
            if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("NekoSolo 安装包需要在 Windows 启动器中导出。");
            string versionId = Path.GetFileName(Path.TrimEndingDirectorySeparator(contentDirectory));
            var profile = new MofuCore.Launch.VersionLoader().Load(YamlRead.GameDirectory, versionId);
            string? javaHome = null;
            if (hasJava)
            {
                string? java = await MofuCore.Java.JavaPathSearch.FindJavaExecutableAsync(
                    string.IsNullOrWhiteSpace(instance.JavaPath) ? YamlRead.JavaExecutable : instance.JavaPath,
                    requiredMajorVersion: profile.RequiredJavaMajorVersion,
                    runtimeDirectory: MofuCore.Java.JavaDownload.GetRuntimeDirectory(YamlRead.GameDirectory));
                if (java is null) throw new InvalidOperationException("未找到可内嵌的 Java，请先安装 Java 或取消内嵌选项。");
                javaHome = Path.GetDirectoryName(Path.GetDirectoryName(java));
            }
            var manifest = BuildSoloManifest(instance, version, silent, javaHome is not null);
            manifest["versionId"] = versionId;
            string manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            byte[] icon;
            using (WriteableBitmap bitmap = CreatePackIconBitmap(ThemeTokens.Color("NekoAccentBrush"), 64))
            using (var memory = new MemoryStream())
            {
                bitmap.Save(memory);
                icon = memory.ToArray();
            }
            IProgress<(int Done, int Total)> reporter = new Progress<(int Done, int Total)>(value =>
            {
                progressBar.Value = value.Done * 100.0 / value.Total;
                status.Text = L.T("Creator.Solo.Status.Writing", "正在写入 NekoSolo 包…{0}/{1}", value.Done, value.Total);
            });
            string launcher = Environment.ProcessPath ?? throw new InvalidOperationException("找不到当前启动器。");
            string gameDirectory = YamlRead.GameDirectory;
            var result = await Task.Run(() => SoloPackageBuilder.Build(targetZipPath, launcher, gameDirectory,
                versionId, manifestJson, icon, javaHome, reporter));
            return (true, targetZipPath, result.Files, result.Size, string.Empty);
        }
        catch (Exception ex)
        {
            AppLog("ERROR", "创作中心生成 NekoSolo 包失败：" + ex.Message);
            return (false, string.Empty, 0, 0, L.T("Creator.Solo.Status.Failed", "生成 NekoSolo 包失败：{0}", ex.Message));
        }
    }

    private static List<string> ParseStyleFiles(TextBox stylesBox)
    {
        List<string> list = new List<string>();
        string[] array = (stylesBox.Text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (string text in array)
        {
            string text2 = SanitizeFileName(text.Trim());
            if (text2.Length != 0)
            {
                if (!text2.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
                {
                    text2 += ".css";
                }
                list.Add(text2);
            }
        }
        return list;
    }

    private static string PluginEntryTemplate(string pluginName)
    {
        string text = (string.IsNullOrWhiteSpace(pluginName) ? "My plugin" : pluginName);
        return "// Mofumofu plugin entry: the launcher calls the default export when the plugin loads.\n// Plugin: " + text + "\nexport default function activate(api) {\n  const { registerWidget, log } = api;\n\n  registerWidget({\n    id: \"hello\",\n    title: \"" + (string.IsNullOrWhiteSpace(pluginName) ? "Hello" : pluginName.Replace("\"", "'")) + "\",\n    description: \"My first Mofumofu widget\",\n    render: () => null,\n  });\n\n  log(\"plugin activated\");\n}\n";
    }

    private static int CreatePluginScaffold(string directory, string yamlText, string entryName, string iconName, IReadOnlyList<string> styleFiles, string pluginName, Color accent)
    {
        Directory.CreateDirectory(directory);
        string text = Path.Combine(directory, "assets");
        Directory.CreateDirectory(text);
        UTF8Encoding encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        File.WriteAllText(Path.Combine(directory, "plugin.yaml"), yamlText, encoding);
        File.WriteAllText(Path.Combine(directory, entryName), PluginEntryTemplate(pluginName), encoding);
        File.WriteAllText(Path.Combine(text, "README.txt"), "Put static assets used by this plugin here (images, css, ...).\n", encoding);
        if (!string.IsNullOrWhiteSpace(iconName) && iconName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
        {
            using WriteableBitmap writeableBitmap = CreatePackIconBitmap(accent, 64);
            using FileStream stream = File.Create(Path.Combine(directory, iconName));
            writeableBitmap.Save(stream);
        }
        foreach (string styleFile in styleFiles)
        {
            File.WriteAllText(Path.Combine(directory, styleFile), "/* Custom styles injected globally by the launcher. */\n", encoding);
        }
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Count();
    }

    private static (int Files, long Size) ZipDirectoryContents(string sourceDirectory, string zipPath)
    {
        string directoryName = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrWhiteSpace(directoryName))
        {
            Directory.CreateDirectory(directoryName);
        }
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }
        int num = 0;
        using (ZipArchive zipArchive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (string item in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                string entryName = Path.GetRelativePath(sourceDirectory, item).Replace('\\', '/');
                ZipArchiveEntry zipArchiveEntry = zipArchive.CreateEntry(entryName, CompressionLevel.Optimal);
                using FileStream fileStream = File.OpenRead(item);
                using Stream destination = zipArchiveEntry.Open();
                fileStream.CopyTo(destination);
                num++;
            }
        }
        return (Files: num, Size: new FileInfo(zipPath).Length);
    }

    private static string? FindInstanceContentDirectory(InstanceInfo instance)
    {
        try
        {
            string gameDirectory = YamlRead.GameDirectory;
            if (string.IsNullOrWhiteSpace(gameDirectory))
            {
                return null;
            }
            string[] source = new string[2]
            {
                Path.Combine(gameDirectory, "versions", instance.Name),
                Path.Combine(gameDirectory, "versions", instance.Version)
            };
            return source.FirstOrDefault(Directory.Exists);
        }
        catch
        {
            return null;
        }
    }

    private static List<InstanceInfo> RealInstances()
    {
        List<InstanceInfo> list = new List<InstanceInfo>();
        try
        {
            string text = (YamlRead.GameDirectory ?? string.Empty).Trim();
            if (text.Length == 0 || !Directory.Exists(text))
            {
                return list;
            }
            string path = Path.Combine(text, "versions");
            if (!Directory.Exists(path))
            {
                return list;
            }
            int maximumMemoryMb = YamlRead.LoadGlobalLaunchSettings().MaximumMemoryMb;
            string[] directories = Directory.GetDirectories(path);
            foreach (string text2 in directories)
            {
                string fileName = Path.GetFileName(text2);
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    continue;
                }
                string path2 = Path.Combine(text2, fileName + ".json");
                if (File.Exists(path2))
                {
                    try
                    {
                        MofuCore.Launch.LaunchProfile launchProfile = new MofuCore.Launch.VersionLoader().Load(text, fileName);
                        string text3 = (launchProfile.InheritsFrom + " " + launchProfile.SourceId + " " + fileName).ToLowerInvariant();
                        string loader;
                        if (text3.Contains("neoforge"))
                        {
                            loader = "NeoForge";
                        }
                        else if (text3.Contains("quilt"))
                        {
                            loader = "Quilt";
                        }
                        else if (text3.Contains("fabric"))
                        {
                            loader = "Fabric";
                        }
                        else
                        {
                            loader = (text3.Contains("forge") ? "Forge" : "Vanilla");
                        }
                        string version = (string.IsNullOrWhiteSpace(launchProfile.InheritsFrom) ? fileName : launchProfile.InheritsFrom);
                        int count = realModNames(text2).Count;
                        list.Add(new InstanceInfo(fileName, fileName, version, loader, "avares://Mofumofu.Launcher/Assets/instance-icons/vanilla.png", File.GetLastWriteTime(path2).ToString("yyyy-MM-dd HH:mm"), string.Empty, YamlRead.JavaExecutable, maximumMemoryMb, count, 0));
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }
        catch (Exception)
        {
        }
        return list;
        static List<string> realModNames(string directory)
        {
            try
            {
                string path3 = Path.Combine(directory, "mods");
                if (!Directory.Exists(path3))
                {
                    string path4 = (YamlRead.GameDirectory ?? string.Empty).Trim();
                    path3 = Path.Combine(path4, "mods");
                }
                return Directory.Exists(path3) ? (from name in Directory.GetFiles(path3, "*.jar").Select(Path.GetFileName)
                    where name != null
                    select (name)).ToList() : new List<string>();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }
    }

    private static List<string> RealModFiles(InstanceInfo instance)
    {
        try
        {
            string text = FindInstanceContentDirectory(instance);
            string path = Path.Combine(text ?? string.Empty, "mods");
            if (!Directory.Exists(path))
            {
                string path2 = (YamlRead.GameDirectory ?? string.Empty).Trim();
                path = Path.Combine(path2, "mods");
            }
            return Directory.Exists(path) ? (from name in Directory.GetFiles(path, "*.jar").Select(Path.GetFileName)
                where name != null
                select (name)).ToList() : new List<string>();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }

    private static List<string> RealAccountNames()
    {
        List<string> list = new List<string>();
        try
        {
            MofuCore.Account.MinecraftTokenHandle minecraftTokenHandle = new MofuCore.Account.MinecraftTokenHandle();
            foreach (MofuCore.Account.MicrosoftAccount item in minecraftTokenHandle.LoadMicrosoftAccounts())
            {
                if (!string.IsNullOrWhiteSpace(item.Username))
                {
                    list.Add(item.Username);
                }
            }
            foreach (MofuCore.Account.AuthlibCredential item2 in minecraftTokenHandle.LoadAuthlibCredentials())
            {
                string text = (string.IsNullOrWhiteSpace(item2.ProfileName) ? item2.Username : item2.ProfileName);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    list.Add(text);
                }
            }
            foreach (string item3 in minecraftTokenHandle.LoadOfflineAccounts())
            {
                if (!string.IsNullOrWhiteSpace(item3))
                {
                    list.Add(item3);
                }
            }
        }
        catch (Exception)
        {
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string SanitizeFileName(string name)
    {
        char[] invalidFileNameChars = Path.GetInvalidFileNameChars();
        StringBuilder stringBuilder = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            stringBuilder.Append(invalidFileNameChars.Contains(c) ? '_' : c);
        }
        return stringBuilder.ToString().Trim();
    }

    private static void AppLog(string type, string message)
    {
        try
        {
            LogWrite.AddLogAsync(type, message);
        }
        catch
        {
        }
    }

    private async void OnExportLogClick(object? sender, RoutedEventArgs e)
    {
        IStorageProvider storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null)
        {
            Shell.Toast(L.T("Creator.Common.NoStorage", "文件对话框不可用"));
            return;
        }
        try
        {
            IStorageFile file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = L.T("Creator.Log.Export", "导出启动器日志"),
                SuggestedFileName = $"Mofumofu-{DateTime.Now:yyyyMMdd-HHmmss}.log",
                DefaultExtension = "log",
                FileTypeChoices = new FilePickerFileType[1]
                {
                    new FilePickerFileType(L.T("Creator.Log.FileType", "日志文件"))
                    {
                        Patterns = new string[2] { "*.log", "*.txt" }
                    }
                }
            });
            if (file != null)
            {
                string path = file.Path.LocalPath;
                Shell.Toast((await LogWrite.ExportCurrentAsync(path)) ? L.T("Creator.Log.Status.Exported", "日志已导出：{0}", path) : L.T("Creator.Log.Status.NoLog", "当前还没有日志文件"));
            }
        }
        catch (Exception ex)
        {
            Shell.Toast(L.T("Creator.Log.Toast.ExportFailed", "导出日志失败：{0}", ex.Message));
        }
    }


    private static void CloseActiveDialog(object? source)
    {
        if (source is Visual visual)
        {
            visual.FindAncestorOfType<MofuDialogHost>(includeSelf: true)?.Manager?.DismissDialog();
        }
    }

    private void BuildPluginPage(StackPanel content)
    {
        TextBox idBox = Input("my-plugin", "my-plugin");
        idBox.Width = 220;
        TextBox nameBox = Input(L.T("Creator.Plugin.NameDefault", "我的插件"), L.T("Creator.Plugin.NamePlaceholder", "显示名称"));
        nameBox.Width = 220;
        TextBox versionBox = Input("1.0.0");
        versionBox.Width = 160;
        TextBox apiBox = Input("1");
        apiBox.Width = 160;
        TextBox authorBox = Input("Mofumofu");
        authorBox.Width = 220;
        TextBox descBox = Input(L.T("Creator.Plugin.DescDefault", "在桌面加一个小组件"), L.T("Creator.Plugin.DescPlaceholder", "一句话简介"));
        descBox.Width = 280;
        TextBox entryBox = Input("index.js");
        entryBox.Width = 220;
        TextBox iconBox = Input("icon.png");
        iconBox.Width = 220;
        TextBox stylesBox = Input("theme.css", L.T("Creator.Plugin.StylesPlaceholder", "逗号分隔，可留空"));
        stylesBox.Width = 280;
        TextBlock idError = Caption(string.Empty);
        (string Key, string Label, ToggleSwitch Box)[] permissions = new (string, string, ToggleSwitch)[5]
        {
            ("network", L.T("Creator.Plugin.Perm.Network", "网络访问（下载 / 请求 API）"), ToolUi.Switch()),
            ("filesystem", L.T("Creator.Plugin.Perm.FileSystem", "文件读写（配置 / 日志）"), ToolUi.Switch()),
            ("notifications", L.T("Creator.Plugin.Perm.Notifications", "系统通知"), ToolUi.Switch()),
            ("commands", L.T("Creator.Plugin.Perm.Commands", "注册启动器命令"), ToolUi.Switch()),
            ("widgets", L.T("Creator.Plugin.Perm.Widgets", "注册桌面小组件"), ToolUi.Switch(isChecked: true))
        };
        SelectableTextBlock yaml = Code(string.Empty);
        TextBlock status = Caption(L.T("Creator.Plugin.Status.Init", "id 只允许字母、数字、连字符与下划线；创建后不可修改。"));
        TextBox[] array = new TextBox[9] { idBox, nameBox, versionBox, apiBox, authorBox, descBox, entryBox, iconBox, stylesBox };
        foreach (TextBox textBox in array)
        {
            textBox.TextChanged += (object? _, TextChangedEventArgs _) =>
            {
                Refresh();
            };
        }
        (string, string, ToggleSwitch)[] array2 = permissions;
        for (int num = 0; num < array2.Length; num++)
        {
            ToggleSwitch item = array2[num].Item3;
            item.Click += (object? _, RoutedEventArgs _) =>
            {
                Refresh();
            };
        }
        Refresh();
        Button create = PrimaryButton(L.T("Creator.Plugin.Create", "创建插件骨架"));
        Button package = GhostButton(L.T("Creator.Plugin.Package", "打包 .nekoex"));
        Button button = GhostButton(L.T("Creator.Plugin.Copy", "复制 plugin.yaml"));
        Button button2 = GhostButton(L.T("Creator.Plugin.OpenFolder", "打开插件目录"));
        string createdPluginDirectory = null;
        button2.Click += (object? _, RoutedEventArgs _) =>
        {
            OpenFolder(createdPluginDirectory ?? PluginRoot);
        };
        create.Click += async (object? _, RoutedEventArgs _) =>
        {
            string id = (idBox.Text ?? string.Empty).Trim();
            if (ValidateId(id).Length > 0)
            {
                Shell.Toast(L.T("Creator.Plugin.Toast.FixId", "请先修正插件 id"));
            }
            else
            {
                IStorageProvider storage = TopLevel.GetTopLevel(this)?.StorageProvider;
                if (storage == null)
                {
                    Shell.Toast(L.T("Creator.Common.NoStorage", "文件对话框不可用"));
                }
                else
                {
                    IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
                    {
                        Title = L.T("Creator.Plugin.Create.Title", "选择插件创建位置"),
                        AllowMultiple = false
                    });
                    if (folders.Count != 0)
                    {
                        try
                        {
                            create.IsEnabled = false;
                            Stopwatch stopwatch = Stopwatch.StartNew();
                            string parent = folders[0].Path.LocalPath;
                            string target = Path.Combine(parent, id);
                            string entryName = (string.IsNullOrWhiteSpace(Value(entryBox)) ? "index.js" : Value(entryBox));
                            string iconName = (string.IsNullOrWhiteSpace(Value(iconBox)) ? "icon.png" : Value(iconBox));
                            string yamlText = yaml.Text ?? string.Empty;
                            List<string> styles = ParseStyleFiles(stylesBox);
                            string pluginName = Value(nameBox);
                            Color accent = ThemeTokens.Color("NekoAccentBrush");
                            int fileCount = await Task.Run(() => CreatePluginScaffold(target, yamlText, entryName, iconName, styles, pluginName, accent));
                            stopwatch.Stop();
                            createdPluginDirectory = target;
                            status.Text = L.T("Creator.Plugin.Status.Created", "已创建插件骨架：{0}（{1} 个文件，{2} ms）", target, fileCount, stopwatch.ElapsedMilliseconds);
                            Shell.Toast(L.T("Creator.Plugin.Toast.Created", "插件骨架已创建：{0}", target));
                            AppLog("INFO", $"创作中心创建插件骨架：{target}（{fileCount} 个文件）");
                        }
                        catch (Exception ex)
                        {
                            Exception ex2 = ex;
                            status.Text = L.T("Creator.Plugin.Status.CreateFailed", "创建插件骨架失败：{0}", ex2.Message);
                            Shell.Toast(status.Text);
                        }
                        finally
                        {
                            create.IsEnabled = true;
                        }
                    }
                }
            }
        };
        package.Click += async (object? _, RoutedEventArgs _) =>
        {
            if (createdPluginDirectory == null || !Directory.Exists(createdPluginDirectory))
            {
                Shell.Toast(L.T("Creator.Plugin.Toast.NeedScaffold", "请先创建插件骨架再打包"));
            }
            else
            {
                IStorageProvider storage = TopLevel.GetTopLevel(this)?.StorageProvider;
                if (storage == null)
                {
                    Shell.Toast(L.T("Creator.Common.NoStorage", "文件对话框不可用"));
                }
                else
                {
                    string id = (idBox.Text ?? string.Empty).Trim();
                    string version = (string.IsNullOrWhiteSpace(Value(versionBox)) ? "1.0.0" : Value(versionBox));
                    IStorageFile file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
                    {
                        Title = L.T("Creator.Plugin.Package.Title", "打包插件"),
                        SuggestedFileName = SanitizeFileName(id) + "-" + SanitizeFileName(version) + ".nekoex",
                        DefaultExtension = "nekoex",
                        FileTypeChoices = new FilePickerFileType[1]
                        {
                            new FilePickerFileType(L.T("Creator.Plugin.Package.FileType", "Mofumofu 插件包"))
                            {
                                Patterns = new string[1] { "*.nekoex" }
                            }
                        }
                    });
                    if (file != null)
                    {
                        try
                        {
                            package.IsEnabled = false;
                            Stopwatch stopwatch = Stopwatch.StartNew();
                            string target = file.Path.LocalPath;
                            var (fileCount, size) = await Task.Run(() => ZipDirectoryContents(createdPluginDirectory, target));
                            stopwatch.Stop();
                            status.Text = L.T("Creator.Plugin.Status.Packaged", "已打包：{0}（{1} 个文件，{2}，{3} ms）", target, fileCount, FormatSize(size), stopwatch.ElapsedMilliseconds);
                            Shell.Toast(L.T("Creator.Plugin.Toast.Packaged", "插件已打包：{0}", target));
                            AppLog("INFO", $"创作中心打包插件：{target}（{fileCount} 个文件）");
                        }
                        catch (Exception ex)
                        {
                            Exception ex2 = ex;
                            status.Text = L.T("Creator.Plugin.Status.PackageFailed", "打包插件失败：{0}", ex2.Message);
                            Shell.Toast(status.Text);
                        }
                        finally
                        {
                            package.IsEnabled = true;
                        }
                    }
                }
            }
        };
        button.Click += async (object? _, RoutedEventArgs _) =>
        {
            await CopyToClipboardAsync(yaml.Text ?? string.Empty);
            Shell.Toast(L.T("Creator.Plugin.Toast.Copied", "plugin.yaml 已复制到剪贴板"));
        };
        StackPanel stackPanel2 = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8.0,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        stackPanel2.Children.Add(button2);
        stackPanel2.Children.Add(package);
        stackPanel2.Children.Add(create);

        var fieldsGroup = ToolUi.Group("IconFluentPuzzle",
            L.T("Creator.Plugin.Card.Title", "清单字段"),
            L.T("Creator.Plugin.Card.Hint", "填写后会实时生成 plugin.yaml"),
            ToolUi.RowWithHintControl(L.T("Creator.Plugin.Id", "插件 id"), idError, idBox),
            ToolUi.Row(L.T("Creator.Plugin.Name", "名称"), nameBox),
            ToolUi.Row(L.T("Creator.Modpack.Version", "版本号"), versionBox),
            ToolUi.Row(L.T("Creator.Plugin.ApiVersion", "API 版本"), apiBox),
            ToolUi.Row(L.T("Creator.Plugin.Author", "作者"), authorBox),
            ToolUi.Row(L.T("Creator.Plugin.Desc", "简介"), descBox),
            ToolUi.Row(L.T("Creator.Plugin.Entry", "入口文件"), entryBox),
            ToolUi.Row(L.T("Creator.Plugin.Icon", "图标文件"), iconBox),
            ToolUi.Row(L.T("Creator.Plugin.Styles", "样式文件"),
                L.T("Creator.Plugin.StylesPlaceholder", "逗号分隔，可留空"), stylesBox));

        var permissionRows = new List<Control>();
        foreach (var (_, label, box) in permissions)
        {
            permissionRows.Add(ToolUi.Row(label, box));
        }
        var permissionsGroup = ToolUi.Group("IconFluentCheck",
            L.T("Creator.Plugin.Permissions", "权限声明"), null,
            permissionRows.ToArray());

        var yamlGroup = ToolUi.Group("IconFluentDocument", "plugin.yaml", null,
            ToolUi.SoftBlock(yaml),
            ToolUi.Actions(button));

        var actionsGroup = ToolUi.Group("IconFluentBox",
            L.T("Creator.Plugin.Actions.Title", "创建与打包"), null,
            stackPanel2,
            status);

        content.Children.Add(fieldsGroup);
        content.Children.Add(permissionsGroup);
        content.Children.Add(yamlGroup);
        content.Children.Add(actionsGroup);
        void Refresh()
        {
            string text = (idBox.Text ?? string.Empty).Trim();
            string text2 = ValidateId(text);
            idError.Text = text2;
            idError.Foreground = ((text2.Length > 0) ? ThemeTokens.Brush("NekoDangerBrush") : ThemeTokens.Brush("NekoMutedBrush"));
            StringBuilder stringBuilder = new StringBuilder();
            stringBuilder.AppendLine("# Mofumofu 插件清单");
            StringBuilder stringBuilder2 = stringBuilder;
            StringBuilder stringBuilder3 = stringBuilder2;
            StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(4, 1, stringBuilder2);
            handler.AppendLiteral("id: ");
            handler.AppendFormatted(Quote((text.Length > 0) ? text : "my-plugin"));
            stringBuilder3.AppendLine(ref handler);
            stringBuilder2 = stringBuilder;
            StringBuilder stringBuilder4 = stringBuilder2;
            handler = new StringBuilder.AppendInterpolatedStringHandler(6, 1, stringBuilder2);
            handler.AppendLiteral("name: ");
            handler.AppendFormatted(Quote(Value(nameBox)));
            stringBuilder4.AppendLine(ref handler);
            stringBuilder2 = stringBuilder;
            StringBuilder stringBuilder5 = stringBuilder2;
            handler = new StringBuilder.AppendInterpolatedStringHandler(9, 1, stringBuilder2);
            handler.AppendLiteral("version: ");
            handler.AppendFormatted(Quote(Value(versionBox)));
            stringBuilder5.AppendLine(ref handler);
            stringBuilder2 = stringBuilder;
            StringBuilder stringBuilder6 = stringBuilder2;
            handler = new StringBuilder.AppendInterpolatedStringHandler(12, 1, stringBuilder2);
            handler.AppendLiteral("apiVersion: ");
            handler.AppendFormatted(Quote(Value(apiBox)));
            stringBuilder6.AppendLine(ref handler);
            stringBuilder2 = stringBuilder;
            StringBuilder stringBuilder7 = stringBuilder2;
            handler = new StringBuilder.AppendInterpolatedStringHandler(8, 1, stringBuilder2);
            handler.AppendLiteral("author: ");
            handler.AppendFormatted(Quote(Value(authorBox)));
            stringBuilder7.AppendLine(ref handler);
            stringBuilder2 = stringBuilder;
            StringBuilder stringBuilder8 = stringBuilder2;
            handler = new StringBuilder.AppendInterpolatedStringHandler(13, 1, stringBuilder2);
            handler.AppendLiteral("description: ");
            handler.AppendFormatted(Quote(Value(descBox)));
            stringBuilder8.AppendLine(ref handler);
            stringBuilder2 = stringBuilder;
            StringBuilder stringBuilder9 = stringBuilder2;
            handler = new StringBuilder.AppendInterpolatedStringHandler(7, 1, stringBuilder2);
            handler.AppendLiteral("entry: ");
            handler.AppendFormatted(Quote(Value(entryBox)));
            stringBuilder9.AppendLine(ref handler);
            stringBuilder2 = stringBuilder;
            StringBuilder stringBuilder10 = stringBuilder2;
            handler = new StringBuilder.AppendInterpolatedStringHandler(6, 1, stringBuilder2);
            handler.AppendLiteral("icon: ");
            handler.AppendFormatted(Quote(Value(iconBox)));
            stringBuilder10.AppendLine(ref handler);
            string[] array4 = (from text3 in (stylesBox.Text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries)
                select text3.Trim() into text3
                where text3.Length > 0
                select text3).ToArray();
            if (array4.Length != 0)
            {
                stringBuilder.AppendLine("styles:");
                string[] array5 = array4;
                foreach (string value in array5)
                {
                    stringBuilder2 = stringBuilder;
                    StringBuilder stringBuilder11 = stringBuilder2;
                    handler = new StringBuilder.AppendInterpolatedStringHandler(4, 1, stringBuilder2);
                    handler.AppendLiteral("  - ");
                    handler.AppendFormatted(Quote(value));
                    stringBuilder11.AppendLine(ref handler);
                }
            }
            else
            {
                stringBuilder.AppendLine("styles: []");
            }
            stringBuilder.AppendLine("permissions:");
            (string, string, ToggleSwitch)[] array6 = permissions;
            for (int num4 = 0; num4 < array6.Length; num4++)
            {
                (string, string, ToggleSwitch) tuple2 = array6[num4];
                string item4 = tuple2.Item1;
                ToggleSwitch item5 = tuple2.Item3;
                stringBuilder2 = stringBuilder;
                StringBuilder stringBuilder12 = stringBuilder2;
                handler = new StringBuilder.AppendInterpolatedStringHandler(4, 2, stringBuilder2);
                handler.AppendLiteral("  ");
                handler.AppendFormatted(item4);
                handler.AppendLiteral(": ");
                handler.AppendFormatted(item5.IsChecked == true);
                stringBuilder12.AppendLine(ref handler);
            }
            yaml.Text = stringBuilder.ToString().TrimEnd();
        }
        static string ValidateId(string id)
        {
            if (id.Length == 0)
            {
                return string.Empty;
            }
            if (id.Length > 64)
            {
                return L.T("Creator.Plugin.Error.IdTooLong", "id 过长（上限 64 字符）");
            }
            if (!id.All((char ch) =>
            {
                bool flag = char.IsLetterOrDigit(ch);
                bool flag2 = flag;
                if (!flag2)
                {
                    bool flag3 = ((ch == '-' || ch == '_') ? true : false);
                    flag2 = flag3;
                }
                return flag2;
            }))
            {
                return L.T("Creator.Plugin.Error.IdChars", "只允许字母、数字、连字符与下划线");
            }
            return string.Empty;
        }
    }

    private static string NormalizeHex(string value)
    {
        var raw = value.Trim().TrimStart('#');
        if (raw.Length == 6 && raw.All(Uri.IsHexDigit)) return $"#{raw.ToUpperInvariant()}";
        if (raw.Length == 3 && raw.All(Uri.IsHexDigit))
        {
            return $"#{raw[0]}{raw[0]}{raw[1]}{raw[1]}{raw[2]}{raw[2]}".ToUpperInvariant();
        }

        return string.Empty;
    }

    private static (int R, int G, int B) HexToRgb(string hex)
    {
        var raw = hex.TrimStart('#');
        if (raw.Length < 6) return (255, 255, 255);

        return (Convert.ToInt32(raw[..2], 16), Convert.ToInt32(raw.Substring(2, 2), 16),
            Convert.ToInt32(raw.Substring(4, 2), 16));
    }

    private static string RgbToHex(double r, double g, double b)
    {
        static string Channel(double value) =>
            ((int)Math.Clamp(Math.Round(value), 0, 255)).ToString("X2");

        return $"#{Channel(r)}{Channel(g)}{Channel(b)}";
    }

    private static string SampleGradient(IReadOnlyList<string> stops, double t)
    {
        if (stops.Count == 0) return "#FFFFFF";
        if (stops.Count == 1) return stops[0];

        var scaled = Math.Clamp(t, 0, 1) * (stops.Count - 1);
        var index = Math.Min(stops.Count - 2, (int)Math.Floor(scaled));
        var fraction = scaled - index;
        var from = HexToRgb(stops[index]);
        var to = HexToRgb(stops[index + 1]);

        return RgbToHex(
            from.R + (to.R - from.R) * fraction,
            from.G + (to.G - from.G) * fraction,
            from.B + (to.B - from.B) * fraction);
    }

    private static List<string?> ComputeCharColors(string text, IReadOnlyList<string> stops)
    {
        var colorable = new List<int>();
        for (var index = 0; index < text.Length; index++)
        {
            if (!char.IsWhiteSpace(text[index])) colorable.Add(index);
        }

        var result = new List<string?>(new string?[text.Length]);
        var total = colorable.Count;
        for (var order = 0; order < colorable.Count; order++)
        {
            var t = total <= 1 ? 0 : (double)order / (total - 1);
            result[colorable[order]] = SampleGradient(stops, t);
        }

        return result;
    }

    private static string ColorCode(string format, string hex, char control)
    {
        var raw = hex.TrimStart('#').ToUpperInvariant();
        switch (format)
        {
            case "section":
                return "§x" + string.Concat(raw.Select(ch => $"§{ch}"));
            case "amp":
                return "&x" + string.Concat(raw.Select(ch => $"&{ch}"));
            case "minimessage":
                return $"<#{raw}>";
            default:
                return $"{control}#{raw}";
        }
    }

    private static string FormatCodes(
        string format, char control,
        bool bold, bool italic, bool underline, bool strikethrough, bool obfuscated)
    {
        if (format == "minimessage")
        {
            var builder = new StringBuilder();
            if (obfuscated) builder.Append("<obfuscated>");
            if (bold) builder.Append("<bold>");
            if (strikethrough) builder.Append("<strikethrough>");
            if (underline) builder.Append("<underlined>");
            if (italic) builder.Append("<italic>");

            return builder.ToString();
        }

        var code = format == "section" ? '§' : control;
        var legacy = new StringBuilder();
        if (obfuscated) legacy.Append(code).Append('k');
        if (bold) legacy.Append(code).Append('l');
        if (strikethrough) legacy.Append(code).Append('m');
        if (underline) legacy.Append(code).Append('n');
        if (italic) legacy.Append(code).Append('o');

        return legacy.ToString();
    }

    private static string BuildGradientOutput(
        string text, IReadOnlyList<string> stops, string format, char control,
        bool bold, bool italic, bool underline, bool strikethrough, bool obfuscated)
    {
        var colors = ComputeCharColors(text, stops);
        var styles = FormatCodes(format, control, bold, italic, underline, strikethrough, obfuscated);
        var builder = new StringBuilder();

        for (var index = 0; index < text.Length; index++)
        {
            var color = colors[index];
            if (color is not null) builder.Append(ColorCode(format, color, control)).Append(styles);
            builder.Append(text[index]);
        }

        return builder.ToString();
    }

    private static IBrush BuildGradientBrush(IReadOnlyList<string> stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        };

        if (stops.Count == 0)
        {
            brush.GradientStops.Add(new GradientStop(Colors.White, 0));
            return brush;
        }

        for (var index = 0; index < stops.Count; index++)
        {
            var offset = stops.Count == 1 ? 0 : (double)index / (stops.Count - 1);
            brush.GradientStops.Add(new GradientStop(Color.Parse(stops[index]), offset));
        }

        return brush;
    }

    private static string Value(Control control) => control switch
    {
        TextBox box => (box.Text ?? string.Empty).Trim(),
        _ => string.Empty,
    };

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static string HexOf(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string NormalizeColorText(string? text, string fallback)
    {
        var normalized = NormalizeHex(text ?? string.Empty);
        return normalized.Length > 0 ? normalized : fallback;
    }

    private static IBrush TryColorBrush(string? text)
    {
        var normalized = NormalizeHex(text ?? string.Empty);
        return normalized.Length > 0
            ? new SolidColorBrush(Color.Parse(normalized))
            : ThemeTokens.Brush("NekoFieldBrush");
    }

    private static IBrush AlphaBrush(string hex, byte alpha)
    {
        var color = Color.Parse(hex);
        return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }

    private async Task CopyToClipboardAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(text);
    }

    private static TextBlock Caption(string text) =>
        new() { Text = text, Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };

    private static TextBox Input(string text, string? placeholder = null) => new()
    {
        Text = text,
        PlaceholderText = placeholder,
        FontSize = 12.5,
        Height = 32,
    };

    private static ComboBox Choice(params string[] items) => new()
    {
        ItemsSource = items,
        SelectedIndex = 0,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        FontSize = 12.5,
        Height = 32,
    };

    private static Button GhostButton(string text) => new()
    {
        Content = text,
        Classes = { "ghost" },
        Height = 32,
        Padding = new Thickness(14, 0),
    };

    private static Button PrimaryButton(string text) => new()
    {
        Content = text,
        Classes = { "primary" },
        Height = 32,
        Padding = new Thickness(16, 0),
    };

    private static SelectableTextBlock Code(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontFamily = new FontFamily("Consolas, Cascadia Mono, Microsoft YaHei"),
        FontSize = 11.5,
        Foreground = ThemeTokens.Brush("NekoTextBrush"),
    };

    private static Control FileRow(string glyphKey, string name, string info)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        row.Children.Add(new IconParkIcon
        {
            Kind = glyphKey,
            Width = 14,
            Height = 14,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var label = new TextBlock
        {
            Text = name,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = ThemeTokens.Brush("NekoTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);

        var hint = new TextBlock
        {
            Text = info,
            Classes = { "caption" },
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(hint, 2);
        row.Children.Add(hint);

        return row;
    }

    private void OnOpenWorkspaceClick(object? sender, RoutedEventArgs e) => OpenFolder(CreatorDataRoot);

    private void OnOpenInstancesClick(object? sender, RoutedEventArgs e) => Shell.Go("instances");

    private void OnOpenPluginsClick(object? sender, RoutedEventArgs e) => Shell.Go("plugins");
}
