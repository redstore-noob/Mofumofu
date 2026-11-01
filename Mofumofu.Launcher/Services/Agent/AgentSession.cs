using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MofuCore.Config;
using MofuCore.Launch;

namespace Mofumofu.Launcher.Services.Agent;

public enum AgentSessionState
{
    Idle,
    Thinking,
    Streaming,
    RunningTool,
    WaitingUser,
    Error
}

/// <summary>
/// 一次对话的运行时：组装请求、流式接收、跑工具轮、维护上下文用量与自动压缩。
/// UI 只需要订阅事件，不关心供应商差异。
/// </summary>
public sealed class AgentSession
{
    private readonly AgentToolRegistry _tools = AgentToolRegistry.CreateDefault();
    private readonly StringBuilder _digest = new();

    public AgentSession(AgentSettings settings, AgentConversation? conversation = null)
    {
        Settings = settings;
        Conversation = conversation ?? new AgentConversation();
        TokenEstimator.Recalculate(Conversation.Messages);
    }

    public AgentSettings Settings { get; }
    public AgentConversation Conversation { get; private set; }

    /// <summary>UI 侧注入：向玩家提问（内联卡片）。</summary>
    public Func<string, IReadOnlyList<string>, bool, CancellationToken, Task<string>>? AskHandler { get; set; }

    /// <summary>UI 侧注入：写操作确认。</summary>
    public Func<AgentToolDefinition, string, CancellationToken, Task<bool>>? ConfirmHandler { get; set; }

    public AgentSessionState State { get; private set; } = AgentSessionState.Idle;

    /// <summary>最近一次请求的用量（供应商真实返回）。</summary>
    public AgentUsage LastUsage { get; private set; } = new();

    /// <summary>当前上下文占用估算（含系统提示与工具 schema）。</summary>
    public int ContextUsed { get; private set; }

    public int ContextWindow => Math.Max(2048, Settings.ContextWindow);

    public double ContextRatio => Math.Clamp(ContextUsed / (double)ContextWindow, 0, 1);

    public bool IsBusy => State is AgentSessionState.Thinking or AgentSessionState.Streaming
        or AgentSessionState.RunningTool or AgentSessionState.WaitingUser;

    public IReadOnlyList<AgentToolDefinition> ActiveTools => _tools.Resolve(Settings.Tools);

    public event Action<AgentMessage>? MessageAdded;
    public event Action<AgentMessage>? MessageUpdated;
    public event Action<string>? TextDelta;
    public event Action<string>? ReasoningDelta;
    public event Action<AgentToolCall>? ToolStarted;
    public event Action<AgentToolCall, string, bool, TimeSpan>? ToolFinished;
    public event Action<AgentSessionState, string>? StateChanged;
    public event Action? UsageChanged;
    public event Action<string>? Notice;

    public void Load(AgentConversation conversation)
    {
        Conversation = conversation;
        TokenEstimator.Recalculate(Conversation.Messages);
        RecalculateContext();
        SetState(AgentSessionState.Idle, "就绪");
        UsageChanged?.Invoke();
    }

    // ==================================================================
    // 主流程
    // ==================================================================

    public async Task SendAsync(string text, CancellationToken cancellationToken)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (IsBusy) return;

        if (Conversation.Messages.Count == 0)
        {
            Conversation.Title = TokenEstimator.MakeTitle(text);
            Conversation.CreatedAt = DateTime.Now;
        }

        AppendMessage(AgentMessage.User(text));
        await RunLoopAsync(cancellationToken);
    }

    /// <summary>重新生成：丢掉最后一条助手回复（连同它的工具结果），再跑一遍。</summary>
    public async Task RegenerateAsync(CancellationToken cancellationToken)
    {
        if (IsBusy) return;

        List<AgentMessage> messages = Conversation.Messages;
        int index = messages.FindLastIndex(m => m.Role == AgentRole.Assistant);
        if (index < 0) return;

        messages.RemoveRange(index, messages.Count - index);
        RecalculateContext();
        await RunLoopAsync(cancellationToken);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        AgentProviderConfig provider = Settings.EnsureProvider();
        if (string.IsNullOrWhiteSpace(Settings.ActiveModel))
        {
            SetState(AgentSessionState.Error, "还没选择模型");
            AppendNotice("还没有可用模型。请到设置里选择供应商并填入模型 id。", isError: true);
            return;
        }

        try
        {
            int rounds = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rounds++ >= Math.Clamp(Settings.MaxToolRounds, 1, 20))
                {
                    AppendNotice($"已达到单轮工具调用上限（{Settings.MaxToolRounds} 轮），先停下来。", isError: true);
                    break;
                }

                CompactIfNeeded();

                var assistant = new AgentMessage { Role = AgentRole.Assistant };
                AppendMessage(assistant);

                SetState(AgentSessionState.Thinking, "正在思考");
                AgentRequest request = BuildRequest(provider);
                var sink = new Sink(this, assistant);

                AgentTurn turn;
                try
                {
                    turn = await AgentClient.CompleteAsync(request, sink, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    if (string.IsNullOrEmpty(assistant.Text)) assistant.Text = "（已停止）";
                    assistant.Tokens = TokenEstimator.EstimateMessage(assistant);
                    MessageUpdated?.Invoke(assistant);
                    throw;
                }
                catch (Exception error)
                {
                    Conversation.Messages.Remove(assistant);
                    MessageUpdated?.Invoke(assistant);
                    AppendNotice(error.Message, isError: true);
                    SetState(AgentSessionState.Error, "请求失败");
                    return;
                }

                if (string.IsNullOrEmpty(assistant.Text) && assistant.ToolCalls.Count == 0)
                    assistant.Text = "（模型没有返回内容）";

                assistant.Tokens = TokenEstimator.EstimateMessage(assistant);
                LastUsage = turn.Usage;
                RecalculateContext();
                MessageUpdated?.Invoke(assistant);

                if (assistant.ToolCalls.Count == 0) break;

                foreach (AgentToolCall call in assistant.ToolCalls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SetState(AgentSessionState.RunningTool, "执行 " + call.Name);
                    ToolStarted?.Invoke(call);

                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    (string content, bool isError) = await _tools.InvokeAsync(call, BuildToolContext(), cancellationToken);
                    stopwatch.Stop();

                    AgentMessage result = AgentMessage.ToolResult(call, content, isError);
                    result.Tokens = TokenEstimator.EstimateMessage(result);
                    AppendMessage(result);
                    ToolFinished?.Invoke(call, content, isError, stopwatch.Elapsed);
                }
            }

            AgentStore.Save(Conversation);
            SetState(AgentSessionState.Idle, "就绪");
        }
        catch (OperationCanceledException)
        {
            AgentStore.Save(Conversation);
            SetState(AgentSessionState.Idle, "已停止");
        }
        finally
        {
            RecalculateContext();
            UsageChanged?.Invoke();
        }
    }

    private AgentToolContext BuildToolContext() => new()
    {
        AskAsync = (question, options, freeText, ct) =>
            AskHandler is null
                ? Task.FromResult("（界面不支持提问，按默认继续）")
                : AskHandler(question, options, freeText, ct),
        ConfirmAsync = (tool, arguments, ct) =>
            ConfirmHandler is null ? Task.FromResult(true) : ConfirmHandler(tool, arguments, ct),
        Model = Settings.ActiveModel,
        Settings = Settings,
    };

    private AgentRequest BuildRequest(AgentProviderConfig provider)
    {
        int toolTokens = ActiveTools.Sum(tool =>
            TokenEstimator.Estimate(tool.Name + tool.Description + tool.ParametersSchema));

        return new AgentRequest
        {
            Provider = provider,
            Model = Settings.ActiveModel,
            SystemPrompt = BuildSystemPrompt(toolTokens),
            Messages = Conversation.Messages
                .Where(message => message.Role != AgentRole.System)
                .Where(message => message.Role != AgentRole.Assistant ||
                                  !string.IsNullOrEmpty(message.Text) ||
                                  message.ToolCalls.Count > 0)
                .ToList(),
            Tools = ActiveTools,
            Temperature = Settings.Temperature,
            TopP = Settings.TopP,
            MaxOutputTokens = Settings.MaxOutputTokens,
            Streaming = Settings.Streaming,
        };
    }

    private string BuildSystemPrompt(int toolTokens)
    {
        var builder = new StringBuilder(Settings.SystemPrompt);

        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("【运行环境】");
        builder.Append("时间：").AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        builder.Append("系统：").AppendLine(Environment.OSVersion.VersionString);
        builder.Append("游戏目录：").AppendLine(YamlRead.GameDirectory ?? "（未设置）");
        builder.Append("选中实例：").AppendLine(string.IsNullOrWhiteSpace(LocalSettings.Current.SelectedInstanceId)
            ? "（未选择）"
            : LocalSettings.Current.SelectedInstanceId);
        builder.Append("当前账号：").AppendLine(GameState.AccountName);
        builder.Append("游戏运行中：").AppendLine(GameLaunch.Shared.IsGameRunning ? "是" : "否");

        if (ActiveTools.Count > 0)
        {
            builder.AppendLine();
            builder.Append("可用工具：").AppendLine(string.Join("、", ActiveTools.Select(tool => tool.Name)));
        }
        else
        {
            builder.AppendLine();
            builder.AppendLine("本轮没有任何可用工具，只能基于对话内容回答。");
        }

        if (_digest.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("【较早对话摘要】");
            builder.AppendLine(_digest.ToString());
        }

        builder.AppendLine();
        builder.Append("（上下文预算：约 ").Append(ContextWindow - toolTokens)
            .Append(" token，回答保持简洁。）");

        return builder.ToString();
    }

    // ==================================================================
    // 上下文
    // ==================================================================

    public void RecalculateContext()
    {
        int system = TokenEstimator.Estimate(Settings.SystemPrompt) + 260;
        int tools = ActiveTools.Sum(tool =>
            TokenEstimator.Estimate(tool.Name + tool.Description + tool.ParametersSchema));
        int messages = Conversation.Messages
            .Where(message => message.Role != AgentRole.System)
            .Sum(message => message.Tokens);

        int digest = TokenEstimator.Estimate(_digest.ToString());
        ContextUsed = system + tools + messages + digest;
    }

    /// <summary>
    /// 超过阈值时把较早的消息压成摘要，保留最近若干条。
    /// 摘要本身也占 token，所以：单条只截很短一段、整体封顶、并且压完如果没省下 token 就继续瘦身。
    /// </summary>
    public bool CompactIfNeeded(bool force = false)
    {
        RecalculateContext();
        if (!force && (!Settings.AutoCompact || ContextRatio < Math.Clamp(Settings.CompactAt, 0.3, 0.95)))
            return false;

        int keep = Math.Clamp(Settings.KeepRecentMessages, 2, 60);
        List<AgentMessage> messages = Conversation.Messages;
        int cut = messages.Count - keep;
        if (cut <= 0) return false;

        // 不要把工具结果和它的调用拆开
        while (cut < messages.Count && messages[cut].Role == AgentRole.Tool) cut++;
        if (cut >= messages.Count) return false;

        int before = ContextUsed;

        var summary = new StringBuilder();
        foreach (AgentMessage message in messages.Take(cut))
        {
            string label = message.Role switch
            {
                AgentRole.User => "玩家",
                AgentRole.Assistant => "助手",
                AgentRole.Tool => "工具" + (message.ToolName is null ? "" : "·" + message.ToolName),
                _ => "系统",
            };

            string body = message.Text.ReplaceLineEndings(" ").Trim();
            int limit = message.Role == AgentRole.Tool ? 60 : 90;
            if (body.Length > limit) body = body[..limit] + "…";
            if (body.Length == 0 && message.ToolCalls.Count > 0)
                body = "调用 " + string.Join("、", message.ToolCalls.Select(call => call.Name));

            summary.Append("- ").Append(label).Append("：").AppendLine(body);
        }

        int removed = cut;
        messages.RemoveRange(0, cut);
        _digest.Append(summary.ToString().TrimEnd());
        TrimDigest(1600);

        RecalculateContext();

        // 兜底：如果摘要反而把上下文撑大了，就继续压缩摘要直到真的省下来
        for (int attempt = 0; attempt < 4 && ContextUsed >= before && _digest.Length > 200; attempt++)
        {
            TrimDigest(_digest.Length / 2);
            RecalculateContext();
        }

        Notice?.Invoke($"上下文已压缩：折叠了 {removed} 条较早消息，" +
                       $"{TokenEstimator.Format(before)} → {TokenEstimator.Format(ContextUsed)} token。");
        UsageChanged?.Invoke();
        return true;
    }

    /// <summary>把摘要裁剪到上限，按行从最早的地方丢弃。</summary>
    private void TrimDigest(int maxChars)
    {
        if (_digest.Length <= maxChars) return;

        string text = _digest.ToString();
        int start = text.Length - maxChars;
        int newline = text.IndexOf('\n', start);
        if (newline >= 0 && newline + 1 < text.Length) start = newline + 1;

        _digest.Clear();
        _digest.Append("（更早的对话已省略）\n").Append(text[start..].TrimStart());
    }

    public void ClearDigest() => _digest.Clear();

    // ==================================================================
    // 内部
    // ==================================================================

    private void AppendMessage(AgentMessage message)
    {
        Conversation.Messages.Add(message);
        message.Tokens = TokenEstimator.EstimateMessage(message);
        MessageAdded?.Invoke(message);
        RecalculateContext();
        UsageChanged?.Invoke();
        AgentStore.Save(Conversation);
    }

    private void AppendNotice(string text, bool isError)
    {
        var notice = new AgentMessage
        {
            Role = AgentRole.System,
            Text = text,
            IsError = isError,
        };
        AppendMessage(notice);
    }

    private void SetState(AgentSessionState state, string detail)
    {
        State = state;
        StateChanged?.Invoke(state, detail);
    }

    /// <summary>把供应商增量喂回当前助手消息。</summary>
    private sealed class Sink(AgentSession session, AgentMessage message) : IAgentStreamSink
    {
        public void OnText(string delta)
        {
            message.Text += delta;
            session.SetState(AgentSessionState.Streaming, "正在输出");
            session.TextDelta?.Invoke(delta);
        }

        public void OnReasoning(string delta)
        {
            message.Reasoning = (message.Reasoning ?? string.Empty) + delta;
            session.SetState(AgentSessionState.Streaming, "正在推理");
            session.ReasoningDelta?.Invoke(delta);
        }

        public void OnToolCall(AgentToolCall call)
        {
            message.ToolCalls.Add(call);
            session.MessageUpdated?.Invoke(message);
        }

        public void OnUsage(AgentUsage usage)
        {
            session.LastUsage = usage;
            session.UsageChanged?.Invoke();
        }
    }
}
