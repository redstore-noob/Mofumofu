using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Shapes = Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Mofumofu.Launcher.Controls;
using Mofumofu.Launcher.Services;
using Mofumofu.Launcher.Services.Agent;

namespace Mofumofu.Launcher.Views;

public partial class MofuAgentSettingsView : UserControl
{
    private const int MaxHistoryNotes = 60;

    private readonly AgentSettings _settings;
    private bool _loadingControls;
    private readonly List<string> _draftModels = new();
    private string _formProviderId = string.Empty;
    private string? _pendingDeleteProviderId;
    private DateTime _pendingDeleteAt;
    private DateTime _pendingKeyResetAt;
    private static bool _focusProviderRequested;

    private bool _keyUnreadable;
    private string _unreadableKey = string.Empty;
    private bool _headersUnreadable;
    private string _unreadableHeaders = string.Empty;

    private bool _creatingProvider;

    public static void RequestFocusProvider() => _focusProviderRequested = true;

    public MofuAgentSettingsView()
    {
        InitializeComponent();

        _settings = AgentSettingsStore.Load();

        Loaded += (_, _) =>
        {
            FillControls();

            if (!_focusProviderRequested) return;
            _focusProviderRequested = false;
            SelectTab("provider");
            OnAddProvider(null, new RoutedEventArgs());
            UiAnimations.Pulse(AddProviderButton, 1.04, 280);
        };
    }

    private void OnBack(object? sender, RoutedEventArgs e) => Shell.Go("mofuagent");

    private void OnSettingsTab(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tab }) SelectTab(tab);
    }

    private void SelectTab(string tab)
    {
        PaneProvider.IsVisible = tab == "provider";
        PaneGeneration.IsVisible = tab == "generation";
        PaneContext.IsVisible = tab == "context";
        PaneTools.IsVisible = tab == "tools";
        PaneData.IsVisible = tab == "data";

        TabProvider.Classes.Set("active", tab == "provider");
        TabGeneration.Classes.Set("active", tab == "generation");
        TabContext.Classes.Set("active", tab == "context");
        TabTools.Classes.Set("active", tab == "tools");
        TabData.Classes.Set("active", tab == "data");

        if (tab == "data") RefreshMemoryPanel();
        if (tab == "tools") RefreshToolToggles();
        if (tab == "provider") RefreshProviderList();
    }

    private void FillControls()
    {
        _loadingControls = true;

        SettingsPathText.Text = "本地配置\nsettings.json";
        ToolTip.SetTip(SettingsPathText, AgentSettingsStore.SettingsPath);
        DataPathBox.Text = AgentSettingsStore.RootDirectory;
        KeyPathText.Text = AgentSecretProtector.KeyPath;

        List<string> kindNames = AgentProviderPresets.AllKinds
            .Select(kind => AgentProviderPresets.DescribeKind(kind))
            .ToList();
        ProviderKindCombo.ItemsSource = kindNames;
        NewKindCombo.ItemsSource = kindNames;

        PresetCombo.ItemsSource = AgentProviderPresets.All
            .Select(preset => preset.Name)
            .ToList();
        PresetCombo.SelectedIndex = -1;
        UpdatePresetHint();

        RefreshProviderList();
        AgentProviderConfig? initial = _settings.EnsureProvider();
        if (initial is not null) LoadProviderForm(initial);
        else ClearProviderForm();

        GenTopPSlider.Value = _settings.TopP;
        GenMaxTokensBox.Text = _settings.MaxOutputTokens.ToString();
        GenMaxRoundsBox.Text = _settings.MaxToolRounds.ToString();
        StreamingSwitch.IsChecked = _settings.Streaming;
        SystemPromptBox.Text = _settings.SystemPrompt;

        WindowPresetCombo.ItemsSource = new List<string> { "8k", "16k", "32k", "64k", "128k", "200k", "1M" };
        WindowPresetCombo.SelectedIndex = -1;
        WindowCustomBox.Text = _settings.ContextWindow.ToString();

        AutoCompactSwitch.IsChecked = _settings.AutoCompact;
        CompactAtSlider.Value = _settings.CompactAt;
        KeepRecentBox.Text = _settings.KeepRecentMessages.ToString();

        ToolsEnabledSwitch.IsChecked = _settings.Tools.Enabled;
        LauncherToolsSwitch.IsChecked = _settings.Tools.LauncherControl;
        ConfirmWritesSwitch.IsChecked = _settings.Tools.ConfirmWrites;
        ToolTimeoutBox.Text = _settings.Tools.TimeoutSeconds.ToString();

        _loadingControls = false;

        UpdateLabels();
        RefreshToolToggles();
        RefreshMemoryPanel();
        SelectTab("provider");

        if (!string.IsNullOrEmpty(AgentSettingsStore.LastError))
            ShowProviderResult(AgentSettingsStore.LastError, "warning");
    }

    private void UpdateLabels()
    {
        GenTopPText.Text = GenTopPSlider.Value.ToString("0.00");
        CompactAtText.Text = (CompactAtSlider.Value * 100).ToString("0") + "%";
    }

    private AgentProviderConfig? CurrentProvider =>
        _settings.Providers.FirstOrDefault(p => p.Id == _formProviderId);

    private void UpdateProviderPaneState()
    {
        bool selected = !_creatingProvider &&
                        _settings.Providers.Any(p => p.Id == _formProviderId);
        ProviderCreateCard.IsVisible = _creatingProvider;
        ProviderEditor.IsVisible = selected;
        ProviderEditor.IsEnabled = selected;
        ProviderEmptyState.IsVisible = !_creatingProvider && !selected;
    }

    private void RefreshProviderList()
    {
        ProviderListPanel.Children.Clear();
        ProviderCountText.Text = _settings.Providers.Count + " 条";
        UpdateProviderPaneState();

        if (_settings.Providers.Count == 0)
        {
            ProviderListPanel.Children.Add(new TextBlock
            {
                Text = "还没有供应商。\n点下面的「添加供应商」开始。",
                FontSize = 10.5,
                LineHeight = 17,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeTokens.Brush("NekoDimBrush"),
                Margin = new Avalonia.Thickness(4, 10, 4, 4),
            });
            return;
        }

        foreach (AgentProviderConfig provider in _settings.Providers)
        {
            bool active = provider.Id == _settings.ActiveProviderId;
            bool editing = provider.Id == _formProviderId;
            var (status, tone) = AgentProviderPresets.DescribeStatus(provider);

            var row = new Border
            {
                Classes = { "provider-row" },
                Tag = provider,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            if (editing) row.Classes.Add("active");
            ToolTip.SetTip(row, active ? "当前正在使用 · 双击切换" : "点击编辑，双击设为当前");

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            grid.Children.Add(new Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = ThemeTokens.Brush(tone switch
                {
                    "success" => "NekoSuccessBrush",
                    "warning" => "NekoWarningBrush",
                    "danger" => "NekoDangerBrush",
                    _ => "NekoInfoBrush",
                }),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(0, 0, 8, 0),
            });

            var label = new StackPanel { Spacing = 1 };
            label.Children.Add(new TextBlock
            {
                Text = provider.Name,
                FontSize = 12.5,
                FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal,
                Foreground = ThemeTokens.Brush("NekoTextBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            label.Children.Add(new TextBlock
            {
                Text = AgentProviderPresets.DescribeKind(provider.Kind) + " · " + status,
                FontSize = 10,
                Foreground = ThemeTokens.Brush("NekoDimBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);

            if (active)
            {
                var badge = new TextBlock
                {
                    Text = "当前",
                    FontSize = 9,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeTokens.Brush("NekoAccentBrush"),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Margin = new Avalonia.Thickness(6, 0, 0, 0),
                };
                Grid.SetColumn(badge, 2);
                grid.Children.Add(badge);
            }

            row.Child = grid;
            row.PointerPressed += (_, _) => SelectProviderForEditing(provider);
            row.DoubleTapped += (_, _) =>
            {
                SelectProviderForEditing(provider);
                SetActiveProvider(provider);
            };

            ProviderListPanel.Children.Add(row);
        }
    }

    private void SelectProviderForEditing(AgentProviderConfig provider)
    {
        _creatingProvider = false;
        if (provider.Id == _formProviderId)
        {
            RefreshProviderList();
            return;
        }

        _pendingDeleteProviderId = null;
        LoadProviderForm(provider);
        RefreshProviderList();
        ProviderResultText.Text = string.Empty;
    }

    private void LoadProviderForm(AgentProviderConfig provider)
    {
        bool wasLoading = _loadingControls;
        _loadingControls = true;
        _formProviderId = provider.Id;
        ProviderNameBox.Text = provider.Name;
        ProviderKindCombo.SelectedIndex = AgentProviderPresets.IndexOfKind(provider.Kind);
        BaseUrlBox.Text = provider.BaseUrl;
        _keyUnreadable = AgentSecretProtector.LooksEncrypted(provider.ApiKey);
        _unreadableKey = _keyUnreadable ? provider.ApiKey : string.Empty;
        _headersUnreadable = AgentSecretProtector.LooksEncrypted(provider.ExtraHeaders);
        _unreadableHeaders = _headersUnreadable ? provider.ExtraHeaders : string.Empty;

        ApiKeyBox.Text = _keyUnreadable ? string.Empty : provider.ApiKey;
        ApiKeyBox.PlaceholderText = _keyUnreadable ? "原 Key 无法解密，请重新填写" : "sk-...";
        HeadersBox.Text = _headersUnreadable ? string.Empty : provider.ExtraHeaders;
        HeadersBox.PlaceholderText = _headersUnreadable ? "原请求头无法解密，请重新填写" : string.Empty;
        ProviderContextBox.Text = provider.ContextWindow > 0 ? provider.ContextWindow.ToString() : string.Empty;
        SupportsToolsSwitch.IsChecked = provider.SupportsTools;

        _draftModels.Clear();
        _draftModels.AddRange(provider.Models);
        RenderModelChips();
        _loadingControls = wasLoading;

        _creatingProvider = false;
        UpdateProviderHint();
        UpdateProviderPaneState();
    }

    private void UpdateProviderHint()
    {
        AgentProviderConfig? provider = CurrentProvider;
        if (provider is null)
        {
            ProviderHintText.Text = string.Empty;
            return;
        }

        int window = provider.ContextWindow > 0
            ? provider.ContextWindow
            : AgentProviderPresets.GuessContextWindow(_draftModels.FirstOrDefault() ?? string.Empty);
        string models = _draftModels.Count == 0 ? "未添加模型" : _draftModels.Count + " 个模型";
        ProviderHintText.Text =
            $"{AgentProviderPresets.DescribeKind(provider.Kind)} · {models} · 上下文 {TokenEstimator.Format(window)}";
    }

    private void RenderModelChips()
    {
        ModelChipsPanel.Children.Clear();

        foreach (string model in _draftModels)
        {
            var chip = new Border
            {
                Classes = { "model-chip" },
            };
            ToolTip.SetTip(chip, model);

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(new TextBlock
            {
                Text = model,
                FontSize = 10.5,
                MaxWidth = 190,
                Foreground = ThemeTokens.Brush("NekoTextBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });

            var remove = new Button
            {
                Classes = { "icon" },
                Width = 16,
                Height = 16,
                Padding = new Avalonia.Thickness(0),
                Margin = new Avalonia.Thickness(4, 0, 0, 0),
                Content = new IconParkIcon { Kind = "IconClose", Width = 8, Height = 8 },
            };
            ToolTip.SetTip(remove, "移除 " + model);
            remove.Click += (_, _) =>
            {
                _draftModels.Remove(model);
                RenderModelChips();
            };
            Grid.SetColumn(remove, 1);
            grid.Children.Add(remove);

            chip.Child = grid;
            ModelChipsPanel.Children.Add(chip);
        }

        ModelCountText.Text = _draftModels.Count + " 个";
        UpdateProviderHint();
    }

    private void ClearProviderForm()
    {
        bool wasLoading = _loadingControls;
        _loadingControls = true;
        _formProviderId = string.Empty;
        _pendingDeleteProviderId = null;
        ProviderNameBox.Text = string.Empty;
        ProviderKindCombo.SelectedIndex = 0;
        BaseUrlBox.Text = string.Empty;
        ApiKeyBox.Text = string.Empty;
        ApiKeyBox.PlaceholderText = "sk-...";
        HeadersBox.Text = string.Empty;
        HeadersBox.PlaceholderText = string.Empty;
        ProviderContextBox.Text = string.Empty;
        SupportsToolsSwitch.IsChecked = true;
        _keyUnreadable = false;
        _unreadableKey = string.Empty;
        _headersUnreadable = false;
        _unreadableHeaders = string.Empty;
        _draftModels.Clear();
        RenderModelChips();
        _loadingControls = wasLoading;
        _creatingProvider = false;
        ProviderHintText.Text = string.Empty;
        ProviderResultText.Text = string.Empty;
        UpdateProviderPaneState();
    }

    private void AddModelIds(IEnumerable<string> ids)
    {
        int added = 0;
        foreach (string raw in ids)
        {
            foreach (string part in raw.Split(new[] { ',', '，', ';', '\n' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                string id = part.Trim();
                if (id.Length == 0) continue;
                if (_draftModels.Contains(id, StringComparer.OrdinalIgnoreCase)) continue;

                _draftModels.Add(id);
                added++;
            }
        }

        if (added > 0) RenderModelChips();
    }

    private void OnNewModelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        AddModelIds(new[] { NewModelBox.Text ?? string.Empty });
        NewModelBox.Text = string.Empty;
    }

    private void OnAddModel(object? sender, RoutedEventArgs e)
    {
        string text = NewModelBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowProviderResult("先输入模型 id。", "warning");
            return;
        }

        AddModelIds(new[] { text });
        NewModelBox.Text = string.Empty;
        NewModelBox.Focus();
    }

    private async void OnFetchModels(object? sender, RoutedEventArgs e)
    {
        AgentProviderConfig? draft = BuildDraft(out string error, allowEmptyModels: true);
        if (draft is null)
        {
            ShowProviderResult(error, "danger");
            return;
        }

        FetchModelsButton.IsEnabled = false;
        ShowProviderResult("正在拉取模型列表…", "muted");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        (bool ok, string message, List<string> models) = await AgentClient.ListModelsAsync(draft, cts.Token);

        FetchModelsButton.IsEnabled = true;

        if (!ok)
        {
            ShowProviderResult(message, "danger");
            return;
        }

        int before = _draftModels.Count;
        AddModelIds(models);
        int added = _draftModels.Count - before;

        if (_draftModels.Count == 0)
        {
            _draftModels.AddRange(models);
            RenderModelChips();
        }

        ShowProviderResult(
            added > 0
                ? $"{message} 已加入 {added} 个新模型。"
                : $"{message} 没有发现新模型。",
            "success");
    }

    private AgentProviderConfig? BuildDraft(out string error, bool allowEmptyModels = false)
    {
        error = string.Empty;
        AgentProviderConfig? provider = CurrentProvider;
        if (provider is null)
        {
            error = "先在左侧选一条供应商。";
            return null;
        }

        var draft = provider.Clone();
        draft.Name = (ProviderNameBox.Text ?? string.Empty).Trim();
        draft.Kind = AgentProviderPresets.KindAt(ProviderKindCombo.SelectedIndex);
        draft.BaseUrl = AgentProviderPresets.NormalizeBaseUrl(BaseUrlBox.Text);
        draft.ApiKey = (ApiKeyBox.Text ?? string.Empty).Trim();
        if (_keyUnreadable && draft.ApiKey.Length == 0) draft.ApiKey = _unreadableKey;
        draft.Models = new List<string>(_draftModels);
        draft.ContextWindow = ParseInt(ProviderContextBox.Text, 0, 0, 4_000_000);
        draft.SupportsTools = SupportsToolsSwitch.IsChecked == true;
        draft.ExtraHeaders = HeadersBox.Text ?? string.Empty;
        if (_headersUnreadable && draft.ExtraHeaders.Trim().Length == 0)
            draft.ExtraHeaders = _unreadableHeaders;

        if (draft.Name.Length == 0)
        {
            error = "名称不能为空。";
            return null;
        }

        if (draft.BaseUrl.Length == 0)
        {
            error = "Base URL 不能为空，且只支持 http:// 或 https:// 开头的地址。";
            return null;
        }

        if (!allowEmptyModels && draft.Models.Count == 0)
        {
            error = "至少添加一个模型 id。";
            return null;
        }

        return draft;
    }

    private bool TryApplyForm(bool showError)
    {
        AgentProviderConfig? draft = BuildDraft(out string error);
        if (draft is null)
        {
            if (showError) ShowProviderResult(error, "danger");
            return false;
        }

        AgentProviderConfig provider = CurrentProvider!;
        provider.Name = draft.Name;
        provider.Kind = draft.Kind;
        provider.BaseUrl = draft.BaseUrl;
        provider.ApiKey = draft.ApiKey;
        provider.Models = draft.Models;
        provider.ContextWindow = draft.ContextWindow;
        provider.SupportsTools = draft.SupportsTools;
        provider.ExtraHeaders = draft.ExtraHeaders;

        if (provider.Id == _settings.ActiveProviderId)
        {
            if (!provider.Models.Contains(_settings.ActiveModel))
                _settings.ActiveModel = provider.Models.FirstOrDefault() ?? string.Empty;
            _settings.ContextWindow = provider.ContextWindow > 0
                ? provider.ContextWindow
                : AgentProviderPresets.GuessContextWindow(_settings.ActiveModel);
        }

        return true;
    }

    private void OnProviderKindChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingControls) return;
        UpdateProviderHint();
    }

    private void OnAddProvider(object? sender, RoutedEventArgs e)
    {
        _creatingProvider = true;
        _pendingDeleteProviderId = null;

        _loadingControls = true;
        PresetCombo.SelectedIndex = -1;
        NewNameBox.Text = string.Empty;
        NewKindCombo.SelectedIndex =
            AgentProviderPresets.IndexOfKind(AgentProviderKind.OpenAiCompatible);
        NewBaseUrlBox.Text = string.Empty;
        NewApiKeyBox.Text = string.Empty;
        _loadingControls = false;
        UpdatePresetHint();

        RefreshProviderList();
        ProviderResultText.Text = string.Empty;
        Dispatcher.UIThread.Post(() => NewNameBox.Focus());
    }

    private void OnCancelCreate(object? sender, RoutedEventArgs e)
    {
        _creatingProvider = false;

        if (_settings.Providers.Count > 0 &&
            _settings.Providers.All(p => p.Id != _formProviderId))
            LoadProviderForm(_settings.Providers[0]);

        RefreshProviderList();
        ProviderResultText.Text = string.Empty;
    }

    private void OnPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingControls) return;
        UpdatePresetHint();

        int index = PresetCombo.SelectedIndex;
        if (index < 0 || index >= AgentProviderPresets.All.Count) return;
        AgentProviderPreset preset = AgentProviderPresets.All[index];

        _loadingControls = true;
        NewNameBox.Text = preset.Name;
        NewKindCombo.SelectedIndex = AgentProviderPresets.IndexOfKind(preset.Kind);
        NewBaseUrlBox.Text = preset.BaseUrl;
        _loadingControls = false;
    }

    private void UpdatePresetHint()
    {
        int index = PresetCombo.SelectedIndex;
        PresetHint.Text = index >= 0 && index < AgentProviderPresets.All.Count
            ? AgentProviderPresets.All[index].Note
            : string.Empty;
    }

    private void OnCreateProvider(object? sender, RoutedEventArgs e)
    {
        string name = (NewNameBox.Text ?? string.Empty).Trim();
        string baseUrl = AgentProviderPresets.NormalizeBaseUrl(NewBaseUrlBox.Text);

        if (name.Length == 0)
        {
            ShowProviderResult("先给这条供应商起个名字。", "warning");
            NewNameBox.Focus();
            return;
        }

        if (baseUrl.Length == 0)
        {
            ShowProviderResult("Base URL 不能为空，且只支持 http:// 或 https:// 开头的地址。", "warning");
            NewBaseUrlBox.Focus();
            return;
        }

        AgentProviderConfig provider = new()
        {
            Id = Guid.NewGuid().ToString("n")[..8],
            Name = name,
            Kind = AgentProviderPresets.KindAt(NewKindCombo.SelectedIndex),
            BaseUrl = baseUrl,
            ApiKey = (NewApiKeyBox.Text ?? string.Empty).Trim(),
            Models = new List<string>(),
            SupportsTools = true,
        };

        _settings.Providers.Add(provider);
        _settings.ActiveProviderId = provider.Id;
        _settings.ActiveModel = string.Empty;
        AgentSettingsStore.Save(_settings);

        _creatingProvider = false;
        _formProviderId = provider.Id;
        RefreshProviderList();
        LoadProviderForm(provider);
        ShowProviderResult($"已添加 {provider.Name}。接着填入模型 id，或点「拉取模型」。", "success");
        Dispatcher.UIThread.Post(() => NewModelBox.Focus());
        Shell.Toast("已添加 " + provider.Name);
    }

    private void OnDeleteProvider(object? sender, RoutedEventArgs e)
    {
        AgentProviderConfig? provider = CurrentProvider;
        if (provider is null) return;

        if (_pendingDeleteProviderId != provider.Id ||
            DateTime.UtcNow - _pendingDeleteAt > TimeSpan.FromSeconds(4))
        {
            _pendingDeleteProviderId = provider.Id;
            _pendingDeleteAt = DateTime.UtcNow;
            ShowProviderResult("再点一次「删除」确认。", "warning");
            return;
        }

        _pendingDeleteProviderId = null;
        _settings.Providers.Remove(provider);
        if (_settings.ActiveProviderId == provider.Id)
        {
            AgentProviderConfig? next = _settings.Providers.FirstOrDefault();
            _settings.ActiveProviderId = next?.Id ?? string.Empty;
            _settings.ActiveModel = next?.Models.FirstOrDefault() ?? string.Empty;
            if (next is not null)
                _settings.ContextWindow = next.ContextWindow > 0
                    ? next.ContextWindow
                    : AgentProviderPresets.GuessContextWindow(_settings.ActiveModel);
        }

        AgentSettingsStore.Save(_settings);

        if (_settings.Providers.Count == 0)
        {
            ClearProviderForm();
            RefreshProviderList();
            ShowProviderResult("已删除，列表已清空。需要时点「添加供应商」。", "muted");
            Shell.Toast("供应商已删除。");
            return;
        }

        // 删掉的不是当前供应商时，编辑台留在当前供应商上，不要莫名其妙跳到第一条。
        AgentProviderConfig select = _settings.Providers
            .FirstOrDefault(p => p.Id == _settings.ActiveProviderId) ?? _settings.Providers[0];
        LoadProviderForm(select);
        RefreshProviderList();
        ShowProviderResult("已删除。", "muted");
        Shell.Toast("供应商已删除。");
    }

    private void OnSaveProvider(object? sender, RoutedEventArgs e)
    {
        if (!TryApplyForm(showError: true)) return;

        AgentSettingsStore.Save(_settings);
        RefreshProviderList();
        LoadProviderForm(CurrentProvider!);
        ShowProviderResult("已保存。", "success");
        Shell.Toast("供应商已保存。");
    }

    private void OnSetActiveProvider(object? sender, RoutedEventArgs e)
    {
        if (!TryApplyForm(showError: true)) return;
        SetActiveProvider(CurrentProvider!);
    }

    private void SetActiveProvider(AgentProviderConfig provider)
    {
        _settings.ActiveProviderId = provider.Id;
        _settings.ActiveModel = provider.Models.FirstOrDefault() ?? string.Empty;
        _settings.ContextWindow = provider.ContextWindow > 0
            ? provider.ContextWindow
            : AgentProviderPresets.GuessContextWindow(_settings.ActiveModel);
        AgentSettingsStore.Save(_settings);

        RefreshProviderList();
        ShowProviderResult("已设为当前供应商。", "success");
        Shell.Toast("当前供应商：" + provider.Name);
    }

    private void OnToggleKeyVisibility(object? sender, RoutedEventArgs e) =>
        ApiKeyBox.PasswordChar = ApiKeyBox.PasswordChar == '\0' ? '●' : '\0';

    private async void OnTestProvider(object? sender, RoutedEventArgs e)
    {
        AgentProviderConfig? draft = BuildDraft(out string error);
        if (draft is null)
        {
            ShowProviderResult(error, "danger");
            return;
        }

        string model = draft.Models.FirstOrDefault() ?? string.Empty;
        if (model.Length == 0)
        {
            ShowProviderResult("至少添加一个模型 id。", "warning");
            return;
        }

        TestProviderButton.IsEnabled = false;
        ShowProviderResult("正在连接 " + draft.Name + " …", "muted");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        (bool ok, string message) = await AgentClient.TestAsync(draft, model, cts.Token);

        TestProviderButton.IsEnabled = true;
        ShowProviderResult(message, ok ? "success" : "danger");
    }

    private void ShowProviderResult(string message, string tone)
    {
        ProviderResultText.Text = message;
        ProviderResultText.Foreground = ThemeTokens.Brush(tone switch
        {
            "success" => "NekoSuccessBrush",
            "danger" => "NekoDangerBrush",
            "warning" => "NekoWarningBrush",
            _ => "NekoMutedBrush",
        });
    }

    private void OnGenTopPChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loadingControls) return;
        GenTopPText.Text = GenTopPSlider.Value.ToString("0.00");
    }

    private void OnCompactAtChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loadingControls) return;
        CompactAtText.Text = (CompactAtSlider.Value * 100).ToString("0") + "%";
    }

    private void OnSaveGeneration(object? sender, RoutedEventArgs e)
    {
        _settings.TopP = Math.Round(GenTopPSlider.Value, 2);
        _settings.MaxOutputTokens = ParseInt(GenMaxTokensBox.Text, 2048, 64, 32768);
        _settings.MaxToolRounds = ParseInt(GenMaxRoundsBox.Text, 6, 1, 20);
        _settings.Streaming = StreamingSwitch.IsChecked == true;
        _settings.SystemPrompt = string.IsNullOrWhiteSpace(SystemPromptBox.Text)
            ? AgentSettings.DefaultSystemPrompt
            : SystemPromptBox.Text!;

        AgentSettingsStore.Save(_settings);
        FillControls();
        SelectTab("generation");
        Shell.Toast("生成设置已保存。");
    }

    private void OnSaveContext(object? sender, RoutedEventArgs e)
    {
        _settings.ContextWindow = ParseInt(WindowCustomBox.Text, _settings.ContextWindow, 2048, 4_000_000);
        _settings.AutoCompact = AutoCompactSwitch.IsChecked == true;
        _settings.CompactAt = Math.Round(CompactAtSlider.Value, 2);
        _settings.KeepRecentMessages = ParseInt(KeepRecentBox.Text, 8, 2, 60);

        AgentSettingsStore.Save(_settings);
        FillControls();
        SelectTab("context");
        Shell.Toast("上下文设置已保存。");
    }

    private void OnSaveTools(object? sender, RoutedEventArgs e)
    {
        _settings.Tools.Enabled = ToolsEnabledSwitch.IsChecked == true;
        _settings.Tools.LauncherControl = LauncherToolsSwitch.IsChecked == true;
        _settings.Tools.ConfirmWrites = ConfirmWritesSwitch.IsChecked == true;
        _settings.Tools.TimeoutSeconds = ParseInt(ToolTimeoutBox.Text, 20, 3, 120);

        _settings.Tools.DisabledTools.Clear();
        foreach (Control child in ToolListPanel.Children)
        {
            if (child is CheckBox { Tag: string name, IsChecked: false })
                _settings.Tools.DisabledTools.Add(name);
        }

        AgentSettingsStore.Save(_settings);
        FillControls();
        SelectTab("tools");
        Shell.Toast("工具设置已保存。");
    }

    private void RefreshToolToggles()
    {
        ToolListPanel.Children.Clear();
        var registry = AgentToolRegistry.CreateDefault();

        foreach (AgentToolDefinition tool in registry.All.OrderBy(tool => tool.Name))
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Avalonia.Thickness(0, 3) };
            var label = new StackPanel { Spacing = 1 };
            label.Children.Add(new TextBlock
            {
                Text = tool.Name,
                FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"),
                FontSize = 11.5,
                Foreground = ThemeTokens.Brush("NekoTextBrush"),
            });
            label.Children.Add(new TextBlock
            {
                Text = tool.Description.Length > 54 ? tool.Description[..54] + "…" : tool.Description,
                FontSize = 10.5,
                Foreground = ThemeTokens.Brush("NekoDimBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
            row.Children.Add(label);

            var check = new CheckBox
            {
                Tag = tool.Name,
                IsChecked = !_settings.Tools.DisabledTools.Contains(tool.Name, StringComparer.OrdinalIgnoreCase),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            Grid.SetColumn(check, 1);
            row.Children.Add(check);

            ToolListPanel.Children.Add(row);
        }
    }

    private void OnWindowPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingControls) return;
        if (WindowPresetCombo.SelectedItem is not string preset) return;

        WindowCustomBox.Text = preset switch
        {
            "8k" => "8192",
            "16k" => "16384",
            "32k" => "32768",
            "64k" => "65536",
            "128k" => "131072",
            "200k" => "200000",
            "1M" => "1000000",
            _ => WindowCustomBox.Text,
        };
    }

    private void OnApplyWindow(object? sender, RoutedEventArgs e)
    {
        _settings.ContextWindow = ParseInt(WindowCustomBox.Text, _settings.ContextWindow, 2048, 4_000_000);
        AgentSettingsStore.Save(_settings);
        Shell.Toast($"上下文窗口已设为 {TokenEstimator.Format(_settings.ContextWindow)}。");
    }

    private void OnOpenDataFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AgentSettingsStore.RootDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = AgentSettingsStore.RootDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception error)
        {
            Shell.Toast("打不开目录：" + error.Message);
        }
    }

    private void OnClearAllSessions(object? sender, RoutedEventArgs e)
    {
        foreach (AgentConversation conversation in AgentStore.List())
            AgentStore.Delete(conversation.Id);

        Shell.Toast("已清空全部会话。");
    }

    private void RefreshMemoryPanel()
    {
        MemoryPanel.Children.Clear();
        IReadOnlyList<string> notes = AgentMemory.All();

        if (notes.Count == 0)
        {
            MemoryPanel.Children.Add(new TextBlock
            {
                Text = "还没有记忆。跟它说「记住我喜欢 1.20.1 整合包」试试。",
                FontSize = 11,
                Foreground = ThemeTokens.Brush("NekoDimBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (string note in notes.Reverse().Take(MaxHistoryNotes))
        {
            MemoryPanel.Children.Add(new Border
            {
                Background = ThemeTokens.Brush("NekoFieldSoftBrush"),
                CornerRadius = new Avalonia.CornerRadius(6),
                Padding = new Avalonia.Thickness(8, 5),
                Child = new TextBlock
                {
                    Text = note,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeTokens.Brush("NekoMutedBrush"),
                },
            });
        }
    }

    private void OnResetKey(object? sender, RoutedEventArgs e)
    {
        if (DateTime.UtcNow - _pendingKeyResetAt > TimeSpan.FromSeconds(4))
        {
            _pendingKeyResetAt = DateTime.UtcNow;
            Shell.Toast("再点一次确认：会删除主密钥并清空所有供应商的 Key。");
            return;
        }

        _pendingKeyResetAt = default;
        AgentSecretProtector.ResetKey();

        foreach (AgentProviderConfig provider in _settings.Providers)
        {
            provider.ApiKey = string.Empty;
            provider.ExtraHeaders = string.Empty;
        }

        AgentSettingsStore.Save(_settings);

        AgentProviderConfig? current = CurrentProvider;
        if (current is not null) LoadProviderForm(current);
        else ClearProviderForm();
        RefreshProviderList();
        Shell.Toast("已重置加密密钥，请重新填写各供应商的 API Key。");
    }

    private void OnClearMemory(object? sender, RoutedEventArgs e)
    {
        try
        {
            string path = Path.Combine(AgentSettingsStore.RootDirectory, "memory.json");
            if (File.Exists(path)) File.Delete(path);
            RefreshMemoryPanel();
            Shell.Toast("记忆已清空。");
        }
        catch (Exception error)
        {
            Shell.Toast("清空失败：" + error.Message);
        }
    }

    private static int ParseInt(string? text, int fallback, int min, int max) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? Math.Clamp(value, min, max)
            : fallback;
}
