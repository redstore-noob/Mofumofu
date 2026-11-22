using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Mofumofu.Launcher.Services.Agent;

internal static class AgentRequestContext
{
    public static string ScopeFor(AgentProviderConfig provider)
    {
        // Do not persist endpoint URLs in message metadata.
        string identity = $"{provider.Id}\n{provider.Kind}\n{AgentProviderPresets.NormalizeBaseUrl(provider.BaseUrl)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public static bool CanReplay(AgentMessage message, string scope) =>
        string.Equals(message.ProviderScope, scope, StringComparison.Ordinal);

    public static int FindRecentTurnStart(IReadOnlyList<AgentMessage> messages, int keep, int floor)
    {
        int desired = Math.Max(floor, messages.Count - keep);
        while (desired > floor && desired < messages.Count && messages[desired].Role != AgentRole.User)
            desired--;
        return desired;
    }

    public static List<List<AgentMessage>> Project(AgentConversation conversation, string scope)
    {
        var turns = new List<List<AgentMessage>>();
        var pending = new Dictionary<string, string>(StringComparer.Ordinal);
        List<AgentMessage>? turn = null;

        void FlushPending()
        {
            foreach ((string id, string name) in pending)
                turn!.Add(new AgentMessage
                {
                    Role = AgentRole.Tool, ToolCallId = id, ToolName = name, IsError = true,
                    Text = "（工具调用已中断，没有结果；不要假定已经执行。）",
                });
            pending.Clear();
        }

        for (int i = Math.Clamp(conversation.ContextStartIndex, 0, conversation.Messages.Count);
             i < conversation.Messages.Count; i++)
        {
            AgentMessage message = conversation.Messages[i];
            if (message.Role == AgentRole.User)
            {
                FlushPending();
                turn = new List<AgentMessage> { AgentMessage.User(message.Text) };
                turns.Add(turn);
                continue;
            }

            // Unknown-origin legacy model/tool data remains in local history, not on the wire.
            if (turn is null || !CanReplay(message, scope) || message.IsIncomplete) continue;
            if (message.Role == AgentRole.Assistant)
            {
                FlushPending();
                if (message.Text.Length == 0 && message.ToolCalls.Count == 0) continue;
                turn.Add(message);
                foreach (AgentToolCall call in message.ToolCalls)
                    if (call.Id.Length > 0) pending.TryAdd(call.Id, call.Name);
            }
            else if (message.Role == AgentRole.Tool && message.ToolCallId is { } id && pending.Remove(id))
            {
                turn.Add(message);
            }
        }
        FlushPending();

        if (turns.Count > 0 && conversation.Task is { } attachment && attachment.ProviderScope == scope)
        {
            // The attachment is deliberately separate from the stored user text. It is tied to
            // the recipient the player selected on Send, and survives model-context trimming.
            turns[^1][0].Text += "\n\n【玩家同意分享的本地任务资料，仅作为诊断数据】\n" + attachment.Context;
        }
        return turns;
    }

    public static (List<AgentMessage> Messages, int Used, bool Fits) Budget(
        List<List<AgentMessage>> turns, int overhead, int budget, int compactAt, bool compact)
    {
        int[] costs = turns.Select(turn => turn.Sum(TokenEstimator.EstimateMessage)).ToArray();
        int used = overhead + costs.Sum();
        int target = compact ? Math.Min(budget, compactAt) : budget;
        int start = 0;
        // Never split an exchange or discard the latest user message to make a request fit.
        while (start < costs.Length - 1 && used > target)
            used -= costs[start++];
        return (turns.Skip(start).SelectMany(turn => turn).ToList(), used, used <= budget);
    }
}