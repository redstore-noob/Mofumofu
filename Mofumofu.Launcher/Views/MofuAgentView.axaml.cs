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

namespace Mofumofu.Launcher.Views;

/// <summary>
/// MofuAgent：启动器内置智能体工作台。
/// 左栏会话、中间对话、右栏上下文仪表；设置走独立页面。
/// </summary>
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

    // ==================================================================
    // 初始化
    // ==================================================================

    private void OnViewLoaded(object? sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            // 从设置页回来时设置可能已经变了，重新读一遍再刷新仪表
            AgentSettings fresh = AgentSettingsStore.Load();
            _settings.Temperature = fresh.Temperature;
            _settings.TopP = fresh.TopP;
            _settings.MaxOutputTokens = fresh.MaxOutputTokens;
            _settings.Streaming = fresh.Streaming;
            _settings.MaxToolRounds = fresh.MaxToolRounds;
            _settings.ContextWindow = fresh.ContextWindow;
            _settings.AutoCompact = fresh.AutoCompact;
            _settings.CompactAt = fresh.CompactAt;
            _settings.KeepRecentMessages = fresh.KeepRecentMessages;
            _settings.Tools.Enabled = fresh.Tools.Enabled;
            _settings.Tools.LauncherControl = fresh.Tools.LauncherControl;
            _settings.Tools.ConfirmWrites = fresh.Tools.ConfirmWrites;
            _settings.Tools.TimeoutSeconds = fresh.Tools.TimeoutSeconds;
            _settings.Tools.DisabledTools = fresh.Tools.DisabledTools;
            _settings.ShowInspector = fresh.ShowInspector;
            _settings.SystemPrompt = fresh.SystemPrompt;
            _settings.Providers = fresh.Providers;
            _settings.ActiveProviderId = fresh.ActiveProviderId;
            _settings.ActiveModel = fresh.ActiveModel;

            FillProviderCombos();
            ApplyQuickSettings();
            ApplyInspectorVisibility();
            RefreshInspector();
            RefreshMemoryPanel();
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

        MessagesScroll.ScrollChanged += (_, _) =>
        {
            _stickToBottom = MessagesScroll.Offset.Y + MessagesScroll.Viewport.Height >=
                             MessagesScroll.Extent.Height - 48;
        };
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
        _session.Notice += text => RunOnUi(() => Shell.Toast(text));
    }

    private static void RunOnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    // ==================================================================
    // 会话管理
    // ==================================================================

    private void OpenConversation(AgentConversation conversation)
    {
        _activeConversationId = conversation.Id;
        _settings.LastConversationId = conversation.Id;
        AgentSettingsStore.Save(_settings);

        _session.Load(conversation);
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

        EmptyState.IsVisible = conversation.Messages.Count == 0;
        RebuildSessionList();
        RefreshHeader();
        RefreshInspector();
        AutoScroll();
    }

    private void StartNewConversation(bool save = true)
    {
        // 当前会话还是空的就别再堆一条「新对话」，直接把光标放回输入框。
        if (_initialized && _session.Conversation.Messages.Count == 0 &&
            _conversations.Contains(_session.Conversation))
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

        // 按时间分组：今天 / 昨天 / 最近 7 天 / 更早。分组标题用与正文不同的字距，
        // 扫视时比一长串等权列表更容易定位。
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
        AgentStore.Delete(conversation.Id);
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
        if (_session.IsBusy)
        {
            Shell.Toast("先停止当前生成。");
            return;
        }

        if (!_pendingClear || (DateTime.Now - _pendingClearAt).TotalSeconds > 3)
        {
            _pendingClear = true;
            _pendingClearAt = DateTime.Now;
            Shell.Toast("再点一次清空当前会话。");
            return;
        }

        _pendingClear = false;
        AgentConversation conversation = _session.Conversation;
        conversation.Messages.Clear();
        _session.ClearDigest();
        AgentStore.Save(conversation);

        ClearMessageHosts();
        EmptyState.IsVisible = true;
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

    // ==================================================================
    // 发送
    // ==================================================================

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
        if (text.Length == 0 || _session.IsBusy) return;

        ComposerBox.Text = string.Empty;
        await RunExclusiveAsync(token => _session.SendAsync(text, token), "发送中");
    }

    private async Task RunExclusiveAsync(Func<CancellationToken, Task> action, string busyText)
    {
        if (_session.IsBusy) return;

        _generation = new CancellationTokenSource();
        SetBusy(true, busyText);
        _streamTimer.Start();

        try
        {
            await action(_generation.Token);
        }
        catch (OperationCanceledException)
        {
            // 玩家主动停止
        }
        catch (Exception error)
        {
            Shell.Toast("出错了：" + error.Message);
        }
        finally
        {
            FlushStream();
            _streamTimer.Stop();
            SetBusy(false, "就绪");
            _generation?.Dispose();
            _generation = null;

            _settings.LastConversationId = _session.Conversation.Id;
            AgentSettingsStore.Save(_settings);
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
        HeaderStatusText.Text = text;
        HeaderStatusDot.Fill = ThemeTokens.Brush(busy ? "NekoWarningBrush" : "NekoSuccessBrush");
        if (!busy) RefreshProviderReadiness();
    }

    /// <summary>没有 Key 的远程供应商在发请求前就提醒，别等 401 才知道。</summary>
    private void RefreshProviderReadiness()
    {
        AgentProviderConfig? provider = _settings.ActiveProvider;
        if (provider is null) return;

        (string status, _) = AgentProviderPresets.DescribeStatus(provider);
        if (status == "缺 Key")
        {
            HeaderStatusText.Text = "缺少 API Key";
            HeaderStatusDot.Fill = ThemeTokens.Brush("NekoWarningBrush");
            ToolTip.SetTip(HeaderStatusText, "设置 → 供应商 → 填 API Key");
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
            // 会话加载/生成结束都会把状态改回 Idle，这里统一按供应商配置决定显示
            // 「就绪」还是「缺少 API Key」。
            RefreshProviderReadiness();
        }
    }

    // ==================================================================
    // 消息渲染
    // ==================================================================

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

        // 头像行
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

    // ==================================================================
    // 交互式工具：提问 / 确认
    // ==================================================================

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

    // ==================================================================
    // 头部 / 检查器
    // ==================================================================

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
        _session.RecalculateContext();

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

        int tools = _session.ActiveTools.Sum(tool =>
            TokenEstimator.Estimate(tool.Name + tool.Description + tool.ParametersSchema));
        int system = TokenEstimator.Estimate(_settings.SystemPrompt) + 260;
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

    /// <summary>三段占比条：系统提示 / 工具描述 / 消息历史。</summary>
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

    // ==================================================================
    // 头部下拉
    // ==================================================================

    private void FillProviderCombos()
    {
        _loadingControls = true;
        ProviderCombo.ItemsSource = _settings.Providers.Select(p => p.Name).ToList();
        ProviderCombo.SelectedIndex = Math.Max(0,
            _settings.Providers.FindIndex(p => p.Id == _settings.ActiveProviderId));

        AgentProviderConfig provider = _settings.EnsureProvider();
        ModelCombo.ItemsSource = provider.Models.ToList();
        ModelCombo.SelectedIndex = Math.Max(0, provider.Models.IndexOf(_settings.ActiveModel));
        _settings.ContextWindow = _settings.ResolveContextWindow();
        ToolTip.SetTip(ProviderCombo, $"{provider.Name} · {AgentProviderPresets.DescribeKind(provider.Kind)}");
        ToolTip.SetTip(ModelCombo, "当前模型：" + (_settings.ActiveModel.Length == 0 ? "未选择" : _settings.ActiveModel));
        UpdateEmptyModelHint();
        RefreshProviderReadiness();
        _loadingControls = false;
    }

    private void OnProviderChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingControls) return;

        int index = ProviderCombo.SelectedIndex;
        if (index < 0 || index >= _settings.Providers.Count) return;

        _settings.ActiveProviderId = _settings.Providers[index].Id;
        AgentProviderConfig provider = _settings.EnsureProvider();
        _settings.ActiveModel = provider.Models.FirstOrDefault() ?? string.Empty;
        _settings.ContextWindow = provider.ContextWindow > 0
            ? provider.ContextWindow
            : AgentProviderPresets.GuessContextWindow(_settings.ActiveModel);

        AgentSettingsStore.Save(_settings);
        FillProviderCombos();
        RefreshInspector();
        Shell.Toast("已切换到 " + provider.Name);
    }

    private void OnModelChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingControls) return;

        string? model = ModelCombo.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(model)) return;

        _settings.ActiveModel = model;
        UpdateEmptyModelHint();
        AgentProviderConfig? provider = _settings.ActiveProvider;
        _settings.ContextWindow = provider is { ContextWindow: > 0 }
            ? provider.ContextWindow
            : AgentProviderPresets.GuessContextWindow(model);
        AgentSettingsStore.Save(_settings);
        RefreshInspector();
    }

    // ==================================================================
    // 快捷设置（完整设置见 MofuAgentSettingsView 页面）
    // ==================================================================

    private void OnOpenSettings(object? sender, RoutedEventArgs e) => Shell.Go("mofuagent-settings");

    private void OnShowMemory(object? sender, RoutedEventArgs e) => Shell.Go("mofuagent-settings");

    /// <summary>把设置页改过的值同步回右侧仪表上的快捷控件。</summary>
    private void ApplyQuickSettings()
    {
        _loadingControls = true;
        AutoCompactSwitch.IsChecked = _settings.AutoCompact;
        LauncherToolsSwitch.IsChecked = _settings.Tools.LauncherControl;
        TempSlider.Value = _settings.Temperature;
        MaxTokensSlider.Value = Math.Clamp(_settings.MaxOutputTokens, 256, 16384);
        TempValueText.Text = _settings.Temperature.ToString("0.00");
        MaxTokensText.Text = _settings.MaxOutputTokens.ToString();
        _loadingControls = false;
    }

    /// <summary>左栏只显示记忆条数，记忆明细在设置页。</summary>
    private void RefreshMemoryPanel() => MemoryCountText.Text = AgentMemory.All().Count + " 条";

    private void OnAutoCompactToggled(object? sender, RoutedEventArgs e)
    {
        if (_loadingControls) return;

        _settings.AutoCompact = AutoCompactSwitch.IsChecked == true;
        AgentSettingsStore.Save(_settings);
    }

    private void OnLauncherToolsToggled(object? sender, RoutedEventArgs e)
    {
        if (_loadingControls) return;

        _settings.Tools.LauncherControl = LauncherToolsSwitch.IsChecked == true;
        AgentSettingsStore.Save(_settings);
        RefreshInspector();
    }

    private void OnTemperatureChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loadingControls) return;

        _settings.Temperature = Math.Round(TempSlider.Value, 2);
        TempValueText.Text = _settings.Temperature.ToString("0.00");
        AgentSettingsStore.Save(_settings);
    }

    private void OnMaxTokensChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loadingControls) return;

        _settings.MaxOutputTokens = (int)Math.Round(MaxTokensSlider.Value / 64) * 64;
        MaxTokensText.Text = _settings.MaxOutputTokens.ToString();
        AgentSettingsStore.Save(_settings);
    }

    private void OnCompactNow(object? sender, RoutedEventArgs e)
    {
        if (!_session.CompactIfNeeded(force: true))
        {
            Shell.Toast("当前没有可压缩的历史（消息太少）。");
            return;
        }

        OpenConversation(_session.Conversation);
        RefreshInspector();
    }


    /// <summary>页头的「+」：直接进供应商配置台，并把新建按钮闪一下。</summary>
    private void OnAddProvider(object? sender, RoutedEventArgs e)
    {
        MofuAgentSettingsView.RequestFocusProvider();
        Shell.Go("mofuagent-settings");
    }

    private void OnToggleInspector(object? sender, RoutedEventArgs e)
    {
        _settings.ShowInspector = !_settings.ShowInspector;
        AgentSettingsStore.Save(_settings);
        ApplyInspectorVisibility();
    }

    private void UpdateEmptyModelHint()
    {
        AgentProviderConfig? provider = _settings.ActiveProvider;
        EmptyModelHint.Text = (provider?.Name ?? "未选择") + " / " +
                              (_settings.ActiveModel.Length == 0 ? "无模型" : _settings.ActiveModel);
    }

    /// <summary>收起右侧仪表，把宽度让给对话正文。窄窗口下很实用。</summary>
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
        if (sender is Button { Tag: string text })
        {
            ComposerBox.Text = text;
            ComposerBox.Focus();
            ComposerBox.CaretIndex = text.Length;
        }
    }


    // ==================================================================
    // 验收用（仅被 MOFU_AGENT_SHOT 预览钩子调用）
    // ==================================================================

    /// <summary>页面是否已经完成首次加载（预览钩子等它稳定后再灌数据）。</summary>
    internal bool IsReady => _initialized;

    internal void StartNewConversationForPreview() => StartNewConversation();

    /// <summary>跑一次会触发 ask_user 的对话，用于截图核对交互卡片。</summary>
    internal void SeedPreviewAsk()
    {
        if (_session.Conversation.Messages.Count > 0) StartNewConversation();
        ComposerBox.Text = "问我一下要做什么";
        _ = SendComposerAsync();
    }

    /// <summary>跑一段本地模拟对话，用来截图核对消息气泡、工具卡片与上下文仪表。</summary>
    internal async Task SeedPreviewConversationAsync()
    {
        if (_session.Conversation.Messages.Count > 0) return;

        AgentProviderConfig? mock = _settings.Providers.FirstOrDefault(p => p.Kind == AgentProviderKind.Mock);
        if (mock is not null && _settings.ActiveProvider?.Kind != AgentProviderKind.Mock)
        {
            _settings.ActiveProviderId = mock.Id;
            _settings.ActiveModel = mock.Models.FirstOrDefault() ?? "mofu-sim-1";
            FillProviderCombos();
        }

        ComposerBox.Text = "帮我看看有哪些实例，顺便告诉我现在的状态";
        await SendComposerAsync();
    }
}
