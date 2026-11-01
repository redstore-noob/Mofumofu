using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Mofumofu.Launcher.Services.Agent;

/// <summary>
/// 本地模拟供应商：不联网，用脚本化的回复把完整的 Agent 流程（流式输出、工具调用、
/// 工具结果回灌、多轮收尾）跑通。没有 API Key 时也能验收界面与工具链路。
/// </summary>
internal static class MockClient
{
    public static async Task<AgentTurn> CompleteAsync(
        AgentRequest request, IAgentStreamSink? sink, CancellationToken cancellationToken)
    {
        var turn = new AgentTurn();

        AgentMessage? lastTool = request.Messages.LastOrDefault(m => m.Role == AgentRole.Tool);
        // 会话在每轮开始时会先塞一条空助手消息占位，所以不能直接看最后一条
        AgentMessage? lastMeaningful = request.Messages.LastOrDefault(m => m.Role != AgentRole.Assistant);
        bool afterTool = lastMeaningful is { Role: AgentRole.Tool };

        if (afterTool && lastTool is not null)
        {
            string summary = SummarizeTool(lastTool);
            await StreamTextAsync(summary, sink, cancellationToken, turn);
            turn.Usage.PromptTokens = EstimatePrompt(request);
            turn.Usage.CompletionTokens = Math.Max(8, turn.Text.Length / 3);
            turn.FinishReason = "stop";
            return turn;
        }

        string userText = request.Messages.LastOrDefault(m => m.Role == AgentRole.User)?.Text ?? string.Empty;
        string lower = userText.ToLowerInvariant();

        // 先吐一句"打算做什么"，再发工具调用——与真实模型的行为一致
        if (Contains(lower, "实例", "版本", "instance", "version"))
        {
            await StreamTextAsync("我先看一眼本地的实例列表。\n", sink, cancellationToken, turn);
            EmitTool(turn, sink, "launcher.list_instances", "{}");
        }
        else if (Contains(lower, "启动", "launch", "开游戏"))
        {
            await StreamTextAsync("好，准备启动你选中的实例。\n", sink, cancellationToken, turn);
            EmitTool(turn, sink, "launcher.launch_selected", "{}");
        }
        else if (Contains(lower, "状态", "现在", "status"))
        {
            await StreamTextAsync("看一下当前启动器状态。\n", sink, cancellationToken, turn);
            EmitTool(turn, sink, "launcher.get_status", "{}");
        }
        else if (Contains(lower, "打开", "页面", "跳转", "设置", "open"))
        {
            string page = Contains(lower, "设置") ? "settings"
                : Contains(lower, "账户", "账号") ? "account"
                : Contains(lower, "下载") ? "download"
                : Contains(lower, "实例") ? "instances"
                : "home";
            await StreamTextAsync($"帮你跳到 {page} 页。\n", sink, cancellationToken, turn);
            EmitTool(turn, sink, "launcher.open_page", $"{{\"page\":\"{page}\"}}");
        }
        else if (Contains(lower, "问我", "让我选", "确认一下"))
        {
            await StreamTextAsync("先跟你确认一件事。\n", sink, cancellationToken, turn);
            EmitTool(turn, sink, "ask_user",
                "{\"question\":\"需要我现在做什么？\",\"options\":[\"看看实例\",\"启动游戏\",\"先聊会儿\"],\"allow_free_text\":true}");
        }
        else if (Contains(lower, "记住", "记忆", "remember"))
        {
            await StreamTextAsync("记下来了。\n", sink, cancellationToken, turn);
            EmitTool(turn, sink, "memory.remember",
                $"{{\"note\":{System.Text.Json.JsonSerializer.Serialize(userText)}}}");
        }
        else if (Contains(lower, "你好", "hello", "hi", "在吗"))
        {
            await StreamTextAsync(
                "在的，我是 MofuAgent。\n" +
                "现在跑的是内置的「本地模拟」供应商——不需要 API Key，专门用来验收界面和工具链。\n" +
                "你可以问我「有哪些实例」「现在的状态」，或者让我「打开设置」试试工具调用；" +
                "要接真模型，去右上角设置里填一条供应商就行。\n",
                sink, cancellationToken, turn);
        }
        else
        {
            await StreamTextAsync(
                $"收到：{Trim(userText, 40)}\n\n" +
                "当前是本地模拟模式，我只会走脚本化回复，但整条链路（流式、上下文、工具）都是真的。\n" +
                "可以试试这几句：\n" +
                "- 帮我看看有哪些实例\n" +
                "- 现在启动器是什么状态\n" +
                "- 打开设置页\n" +
                "接上真模型后，这些能力会由模型自己决定何时调用。\n",
                sink, cancellationToken, turn);
        }

        turn.Usage.PromptTokens = EstimatePrompt(request);
        turn.Usage.CompletionTokens = Math.Max(8, turn.Text.Length / 3);
        turn.FinishReason = turn.ToolCalls.Count > 0 ? "tool_calls" : "stop";
        return turn;
    }

    private static void EmitTool(AgentTurn turn, IAgentStreamSink? sink, string name, string arguments)
    {
        var call = new AgentToolCall
        {
            Id = "call_" + Guid.NewGuid().ToString("n")[..12],
            Name = name,
            ArgumentsJson = arguments,
        };
        turn.ToolCalls.Add(call);
        sink?.OnToolCall(call);
    }

    private static async Task StreamTextAsync(
        string text, IAgentStreamSink? sink, CancellationToken cancellationToken, AgentTurn turn)
    {
        // 按 3~6 个字符一块往外吐，模拟真实流式的节奏（也让 UI 的增量渲染有东西可测）
        int index = 0;
        var random = new Random(text.Length);
        while (index < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int size = Math.Min(random.Next(3, 7), text.Length - index);
            string chunk = text.Substring(index, size);
            index += size;

            turn.Text += chunk;
            sink?.OnText(chunk);
            await Task.Delay(18, cancellationToken);
        }
    }

    private static string SummarizeTool(AgentMessage tool)
    {
        string body = Trim(tool.Text.ReplaceLineEndings(" ").Trim(), 220);
        if (tool.IsError)
            return $"工具执行失败：{body}\n需要的话我可以换个方式再试一次。";

        return tool.ToolName switch
        {
            "launcher.list_instances" => $"这是本地实例的情况：\n\n{body}\n\n想进哪个？我可以直接帮你启动选中的那个。",
            "launcher.get_status" => $"当前状态：\n\n{body}",
            "launcher.launch_selected" => body,
            "launcher.open_page" => body,
            "memory.remember" => "已经记到本地记忆里了，下次对话我能翻出来。",
            "ask_user" => "收到你的选择，继续。",
            _ => body,
        };
    }

    private static int EstimatePrompt(AgentRequest request)
    {
        int tokens = TokenEstimator.Estimate(request.SystemPrompt);
        foreach (AgentMessage message in request.Messages) tokens += TokenEstimator.EstimateMessage(message);
        foreach (AgentToolDefinition tool in request.Tools) tokens += TokenEstimator.Estimate(tool.Name + tool.Description);
        return tokens;
    }

    private static bool Contains(string text, params string[] keywords) =>
        keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static string Trim(string text, int length) =>
        text.Length <= length ? text : text[..length] + "…";
}
