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

/// <summary>
/// 创作中心：工具列表页 + 每个工具自己的页面（页面内导航，不再是弹窗/独立窗口）。
/// 3D 文字生成器在本页内嵌 WebView2 只负责渲染预览，参数面板是 Avalonia 控件，因此跟随主题。
/// </summary>
public partial class CreatorView : UserControl
{
    // ---------------- 3D 文字：参数选项 ----------------

    private static readonly (string Value, string Label)[] ColorModes =
    {
        ("gradient", L.T("Creator.Cube.ColorMode.Gradient", "上下渐变")), ("solid", L.T("Creator.Cube.ColorMode.Solid", "纯色")), ("rainbow", L.T("Creator.Cube.ColorMode.Rainbow", "彩虹")),
    };

    private static readonly (string Value, string Label)[] Textures =
    {
        ("grid", L.T("Creator.Cube.Texture.Grid", "方块边框")), ("noise", L.T("Creator.Cube.Texture.Noise", "噪点石纹")), ("none", L.T("Creator.Cube.Texture.None", "无纹理")),
    };

    private readonly Dictionary<string, Control> _toolPages = new();
    private readonly List<string> _fontNames = new();

    private WebRegionHost? _cubeHost;
    private bool _cubeAttached;
    private bool _cubeUiReady;
    private bool _themeSubscribed;

    public CreatorView()
    {
        InitializeComponent();

        BuildToolCards();
        LoadSystemFonts();
        InitCubeControls();

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

        // 离开创作中心时回到工具列表，下次从开始菜单进来是列表页
        ShowToolList();
    }

    private void OnThemeChanged()
    {
        Dispatcher.UIThread.Post(PushCubeState);
    }

    // ================= 工具列表页 =================

    private void BuildToolCards()
    {
        ToolGrid.Children.Clear();

        foreach (var tool in CreatorToolCatalog.Tools)
        {
            ToolGrid.Children.Add(BuildToolCard(tool));
        }

        ToolCountChip.Text = L.T("Creator.List.Chip.Tools", "{0} 个工具", CreatorToolCatalog.Tools.Count);
    }

    private Control BuildToolCard(CreatorTool tool)
    {
        var accent = ThemeTokens.FromHex(tool.AccentHex);
        var softBrush = AlphaBrush(tool.AccentHex, 0x2E);
        var accentBorder = AlphaBrush(tool.AccentHex, 0x66);

        var icon = new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(10),
            Background = softBrush,
            BorderBrush = accentBorder,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new IconParkIcon
            {
                Kind = tool.IconKey,
                Width = 20,
                Height = 20,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var text = new StackPanel
        {
            Spacing = 3,
            Margin = new Thickness(11, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        text.Children.Add(new TextBlock
        {
            Text = L.T($"Creator.Tool.{tool.Id}.Title", tool.Title),
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeTokens.Brush("NekoTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = L.T($"Creator.Tool.{tool.Id}.Desc", tool.Description),
            FontSize = 10.8,
            Foreground = ThemeTokens.Brush("NekoMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var open = new Button
        {
            Content = L.T("Common.Open", "打开"),
            Classes = { "ghost" },
            Height = 28,
            Padding = new Thickness(11, 0),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(open, 2);
        grid.Children.Add(open);

        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeTokens.Brush("NekoBorderBrush"),
            Background = ThemeTokens.Brush("NekoCardBrush"),
            Padding = new Thickness(14),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = grid,
        };

        ToolTip.SetTip(card, L.T($"Creator.Tool.{tool.Id}.Desc", tool.Description));

        void Open() => ShowTool(tool);

        open.Click += (_, e) =>
        {
            e.Handled = true;
            Open();
        };

        card.PointerEntered += (_, _) =>
        {
            card.Background = ThemeTokens.Brush("NekoCardHoverBrush");
            card.BorderBrush = AlphaBrush(tool.AccentHex, 0x88);
        };
        card.PointerExited += (_, _) =>
        {
            card.Background = ThemeTokens.Brush("NekoCardBrush");
            card.BorderBrush = ThemeTokens.Brush("NekoBorderBrush");
        };
        card.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            Open();
        };

        return card;
    }

    /// <summary>开始菜单 ComboBox 的工具入口：直接打开对应工具，不再经过工具列表。</summary>
    public void OpenToolById(string toolId)
    {
        foreach (var tool in CreatorToolCatalog.Tools)
        {
            if (tool.Id != toolId) continue;
            ShowTool(tool);
            return;
        }

        ShowToolList();
    }

    private void ShowToolList()
    {
        ToolView.IsVisible = false;
        ListView.IsVisible = true;
        UiAnimations.Reset(ListView);
        UiAnimations.FadeSlideIn(ListView);
    }

    private void ShowTool(CreatorTool tool)
    {
        ListView.IsVisible = false;
        ToolView.IsVisible = true;
        // 没有「创作中心」列表页可返回，工具页直接由开始菜单打开
        ToolBackButton.IsVisible = false;

        ToolTitle.Text = L.T($"Creator.Tool.{tool.Id}.Title", tool.Title);
        ToolSubtitle.Text = L.T($"Creator.Tool.{tool.Id}.Desc", tool.Description);
        ToolIcon.Kind = tool.IconKey;
        ToolIconTile.Background = AlphaBrush(tool.AccentHex, 0x2E);

        var isCube = tool.Id == "cube3d";
        CubeTextPanel.IsVisible = isCube;
        ToolBody.IsVisible = !isCube;

        if (isCube)
        {
            ToolBody.Content = null;
            PushCubeState();
        }
        else
        {
            if (!_toolPages.TryGetValue(tool.Id, out var page))
            {
                page = BuildToolPage(tool);
                _toolPages[tool.Id] = page;
            }

            ToolBody.Content = page;
        }

        UiAnimations.Reset(ToolView);
        UiAnimations.FadeSlideIn(ToolView);
    }

    private void OnToolBackClick(object? sender, RoutedEventArgs e) => ShowToolList();

    // ================= 3D 文字生成器（内嵌 WebView2） =================

    private async Task AttachCubeHostAsync()
    {
        _cubeHost = new WebRegionHost(CubeTextHost, "cubetext/index.html", "cubetext");
        _cubeHost.MessageReceived += OnCubeMessageReceived;

        await _cubeHost.AttachAsync();

        // 页面导航完成前 Post 会排队；这里再持续推几秒，覆盖「导航晚于首推」的情况
        for (var attempt = 0; attempt < 14; attempt++)
        {
            PushCubeState();
            await Task.Delay(250);
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

    // ================= 工具子页 =================

    private Control BuildToolPage(CreatorTool tool)
    {
        // 命令生成器：独立页面，自带三栏布局与滚动
        if (tool.Id == "command")
            return new CommandGeneratorPage();

        var content = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(0, 13, 0, 18),
            MaxWidth = 1040,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        switch (tool.Id)
        {
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
                content.Children.Add(Card(L.T("Creator.Tool.PlaceholderTitle", "这个工具正在迁移中"), null,
                    Muted(L.T("Creator.Tool.PlaceholderDesc", "工具页壳已就位，能力会随后续版本接入。"))));
                break;
        }

        return new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    // ---------------- 资源包编辑器 ----------------

    private static readonly (string Version, int Format)[] PackFormats =
    {
        ("1.21.4", 46), ("1.21.1", 34), ("1.20.6", 32), ("1.20.4", 22), ("1.20.1", 15),
        ("1.19.4", 13), ("1.18.2", 9), ("1.16.5", 6), ("1.12.2", 3),
    };

    private void BuildResourcePackPage(StackPanel content)
    {
        var nameBox = Input("my-resource-pack", L.T("Creator.ResourcePack.NamePlaceholder", "例如 MofuPack"));
        var descBox = Input(L.T("Creator.ResourcePack.DescDefault", "Mofumofu 风格的原版增强材质"), L.T("Creator.ResourcePack.DescHint", "会写进 pack.mcmeta 的 description"));
        var resolution = Choice(L.T("Creator.ResourcePack.Res16", "16x（原版）"), "32x", "64x", "128x");
        var version = Choice(PackFormats.Select(item => item.Version).ToArray());
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

        var form = Card(L.T("Creator.ResourcePack.Card.Title", "资源包信息"), L.T("Creator.ResourcePack.Card.Hint", "决定 pack.mcmeta 与目录结构"),
            Field(L.T("Creator.ResourcePack.Name", "资源包名称"), nameBox),
            Field(L.T("Creator.ResourcePack.Desc", "描述"), descBox),
            Split(Field(L.T("Creator.ResourcePack.Resolution", "贴图分辨率"), resolution), Field(L.T("Creator.ResourcePack.Version", "对应游戏版本"), version)),
            status);

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

        var metaCard = Card("pack.mcmeta", L.T("Creator.ResourcePack.MetaCard.Hint", "随右侧信息实时更新"),
            CodeCard(metaCode),
            FileListCard(files));

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

        // 表单改动后不再使用导入的工程，导出时按当前表单重建
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

        content.Children.Add(Split(form, metaCard));
        content.Children.Add(actionRow);
    }

    // ---------------- 创作中心本地文件操作（真实落盘） ----------------

    /// <summary>创作中心的工作目录（资源包草稿 / 插件骨架 / 整合包输出），随程序目录便携存放。</summary>
    private static string CreatorDataRoot => Path.Combine(
        MofuCore.Config.YamlRead.DataRoot,
        MofuCore.Config.YamlRead.DataDirectoryName,
        "creator");

    private static string PluginRoot => Path.Combine(CreatorDataRoot, "plugins");
    private static string ModpackOutputRoot => Path.Combine(CreatorDataRoot, "modpacks");
    private static string SoloOutputRoot => Path.Combine(CreatorDataRoot, "solo");

    /// <summary>在资源管理器里打开目录（不存在则先创建）；失败给可读提示。</summary>
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

    /// <summary>按当前表单组装资源包工程（MofuCore.Tools.ResourcePackMake）。</summary>
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

    /// <summary>生成 64×64 资源包图标（强调色渐变 + 边框 + 中心菱形）。</summary>
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

    /// <summary>生成棋盘格示例贴图（尺寸 = 表单里的分辨率）。</summary>
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

    // ---------------- 渐变文字 ----------------

    private void BuildGradientPage(StackPanel content)
    {
        string[] formatLabels =
        {
            L.T("Creator.Gradient.Format.Legacy", "原版 · &#RRGGBB"), L.T("Creator.Gradient.Format.Section", "标准十六进制 · §x§R…"), L.T("Creator.Gradient.Format.Amp", "& 变体 · &x&R&R…"), "MiniMessage · <#RRGGBB>",
        };
        string[] formatValues = { "legacy", "section", "amp", "minimessage" };
        string[] controlLabels = { L.T("Creator.Gradient.Control.Amp", "& （插件常用）"), L.T("Creator.Gradient.Control.Section", "§ （原版标准）") };

        var textBox = Input("Mofumofu", L.T("Creator.Gradient.TextPlaceholder", "输入要着色的文字（支持中文）"));
        var formatCombo = Choice(formatLabels);
        var controlCombo = Choice(controlLabels);
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

        var boldCheck = Toggle(L.T("Creator.Gradient.Style.Bold", "粗体"));
        var italicCheck = Toggle(L.T("Creator.Gradient.Style.Italic", "斜体"));
        var underlineCheck = Toggle(L.T("Creator.Gradient.Style.Underline", "下划线"));
        var strikeCheck = Toggle(L.T("Creator.Gradient.Style.Strike", "删除线"));
        var obfuscatedCheck = Toggle(L.T("Creator.Gradient.Style.Obfuscated", "乱码"));

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

                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                row.Children.Add(new TextBlock
                {
                    Text = L.T("Creator.Gradient.StopLabel", "色标 {0}", i + 1),
                    Classes = { "caption" },
                    Width = 48,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                row.Children.Add(colorBox);
                row.Children.Add(swatch);
                row.Children.Add(remove);
                stopsHost.Children.Add(row);
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

        var styleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
        };
        styleRow.Children.Add(boldCheck);
        styleRow.Children.Add(italicCheck);
        styleRow.Children.Add(underlineCheck);
        styleRow.Children.Add(strikeCheck);
        styleRow.Children.Add(obfuscatedCheck);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(addStop);
        buttons.Children.Add(copy);

        RebuildStops();

        var formCard = Card(L.T("Creator.Gradient.Card.Title", "渐变设置"), L.T("Creator.Gradient.Card.Hint", "最多 6 个色标，按顺序插值"),
            Field(L.T("Creator.Gradient.Text", "文字内容"), textBox),
            stopsPanel,
            Split(Field(L.T("Creator.Gradient.Format", "输出格式"), formatCombo), Field(L.T("Creator.Gradient.Control", "控制符"), controlCombo)),
            Field(L.T("Creator.Gradient.Styles", "文字样式"), styleRow),
            buttons,
            status);

        var previewCard = Card(L.T("Creator.Gradient.Preview", "预览与输出"), null,
            new Border { Classes = { "card-soft" }, Child = preview },
            CodeCard(output));

        content.Children.Add(Split(formCard, previewCard));
        Refresh();
    }

    // ---------------- 皮肤编辑器 ----------------

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

    /// <summary>皮肤库目录：与账户页（ui-account）读取的目录保持一致。</summary>
    private static string SkinLibraryDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mofumofu.Launcher",
        "skins");

    /// <summary>自定义皮肤目录：账户页读取 <c>custom/&lt;账号&gt;.png</c>。</summary>
    private static string CustomSkinRoot => Path.Combine(SkinLibraryDirectory, "custom");

    private void BuildSkinPage(StackPanel content)
    {
        var accounts = Choice(RealAccountNames().ToArray());
        var model = Choice(L.T("Creator.Skin.Model.Classic", "经典（4 格手臂）"), L.T("Creator.Skin.Model.Slim", "纤细（3 格手臂）"));
        var cape = Toggle(L.T("Creator.Skin.Cape", "显示披风"));
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

        // 调色板
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

        var formCard = Card(L.T("Creator.Skin.Card.Title", "账号与模型"), L.T("Creator.Skin.Card.Hint", "选择皮肤归属与体型"),
            Field(L.T("Creator.Skin.Account", "账号"), accounts),
            Field(L.T("Creator.Skin.Model", "皮肤模型"), model),
            cape,
            Field(L.T("Creator.Skin.Tools", "绘图工具"), new StackPanel { Spacing = 8, Children = { brushRow } }),
            status);

        var canvasCard = Card(L.T("Creator.Skin.Canvas.Title", "64 × 64 像素画布"), L.T("Creator.Skin.Canvas.Hint", "左键绘制 · 右侧调色板取色"),
            canvasBorder,
            paletteGrid);

        content.Children.Add(Split(formCard, canvasCard));
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
            // WriteableBitmap 改像素后需要重新挂一次才会重画
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
        TextBox versionBox = Input("1.0.0", L.T("Creator.Modpack.VersionPlaceholder", "整合包版本号"));
        ComboBox field = Choice(L.T("Creator.Modpack.Format.Mrpack", ".mrpack（Modrinth）"), L.T("Creator.Modpack.Format.MultiMc", ".zip（MultiMC / Prism）"));
        CheckBox includeConfig = Toggle(L.T("Creator.Modpack.IncludeConfig", "包含 config 与 options 覆盖文件"), isChecked: true);
        CheckBox includeMods = Toggle(L.T("Creator.Modpack.IncludeMods", "包含模组清单"), isChecked: true);
        CheckBox includeJava = Toggle(L.T("Creator.Solo.IncludeJava", "内嵌 Java 运行时"), isChecked: true);
        CheckBox silent = Toggle(L.T("Creator.Solo.Silent", "玩家双击后静默启动"));
        TextBox outputBox = Input(solo ? SoloOutputRoot : ModpackOutputRoot);
        ProgressBar progress = new ProgressBar
        {
            Minimum = 0.0,
            Maximum = 100.0,
            Height = 6.0
        };
        TextBlock status = Caption(solo ? L.T("Creator.Solo.Status.Init", "把启动器、Java 与整合包打进单个 exe，仅支持 Windows。") : L.T("Creator.Modpack.Status.Init", "导出后可分享给朋友或上传到整合包平台。"));
        SelectableTextBlock manifest = Code(string.Empty);
        // 清单预览放进内部滚动容器：再长也不会把整页撑出窗口
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
                                    string errors = string.Join("； ", result.Errors.Take(3));
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
        StackPanel stackPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8.0,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        if (!solo)
        {
            stackPanel.Children.Add(button);
        }
        if (!solo)
        {
            stackPanel.Children.Add(installButton);
        }
        stackPanel.Children.Add(startButton);
        Border border = Card(solo ? L.T("Creator.Solo.Card.Title", "NekoSolo 设置") : L.T("Creator.Modpack.Card.Title", "整合包设置"), L.T("Creator.Modpack.Card.Hint", "按实例导出，可勾选包含内容"), Field(L.T("Creator.Modpack.Instance", "实例"), instanceCombo), solo ? ((Control)Caption(L.T("Creator.Solo.OutputHint", "点「开始打包」后选择 .zip 保存位置。"))) : ((Control)Split(Field(L.T("Creator.Modpack.Format", "格式"), field), Field(L.T("Creator.Modpack.Version", "版本号"), versionBox))), solo ? new ContentControl
        {
            IsVisible = false
        } : includeConfig, solo ? new ContentControl
        {
            IsVisible = false
        } : includeMods, solo ? includeJava : new CheckBox
        {
            IsVisible = false
        }, silent, solo ? ((Control)new ContentControl
        {
            IsVisible = false
        }) : ((Control)Caption(L.T("Creator.Modpack.Install.Hint", "安装 .mrpack：选择本地整合包，把其中的 overrides 解压进上面所选的实例目录。"))), solo ? new ContentControl
        {
            IsVisible = false
        } : Field(L.T("Creator.Modpack.Output", "输出目录"), outputBox), progress, status, stackPanel);
        Border border2 = Card(solo ? L.T("Creator.Solo.Manifest", "打包清单") : "modrinth.index.json", L.T("Creator.Modpack.Manifest", "导出前预览生成内容"), CodeCard(manifestScroll));

        // 页面纵向填满：两张卡片撑满视口高度，窗口太矮时整页仍可滚动兜底
        border.VerticalAlignment = VerticalAlignment.Stretch;
        border2.VerticalAlignment = VerticalAlignment.Stretch;
        Grid page = new()
        {
            Margin = new Thickness(0, 13, 0, 18),
            MaxWidth = 1040,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        page.Children.Add(Split(border, border2));

        ScrollViewer root = new()
        {
            Content = page,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        root.SizeChanged += (_, e) =>
        {
            double height = e.NewSize.Height;
            // 31 = 页面上下边距(13+18)；190 = 卡片头/内边距/进度与状态行的大致占位
            page.MinHeight = Math.Max(0.0, height - 31);
            manifestScroll.MaxHeight = Math.Max(160.0, height - 190);
        };
        return root;
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
                    manifest.Text = "# NekoSolo payload layout\n#   manifest.json\n#   icon.png\n#   overrides/<instance files>\n\n" + text2;
                }
                else
                {
                    StringBuilder stringBuilder = new StringBuilder();
                    stringBuilder.Append("  \"files\": [");
                    if (includeMods.IsChecked == true)
                    {
                        string[] array = RealModFiles(instanceInfo).Take(12).ToArray();
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
        dictionary["format"] = 1;
        dictionary["packId"] = value;
        dictionary["packName"] = instance.Name;
        dictionary["packVersion"] = version;
        dictionary["author"] = (string.IsNullOrWhiteSpace(selectedAccountName) ? "Mofumofu" : selectedAccountName);
        dictionary["description"] = L.T("Creator.Solo.PackDescription", "{0} · NekoSolo 整合包", instance.Name);
        dictionary["mcVersion"] = instance.Version;
        dictionary["loaderName"] = instance.Loader;
        dictionary["loaderVersion"] = string.Empty;
        dictionary["versionId"] = value;
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
        string contentDirectory = FindInstanceContentDirectory(instance);
        if (contentDirectory == null)
        {
            return (Ok: false, Path: string.Empty, Files: 0, Size: 0L, Message: L.T("Creator.Solo.Status.NoInstanceDir", "找不到实例目录，无法生成 NekoSolo 包"));
        }
        try
        {
            List<ModpackContentItem> files = (from item in ModrinthBundleMake.CollectContent(contentDirectory)
                where !item.IsDirectory
                select item).ToList();
            if (files.Count == 0)
            {
                return (Ok: false, Path: string.Empty, Files: 0, Size: 0L, Message: L.T("Creator.Solo.Status.EmptyInstance", "实例目录里没有可打包的文件"));
            }
            string manifestJson = JsonSerializer.Serialize(BuildSoloManifest(instance, version, silent, hasJava), new JsonSerializerOptions
            {
                WriteIndented = true
            });
            int total = files.Count + 2;
            UTF8Encoding utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Color accent = ThemeTokens.Color("NekoAccentBrush");
            IProgress<int> reporter = new Progress<int>((int done) =>
            {
                progressBar.Value = Math.Clamp((double)done * 100.0 / (double)total, 0.0, 100.0);
                status.Text = L.T("Creator.Solo.Status.Writing", "正在写入 NekoSolo 包…{0}/{1}", done, total);
            });
            (int done, long Length) tuple = await Task.Run(() =>
            {
                string directoryName = Path.GetDirectoryName(targetZipPath);
                if (!string.IsNullOrWhiteSpace(directoryName))
                {
                    Directory.CreateDirectory(directoryName);
                }
                if (File.Exists(targetZipPath))
                {
                    File.Delete(targetZipPath);
                }
                int num = 0;
                using (ZipArchive zipArchive = ZipFile.Open(targetZipPath, ZipArchiveMode.Create))
                {
                    AddZipText(zipArchive, "manifest.json", manifestJson, utf8);
                    num++;
                    reporter.Report(num);
                    using (WriteableBitmap writeableBitmap = CreatePackIconBitmap(accent, 64))
                    {
                        using MemoryStream memoryStream = new MemoryStream();
                        writeableBitmap.Save(memoryStream);
                        AddZipBytes(zipArchive, "icon.png", memoryStream.ToArray());
                    }
                    num++;
                    reporter.Report(num);
                    foreach (ModpackContentItem item in files)
                    {
                        string path = Path.Combine(contentDirectory, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(path))
                        {
                            ZipArchiveEntry zipArchiveEntry = zipArchive.CreateEntry("files/" + item.RelativePath, CompressionLevel.Optimal);
                            using (FileStream fileStream = File.OpenRead(path))
                            {
                                using Stream destination = zipArchiveEntry.Open();
                                fileStream.CopyTo(destination);
                            }
                            num++;
                            if (num % 10 == 0 || num == total)
                            {
                                reporter.Report(num);
                            }
                        }
                    }
                }
                return (done: num, Length: new FileInfo(targetZipPath).Length);
            });
            return new ValueTuple<bool, string, int, long, string>(item3: tuple.done, item4: tuple.Length, item1: true, item2: targetZipPath, item5: string.Empty);
        }
        catch (Exception ex)
        {
            AppLog("ERROR", "创作中心生成 NekoSolo 包失败：" + ex.Message);
            return (Ok: false, Path: string.Empty, Files: 0, Size: 0L, Message: L.T("Creator.Solo.Status.Failed", "生成 NekoSolo 包失败：{0}", ex.Message));
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
            visual.FindAncestorOfType<SukiDialogHost>(includeSelf: true)?.Manager?.DismissDialog();
        }
    }

    private void BuildPluginPage(StackPanel content)
    {
        TextBox idBox = Input("my-plugin", "my-plugin");
        TextBox nameBox = Input(L.T("Creator.Plugin.NameDefault", "我的插件"), L.T("Creator.Plugin.NamePlaceholder", "显示名称"));
        TextBox versionBox = Input("1.0.0");
        TextBox apiBox = Input("1");
        TextBox authorBox = Input("Mofumofu");
        TextBox descBox = Input(L.T("Creator.Plugin.DescDefault", "在桌面加一个小组件"), L.T("Creator.Plugin.DescPlaceholder", "一句话简介"));
        TextBox entryBox = Input("index.js");
        TextBox iconBox = Input("icon.png");
        TextBox stylesBox = Input("theme.css", L.T("Creator.Plugin.StylesPlaceholder", "逗号分隔，可留空"));
        TextBlock idError = Caption(string.Empty);
        (string Key, string Label, CheckBox Box)[] permissions = new (string, string, CheckBox)[5]
        {
            ("network", L.T("Creator.Plugin.Perm.Network", "网络访问（下载 / 请求 API）"), Toggle("network")),
            ("filesystem", L.T("Creator.Plugin.Perm.FileSystem", "文件读写（配置 / 日志）"), Toggle("filesystem")),
            ("notifications", L.T("Creator.Plugin.Perm.Notifications", "系统通知"), Toggle("notifications")),
            ("commands", L.T("Creator.Plugin.Perm.Commands", "注册启动器命令"), Toggle("commands")),
            ("widgets", L.T("Creator.Plugin.Perm.Widgets", "注册桌面小组件"), Toggle("widgets", isChecked: true))
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
        (string, string, CheckBox)[] array2 = permissions;
        for (int num = 0; num < array2.Length; num++)
        {
            CheckBox item = array2[num].Item3;
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
        StackPanel stackPanel = new StackPanel
        {
            Spacing = 6.0
        };
        (string, string, CheckBox)[] array3 = permissions;
        for (int num2 = 0; num2 < array3.Length; num2++)
        {
            (string, string, CheckBox) tuple = array3[num2];
            string item2 = tuple.Item2;
            CheckBox item3 = tuple.Item3;
            item3.Content = item2;
            item3.FontSize = 12.0;
            stackPanel.Children.Add(item3);
        }
        StackPanel stackPanel2 = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8.0,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        stackPanel2.Children.Add(button2);
        stackPanel2.Children.Add(button);
        stackPanel2.Children.Add(package);
        stackPanel2.Children.Add(create);
        Border border = Card(L.T("Creator.Plugin.Card.Title", "清单字段"), L.T("Creator.Plugin.Card.Hint", "填写后会实时生成 plugin.yaml"), Split(Field(L.T("Creator.Plugin.Id", "插件 id"), idBox, idError), Field(L.T("Creator.Plugin.Name", "名称"), nameBox)), Split(Field(L.T("Creator.Modpack.Version", "版本号"), versionBox), Field(L.T("Creator.Plugin.ApiVersion", "API 版本"), apiBox)), Split(Field(L.T("Creator.Plugin.Author", "作者"), authorBox), Field(L.T("Creator.Plugin.Desc", "简介"), descBox)), Split(Field(L.T("Creator.Plugin.Entry", "入口文件"), entryBox), Field(L.T("Creator.Plugin.Icon", "图标文件"), iconBox)), Field(L.T("Creator.Plugin.Styles", "样式文件"), stylesBox), Field(L.T("Creator.Plugin.Permissions", "权限声明"), stackPanel), stackPanel2, status);
        Border border2 = Card("plugin.yaml", null, CodeCard(yaml));
        content.Children.Add(Split(border, border2));
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
            (string, string, CheckBox)[] array6 = permissions;
            for (int num4 = 0; num4 < array6.Length; num4++)
            {
                (string, string, CheckBox) tuple2 = array6[num4];
                string item4 = tuple2.Item1;
                CheckBox item5 = tuple2.Item3;
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

    // ================= 渐变文字纯逻辑（移植自旧版 gradient.ts） =================

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

    // ================= 小工具 =================

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

    private static TextBlock Muted(string text) => new()
    {
        Text = text,
        Classes = { "muted" },
        TextWrapping = TextWrapping.Wrap,
    };

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

    private static CheckBox Toggle(string label, bool isChecked = false) => new()
    {
        Content = label,
        IsChecked = isChecked,
        FontSize = 12,
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

    private static Border CodeCard(Control code) => new()
    {
        Classes = { "card-soft" },
        Padding = new Thickness(12, 10),
        Child = code,
    };

    private static Control Field(string label, Control field, Control? hint = null)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = label, Classes = { "caption" } });

        // 命令生成器会在切换类型时复用同一个输入框；先把它从旧表单里摘下来，
        // 否则 Avalonia 会抛「already has a visual parent」并让整个程序崩溃。
        DetachFromParent(field);
        panel.Children.Add(field);
        if (hint is not null) panel.Children.Add(hint);

        return panel;
    }

    private static void DetachFromParent(Control control)
    {
        switch (control.Parent)
        {
            case Panel panel:
                panel.Children.Remove(control);
                break;
            case Decorator decorator:
                decorator.Child = null;
                break;
            case ContentControl contentControl:
                contentControl.Content = null;
                break;
        }
    }

    private static Grid Split(params Control[] children)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(string.Join(',', Enumerable.Repeat("*", children.Length))),
            ColumnSpacing = 12,
        };

        for (var index = 0; index < children.Length; index++)
        {
            Grid.SetColumn(children[index], index);
            grid.Children.Add(children[index]);
        }

        return grid;
    }

    private static Border Card(string title, string? hint, params Control[] children)
    {
        var panel = new StackPanel { Spacing = 10 };

        var header = new StackPanel { Spacing = 2 };
        header.Children.Add(new TextBlock
        {
            Text = title,
            Classes = { "section-title" },
            TextWrapping = TextWrapping.Wrap,
        });

        if (!string.IsNullOrWhiteSpace(hint))
        {
            header.Children.Add(Caption(hint));
        }

        panel.Children.Add(header);

        foreach (var child in children)
        {
            if (child is CheckBox check)
            {
                check.FontSize = 12;
                check.Margin = new Thickness(0, 1);
            }

            panel.Children.Add(child);
        }

        return new Border
        {
            Classes = { "card" },
            Child = panel,
            VerticalAlignment = VerticalAlignment.Top,
        };
    }

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

    private static Border FileListCard(Control rows) => new()
    {
        Classes = { "card-soft" },
        Padding = new Thickness(12, 10),
        Child = rows,
    };

    // ================= 列表页脚操作 =================

    private void OnOpenWorkspaceClick(object? sender, RoutedEventArgs e) => OpenFolder(CreatorDataRoot);

    private void OnOpenInstancesClick(object? sender, RoutedEventArgs e) => Shell.Go("instances");

    private void OnOpenPluginsClick(object? sender, RoutedEventArgs e) => Shell.Go("plugins");
}
