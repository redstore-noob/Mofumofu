using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mofumofu.Launcher.Services.Agent;

/// <summary>会话持久化：一个会话一个 JSON 文件，放在 {配置目录}/mofuagent/sessions。</summary>
public static class AgentStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IReadOnlyList<AgentConversation> List()
    {
        var result = new List<AgentConversation>();
        try
        {
            string directory = AgentSettingsStore.SessionsDirectory;
            if (!Directory.Exists(directory)) return result;

            foreach (string file in Directory.GetFiles(directory, "*.json"))
            {
                try
                {
                    AgentConversation? conversation = JsonSerializer.Deserialize<AgentConversation>(
                        File.ReadAllText(file), Options);
                    if (conversation is null || conversation.Messages.Count == 0) continue;
                    result.Add(conversation);
                }
                catch
                {
                    // 坏文件跳过，不影响其它会话
                }
            }
        }
        catch
        {
            // 目录不可读按空列表处理
        }

        return result.OrderByDescending(conversation => conversation.UpdatedAt).ToList();
    }

    public static string PathFor(string id) =>
        Path.Combine(AgentSettingsStore.SessionsDirectory, id + ".json");

    public static bool Save(AgentConversation conversation)
    {
        try
        {
            Directory.CreateDirectory(AgentSettingsStore.SessionsDirectory);
            conversation.UpdatedAt = DateTime.Now;
            File.WriteAllText(PathFor(conversation.Id), JsonSerializer.Serialize(conversation, Options));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool Delete(string id)
    {
        try
        {
            string path = PathFor(id);
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>导出为可读的 Markdown，方便贴到别处。</summary>
    public static string ExportMarkdown(AgentConversation conversation)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("# ").AppendLine(conversation.Title);
        builder.AppendLine();
        builder.Append("导出时间：").AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm"));

        foreach (AgentMessage message in conversation.Messages)
        {
            builder.AppendLine();
            switch (message.Role)
            {
                case AgentRole.User:
                    builder.Append("## 玩家").AppendLine();
                    break;
                case AgentRole.Assistant:
                    builder.Append("## MofuAgent").AppendLine();
                    break;
                case AgentRole.Tool:
                    builder.Append("> 工具 ").Append(message.ToolName).AppendLine(message.IsError ? "（失败）" : "");
                    break;
                default:
                    builder.Append("## 系统").AppendLine();
                    break;
            }

            builder.AppendLine();
            builder.AppendLine(message.Text);

            foreach (AgentToolCall call in message.ToolCalls)
            {
                builder.AppendLine();
                builder.Append("```json").AppendLine();
                builder.Append(call.Name).Append(' ').AppendLine(call.ArgumentsJson);
                builder.AppendLine("```");
            }
        }

        return builder.ToString();
    }
}
