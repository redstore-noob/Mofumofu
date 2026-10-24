using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MofuCore.Config;
using Mofumofu.Launcher.Controls;
using Mofumofu.Launcher.Services;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views;

/// <summary>
/// 命令生成器（创作中心 · command 工具页）。
/// 三栏布局：左侧命令列表（搜索 + 分类）→ 中间参数表单（目标选择器 + 动态字段 +
/// 物品组件 / 实体 NBT 高级面板）→ 右侧实时输出（语法高亮、一键复制、历史与收藏）。
/// 输出随表单实时刷新，不再需要「生成」按钮。
/// </summary>
public sealed class CommandGeneratorPage : UserControl
{
    private const string MonoFont = "Consolas, Cascadia Mono, Microsoft YaHei";
    private const string LineBreak = "\n";
    private const int ChatLimit = 256;
    private const int HistoryLimit = 50;

    // ---------------- 状态 ----------------

    private readonly Dictionary<string, string> _values = new();
    private CommandSpec _spec = CommandSpecs.All[0];
    private string _category = "all";
    private readonly HashSet<string> _searchTerms = new(StringComparer.OrdinalIgnoreCase);

    // 目标选择器
    private string _selectorBase = "@a";
    private readonly Dictionary<string, string> _selectorFilters = new();

    // give 组件 / summon NBT
    private readonly List<EnchantRow> _enchants = new();
    private bool _componentsEnabled;
    private bool _nbtEnabled;

    private readonly List<string> _outputLines = new();
    private bool _historyTabFavorites;

    // ---------------- 控件 ----------------

    private readonly TextBox _searchBox = new();
    private readonly StackPanel _categoryHost = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _listHost = new();
    private readonly StackPanel _formHost = new();
    private readonly StackPanel _fieldsHost = new();
    private readonly SelectableTextBlock _outputBlock = new() { Inlines = new InlineCollection() };
    private readonly TextBlock _usageText = new();
    private readonly TextBlock _specHead = new();
    private readonly TextBlock _specSubtitle = new();
    private readonly TextBlock _lengthChip = new();
    private readonly StackPanel _historyHost = new();
    private readonly Border _historyCard = new();
    private readonly StackPanel _tabsHost = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly Dictionary<string, Control> _fieldPanels = new();

    private readonly Border _listCard = new();
    private readonly Border _formCard = new();
    private readonly ScrollViewer _listScroll = new();

    public CommandGeneratorPage()
    {
        _values["__ns"] = "1";
        LoadStore();
        BuildLayout();
        BuildHistoryCard();
        RebuildCategories();
        SelectSpec(CommandSpecs.All[0], animate: false);
    }

    // ================= 布局 =================

    private void BuildLayout()
    {
        // ---- 左栏：搜索 + 分类 + 命令列表 ----
        _searchBox.PlaceholderText = L.T("Creator.Command.Search", "搜索命令…");
        _searchBox.FontSize = 12.5;
        _searchBox.Height = 32;
        _searchBox.InnerLeftContent = new IconParkIcon
        {
            Kind = "IconSearch",
            Width = 13,
            Height = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 4, 0),
        };
        _searchBox.TextChanged += (_, _) =>
        {
            _searchTerms.Clear();
            foreach (var word in (_searchBox.Text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                _searchTerms.Add(word);
            RebuildList();
        };

        _listScroll.Content = _listHost;
        _listScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _listScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

        _listCard.Classes.Add("card");
        _listCard.Padding = new Thickness(12);
        _listCard.VerticalAlignment = VerticalAlignment.Stretch;
        _listCard.Child = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            RowSpacing = 9,
            Children =
            {
                _searchBox,
            },
        };
        Grid.SetRow(_categoryHost, 1);
        ((Grid)_listCard.Child).Children.Add(_categoryHost);
        Grid.SetRow(_listScroll, 2);
        ((Grid)_listCard.Child).Children.Add(_listScroll);

        // ---- 中栏：表单 ----
        _formCard.Classes.Add("card");
        _formCard.Padding = new Thickness(16);
        _formCard.VerticalAlignment = VerticalAlignment.Top;
        // _formHost 只挂在 _formCard 下；滚动区滚动的是整张卡片
        _formCard.Child = _formHost;

        var formScroll = new ScrollViewer
        {
            Content = _formCard,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        // ---- 右栏：输出 + 历史 ----
        var outputCard = BuildOutputCard();
        var rightGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 14 };
        rightGrid.Children.Add(outputCard);
        Grid.SetRow(_historyCard, 1);
        rightGrid.Children.Add(_historyCard);

        var root = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("232,*,16,416"),
            Margin = new Thickness(0, 13, 0, 4),
        };

        Grid.SetColumn(_listCard, 0);
        Grid.SetColumn(formScroll, 1);
        Grid.SetColumn(rightGrid, 3);
        root.Children.Add(_listCard);
        root.Children.Add(formScroll);
        root.Children.Add(rightGrid);

        Content = root;

        // Ctrl+Enter 快速复制
        this.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                e.Handled = true;
                CopyCurrent();
            }
        }, RoutingStrategies.Tunnel);
    }

    private Border BuildOutputCard()
    {
        _specHead.FontFamily = new FontFamily(MonoFont);
        _specHead.FontSize = 20;
        _specHead.FontWeight = FontWeight.Bold;
        _specHead.Foreground = ThemeTokens.Brush("NekoAccentBrush");

        _specSubtitle.Classes.Add("caption");

        _usageText.Classes.Add("caption");
        _usageText.FontFamily = new FontFamily(MonoFont);
        _usageText.Margin = new Thickness(0, 1, 0, 0);

        _outputBlock.TextWrapping = TextWrapping.Wrap;
        _outputBlock.FontFamily = new FontFamily(MonoFont);
        _outputBlock.FontSize = 13;

        _lengthChip.Classes.Add("caption");
        _lengthChip.HorizontalAlignment = HorizontalAlignment.Right;
        _lengthChip.VerticalAlignment = VerticalAlignment.Top;

        var outputSurface = new Border
        {
            Classes = { "card-soft" },
            Padding = new Thickness(12, 11),
            MinHeight = 64,
            Child = _outputBlock,
        };

        var copy = new Button
        {
            Content = L.T("Creator.Command.Copy", "复制命令"),
            Classes = { "primary" },
            Height = 32,
            Padding = new Thickness(16, 0),
        };
        copy.Click += (_, _) => CopyCurrent();

        var star = new Button
        {
            Content = "★ " + L.T("Creator.Command.Star", "收藏"),
            Classes = { "ghost" },
            Height = 32,
            Padding = new Thickness(13, 0),
        };
        star.Click += (_, _) => ToggleFavorite(CurrentCommandText());

        var nsCheck = new CheckBox
        {
            Content = L.T("Creator.Command.Namespace", "带 minecraft:"),
            IsChecked = true,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        nsCheck.Click += (_, _) =>
        {
            _values["__ns"] = nsCheck.IsChecked == true ? "1" : "0";
            RefreshOutput();
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        buttons.Children.Add(nsCheck);
        buttons.Children.Add(star);
        buttons.Children.Add(copy);

        var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        headerRow.Children.Add(new StackPanel
        {
            Spacing = 2,
            Children = { _specHead, _specSubtitle },
        });
        Grid.SetColumn(_lengthChip, 1);
        headerRow.Children.Add(_lengthChip);

        var panel = new StackPanel { Spacing = 9 };
        panel.Children.Add(new TextBlock
        {
            Text = L.T("Creator.Command.Result", "实时生成"),
            Classes = { "section-title" },
        });
        panel.Children.Add(headerRow);
        panel.Children.Add(_usageText);
        panel.Children.Add(outputSurface);
        panel.Children.Add(buttons);

        return new Border
        {
            Classes = { "card" },
            Padding = new Thickness(14),
            VerticalAlignment = VerticalAlignment.Top,
            Child = panel,
        };
    }

    // ================= 左栏：命令列表 =================

    private static string CategoryLabel(string cat) => cat switch
    {
        "all" => L.T("Creator.Command.Cat.All", "全部"),
        "player" => L.T("Creator.Command.Cat.Player", "玩家"),
        "world" => L.T("Creator.Command.Cat.World", "世界"),
        "block" => L.T("Creator.Command.Cat.Block", "方块"),
        "entity" => L.T("Creator.Command.Cat.Entity", "实体"),
        "message" => L.T("Creator.Command.Cat.Message", "消息"),
        _ => cat,
    };

    private void RebuildCategories()
    {
        _categoryHost.Children.Clear();

        foreach (var cat in new[] { "all" }.Concat(CommandSpecs.Categories))
        {
            var selected = _category == cat;

            var pill = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 4),
                Margin = new Thickness(0, 0, 5, 0),
                Background = selected
                    ? ThemeTokens.Brush("NekoAccentSoftBrush")
                    : ThemeTokens.Brush("NekoFieldBrush"),
                BorderThickness = new Thickness(1),
                BorderBrush = selected
                    ? ThemeTokens.Brush("NekoAccentLineBrush")
                    : ThemeTokens.Brush("NekoBorderBrush"),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock
                {
                    Text = CategoryLabel(cat),
                    FontSize = 11.5,
                    Foreground = selected
                        ? ThemeTokens.Brush("NekoAccentBrush")
                        : ThemeTokens.Brush("NekoMutedBrush"),
                },
            };

            var captured = cat;
            pill.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                _category = captured;
                RebuildCategories();
                RebuildList();
            };
            _categoryHost.Children.Add(pill);
        }
    }

    private bool Matches(CommandSpec spec)
    {
        if (_category != "all" && spec.Category != _category) return false;
        if (_searchTerms.Count == 0) return true;

        var haystack = string.Join(' ',
            spec.Id, spec.Command, spec.Title,
            L.T($"Creator.Command.T.{spec.Id}", spec.Title),
            L.T($"Creator.Command.Cat.{spec.Category}", spec.Category)).ToLowerInvariant();

        return _searchTerms.All(word => haystack.Contains(word, StringComparison.Ordinal));
    }

    private void RebuildList()
    {
        _listHost.Children.Clear();

        var visible = CommandSpecs.All.Where(Matches).ToList();
        var grouped = visible
            .GroupBy(spec => spec.Category)
            .OrderBy(group => Array.IndexOf(CommandSpecs.Categories, group.Key));

        foreach (var group in grouped)
        {
            _listHost.Children.Add(new TextBlock
            {
                Text = CategoryLabel(group.Key),
                Classes = { "caption" },
                Margin = new Thickness(2, 9, 0, 4),
            });

            foreach (var spec in group)
                _listHost.Children.Add(BuildListItem(spec));
        }

        if (visible.Count == 0)
        {
            _listHost.Children.Add(new TextBlock
            {
                Text = L.T("Creator.Command.List.Empty", "没有匹配的命令"),
                Classes = { "muted" },
                Margin = new Thickness(2, 12, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    private Control BuildListItem(CommandSpec spec)
    {
        var selected = spec.Id == _spec.Id;

        var row = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10, 6),
            Margin = new Thickness(0, 0, 0, 3),
            Background = selected
                ? ThemeTokens.Brush("NekoAccentSoftBrush")
                : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            BorderBrush = selected
                ? ThemeTokens.Brush("NekoAccentLineBrush")
                : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Spacing = 1,
                Children =
                {
                    new TextBlock
                    {
                        Text = "/" + spec.Command,
                        FontFamily = new FontFamily(MonoFont),
                        FontSize = 12.5,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = selected
                            ? ThemeTokens.Brush("NekoAccentBrush")
                            : ThemeTokens.Brush("NekoTextBrush"),
                    },
                    new TextBlock
                    {
                        Text = L.T($"Creator.Command.T.{spec.Id}", spec.Title),
                        FontSize = 10.8,
                        Foreground = ThemeTokens.Brush("NekoMutedBrush"),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                },
            },
        };

        row.PointerEntered += (_, _) =>
        {
            if (!selected) row.Background = ThemeTokens.Brush("NekoCardHoverBrush");
        };
        row.PointerExited += (_, _) =>
        {
            if (!selected) row.Background = Brushes.Transparent;
        };
        row.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            SelectSpec(spec, animate: true);
        };

        return row;
    }

    // ================= 中栏：表单 =================

    private void SelectSpec(CommandSpec spec, bool animate)
    {
        _spec = spec;
        _selectorBase = spec.DefaultTarget.Length > 0 ? spec.DefaultTarget : "@a";
        _selectorFilters.Clear();
        _values.Clear();
        _values["__ns"] = "1";
        _componentsEnabled = false;
        _nbtEnabled = false;
        _enchants.Clear();

        RebuildCategories();
        RebuildList();
        BuildForm();

        _specHead.Text = "/" + spec.Command;
        _specSubtitle.Text = L.T($"Creator.Command.T.{spec.Id}", spec.Title);
        _usageText.Text = L.T("Creator.Command.Usage", "用法：{0}", spec.Usage);

        if (animate) UiAnimations.FadeSlideIn(_formCard);
        RefreshOutput();
    }

    private void BuildForm()
    {
        _formHost.Children.Clear();
        _fieldsHost.Children.Clear();
        _fieldPanels.Clear();

        _formHost.Children.Add(new StackPanel
        {
            Spacing = 3,
            Margin = new Thickness(2, 0, 0, 6),
            Children =
            {
                new TextBlock
                {
                    Text = L.T("Creator.Command.Form.Title", "参数"),
                    Classes = { "section-title" },
                },
                new TextBlock
                {
                    Text = L.T("Creator.Command.Form.Hint", "改动任意参数，右侧命令实时更新。"),
                    Classes = { "caption" },
                },
            },
        });

        if (_spec.NeedsTarget)
            _formHost.Children.Add(BuildTargetSection());

        foreach (var field in _spec.Fields)
        {
            var panel = BuildFieldPanel(field);
            _fieldPanels[field.Key] = panel;
            _fieldsHost.Children.Add(panel);
        }

        _formHost.Children.Add(_fieldsHost);

        if (_spec.Id == "give") _formHost.Children.Add(BuildComponentsSection());
        if (_spec.Id == "summon") _formHost.Children.Add(BuildNbtSection());

        var reset = new Button
        {
            Content = L.T("Creator.Command.Reset", "重置参数"),
            Classes = { "ghost" },
            Height = 30,
            Padding = new Thickness(13, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        reset.Click += (_, _) =>
        {
            var keep = _values.TryGetValue("__ns", out var ns) ? ns : "1";
            SelectSpec(_spec, animate: false);
            _values["__ns"] = keep;
            RefreshOutput();
            Shell.Toast(L.T("Creator.Command.Toast.Reset", "参数已重置"));
        };
        _formHost.Children.Add(reset);

        ApplyVisibility();
    }

    // ---------------- 目标选择器 ----------------

    private Control BuildTargetSection()
    {
        var preview = new TextBlock
        {
            FontFamily = new FontFamily(MonoFont),
            FontSize = 12.5,
            Foreground = ThemeTokens.Brush("NekoWarningBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
        var nameBox = new TextBox
        {
            PlaceholderText = L.T("Creator.Command.Target.Name", "玩家名"),
            FontSize = 12.5,
            Height = 30,
            Width = 128,
        };
        var chipsHost = new WrapPanel { Orientation = Orientation.Horizontal };

        void RebuildChips()
        {
            chipsHost.Children.Clear();
            var name = (nameBox.Text ?? string.Empty).Trim();

            foreach (var mode in new[] { "@a", "@p", "@s", "@e", "@r" })
            {
                var active = name.Length == 0 && _selectorBase == mode;
                var chip = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(11, 5),
                    Margin = new Thickness(0, 0, 6, 6),
                    Background = active
                        ? ThemeTokens.Brush("NekoAccentSoftBrush")
                        : ThemeTokens.Brush("NekoFieldBrush"),
                    BorderThickness = new Thickness(1),
                    BorderBrush = active
                        ? ThemeTokens.Brush("NekoAccentLineBrush")
                        : ThemeTokens.Brush("NekoBorderBrush"),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Child = new TextBlock
                    {
                        Text = mode,
                        FontFamily = new FontFamily(MonoFont),
                        FontSize = 12.5,
                        FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal,
                        Foreground = active
                            ? ThemeTokens.Brush("NekoAccentBrush")
                            : ThemeTokens.Brush("NekoTextBrush"),
                    },
                };

                var captured = mode;
                chip.PointerPressed += (_, e) =>
                {
                    e.Handled = true;
                    _selectorBase = captured;
                    nameBox.Text = string.Empty;
                    RebuildChips();
                    UpdateSelector();
                };
                chipsHost.Children.Add(chip);
            }

            chipsHost.Children.Add(nameBox);
        }

        void UpdateSelector()
        {
            var name = (nameBox.Text ?? string.Empty).Trim();
            string GetFilter(string key) =>
                _selectorFilters.TryGetValue(key, out var value) ? value : string.Empty;

            var selector = name.Length > 0
                ? name
                : CommandSpecs.BuildSelector(
                    _selectorBase,
                    GetFilter("limit"), GetFilter("sort"), GetFilter("distance"),
                    GetFilter("tag"), GetFilter("gamemode"), GetFilter("levelMin"), GetFilter("levelMax"),
                    GetFilter("x"), GetFilter("y"), GetFilter("z"),
                    GetFilter("dx"), GetFilter("dy"), GetFilter("dz"));
            _values["target"] = selector;
            preview.Text = selector;
            RefreshOutput();
        }

        nameBox.TextChanged += (_, _) =>
        {
            RebuildChips();
            UpdateSelector();
        };

        // 高级筛选（可折叠）
        var filtersPanel = new WrapPanel { Orientation = Orientation.Horizontal };
        var filtersCard = new Border
        {
            Classes = { "card-soft" },
            Padding = new Thickness(10, 10, 2, 2),
            Margin = new Thickness(0, 2, 0, 0),
            IsVisible = false,
            Child = filtersPanel,
        };

        void FilterBox(string key, string label, string placeholder, double width = 118)
        {
            var box = new TextBox
            {
                PlaceholderText = placeholder,
                FontSize = 12,
                Height = 30,
                Width = width,
            };
            box.TextChanged += (_, _) =>
            {
                _selectorFilters[key] = (box.Text ?? string.Empty).Trim();
                UpdateSelector();
            };
            filtersPanel.Children.Add(new StackPanel
            {
                Spacing = 3,
                Margin = new Thickness(0, 0, 8, 8),
                Children = { new TextBlock { Text = label, Classes = { "caption" } }, box },
            });
        }

        void FilterPick(string key, string label, string[] options)
        {
            var combo = new ComboBox
            {
                ItemsSource = options.Prepend(string.Empty).ToArray(),
                SelectedIndex = 0,
                FontSize = 12,
                Height = 30,
                Width = 118,
            };
            combo.SelectionChanged += (_, _) =>
            {
                _selectorFilters[key] = combo.SelectedItem as string ?? string.Empty;
                UpdateSelector();
            };
            filtersPanel.Children.Add(new StackPanel
            {
                Spacing = 3,
                Margin = new Thickness(0, 0, 8, 8),
                Children = { new TextBlock { Text = label, Classes = { "caption" } }, combo },
            });
        }

        FilterBox("limit", L.T("Creator.Command.Target.Limit", "数量上限"), "5");
        FilterPick("sort", L.T("Creator.Command.Target.Sort", "排序"), new[] { "nearest", "furthest", "random", "arbitrary" });
        FilterBox("distance", L.T("Creator.Command.Target.Distance", "距离"), "10 / ..16");
        FilterBox("tag", L.T("Creator.Command.Target.Tag", "标签"), "tag / !tag");
        FilterPick("gamemode", L.T("Creator.Command.Target.Gamemode", "游戏模式"), new[] { "survival", "creative", "adventure", "spectator" });
        FilterBox("levelMin", L.T("Creator.Command.Target.LevelMin", "等级 ≥"), "0");
        FilterBox("levelMax", L.T("Creator.Command.Target.LevelMax", "等级 ≤"), "30");
        FilterBox("x", "x", "~", 74);
        FilterBox("y", "y", "~", 74);
        FilterBox("z", "z", "~", 74);
        FilterBox("dx", "dx", "10", 74);
        FilterBox("dy", "dy", "10", 74);
        FilterBox("dz", "dz", "10", 74);

        var filtersText = L.T("Creator.Command.Target.Filters", "高级筛选（距离 / 标签 / 范围…）");
        var filtersToggle = new Button
        {
            Content = "▸ " + filtersText,
            Classes = { "ghost" },
            Height = 28,
            Padding = new Thickness(10, 0),
            FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        filtersToggle.Click += (_, _) =>
        {
            filtersCard.IsVisible = !filtersCard.IsVisible;
            filtersToggle.Content = (filtersCard.IsVisible ? "▾ " : "▸ ") + filtersText;
        };

        RebuildChips();
        UpdateSelector();

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = L.T("Creator.Command.Target.Title", "目标"),
            Classes = { "section-title" },
        });
        panel.Children.Add(chipsHost);
        panel.Children.Add(preview);
        panel.Children.Add(filtersToggle);
        panel.Children.Add(filtersCard);

        return new Border
        {
            Classes = { "card" },
            Padding = new Thickness(14),
            VerticalAlignment = VerticalAlignment.Top,
            Child = panel,
        };
    }

    // ---------------- 普通字段 ----------------

    private Control BuildFieldPanel(CommandField field)
    {
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 2, 0, 4) };

        if (field.Kind != CommandFieldKind.Toggle)
            panel.Children.Add(new TextBlock { Text = FieldLabel(field), Classes = { "caption" } });

        panel.Children.Add(field.Kind switch
        {
            CommandFieldKind.Pick => BuildPick(field),
            CommandFieldKind.Choice => BuildAutoComplete(field),
            CommandFieldKind.Toggle => BuildToggle(field),
            _ => BuildTextBox(field),
        });

        if (field.Hint is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = FieldHint(field, field.Hint),
                Classes = { "caption" },
                FontSize = 10.5,
                Opacity = 0.85,
            });
        }

        return panel;
    }

    private string FieldLabel(CommandField field) =>
        L.T($"Creator.Command.F.{_spec.Id}.{field.Key}", field.Label);

    private string FieldHint(CommandField field, string fallback) =>
        L.T($"Creator.Command.FH.{_spec.Id}.{field.Key}", fallback);

    private Control BuildTextBox(CommandField field)
    {
        var box = new TextBox
        {
            Text = field.Default,
            PlaceholderText = field.Placeholder,
            FontSize = 12.5,
            Height = 32,
        };
        box.TextChanged += (_, _) => _values[field.Key] = box.Text ?? string.Empty;
        _values[field.Key] = field.Default;
        return box;
    }

    private Control BuildPick(CommandField field)
    {
        var options = field.Options ?? Array.Empty<string>();
        var index = Math.Max(0, Array.IndexOf(options, field.Default));
        var combo = new ComboBox
        {
            ItemsSource = options,
            SelectedIndex = index,
            FontSize = 12.5,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        combo.SelectionChanged += (_, _) =>
        {
            _values[field.Key] = combo.SelectedItem as string ?? options.FirstOrDefault() ?? string.Empty;
            ApplyVisibility();
            RefreshOutput();
        };
        _values[field.Key] = options.Length > 0 ? options[Math.Max(0, index)] : string.Empty;
        return combo;
    }

    private Control BuildToggle(CommandField field)
    {
        var check = new CheckBox
        {
            Content = FieldLabel(field),
            IsChecked = field.Default == bool.TrueString,
            FontSize = 12.5,
            Margin = new Thickness(0, 2, 0, 0),
        };
        check.Click += (_, _) =>
        {
            _values[field.Key] = check.IsChecked == true ? bool.TrueString : bool.FalseString;
            ApplyVisibility();
            RefreshOutput();
        };
        _values[field.Key] = field.Default == bool.TrueString ? bool.TrueString : bool.FalseString;
        return check;
    }

    /// <summary>
    /// 可输入 + 自动补全：TextBox + 下拉建议弹层。
    /// 字段声明了 Catalog 时用 <see cref="ItemCatalog"/> 全量注册表搜索
    /// （中文名 / 英文名 / ID 混合匹配），否则回退到规格里的预设选项。
    /// </summary>
    private Control BuildAutoComplete(CommandField field)
    {
        var fallback = (field.Options ?? Array.Empty<string>())
            .Select(option => ItemOption.OfId(option.Replace("minecraft:", string.Empty)))
            .ToList();

        var box = new TextBox
        {
            Text = field.Default.Replace("minecraft:", string.Empty),
            PlaceholderText = field.Placeholder,
            FontSize = 12.5,
            Height = 32,
        };

        var list = new ListBox { MaxHeight = 236, FontSize = 12.5 };
        list.ItemTemplate = new FuncDataTemplate<ItemOption>((option, _) =>
            option is null ? new Control() : BuildOptionRow(option), supportsRecycling: true);
        var popup = new Popup
        {
            Child = new Border
            {
                Classes = { "card-soft" },
                Padding = new Thickness(4),
                MinWidth = 240,
                Child = new ScrollViewer
                {
                    Content = list,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                },
            },
            PlacementTarget = box,
            Placement = PlacementMode.Bottom,
        };

        // 目录表头：显示数据来源版本，让用户知道译名覆盖到哪个游戏版本
        if (field.Catalog is not null && ItemCatalog.Count(field.Catalog) > 0)
        {
            var popupContent = new StackPanel { Spacing = 2 };
            popupContent.Children.Add(new TextBlock
            {
                Text = L.T("Creator.Command.CatalogHeader", "{0} 条 · 原版 {1}",
                    ItemCatalog.Count(field.Catalog), ItemCatalog.Version),
                Classes = { "caption" },
                Margin = new Thickness(6, 4, 6, 2),
            });
            popupContent.Children.Add(new ScrollViewer
            {
                Content = list,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            });
            popup.Child = new Border
            {
                Classes = { "card-soft" },
                Padding = new Thickness(4),
                MinWidth = 240,
                Child = popupContent,
            };
        }

        var drop = new Button
        {
            Content = "▾",
            Classes = { "ghost" },
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };

        void Refill(string keyword)
        {
            var query = (keyword ?? string.Empty).Trim();
            List<ItemOption> matches;

            if (field.Catalog is not null && ItemCatalog.Count(field.Catalog) > 0)
            {
                matches = ItemCatalog.Search(field.Catalog, query, limit: 12).ToList();
            }
            else if (query.Length == 0)
            {
                matches = fallback.Take(10).ToList();
            }
            else
            {
                matches = fallback
                    .Where(option => option.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(option => option.Id.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .Take(10)
                    .ToList();
            }

            list.ItemsSource = matches;
        }

        bool HasSuggestions()
        {
            return list.ItemsSource is System.Collections.ICollection { Count: > 0 };
        }

        void Commit(string value)
        {
            box.Text = value;
            popup.IsOpen = false;
        }

        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is ItemOption picked) Commit(picked.Id);
        };

        box.TextChanged += (_, _) =>
        {
            _values[field.Key] = box.Text ?? string.Empty;
            RefreshOutput();

            if (!IsAttachedToPage(box)) return;
            Refill(box.Text);
            popup.IsOpen = HasSuggestions();
        };
        box.LostFocus += (_, _) => Dispatcher.UIThread.Post(() => popup.IsOpen = false);

        drop.Click += (_, _) =>
        {
            if (popup.IsOpen)
            {
                popup.IsOpen = false;
                return;
            }

            Refill(box.Text);
            popup.IsOpen = HasSuggestions();
        };

        _values[field.Key] = field.Default.Replace("minecraft:", string.Empty);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(box);
        Grid.SetColumn(drop, 1);
        row.Children.Add(drop);
        // Popup 必须挂在视觉树里才会显示；本身尺寸为 0，不影响布局
        row.Children.Add(popup);
        return row;
    }

    // ---------------- 字段联动可见性 ----------------

    private void ApplyVisibility()
    {
        foreach (var (key, panel) in _fieldPanels)
            panel.IsVisible = IsFieldVisible(key);
    }

    private bool IsFieldVisible(string key) => (_spec.Id, key) switch
    {
        ("effect", "seconds") or ("effect", "amplifier") or ("effect", "hideParticles")
            => _values.TryGetValue("mode", out var mode) && mode == "give",
        ("tp", "facingTarget")
            => _values.TryGetValue("facing", out var facing) && facing == "entity",
        ("tp", "yaw") or ("tp", "pitch")
            => _values.TryGetValue("facing", out var facing) && facing == "rotation",
        ("time", "preset")
            => !_values.TryGetValue("mode", out var mode) || mode == "set",
        ("title", "fadeIn") or ("title", "stay") or ("title", "fadeOut")
            => _values.TryGetValue("useTimes", out var useTimes) && useTimes == bool.TrueString,
        _ => true,
    };

    // ---------------- give 组件面板 ----------------

    private sealed class EnchantRow
    {
        public string Id = string.Empty;
        public string Level = string.Empty;
    }

    private Control BuildComponentsSection()
    {
        var nameBox = new TextBox { PlaceholderText = "Mofu 神剑", FontSize = 12.5, Height = 32 };
        var loreBox = new TextBox
        {
            PlaceholderText = L.T("Creator.Command.Components.LoreHint", "每行一条"),
            FontSize = 12.5,
            MinHeight = 32,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
        };
        var modelBox = new TextBox { PlaceholderText = "0", FontSize = 12.5, Height = 32, Width = 128 };
        var unbreakable = new CheckBox
        {
            Content = L.T("Creator.Command.Components.Unbreakable", "不可破坏"),
            FontSize = 12.5,
        };
        var rowsHost = new StackPanel { Spacing = 6 };

        void RenderRows()
        {
            rowsHost.Children.Clear();

            for (var index = 0; index < _enchants.Count; index++)
            {
                var row = _enchants[index];
                var idBox = new TextBox { Text = row.Id, PlaceholderText = "sharpness", FontSize = 12.5, Height = 30 };
                var levelBox = new TextBox { Text = row.Level, FontSize = 12.5, Height = 30, Width = 52 };

                var captured = index;
                idBox.TextChanged += (_, _) =>
                {
                    _enchants[captured].Id = idBox.Text ?? string.Empty;
                    RebuildComponents();
                };
                levelBox.TextChanged += (_, _) =>
                {
                    _enchants[captured].Level = levelBox.Text ?? string.Empty;
                    RebuildComponents();
                };

                var remove = new Button
                {
                    Content = "×",
                    Classes = { "ghost" },
                    Width = 30,
                    Height = 30,
                    Padding = new Thickness(0),
                    FontSize = 13,
                };
                remove.Click += (_, _) =>
                {
                    _enchants.RemoveAt(captured);
                    RenderRows();
                    RebuildComponents();
                };

                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,58,34") };
                grid.Children.Add(idBox);
                Grid.SetColumn(levelBox, 1);
                levelBox.Margin = new Thickness(6, 0, 0, 0);
                grid.Children.Add(levelBox);
                Grid.SetColumn(remove, 2);
                remove.Margin = new Thickness(4, 0, 0, 0);
                grid.Children.Add(remove);
                rowsHost.Children.Add(grid);
            }

            var add = new Button
            {
                Content = "+ " + L.T("Creator.Command.Components.AddEnchant", "添加附魔"),
                Classes = { "ghost" },
                Height = 28,
                Padding = new Thickness(10, 0),
                FontSize = 11.5,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            add.Click += (_, _) =>
            {
                _enchants.Add(new EnchantRow { Id = "sharpness", Level = "5" });
                RenderRows();
                RebuildComponents();
            };
            rowsHost.Children.Add(add);
        }

        void RebuildComponents()
        {
            _values["components"] = _componentsEnabled
                ? CommandSpecs.BuildComponents(
                    nameBox.Text,
                    _enchants.Select(row => (row.Id,
                        int.TryParse(row.Level, out var level) ? Math.Clamp(level, 1, 255) : 1)).ToList(),
                    unbreakable.IsChecked == true,
                    (loreBox.Text ?? string.Empty).Split(LineBreak, StringSplitOptions.RemoveEmptyEntries)
                        .Select(line => line.Trim()).Where(line => line.Length > 0).ToList(),
                    modelBox.Text ?? string.Empty,
                    _values["__ns"] == "1")
                : string.Empty;
            RefreshOutput();
        }

        nameBox.TextChanged += (_, _) => RebuildComponents();
        loreBox.TextChanged += (_, _) => RebuildComponents();
        modelBox.TextChanged += (_, _) => RebuildComponents();
        unbreakable.Click += (_, _) => RebuildComponents();

        var panel = new StackPanel { Spacing = 9, IsVisible = false };
        panel.Children.Add(Labeled(L.T("Creator.Command.Components.CustomName", "自定义名称"), nameBox));
        panel.Children.Add(Labeled(L.T("Creator.Command.Components.Enchants", "附魔"), rowsHost));
        panel.Children.Add(unbreakable);
        panel.Children.Add(Labeled(L.T("Creator.Command.Components.Lore", "Lore 描述"), loreBox));
        panel.Children.Add(Labeled(L.T("Creator.Command.Components.ModelData", "自定义模型数据"), modelBox));

        var enable = new ToggleButton
        {
            Content = L.T("Creator.Command.Components.Title", "物品组件（1.20.5+）"),
            FontSize = 12.5,
        };
        enable.Click += (_, _) =>
        {
            _componentsEnabled = enable.IsChecked == true;
            panel.IsVisible = _componentsEnabled;
            RebuildComponents();
        };

        var card = new StackPanel { Spacing = 8 };
        card.Children.Add(new TextBlock
        {
            Text = L.T("Creator.Command.Advanced", "高级"),
            Classes = { "section-title" },
        });
        card.Children.Add(enable);
        card.Children.Add(panel);

        RenderRows();
        RebuildComponents();

        return new Border
        {
            Classes = { "card" },
            Padding = new Thickness(14),
            VerticalAlignment = VerticalAlignment.Top,
            Child = card,
        };
    }

    // ---------------- summon NBT 面板 ----------------

    private Control BuildNbtSection()
    {
        var nameBox = new TextBox { PlaceholderText = "Mofu", FontSize = 12.5, Height = 32 };
        var rawBox = new TextBox
        {
            PlaceholderText = L.T("Creator.Command.Nbt.RawHint", "例如 Health:40f, Tame:1b"),
            FontSize = 12.5,
            MinHeight = 32,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
        };

        var noAi = new CheckBox { Content = L.T("Creator.Command.Nbt.NoAi", "无 AI"), FontSize = 12.5 };
        var invulnerable = new CheckBox { Content = L.T("Creator.Command.Nbt.Invulnerable", "无敌"), FontSize = 12.5 };
        var glowing = new CheckBox { Content = L.T("Creator.Command.Nbt.Glowing", "发光"), FontSize = 12.5 };
        var silent = new CheckBox { Content = L.T("Creator.Command.Nbt.Silent", "静音"), FontSize = 12.5 };
        var noGravity = new CheckBox { Content = L.T("Creator.Command.Nbt.NoGravity", "无重力"), FontSize = 12.5 };

        var flags = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var check in new CheckBox[] { noAi, invulnerable, glowing, silent, noGravity })
        {
            check.Margin = new Thickness(0, 0, 16, 6);
            flags.Children.Add(check);
        }

        var panel = new StackPanel { Spacing = 9, IsVisible = false };
        panel.Children.Add(Labeled(L.T("Creator.Command.Components.CustomName", "自定义名称"), nameBox));
        panel.Children.Add(flags);
        panel.Children.Add(Labeled(L.T("Creator.Command.Nbt.Raw", "自定义 SNBT（原样附加）"), rawBox));

        void RebuildNbt()
        {
            _values["nbt"] = _nbtEnabled
                ? CommandSpecs.BuildSummonNbt(
                    nameBox.Text, noAi.IsChecked == true, invulnerable.IsChecked == true,
                    glowing.IsChecked == true, silent.IsChecked == true, noGravity.IsChecked == true,
                    rawBox.Text ?? string.Empty)
                : string.Empty;
            RefreshOutput();
        }

        nameBox.TextChanged += (_, _) => RebuildNbt();
        rawBox.TextChanged += (_, _) => RebuildNbt();
        foreach (var check in new CheckBox[] { noAi, invulnerable, glowing, silent, noGravity })
            check.Click += (_, _) => RebuildNbt();

        var enable = new ToggleButton
        {
            Content = L.T("Creator.Command.Nbt.Title", "实体 NBT"),
            FontSize = 12.5,
        };
        enable.Click += (_, _) =>
        {
            _nbtEnabled = enable.IsChecked == true;
            panel.IsVisible = _nbtEnabled;
            RebuildNbt();
        };

        var card = new StackPanel { Spacing = 8 };
        card.Children.Add(new TextBlock
        {
            Text = L.T("Creator.Command.Advanced", "高级"),
            Classes = { "section-title" },
        });
        card.Children.Add(enable);
        card.Children.Add(panel);

        RebuildNbt();

        return new Border
        {
            Classes = { "card" },
            Padding = new Thickness(14),
            VerticalAlignment = VerticalAlignment.Top,
            Child = card,
        };
    }

    // ================= 右栏：输出渲染 =================

    private string CurrentCommandText() => string.Join(LineBreak, _outputLines);

    private void RefreshOutput()
    {
        _outputLines.Clear();
        try
        {
            _outputLines.AddRange(_spec.Build(_values));
        }
        catch (Exception)
        {
            _outputLines.Add("/" + _spec.Command + " …");
        }

        RenderOutput();
    }

    private void RenderOutput()
    {
        var inlines = _outputBlock.Inlines!;
        inlines.Clear();

        var isFirstLine = true;
        foreach (var line in _outputLines)
        {
            if (!isFirstLine) inlines.Add(new Run(LineBreak) { FontSize = 9 });
            isFirstLine = false;

            var first = true;
            foreach (var token in CommandSpecs.Tokenize(line))
            {
                if (!first) inlines.Add(new Run(" "));
                first = false;
                inlines.Add(new Run(token.Text) { Foreground = BrushOfToken(token.Type) });
            }
        }

        var length = CurrentCommandText().Length;
        var tooLong = length > ChatLimit;
        _lengthChip.Text = tooLong
            ? $"{length} · {L.T("Creator.Command.ChatTooLong", "超 256 字，用命令方块")}"
            : $"{length} · {L.T("Creator.Command.ChatOk", "可粘贴聊天栏")}";
        _lengthChip.Foreground = tooLong
            ? ThemeTokens.Brush("NekoWarningBrush")
            : ThemeTokens.Brush("NekoMutedBrush");
        ToolTip.SetTip(_lengthChip, tooLong
            ? L.T("Creator.Command.ChatTooLongTip", "命令超过聊天栏 256 字符上限，请放入命令方块执行。")
            : L.T("Creator.Command.ChatOkTip", "长度在聊天栏 256 字符限制内，可直接粘贴执行。"));
    }

    private static IBrush BrushOfToken(CommandSpecs.TokenType type) => type switch
    {
        CommandSpecs.TokenType.Head => ThemeTokens.Brush("NekoAccentBrush"),
        CommandSpecs.TokenType.Selector => ThemeTokens.Brush("NekoWarningBrush"),
        CommandSpecs.TokenType.Id => ThemeTokens.Brush("NekoInfoBrush"),
        CommandSpecs.TokenType.Number => ThemeTokens.Brush("NekoSuccessBrush"),
        CommandSpecs.TokenType.Nbt => ThemeTokens.Brush("NekoTerminalTextBrush"),
        CommandSpecs.TokenType.Text => ThemeTokens.Brush("NekoTerminalTextBrush"),
        _ => ThemeTokens.Brush("NekoTextBrush"),
    };

    // ================= 历史 / 收藏 =================

    private sealed class CommandEntry
    {
        [JsonPropertyName("command")] public string Command { get; set; } = string.Empty;
        [JsonPropertyName("count")] public int Count { get; set; } = 1;
        [JsonPropertyName("time")] public long Time { get; set; }
    }

    private sealed class CommandStore
    {
        [JsonPropertyName("history")] public List<CommandEntry> History { get; set; } = new();
        [JsonPropertyName("favorites")] public List<CommandEntry> Favorites { get; set; } = new();
    }

    private static string StorePath => Path.Combine(
        YamlRead.DataRoot, YamlRead.DataDirectoryName, "creator", "commands.json");

    private CommandStore _store = new();

    private void LoadStore()
    {
        try
        {
            if (File.Exists(StorePath))
                _store = JsonSerializer.Deserialize<CommandStore>(File.ReadAllText(StorePath)) ?? new CommandStore();
        }
        catch
        {
            _store = new CommandStore();
        }
    }

    private void SaveStore()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_store, new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
        }
        catch
        {
            // 历史写失败不影响使用
        }
    }

    private void BuildHistoryCard()
    {
        var clear = new Button
        {
            Content = L.T("Creator.Command.Clear", "清空"),
            Classes = { "ghost" },
            Height = 26,
            Padding = new Thickness(10, 0),
            FontSize = 11,
        };
        clear.Click += (_, _) =>
        {
            if (_historyTabFavorites) _store.Favorites.Clear();
            else _store.History.Clear();
            SaveStore();
            RenderTabs();
            RenderHistory();
            Shell.Toast(L.T("Creator.Command.Toast.Cleared", "已清空"));
        };

        var scroll = new ScrollViewer
        {
            Content = _historyHost,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { _tabsHost, clear } };
        Grid.SetColumn(clear, 1);

        var cardGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 8,
            Children = { header },
        };
        Grid.SetRow(scroll, 1);
        cardGrid.Children.Add(scroll);

        _historyCard.Classes.Add("card");
        _historyCard.Padding = new Thickness(14);
        _historyCard.VerticalAlignment = VerticalAlignment.Stretch;
        _historyCard.Child = cardGrid;

        RenderTabs();
        RenderHistory();
    }

    private void RenderTabs()
    {
        _tabsHost.Children.Clear();

        void AddTab(bool favorites)
        {
            var active = _historyTabFavorites == favorites;
            var pill = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 4),
                Background = active
                    ? ThemeTokens.Brush("NekoAccentSoftBrush")
                    : ThemeTokens.Brush("NekoFieldBrush"),
                BorderThickness = new Thickness(1),
                BorderBrush = active
                    ? ThemeTokens.Brush("NekoAccentLineBrush")
                    : ThemeTokens.Brush("NekoBorderBrush"),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock
                {
                    Text = favorites
                        ? L.T("Creator.Command.Favorites", "收藏")
                        : L.T("Creator.Command.History", "最近使用"),
                    FontSize = 11.5,
                    Foreground = active
                        ? ThemeTokens.Brush("NekoAccentBrush")
                        : ThemeTokens.Brush("NekoMutedBrush"),
                },
            };
            pill.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                if (_historyTabFavorites == favorites) return;
                _historyTabFavorites = favorites;
                RenderTabs();
                RenderHistory();
            };
            _tabsHost.Children.Add(pill);
        }

        AddTab(favorites: true);
        AddTab(favorites: false);
    }

    private void RenderHistory()
    {
        _historyHost.Children.Clear();
        var entries = _historyTabFavorites ? _store.Favorites : _store.History;

        if (entries.Count == 0)
        {
            _historyHost.Children.Add(new TextBlock
            {
                Text = _historyTabFavorites
                    ? L.T("Creator.Command.Fav.Empty", "点输出卡片的「收藏」保存常用命令。")
                    : L.T("Creator.Command.Hist.Empty", "复制过的命令会出现在这里，点击即可再次复制。"),
                Classes = { "muted" },
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11.5,
            });
            return;
        }

        foreach (var entry in entries)
        {
            var captured = entry;

            var text = new TextBlock
            {
                Text = entry.Command,
                FontFamily = new FontFamily(MonoFont),
                FontSize = 11.5,
                Foreground = ThemeTokens.Brush("NekoTextBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Cursor = new Cursor(StandardCursorType.Hand),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var copy = new Button
            {
                Content = L.T("Common.Copy", "复制"),
                Classes = { "ghost" },
                Height = 24,
                Padding = new Thickness(9, 0),
                FontSize = 10.5,
            };
            copy.Click += async (_, _) => await CopyTextAsync(captured.Command);

            var starred = _store.Favorites.Any(fav => fav.Command == captured.Command);
            var star = new Button
            {
                Content = starred ? "★" : "☆",
                Classes = { "ghost" },
                Height = 24,
                Width = 26,
                Padding = new Thickness(0),
                FontSize = 12,
            };
            star.Click += (_, _) => ToggleFavorite(captured.Command);

            var remove = new Button
            {
                Content = "×",
                Classes = { "ghost" },
                Height = 24,
                Width = 26,
                Padding = new Thickness(0),
                FontSize = 12,
            };
            remove.Click += (_, _) =>
            {
                if (_historyTabFavorites) _store.Favorites.Remove(captured);
                else _store.History.Remove(captured);
                SaveStore();
                RenderHistory();
            };

            var left = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { text },
            };
            if (!_historyTabFavorites && captured.Count > 1)
            {
                left.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(6, 1),
                    Background = ThemeTokens.Brush("NekoFieldBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = "×" + captured.Count,
                        FontSize = 10,
                        Foreground = ThemeTokens.Brush("NekoMutedBrush"),
                    },
                });
            }

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
                Margin = new Thickness(0, 0, 0, 5),
            };
            text.PointerPressed += async (_, e) =>
            {
                e.Handled = true;
                await CopyTextAsync(captured.Command);
            };
            row.Children.Add(left);
            Grid.SetColumn(copy, 1);
            copy.Margin = new Thickness(6, 0, 2, 0);
            row.Children.Add(copy);
            Grid.SetColumn(star, 2);
            star.Margin = new Thickness(2, 0);
            row.Children.Add(star);
            Grid.SetColumn(remove, 3);
            remove.Margin = new Thickness(2, 0, 0, 0);
            row.Children.Add(remove);

            _historyHost.Children.Add(row);
        }
    }

    private void PushHistory(string command)
    {
        var existing = _store.History.FirstOrDefault(entry => entry.Command == command);
        if (existing is not null)
        {
            existing.Count++;
            existing.Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _store.History.Remove(existing);
            _store.History.Insert(0, existing);
        }
        else
        {
            _store.History.Insert(0, new CommandEntry
            {
                Command = command,
                Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
        }

        while (_store.History.Count > HistoryLimit)
            _store.History.RemoveAt(_store.History.Count - 1);

        SaveStore();
    }

    private void ToggleFavorite(string command)
    {
        var existing = _store.Favorites.FirstOrDefault(entry => entry.Command == command);
        if (existing is not null)
        {
            _store.Favorites.Remove(existing);
            Shell.Toast(L.T("Creator.Command.Toast.Unstarred", "已从收藏移除"));
        }
        else
        {
            _store.Favorites.Insert(0, new CommandEntry
            {
                Command = command,
                Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            Shell.Toast(L.T("Creator.Command.Toast.Starred", "已加入收藏"));
        }

        SaveStore();
        RenderHistory();
    }

    // ================= 复制 =================

    private async void CopyCurrent()
    {
        var text = CurrentCommandText();
        await CopyTextAsync(text);
        PushHistory(text);
        if (!_historyTabFavorites) RenderHistory();
    }

    private async Task CopyTextAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            Shell.Toast(L.T("Creator.Command.Toast.NoClipboard", "剪贴板不可用"));
            return;
        }

        await clipboard.SetTextAsync(text);
        Shell.Toast(L.T("Creator.Command.Toast.Copied", "命令已复制到剪贴板"));
    }

    // ================= 通用小部件 =================

    private static StackPanel Labeled(string label, Control control) => new()
    {
        Spacing = 4,
        Children =
        {
            new TextBlock { Text = label, Classes = { "caption" } },
            control,
        },
    };

    /// <summary>自动补全选项行：中文名（主）+ ID（等宽弱化）；无译名时只显示 ID。</summary>
    private static Control BuildOptionRow(ItemOption option)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (option.Zh.Length > 0)
        {
            row.Children.Add(new TextBlock
            {
                Text = option.Zh,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        row.Children.Add(new TextBlock
        {
            Text = option.Id,
            FontFamily = new FontFamily(MonoFont),
            FontSize = option.Zh.Length > 0 ? 11 : 12.5,
            Foreground = ThemeTokens.Brush(option.Zh.Length > 0 ? "NekoMutedBrush" : "NekoTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        return row;
    }

    /// <summary>控件是否已挂在本页可见分支上（建表单期间的 TextChanged 不弹建议层）。</summary>
    private bool IsAttachedToPage(Control control)
    {
        for (var current = (Visual?)control; current is not null; current = current.GetVisualParent())
        {
            if (current is CommandGeneratorPage page && ReferenceEquals(page, this)) return true;
        }

        return false;
    }
}
