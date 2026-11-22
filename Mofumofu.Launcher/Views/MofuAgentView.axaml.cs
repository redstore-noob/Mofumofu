using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Mofumofu.Launcher.Controls;
using Mofumofu.Launcher.Services;
using Mofumofu.Launcher.Services.Agent;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Views;

public partial class MofuAgentView : UserControl
{
    private readonly AgentSettings _settings;
    private readonly AgentSession _session;
    private readonly List<AgentConversation> _conversations = new();
    private readonly Dictionary<AgentMessage, StackPanel> _messageHosts = new();
    private readonly Dictionary<string, ToolRunState> _toolRuns = new();

    private readonly DispatcherTimer _streamTimer;
    private CancellationTokenSource? _generation;
    private string _activeConversationId = string.Empty;
    private string _sessionFilter = string.Empty;
    private bool _initialized;
    private bool _reloadSettingsWhenIdle;
    private AgentTaskDraft? _taskOnLoad;
    private readonly Dictionary<string, AgentTaskDraft> _taskDrafts = new();
    private readonly Dictionary<string, string> _composerDrafts = new();
    private bool IsRunActive => _generation is not null || _session?.IsBusy == true;
    private bool _loadingControls;
    private bool _dirtyStream;
    private bool _stickToBottom = true;
    private bool _pendingClear;
    private DateTime _pendingClearAt;

    private sealed record ToolRunState(string Name, string Status, string Detail);

    public MofuAgentView()
    {
        InitializeComponent();

        _settings = AgentSettingsStore.Load();
        _settings.EnsureProvider();
        _session = new AgentSession(_settings)
        {
            AskHandler = AskUserAsync,
            ConfirmHandler = ConfirmToolAsync,
        };

        _streamTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _streamTimer.Tick += (_, _) => FlushStream();

        WireSessionEvents();
        Loaded += OnViewLoaded;
    }

    private void OnViewLoaded(object? sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            if (IsRunActive) _reloadSettingsWhenIdle = true;
            else ReloadSettings();
            return;
        }

        _initialized = true;

        FillProviderCombos();
        ApplyQuickSettings();
        ApplyInspectorVisibility();

        _conversations.Clear();
        _conversations.AddRange(AgentStore.List());
        RebuildSessionList();

        AgentConversation? restore = _conversations.FirstOrDefault(c => c.Id == _settings.LastConversationId);
        if (restore is not null)
        {
            OpenConversation(restore);
        }
        else
        {
            StartNewConversation(save: false);
        }

        if (_taskOnLoad is { } task)
        {
            _taskOnLoad = null;
            OpenTaskDraft(task);
        }

        MessagesScroll.ScrollChanged += (_, _) =>
        {
            _stickToBottom = MessagesScroll.Offset.Y + MessagesScroll.Viewport.Height >=
                             MessagesScroll.Extent.Height - 48;
        };
    }

    private void ReloadSettings()
    {
        _settings.CopyFrom(AgentSettingsStore.Load());
        FillProviderCombos();
        ApplyQuickSettings();
        ApplyInspectorVisibility();
        _session.RecalculateContext();
        RefreshInspector();
        RefreshMemoryPanel();
        RefreshTaskCard();
    }

    public bool TryPrepareTask(AgentTaskDraft task)
    {
        if (BlockWhileRunning()) return false;
        if (!_initialized) _taskOnLoad = task;
        else OpenTaskDraft(task);
        return true;
    }

    private void OpenTaskDraft(AgentTaskDraft task)
    {
        var conversation = new AgentConversation();
        _taskDrafts[conversation.Id] = task;
        _composerDrafts[conversation.Id] = task.Prompt;
        _conversations.Insert(0, conversation);
        OpenConversation(conversation);
        TaskPreview.IsExpanded = true;
    }

    private bool BlockWhileRunning()
    {
        if (!IsRunActive) return false;
        Shell.Toast(L.T("Agent.Task.Busy", "请先结束当前 MofuAgent 任务。"));
        return true;
    }

    private void RefreshTaskCard()
    {
        string id = _session.Conversation.Id;
        _taskDrafts.TryGetValue(id, out AgentTaskDraft? draft);
        AgentTaskAttachment? attached = _session.Conversation.Task;
        AgentTaskKind? kind = draft?.Kind ?? attached?.Kind;
        TaskCard.IsVisible = kind is not null;
        EmptyState.IsVisible = kind is null && _session.Conversation.Messages.Count == 0;
        if (kind is null) return;
        bool isCrash = kind == AgentTaskKind.CrashDiagnosis;
        TaskCardTitle.Text = isCrash
            ? L.T("Agent.Task.Crash.Title", "崩溃排查 · 只读任务")
            : L.T("Agent.Task.Instance.Title", "实例启动检查 · 只读任务");
        TaskReturnButton.Content = isCrash
            ? L.T("Agent.Task.Return.Launch", "打开启动控制台")
            : L.T("Agent.Task.Return.Instances", "返回实例页");
        TaskPreviewText.Text = draft?.Context ?? attached?.Context;
        if (draft is not null)
        {
            AgentProviderConfig? provider = _settings.ActiveProvider;
            string address = AgentProviderPresets.NormalizeBaseUrl(provider?.BaseUrl);
            TaskCardHint.Text = provider is not null && !string.IsNullOrWhiteSpace(_settings.ActiveModel)
                && Uri.TryCreate(address, UriKind.Absolute, out Uri? endpoint)
                ? L.T("Agent.Task.Consent", "本地检查已完成。点击发送会将下方资料分享给 {0}（{1}）。", provider.Name, endpoint.Scheme + "://" + endpoint.Authority)
                : L.T("Agent.Task.LocalOnly", "本地检查已完成。若需 AI 解读，请先配置供应商和模型。");
        }
        else if (attached is not null)
        {
            TaskCardHint.Text = _session.CanShareTaskWithCurrentProvider(attached)
                ? L.T("Agent.Task.Attached", "当前任务的资料只会发送给最初确认的供应商。")
                : L.T("Agent.Task.NotShared", "供应商已变更，当前资料不会发送。若要分享，请从原入口新建任务。");
        }
    }

    private void OnTaskReturn(object? sender, RoutedEventArgs e)
    {
        _taskDrafts.TryGetValue(_session.Conversation.Id, out AgentTaskDraft? draft);
        var kind = draft?.Kind ?? _session.Conversation.Task?.Kind;
        if (kind is not null) Shell.Go(kind == AgentTaskKind.CrashDiagnosis ? "launchconsole" : "instances");
    }

    private void WireSessionEvents()
    {
        _session.MessageAdded += message => RunOnUi(() =>
        {
            var host = new StackPanel { Spacing = 8 };
            _messageHosts[message] = host;
            MessagePanel.Children.Add(host);
            RenderMessage(host, message);
            EmptyState.IsVisible = false;
            AutoScroll();
            RefreshHeader();
        });

        _session.MessageUpdated += message => RunOnUi(() =>
        {
            if (message.Role == AgentRole.Assistant && _session.IsBusy)
            {
                _dirtyStream = true;
                return;
            }

            if (_messageHosts.TryGetValue(message, out var host)) RenderMessage(host, message);
            AutoScroll();
        });

        _session.TextDelta += _ => RunOnUi(() => _dirtyStream = true);
        _session.ReasoningDelta += _ => RunOnUi(() => _dirtyStream = true);

        _session.ToolStarted += call => RunOnUi(() =>
        {
            _toolRuns[call.Id] = new ToolRunState(call.Name, "running", "运行中");
            RefreshLastAssistant();
            RefreshRecentTools();
        });

        _session.ToolFinished += (call, content, isError, elapsed) => RunOnUi(() =>
        {
            _toolRuns[call.Id] = new ToolRunState(
                call.Name,
                isError ? "error" : "ok",
                (isError ? "失败 " : "完成 ") + $"{(int)elapsed.TotalMilliseconds}ms");
            RefreshLastAssistant();
            RefreshRecentTools();
        });

        _session.StateChanged += (state, detail) => RunOnUi(() => ApplyState(state, detail));
        _session.UsageChanged += () => RunOnUi(RefreshInspector);
        _session.MessagesReset += () => RunOnUi(RebuildMessagePanel);
        _session.Notice += text => RunOnUi(() => Shell.Toast(text));
    }

    private static void RunOnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    private void OpenConversation(AgentConversation conversation)
    {
        if (BlockWhileRunning()) return;
        if (_activeConversationId is { Length: > 0 } previous && _conversations.Any(item => item.Id == previous))
            _composerDrafts[previous] = ComposerBox.Text ?? string.Empty;
        if (!_session.TryLoad(conversation)) return;

        _pendingClear = false;
        _activeConversationId = conversation.Id;
        _settings.LastConversationId = conversation.Id;
        AgentSettingsStore.Save(_settings);
        ComposerBox.Text = _composerDrafts.GetValueOrDefault(conversation.Id) ?? string.Empty;
        TaskPreview.IsExpanded = _taskDrafts.ContainsKey(conversation.Id);
        RefreshTaskCard();
        RebuildSessionList();
        RefreshHeader();
        RefreshInspector();
    }

    private void RebuildMessagePanel()
    {
        AgentConversation conversation = _session.Conversation;

        _toolRuns.Clear();
        foreach (AgentMessage message in conversation.Messages.Where(m => m.Role == AgentRole.Tool))
        {
            if (string.IsNullOrEmpty(message.ToolCallId)) continue;
            _toolRuns[message.ToolCallId] = new ToolRunState(
                message.ToolName ?? "tool",
                message.IsError ? "error" : "ok",
                message.IsError ? "失败" : "已完成");
        }

        ClearMessageHosts();
        foreach (AgentMessage message in conversation.Messages)
        {
            var host = new StackPanel { Spacing = 8 };
            _messageHosts[message] = host;
            MessagePanel.Children.Add(host);
            RenderMessage(host, message);
        }

        RefreshTaskCard();
        RefreshHeader();
        RefreshInspector();
        AutoScroll();
    }

    private void StartNewConversation(bool save = true)
    {
        if (BlockWhileRunning()) return;
        if (_initialized && _session.Conversation.Messages.Count == 0 &&
            !_taskDrafts.ContainsKey(_session.Conversation.Id) && _conversations.Contains(_session.Conversation))
        {
            ComposerBox.Focus();
            return;
        }

        var conversation = new AgentConversation();
        if (save) AgentStore.Save(conversation);

        _conversations.Insert(0, conversation);
        OpenConversation(conversation);
        ComposerBox.Focus();
    }

    private void ClearMessageHosts()
    {
        MessagePanel.Children.Clear();
        _messageHosts.Clear();
    }

    private void RebuildSessionList()
    {
        SessionList.Children.Clear();

        List<AgentConversation> filtered = string.IsNullOrWhiteSpace(_sessionFilter)
            ? _conversations
            : _conversations.Where(c =>
                    c.Title.Contains(_sessionFilter, StringComparison.OrdinalIgnoreCase) ||
                    c.Messages.Any(m => m.Text.Contains(_sessionFilter, StringComparison.OrdinalIgnoreCase)))
                .ToList();

        SessionCountText.Text = filtered.Count == _conversations.Count
            ? _conversations.Count + " 条"
            : $"{filtered.Count} / {_conversations.Count} 条";

        DateTime today = DateTime.Today;
        var buckets = new (string Label, Func<DateTime, bool> Match)[]
        {
            ("今天", date => date == today),
            ("昨天", date => date == today.AddDays(-1)),
            ("最近 7 天", date => date > today.AddDays(-7) && date < today.AddDays(-1)),
            ("更早", date => date <= today.AddDays(-7)),
        };

        foreach (var bucket in buckets)
        {
            List<AgentConversation> items = filtered
                .Where(c => bucket.Match(c.UpdatedAt.Date))
                .OrderByDescending(c => c.UpdatedAt)
                .ToList();
            if (items.Count == 0) continue;

            SessionList.Children.Add(new TextBlock
            {
                Text = bucket.Label,
                Classes = { "session-group" },
            });

            foreach (AgentConversation conversation in items)
                SessionList.Children.Add(BuildSessionRow(conversation));
        }
    }

    private Control BuildSessionRow(AgentConversation conversation)
    {
        var row = new Border
        {
            Classes = { "agent-row" },
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        if (conversation.Id == _activeConversationId) row.Classes.Add("active");

        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(new TextBlock
        {
            Text = conversation.Title,
            FontSize = 12,
            FontWeight = conversation.Id == _activeConversationId ? FontWeight.SemiBold : FontWeight.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ThemeTokens.Brush("NekoTextBrush"),
        });
        text.Children.Add(new TextBlock
        {
            Text = $"{conversation.DisplayTime} · {conversation.Messages.Count} 条",
            FontSize = 10,
            Foreground = ThemeTokens.Brush("NekoDimBrush"),
        });
        content.Children.Add(text);

        var delete = new Button
        {
            Classes = { "icon" },
            Width = 21,
            Height = 21,
            Content = new IconParkIcon { Kind = "IconClose", Width = 8, Height = 8 },
            Opacity = 0,
        };
        ToolTip.SetTip(delete, "删除会话");
        Grid.SetColumn(delete, 1);
        delete.Click += (_, args) =>
        {
            args.Handled = true;
            DeleteConversation(conversation);
        };
        content.Children.Add(delete);

        row.Child = content;
        row.PointerEntered += (_, _) => delete.Opacity = 1;
        row.PointerExited += (_, _) => delete.Opacity = 0;
        row.PointerPressed += (_, _) =>
        {
            if (conversation.Id == _activeConversationId) return;
            OpenConversation(conversation);
        };

        return row;
    }

    private void DeleteConversation(AgentConversation conversation)
    {
        if (BlockWhileRunning()) return;
        AgentStore.Delete(conversation.Id);
        _taskDrafts.Remove(conversation.Id);
        _composerDrafts.Remove(conversation.Id);
        _conversations.Remove(conversation);

        if (conversation.Id == _activeConversationId)
        {
            AgentConversation? next = _conversations.OrderByDescending(c => c.UpdatedAt).FirstOrDefault();
            if (next is not null) OpenConversation(next);
            else StartNewConversation();
        }
        else
        {
            RebuildSessionList();
        }
    }

    private void OnNewChat(object? sender, RoutedEventArgs e) => StartNewConversation();

    private void OnBackToDesktop(object? sender, RoutedEventArgs e) => Shell.BackToDesktop();

    private void OnSessionSearchChanged(object? sender, TextChangedEventArgs e)
    {
        _sessionFilter = SessionSearchBox.Text?.Trim() ?? string.Empty;
        RebuildSessionList();
    }

    private void OnClearConversation(object? sender, RoutedEventArgs e)
    {
        if (BlockWhileRunning()) return;

        if (!_pendingClear || (DateTime.Now - _pendingClearAt).TotalSeconds > 3)
        {
            _pendingClear = true;
            _pendingClearAt = DateTime.Now;
            Shell.Toast("再点一次清空当前会话。");
            return;
        }

        _pendingClear = false;
        if (!_session.TryClear()) return;
        _taskDrafts.Remove(_session.Conversation.Id);
        ComposerBox.Text = string.Empty;
        RefreshTaskCard();
        RebuildSessionList();
        RefreshHeader();
        RefreshInspector();
    }

    private async void OnExportConversation(object? sender, RoutedEventArgs e)
    {
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null) return;

            IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出会话",
                SuggestedFileName = "mofuagent-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".md",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Markdown") { Patterns = new[] { "*.md" } },
                },
            });

            if (file is null) return;

            await using Stream stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream, Encoding.UTF8);
            await writer.WriteAsync(AgentStore.ExportMarkdown(_session.Conversation));
            Shell.Toast("已导出。");
        }
        catch (Exception error)
        {
            Shell.Toast("导出失败：" + error.Message);
        }
    }

    private void OnRegenerate(object? sender, RoutedEventArgs e) => _ = RunExclusiveAsync(
        token => _session.RegenerateAsync(token), "重新生成中");

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

        e.Handled = true;
        _ = SendComposerAsync();
    }

    private void OnSend(object? sender, RoutedEventArgs e) => _ = SendComposerAsync();

    private async Task SendComposerAsync()
    {
        string text = ComposerBox.Text?.Trim() ?? string.Empty;
        if (text.Length == 0 || IsRunActive) return;
        if (AgentProviderPresets.NormalizeBaseUrl(_settings.ActiveProvider?.BaseUrl).Length == 0 ||
            string.IsNullOrWhiteSpace(_settings.ActiveModel))
        {
            Shell.Toast(L.T("Agent.Task.Configure", "请先配置有效的供应商地址并选择模型。"));
            return;
        }
        string id = _session.Conversation.Id;
        _taskDrafts.TryGetValue(id, out AgentTaskDraft? draft);
        ComposerBox.Text = string.Empty;
        await RunExclusiveAsync(token => _session.SendAsync(text, token, draft), "发送中");
        if (draft is not null && _session.Conversation.Task is not null) _taskDrafts.Remove(id);
        RefreshTaskCard();
    }

    private async Task RunExclusiveAsync(Func<CancellationToken, Task> action, string busyText)
    {
        if (BlockWhileRunning()) return;

        using var generation = new CancellationTokenSource();
        _generation = generation;
        SetBusy(true, busyText);
        _streamTimer.Start();

        try
        {
            await action(generation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Shell.Toast("出错了：" + error.Message);
        }
        finally
        {
            FlushStream();
            _streamTimer.Stop();
            _generation = null;
            SetBusy(false, "就绪");
            if (_reloadSettingsWhenIdle)
            {
                _reloadSettingsWhenIdle = false;
                ReloadSettings();
            }
            RefreshHeader();
            RefreshInspector();
            RebuildSessionList();
            RefreshMemoryPanel();
        }
    }

    private void OnStopGenerating(object? sender, RoutedEventArgs e)
    {
        _generation?.Cancel();
        Shell.Toast("已停止生成。");
    }

    private void SetBusy(bool busy, string text)
    {
        SendButton.IsVisible = !busy;
        StopButton.IsVisible = busy;
        ComposerBox.IsEnabled = !busy;
        ProviderCombo.IsEnabled = !busy;
        ModelCombo.IsEnabled = !busy;
        NewChatButton.IsEnabled = !busy;
        SessionList.IsEnabled = !busy;
        AutoCompactSwitch.IsEnabled = !busy;
        LauncherToolsSwitch.IsEnabled = !busy;
        MaxTokensSlider.IsEnabled = !busy;
        CompactNowButton.IsEnabled = !busy;
        if (busy)
        {
            HeaderStatusText.Text = text;
            HeaderStatusDot.Fill = ThemeTokens.Brush("NekoWarningBrush");
        }
        else if (_session.State != AgentSessionState.Error)
        {
            RefreshProviderReadiness();
        }
    }

    private void RefreshProviderReadiness()
    {
        AgentProviderConfig? provider = _settings.ActiveProvider;
        if (provider is null)
        {
            HeaderStatusText.Text = "未添加供应商";
            HeaderStatusDot.Fill = ThemeTokens.Brush("NekoWarningBrush");
            ToolTip.SetTip(HeaderStatusText, "设置 → 供应商 → 添加一条，填好地址、Key 与模型 id");
            return;
        }

        (string status, string tone) = AgentProviderPresets.DescribeStatus(provider);
        if (tone == "warning")
        {
            HeaderStatusText.Text = status;
            HeaderStatusDot.Fill = ThemeTokens.Brush("NekoWarningBrush");
            ToolTip.SetTip(HeaderStatusText, "设置 → 供应商 → " + status);
            return;
        }

        HeaderStatusText.Text = "就绪";
        HeaderStatusDot.Fill = ThemeTokens.Brush("NekoSuccessBrush");
        ToolTip.SetTip(HeaderStatusText, provider.Name + " · " + AgentProviderPresets.DescribeKind(provider.Kind));
    }

    private void ApplyState(AgentSessionState state, string detail)
    {
        HeaderStatusText.Text = detail;
        HeaderStatusDot.Fill = ThemeTokens.Brush(state switch
        {
            AgentSessionState.Error => "NekoDangerBrush",
            AgentSessionState.RunningTool => "NekoWarningBrush",
            AgentSessionState.Thinking or AgentSessionState.Streaming => "NekoInfoBrush",
            AgentSessionState.WaitingUser => "NekoAccentBrush",
            _ => "NekoSuccessBrush",
        });

        if (state == AgentSessionState.Idle)
        {
            _streamTimer.Stop();
            RefreshProviderReadiness();
        }
    }

    private void FlushStream()
    {
        if (!_dirtyStream) return;
        _dirtyStream = false;

        AgentMessage? last = _session.Conversation.Messages.LastOrDefault(m => m.Role == AgentRole.Assistant);
        if (last is null || !_messageHosts.TryGetValue(last, out var host)) return;

        RenderMessage(host, last);
        AutoScroll();
        RefreshInspector();
    }

    private void RefreshLastAssistant()
    {
        AgentMessage? last = _session.Conversation.Messages.LastOrDefault(m => m.Role == AgentRole.Assistant);
        if (last is not null && _messageHosts.TryGetValue(last, out var host)) RenderMessage(host, last);
    }

    private void RenderMessage(StackPanel host, AgentMessage message)
    {
        host.Children.Clear();

        switch (message.Role)
        {
            case AgentRole.User:
                host.Children.Add(BuildUserBubble(message));
                break;

            case AgentRole.Assistant:
                host.Children.Add(BuildAssistantBlock(message));
                break;

            case AgentRole.Tool:
                host.Children.Add(BuildToolResultCard(message));
                break;

            default:
                host.Children.Add(BuildNoticeCard(message));
                break;
        }
    }

    private Control BuildUserBubble(AgentMessage message)
    {
        var bubble = new Border
        {
            Background = ThemeTokens.Brush("NekoAccentSoftBrush"),
            BorderBrush = ThemeTokens.Brush("NekoAccentLineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(12, 9),
            MaxWidth = 620,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        bubble.Child = new TextBlock
        {
            Text = message.Text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 21,
            Foreground = ThemeTokens.Brush("NekoTextBrush"),
        };

        return bubble;
    }

    private Control BuildAssistantBlock(AgentMessage message)
    {
        var stack = new StackPanel { Spacing = 9 };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        header.Children.Add(new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(5),
            Background = ThemeTokens.Brush("NekoAccentBrush"),
            Child = new TextBlock
            {
                Text = "M",
                FontSize = 10.5,
                FontWeight = FontWeight.Bold,
                Foreground = ThemeTokens.Brush("NekoAccentOnBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        header.Children.Add(new TextBlock
        {
            Text = "MofuAgent",
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeTokens.Brush("NekoMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (!string.IsNullOrEmpty(message.Reasoning))
        {
            header.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1),
                Background = ThemeTokens.Brush("NekoFieldBrush"),
                Child = new TextBlock
                {
                    Text = "思考 " + TokenEstimator.Estimate(message.Reasoning) + "t",
                    FontSize = 9.5,
                    Foreground = ThemeTokens.Brush("NekoDimBrush"),
                },
            });
        }

        stack.Children.Add(header);

        if (!string.IsNullOrWhiteSpace(message.Reasoning))
            stack.Children.Add(BuildReasoningBox(message.Reasoning));

        if (!string.IsNullOrWhiteSpace(message.Text))
        {
            stack.Children.Add(AgentMarkdown.Render(message.Text, null, text =>
            {
                _ = CopyTextAsync(text);
            }));
        }

        foreach (AgentToolCall call in message.ToolCalls)
            stack.Children.Add(BuildToolCallChip(call));

        if (string.IsNullOrWhiteSpace(message.Text) && message.ToolCalls.Count == 0 && _session.IsBusy)
            stack.Children.Add(BuildTypingIndicator());

        return stack;
    }

    private Control BuildReasoningBox(string reasoning)
    {
        var body = new TextBlock
        {
            Text = reasoning,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
            LineHeight = 18,
            Foreground = ThemeTokens.Brush("NekoDimBrush"),
            Margin = new Thickness(10, 6, 10, 8),
            IsVisible = false,
        };

        var toggle = new Button
        {
            Classes = { "agent-mini" },
            Content = "展开思考过程",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(10, 7, 0, 0),
        };
        toggle.Click += (_, _) =>
        {
            body.IsVisible = !body.IsVisible;
            toggle.Content = body.IsVisible ? "收起思考过程" : "展开思考过程";
        };

        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(toggle);
        panel.Children.Add(body);

        return new Border
        {
            Background = ThemeTokens.Brush("NekoFieldSoftBrush"),
            BorderBrush = ThemeTokens.Brush("NekoBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = panel,
        };
    }

    private Control BuildTypingIndicator()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        for (int index = 0; index < 3; index++)
        {
            panel.Children.Add(new Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = ThemeTokens.WithAlpha("NekoAccentBrush", 0.55 - index * 0.12),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = "正在生成",
            FontSize = 11,
            Foreground = ThemeTokens.Brush("NekoDimBrush"),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });

        return panel;
    }

    private Control BuildToolCallChip(AgentToolCall call)
    {
        if (!_toolRuns.TryGetValue(call.Id, out ToolRunState? state))
        {
            AgentMessage? result = _session.Conversation.Messages.FirstOrDefault(
                m => m.Role == AgentRole.Tool && m.ToolCallId == call.Id);
            state = result is null
                ? new ToolRunState(call.Name, "pending", "等待执行")
                : new ToolRunState(call.Name, result.IsError ? "error" : "ok",
                    result.IsError ? "失败" : "已完成");
        }

        string status = state.Status;
        string detail = state.Detail;

        IBrush accent = ThemeTokens.Brush(status switch
        {
            "ok" => "NekoSuccessBrush",
            "error" => "NekoDangerBrush",
            "running" => "NekoWarningBrush",
            _ => "NekoMutedBrush",
        });

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        header.Children.Add(new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = accent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        header.Children.Add(new TextBlock
        {
            Text = call.Name,
            FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"),
            FontSize = 11.5,
            Foreground = ThemeTokens.Brush("NekoTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var detailText = new TextBlock
        {
            Text = detail,
            FontSize = 10.5,
            Foreground = accent,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(detailText, 2);
        header.Children.Add(detailText);

        var body = new TextBlock
        {
            Text = PrettyJson(call.ArgumentsJson),
            FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"),
            FontSize = 10.5,
            LineHeight = 16,
            Foreground = ThemeTokens.Brush("NekoDimBrush"),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
            Margin = new Thickness(14, 6, 0, 0),
        };

        header.Cursor = new Cursor(StandardCursorType.Hand);
        header.PointerPressed += (_, _) => body.IsVisible = !body.IsVisible;

        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(header);
        stack.Children.Add(body);

        return new Border
        {
            Background = ThemeTokens.Brush("NekoFieldSoftBrush"),
            BorderBrush = ThemeTokens.Brush("NekoBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7),
            Child = stack,
        };
    }

    private Control BuildToolResultCard(AgentMessage message)
    {
        bool isError = message.IsError;
        string text = message.Text ?? string.Empty;
        bool longText = text.Length > 260 || text.Count(ch => ch == '\n') > 6;

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            Children =
            {
                new TextBlock
                {
                    Text = message.ToolName ?? "tool",
                    FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"),
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeTokens.Brush(isError ? "NekoDangerBrush" : "NekoMutedBrush"),
                },
                new TextBlock
                {
                    Text = isError ? "执行失败" : "执行结果",
                    FontSize = 10.5,
                    Foreground = ThemeTokens.Brush("NekoDimBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        });

        var body = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"),
            FontSize = 11,
            LineHeight = 17,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeTokens.Brush(isError ? "NekoDangerBrush" : "NekoTextBrush"),
            Margin = new Thickness(0, 7, 0, 0),
            MaxHeight = longText ? 88 : double.PositiveInfinity,
        };

        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(header);

        if (longText)
        {
            var toggle = new Button
            {
                Classes = { "agent-mini" },
                Content = "展开全部",
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
            };
            toggle.Click += (_, _) =>
            {
                body.MaxHeight = double.IsPositiveInfinity(body.MaxHeight) ? 88 : double.PositiveInfinity;
                toggle.Content = double.IsPositiveInfinity(body.MaxHeight) ? "收起" : "展开全部";
            };
            Grid.SetColumn(toggle, 1);
            header.Children.Add(toggle);
        }

        stack.Children.Add(body);

        return new Border
        {
            Background = ThemeTokens.Brush("NekoFieldSoftBrush"),
            BorderBrush = ThemeTokens.WithAlpha(isError ? "NekoDangerBrush" : "NekoBorderBrush", isError ? 0.5 : 1),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(11, 8),
            Margin = new Thickness(0, 0, 26, 0),
            Child = stack,
        };
    }

    private Control BuildNoticeCard(AgentMessage message)
    {
        bool isError = message.IsError;
        var stack = new StackPanel { Spacing = 3 };
        stack.Children.Add(new TextBlock
        {
            Text = isError ? "错误" : "提示",
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            LetterSpacing = 1,
            Foreground = ThemeTokens.Brush(isError ? "NekoDangerBrush" : "NekoMutedBrush"),
        });
        stack.Children.Add(new TextBlock
        {
            Text = message.Text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            LineHeight = 19,
            Foreground = ThemeTokens.Brush(isError ? "NekoDangerBrush" : "NekoMutedBrush"),
        });

        return new Border
        {
            Background = ThemeTokens.Brush(isError ? "NekoDangerSoftBrush" : "NekoFieldSoftBrush"),
            BorderBrush = ThemeTokens.WithAlpha(isError ? "NekoDangerBrush" : "NekoBorderBrush", 0.6),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(11, 8),
            Child = stack,
        };
    }

    private static string PrettyJson(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return System.Text.Json.JsonSerializer.Serialize(document.RootElement,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }

    private void AutoScroll()
    {
        if (!_stickToBottom) return;
        Dispatcher.UIThread.Post(() => MessagesScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private async Task CopyTextAsync(string text)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) return;
            await clipboard.SetTextAsync(text);
            Shell.Toast("已复制。");
        }
        catch
        {
            Shell.Toast("复制失败。");
        }
    }

    private Task<string> AskUserAsync(
        string question, IReadOnlyList<string> options, bool freeText, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        RunOnUi(() =>
        {
            var stack = new StackPanel { Spacing = 8 };
            stack.Children.Add(new TextBlock
            {
                Text = "需要你确认",
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                LetterSpacing = 1,
                Foreground = ThemeTokens.Brush("NekoAccentBrush"),
            });
            stack.Children.Add(new TextBlock
            {
                Text = question,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                LineHeight = 20,
                Foreground = ThemeTokens.Brush("NekoTextBrush"),
            });

            var answerPanel = new WrapPanel { ItemWidth = double.NaN };
            var freeBox = new TextBox
            {
                Classes = { "agent-field" },
                Watermark = "直接输入回答",
                Margin = new Thickness(0, 2, 0, 0),
                IsVisible = freeText,
            };
            var submit = new Button
            {
                Classes = { "primary" },
                Height = 28,
                Padding = new Thickness(14, 0),
                Content = "提交",
                IsVisible = freeText,
                HorizontalAlignment = HorizontalAlignment.Left,
            };

            void Answer(string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                completion.TrySetResult(value);
                answerPanel.IsVisible = false;
                freeBox.IsVisible = false;
                submit.IsVisible = false;
                stack.Children.Add(new TextBlock
                {
                    Text = "已选择：" + value,
                    FontSize = 11.5,
                    Foreground = ThemeTokens.Brush("NekoMutedBrush"),
                });
            }

            foreach (string option in options.Take(4))
            {
                var button = new Button
                {
                    Classes = { "pill" },
                    Content = option,
                    Height = 28,
                    Padding = new Thickness(12, 0),
                    Margin = new Thickness(0, 0, 6, 6),
                };
                button.Click += (_, _) => Answer(option);
                answerPanel.Children.Add(button);
            }

            submit.Click += (_, _) => Answer(freeBox.Text ?? string.Empty);
            freeBox.KeyDown += (_, args) =>
            {
                if (args.Key != Key.Enter) return;
                args.Handled = true;
                Answer(freeBox.Text ?? string.Empty);
            };

            stack.Children.Add(answerPanel);
            stack.Children.Add(freeBox);
            stack.Children.Add(submit);

            var card = new Border
            {
                Background = ThemeTokens.Brush("NekoAccentSoftBrush"),
                BorderBrush = ThemeTokens.Brush("NekoAccentLineBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10),
                MaxWidth = 560,
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = stack,
            };

            MessagePanel.Children.Add(card);
            AutoScroll();

            cancellationToken.Register(() => Dispatcher.UIThread.Post(() =>
            {
                if (completion.TrySetCanceled())
                {
                    answerPanel.IsVisible = false;
                    freeBox.IsVisible = false;
                    submit.IsVisible = false;
                }
            }));
        });

        return completion.Task;
    }

    private Task<bool> ConfirmToolAsync(
        AgentToolDefinition tool, string arguments, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        RunOnUi(() =>
        {
            var stack = new StackPanel { Spacing = 8 };
            stack.Children.Add(new TextBlock
            {
                Text = "执行前确认",
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                LetterSpacing = 1,
                Foreground = ThemeTokens.Brush("NekoWarningBrush"),
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"MofuAgent 想执行 {tool.Name}",
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeTokens.Brush("NekoTextBrush"),
            });
            stack.Children.Add(new TextBlock
            {
                Text = tool.Description,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11.5,
                Foreground = ThemeTokens.Brush("NekoMutedBrush"),
            });
            stack.Children.Add(new TextBlock
            {
                Text = PrettyJson(arguments),
                FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"),
                FontSize = 10.5,
                Foreground = ThemeTokens.Brush("NekoDimBrush"),
                TextWrapping = TextWrapping.Wrap,
            });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var allow = new Button { Classes = { "primary" }, Height = 28, Padding = new Thickness(14, 0), Content = "允许" };
            var deny = new Button { Classes = { "ghost" }, Height = 28, Padding = new Thickness(14, 0), Content = "拒绝" };
            buttons.Children.Add(allow);
            buttons.Children.Add(deny);
            stack.Children.Add(buttons);

            var card = new Border
            {
                Background = ThemeTokens.Brush("NekoWarningSoftBrush"),
                BorderBrush = ThemeTokens.WithAlpha("NekoWarningBrush", 0.55),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10),
                MaxWidth = 560,
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = stack,
            };

            void Decide(bool approved)
            {
                completion.TrySetResult(approved);
                buttons.IsVisible = false;
                stack.Children.Add(new TextBlock
                {
                    Text = approved ? "已允许执行" : "已拒绝",
                    FontSize = 11.5,
                    Foreground = ThemeTokens.Brush(approved ? "NekoSuccessBrush" : "NekoMutedBrush"),
                });
            }

            allow.Click += (_, _) => Decide(true);
            deny.Click += (_, _) => Decide(false);

            MessagePanel.Children.Add(card);
            AutoScroll();

            cancellationToken.Register(() => Dispatcher.UIThread.Post(() =>
            {
                if (completion.TrySetCanceled()) buttons.IsVisible = false;
            }));
        });

        return completion.Task;
    }

    private void RefreshHeader()
    {
        AgentConversation conversation = _session.Conversation;
        ConversationTitle.Text = conversation.Title;
        ConversationMeta.Text = conversation.Messages.Count == 0
            ? "尚未开始"
            : $"{conversation.Messages.Count} 条消息 · {conversation.DisplayTime}";
        ContextPill.Text = $"{TokenEstimator.Format(_session.ContextUsed)} / {TokenEstimator.Format(_session.ContextWindow)}";
    }

    private void RefreshInspector()
    {
        int used = _session.ContextUsed;
        int window = _session.ContextWindow;
        double ratio = _session.ContextRatio;

        ContextUsedText.Text = TokenEstimator.Format(used);
        ContextWindowText.Text = "/ " + TokenEstimator.Format(window);
        ContextPercentText.Text = (ratio * 100).ToString("0") + "%";
        ContextPercentText.Foreground = ThemeTokens.Brush(ratio switch
        {
            >= 0.9 => "NekoDangerBrush",
            >= 0.7 => "NekoWarningBrush",
            _ => "NekoAccentBrush",
        });
        ComposerHint.Text = "上下文 " + (ratio * 100).ToString("0") + "%";

        int tools = _session.ContextToolTokens;
        int system = _session.ContextSystemTokens;
        int messages = Math.Max(0, used - tools - system);

        SystemTokensText.Text = TokenEstimator.Format(system);
        ToolTokensText.Text = TokenEstimator.Format(tools);
        MessageTokensText.Text = TokenEstimator.Format(messages);
        UpdateCompositionBar(system, tools, messages);
        UsageDetailText.Text = _session.LastUsage.Total == 0
            ? "—"
            : $"{TokenEstimator.Format(_session.LastUsage.PromptTokens)} + {TokenEstimator.Format(_session.LastUsage.CompletionTokens)}";

        ToolCountText.Text = _session.ActiveTools.Count + " 个";
        RefreshRecentTools();
        RefreshHeader();
    }

    private void UpdateCompositionBar(int system, int tools, int messages)
    {
        int total = Math.Max(1, system + tools + messages);
        static string Star(int value, int total) =>
            (value / (double)total).ToString("0.###", CultureInfo.InvariantCulture) + "*";

        ContextComposition.ColumnDefinitions = new ColumnDefinitions(
            Star(system, total) + "," + Star(tools, total) + "," + Star(messages, total));

        ToolTip.SetTip(SystemSegment, $"系统提示 {TokenEstimator.Format(system)} token");
        ToolTip.SetTip(ToolSegment, $"工具描述 {TokenEstimator.Format(tools)} token");
        ToolTip.SetTip(MessageSegment, $"消息历史 {TokenEstimator.Format(messages)} token");
    }

    private void RefreshRecentTools()
    {
        RecentToolsPanel.Children.Clear();

        if (_toolRuns.Count == 0)
        {
            RecentToolsPanel.Children.Add(new TextBlock
            {
                Text = "还没有调用过工具",
                FontSize = 11,
                Foreground = ThemeTokens.Brush("NekoDimBrush"),
            });
            return;
        }

        foreach (var (id, state) in _toolRuns.Reverse().Take(5))
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            row.Children.Add(new Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = ThemeTokens.Brush(state.Status switch
                {
                    "ok" => "NekoSuccessBrush",
                    "error" => "NekoDangerBrush",
                    "running" => "NekoWarningBrush",
                    _ => "NekoMutedBrush",
                }),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0),
            });

            string name = state.Name;
            var label = new TextBlock
            {
                Text = name,
                FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"),
                FontSize = 10.5,
                Foreground = ThemeTokens.Brush("NekoMutedBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);

            var detail = new TextBlock
            {
                Text = state.Detail,
                FontSize = 10,
                Foreground = ThemeTokens.Brush("NekoDimBrush"),
            };
            Grid.SetColumn(detail, 2);
            row.Children.Add(detail);

            RecentToolsPanel.Children.Add(row);
        }
    }

    private void FillProviderCombos()
    {
        _loadingControls = true;
        ProviderCombo.ItemsSource = _settings.Providers.Select(p => p.Name).ToList();
        ProviderCombo.SelectedIndex = _settings.Providers.Count == 0
            ? -1
            : Math.Max(0, _settings.Providers.FindIndex(p => p.Id == _settings.ActiveProviderId));

        AgentProviderConfig? provider = _settings.EnsureProvider();
        ModelCombo.ItemsSource = provider?.Models.ToList() ?? new List<string>();
        ModelCombo.SelectedIndex = provider is null || provider.Models.Count == 0
            ? -1
            : Math.Max(0, provider.Models.IndexOf(_settings.ActiveModel));
        _settings.ContextWindow = _settings.ResolveContextWindow();
        ToolTip.SetTip(ProviderCombo, provider is null
            ? "还没有供应商，请到「设置 → 供应商」里添加一条"
            : $"{provider.Name} · {AgentProviderPresets.DescribeKind(provider.Kind)}");
        ToolTip.SetTip(ModelCombo, "当前模型：" +
            (string.IsNullOrEmpty(_settings.ActiveModel) ? "未选择" : _settings.ActiveModel));
        UpdateEmptyModelHint();
        RefreshProviderReadiness();
        _loadingControls = false;
    }

    private void OnProviderChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingControls || IsRunActive) return;

        int index = ProviderCombo.SelectedIndex;
        if (index < 0 || index >= _settings.Providers.Count) return;

        _settings.ActiveProviderId = _settings.Providers[index].Id;
        AgentProviderConfig? provider = _settings.EnsureProvider();
        _settings.ActiveModel = provider?.Models.FirstOrDefault() ?? string.Empty;
        _settings.ContextWindow = provider is { ContextWindow: > 0 }
            ? provider.ContextWindow
            : AgentProviderPresets.GuessContextWindow(_settings.ActiveModel);

        AgentSettingsStore.Save(_settings);
        FillProviderCombos();
        _session.RecalculateContext();
        RefreshInspector();
        RefreshTaskCard();
        Shell.Toast("已切换到 " + (provider?.Name ?? string.Empty));
    }

    private void OnModelChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingControls || IsRunActive) return;

        string? model = ModelCombo.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(model)) return;

        _settings.ActiveModel = model;
        UpdateEmptyModelHint();
        AgentProviderConfig? provider = _settings.ActiveProvider;
        _settings.ContextWindow = provider is { ContextWindow: > 0 }
            ? provider.ContextWindow
            : AgentProviderPresets.GuessContextWindow(model);
        AgentSettingsStore.Save(_settings);
        _session.RecalculateContext();
        RefreshInspector();
        RefreshTaskCard();
    }

    private void OnOpenSettings(object? sender, RoutedEventArgs e) => Shell.Go("mofuagent-settings");

    private void OnShowMemory(object? sender, RoutedEventArgs e) => Shell.Go("mofuagent-settings");

    private void ApplyQuickSettings()
    {
        _loadingControls = true;
        AutoCompactSwitch.IsChecked = _settings.AutoCompact;
        LauncherToolsSwitch.IsChecked = _settings.Tools.LauncherControl;
        MaxTokensSlider.Value = Math.Clamp(_settings.MaxOutputTokens, 256, 16384);
        MaxTokensText.Text = _settings.MaxOutputTokens.ToString();
        _loadingControls = false;
    }

    private void RefreshMemoryPanel() => MemoryCountText.Text = AgentMemory.All().Count + " 条";

    private void OnAutoCompactToggled(object? sender, RoutedEventArgs e)
    {
        if (_loadingControls || IsRunActive) return;

        _settings.AutoCompact = AutoCompactSwitch.IsChecked == true;
        AgentSettingsStore.Save(_settings);
        _session.RecalculateContext();
        RefreshInspector();
    }

    private void OnLauncherToolsToggled(object? sender, RoutedEventArgs e)
    {
        if (_loadingControls || IsRunActive) return;

        _settings.Tools.LauncherControl = LauncherToolsSwitch.IsChecked == true;
        AgentSettingsStore.Save(_settings);
        _session.RecalculateContext();
        RefreshInspector();
        RefreshTaskCard();
    }

    private void OnMaxTokensChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loadingControls || IsRunActive) return;

        _settings.MaxOutputTokens = (int)Math.Round(MaxTokensSlider.Value / 64) * 64;
        MaxTokensText.Text = _settings.MaxOutputTokens.ToString();
        AgentSettingsStore.Save(_settings);
        _session.RecalculateContext();
        RefreshInspector();
    }

    private void OnCompactNow(object? sender, RoutedEventArgs e)
    {
        if (_session.IsBusy)
        {
            Shell.Toast("正在生成，等这一轮结束再压缩。");
            return;
        }

        if (!_session.CompactContext())
        {
            Shell.Toast("当前没有可压缩的历史（消息太少）。");
            return;
        }

        RebuildSessionList();
    }

    private void OnAddProvider(object? sender, RoutedEventArgs e)
    {
        MofuAgentSettingsView.RequestFocusProvider();
        Shell.Go("mofuagent-settings");
    }

    private void OnToggleInspector(object? sender, RoutedEventArgs e)
    {
        if (BlockWhileRunning()) return;
        _settings.ShowInspector = !_settings.ShowInspector;
        AgentSettingsStore.Save(_settings);
        ApplyInspectorVisibility();
    }

    private void UpdateEmptyModelHint()
    {
        AgentProviderConfig? provider = _settings.ActiveProvider;
        EmptyModelHint.Text = (provider?.Name ?? "未添加供应商") + " / " +
                              (string.IsNullOrEmpty(_settings.ActiveModel) ? "无模型" : _settings.ActiveModel);
    }

    private void ApplyInspectorVisibility()
    {
        bool show = _settings.ShowInspector;
        InspectorPanel.IsVisible = show;
        WorkspaceGrid.ColumnDefinitions[2].Width = show ? new GridLength(288) : new GridLength(0);
        InspectorToggleButton.Classes.Set("active", show);
        ToolTip.SetTip(InspectorToggleButton, show ? "收起上下文仪表" : "展开上下文仪表");
    }

    private void OnSuggestionClick(object? sender, RoutedEventArgs e)
    {
        if (!IsRunActive && _session.Conversation.Task is null &&
            !_taskDrafts.ContainsKey(_session.Conversation.Id) && sender is Button { Tag: string text })
        {
            ComposerBox.Text = text;
            ComposerBox.Focus();
            ComposerBox.CaretIndex = text.Length;
        }
    }
}
