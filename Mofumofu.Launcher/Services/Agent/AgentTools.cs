using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mofumofu.Launcher.Services.Agent;

/// <summary>工具的风险等级：决定要不要先问玩家一句。</summary>
public enum AgentToolRisk
{
    /// <summary>只读，随便调。</summary>
    Read,

    /// <summary>会产生实际动作（启动游戏、跳页面）。</summary>
    Write,

    /// <summary>需要玩家当场回答（ask_user）。</summary>
    Interactive
}

/// <summary>工具执行时可用的回调与上下文。</summary>
public sealed class AgentToolContext
{
    /// <summary>向玩家提问并等待回答（UI 侧弹内联卡片）。</summary>
    public required Func<string, IReadOnlyList<string>, bool, CancellationToken, Task<string>> AskAsync { get; init; }

    /// <summary>执行写操作前的确认。返回 false 表示玩家拒绝。</summary>
    public required Func<AgentToolDefinition, string, CancellationToken, Task<bool>> ConfirmAsync { get; init; }

    /// <summary>当前选中的供应商/模型，工具可以据此调整输出。</summary>
    public string Model { get; init; } = string.Empty;

    public AgentSettings Settings { get; init; } = new();
}

/// <summary>一个可被模型调用的工具。</summary>
public sealed class AgentToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>JSON Schema 原文（对象）。</summary>
    public required string ParametersSchema { get; init; }

    public AgentToolRisk Risk { get; init; } = AgentToolRisk.Read;

    /// <summary>写操作是否默认要求确认。</summary>
    public bool RequiresConfirm { get; init; }

    public required Func<JsonElement, AgentToolContext, CancellationToken, Task<string>> Handler { get; init; }

    public JsonNode ParametersNode() =>
        JsonNode.Parse(string.IsNullOrWhiteSpace(ParametersSchema) ? "{}" : ParametersSchema) ?? new JsonObject();
}

/// <summary>内置工具集合 + 调用入口。</summary>
public sealed class AgentToolRegistry
{
    private readonly Dictionary<string, AgentToolDefinition> _tools = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<AgentToolDefinition> All => _tools.Values;

    public AgentToolDefinition? Find(string name) => _tools.TryGetValue(name, out var tool) ? tool : null;

    public void Register(AgentToolDefinition tool) => _tools[tool.Name] = tool;

    /// <summary>按设置过滤出本轮真正暴露给模型的工具。</summary>
    public IReadOnlyList<AgentToolDefinition> Resolve(AgentToolSettings settings) =>
        settings.Enabled
            ? _tools.Values
                .Where(tool => !settings.DisabledTools.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
                .Where(tool => settings.LauncherControl || !tool.Name.StartsWith("launcher.", StringComparison.Ordinal))
                .OrderBy(tool => tool.Name, StringComparer.Ordinal)
                .ToList()
            : Array.Empty<AgentToolDefinition>();

    /// <summary>执行一次调用：解参数、确认、超时、异常兜底，全部收敛成一段文本结果。</summary>
    public async Task<(string Content, bool IsError)> InvokeAsync(
        AgentToolCall call, AgentToolContext context, CancellationToken cancellationToken)
    {
        AgentToolDefinition? tool = Find(call.Name);
        if (tool is null)
            return ($"错误：不存在名为 {call.Name} 的工具。可用工具见系统提示。", true);

        JsonElement arguments;
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            arguments = document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            return ($"错误：工具参数不是合法 JSON（{error.Message}）。请重新生成参数。", true);
        }

        if (tool.Risk == AgentToolRisk.Write && tool.RequiresConfirm && context.Settings.Tools.ConfirmWrites)
        {
            bool approved = await context.ConfirmAsync(tool, call.ArgumentsJson, cancellationToken);
            if (!approved) return ("玩家拒绝执行这个操作。请换一种方式，或先和玩家确认需求。", true);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        int seconds = Math.Clamp(context.Settings.Tools.TimeoutSeconds, 3, 120);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));

        try
        {
            string content = await tool.Handler(arguments, context, timeout.Token);
            return (string.IsNullOrWhiteSpace(content) ? "（工具执行完成，没有返回内容）" : content, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ($"错误：工具 {tool.Name} 执行超过 {seconds} 秒，已中止。", true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            return ($"错误：工具 {tool.Name} 执行失败：{error.Message}", true);
        }
    }

    /// <summary>默认工具集：启动器控制 + 交互 + 记忆。</summary>
    public static AgentToolRegistry CreateDefault()
    {
        var registry = new AgentToolRegistry();

        registry.Register(new AgentToolDefinition
        {
            Name = "launcher.list_instances",
            Description = "列出本机所有 Minecraft 实例（版本目录），包含游戏版本、模组数量、上次游玩时间。需要了解玩家装了什么时调用。",
            ParametersSchema = """{"type":"object","properties":{},"required":[]}""",
            Handler = LauncherAgentTools.ListInstancesAsync,
        });

        registry.Register(new AgentToolDefinition
        {
            Name = "launcher.get_status",
            Description = "读取启动器当前状态：选中的实例与账号、游戏是否在运行、游戏目录。回答「现在什么情况」类问题前先调用。",
            ParametersSchema = """{"type":"object","properties":{},"required":[]}""",
            Handler = LauncherAgentTools.GetStatusAsync,
        });

        registry.Register(new AgentToolDefinition
        {
            Name = "launcher.launch_selected",
            Description = "启动玩家当前选中的实例。执行前应确认玩家确实想现在启动。",
            ParametersSchema = """{"type":"object","properties":{},"required":[]}""",
            Risk = AgentToolRisk.Write,
            RequiresConfirm = true,
            Handler = LauncherAgentTools.LaunchSelectedAsync,
        });

        registry.Register(new AgentToolDefinition
        {
            Name = "launcher.open_page",
            Description = "在启动器里切换到指定页面。可选值：home（桌面）、account（账户）、download（下载）、instances（实例）、music（音乐）、online（联机）、servers（服务器）、plugins（插件）、settings（设置）。",
            ParametersSchema = """
                {"type":"object","properties":{"page":{"type":"string","description":"页面 id","enum":["home","account","download","instances","music","online","servers","plugins","settings"]}},"required":["page"]}
                """,
            Risk = AgentToolRisk.Write,
            Handler = LauncherAgentTools.OpenPageAsync,
        });

        registry.Register(new AgentToolDefinition
        {
            Name = "launcher.toast",
            Description = "在启动器右下角弹一条轻提示，用来即时反馈一句话（不超过 60 字）。",
            ParametersSchema = """{"type":"object","properties":{"message":{"type":"string","description":"提示内容"}},"required":["message"]}""",
            Handler = LauncherAgentTools.ToastAsync,
        });

        registry.Register(new AgentToolDefinition
        {
            Name = "launcher.list_mods",
            Description = "列出某个实例 mods 目录里的模组文件名。不传 instance 时使用当前选中实例。",
            ParametersSchema = """{"type":"object","properties":{"instance":{"type":"string","description":"实例目录名，可省略"}},"required":[]}""",
            Handler = LauncherAgentTools.ListModsAsync,
        });

        registry.Register(new AgentToolDefinition
        {
            Name = "ask_user",
            Description = "当需要玩家做选择、或者缺少关键信息时，向玩家提问并等待回答。options 给 2~4 个短选项；玩家也可以自己输入。",
            ParametersSchema = """
                {"type":"object","properties":{"question":{"type":"string","description":"要向玩家问的问题"},"options":{"type":"array","items":{"type":"string"},"description":"可选项，最多 4 个"},"allow_free_text":{"type":"boolean","description":"是否允许自由输入，默认 true"}},"required":["question"]}
                """,
            Risk = AgentToolRisk.Interactive,
            Handler = async (arguments, context, cancellationToken) =>
            {
                string question = AgentToolArgs.String(arguments, "question") ?? "需要你确认一下：";
                var options = AgentToolArgs.StringArray(arguments, "options");
                bool freeText = AgentToolArgs.Bool(arguments, "allow_free_text", true);
                string answer = await context.AskAsync(question, options, freeText, cancellationToken);
                return "玩家的回答：" + answer;
            },
        });

        registry.Register(new AgentToolDefinition
        {
            Name = "memory.remember",
            Description = "把一条值得长期记住的信息写进本地记忆（玩家偏好、常用实例、约定等）。",
            ParametersSchema = """{"type":"object","properties":{"note":{"type":"string","description":"要记住的内容，一句话"}},"required":["note"]}""",
            Risk = AgentToolRisk.Write,
            Handler = (arguments, _, _) =>
            {
                string note = AgentToolArgs.String(arguments, "note") ?? string.Empty;
                AgentMemory.Add(note);
                return Task.FromResult("已记住：" + note);
            },
        });

        registry.Register(new AgentToolDefinition
        {
            Name = "memory.recall",
            Description = "在本地记忆里按关键词搜索以前记下的内容。没有关键词时返回最近的若干条。",
            ParametersSchema = """{"type":"object","properties":{"keyword":{"type":"string","description":"关键词，可省略"}},"required":[]}""",
            Handler = (arguments, _, _) =>
            {
                string? keyword = AgentToolArgs.String(arguments, "keyword");
                IReadOnlyList<string> notes = AgentMemory.Search(keyword, 12);
                return Task.FromResult(notes.Count == 0
                    ? "本地记忆里没有匹配的内容。"
                    : string.Join('\n', notes.Select(note => "- " + note)));
            },
        });

        return registry;
    }
}

/// <summary>工具参数的宽容读取：模型经常把数字写成字符串、把数组写成逗号分隔。</summary>
internal static class AgentToolArgs
{
    public static string? String(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return null;
        if (!arguments.TryGetProperty(name, out JsonElement value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static bool Bool(JsonElement arguments, string name, bool fallback)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return fallback;
        if (!arguments.TryGetProperty(name, out JsonElement value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out bool parsed) ? parsed : fallback,
            _ => fallback,
        };
    }

    public static List<string> StringArray(JsonElement arguments, string name)
    {
        var result = new List<string>();
        if (arguments.ValueKind != JsonValueKind.Object) return result;
        if (!arguments.TryGetProperty(name, out JsonElement value)) return result;

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                string? text = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
                if (!string.IsNullOrWhiteSpace(text)) result.Add(text.Trim());
            }
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            result.AddRange((value.GetString() ?? string.Empty)
                .Split(new[] { ',', '，', '|', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0));
        }

        return result.Take(6).ToList();
    }
}

/// <summary>跨会话的本地记忆（就是一个小 JSON 数组，存在配置目录下）。</summary>
public static class AgentMemory
{
    private static readonly object Gate = new();

    private static string PathFile => Path.Combine(AgentSettingsStore.RootDirectory, "memory.json");

    public static IReadOnlyList<string> All()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(PathFile)) return Array.Empty<string>();
                return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(PathFile)) ?? new List<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }

    public static void Add(string note)
    {
        if (string.IsNullOrWhiteSpace(note)) return;

        lock (Gate)
        {
            try
            {
                List<string> notes = All().ToList();
                string trimmed = note.Trim();
                if (trimmed.Length > 500) trimmed = trimmed[..500];
                if (notes.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) return;

                notes.Add(trimmed);
                if (notes.Count > 200) notes.RemoveRange(0, notes.Count - 200);

                Directory.CreateDirectory(AgentSettingsStore.RootDirectory);
                File.WriteAllText(PathFile, JsonSerializer.Serialize(notes));
            }
            catch
            {
                // 记忆写失败不影响对话
            }
        }
    }

    public static IReadOnlyList<string> Search(string? keyword, int limit)
    {
        IReadOnlyList<string> notes = All();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            notes = notes
                .Where(note => note.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return notes.TakeLast(limit).Reverse().ToList();
    }
}
