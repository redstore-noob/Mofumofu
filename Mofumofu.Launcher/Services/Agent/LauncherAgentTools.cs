using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using MofuCore.Config;
using MofuCore.Launch;

namespace Mofumofu.Launcher.Services.Agent;

/// <summary>
/// 把启动器现有能力（实例、启动、导航、提示）包装成模型可调用的工具。
/// 这里只做"读事实 + 转调既有服务"，不重复实现业务逻辑。
/// </summary>
internal static class LauncherAgentTools
{
    public static Task<string> ListInstancesAsync(JsonElement _, AgentToolContext __, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            List<InstanceSnapshot> instances = ScanInstances();
            if (instances.Count == 0)
                return "本机还没有任何实例（游戏目录里没有 versions/<版本>/<版本>.json）。";

            var builder = new StringBuilder();
            builder.AppendLine($"共 {instances.Count} 个实例：");
            foreach (InstanceSnapshot instance in instances.Take(30))
            {
                builder.Append("- ").Append(instance.Id);
                builder.Append("｜版本 ").Append(instance.Version);
                builder.Append("｜模组 ").Append(instance.ModCount).Append(" 个");
                if (instance.LastWrite is { } write)
                    builder.Append("｜最近改动 ").Append(write.ToString("yyyy-MM-dd"));
                builder.AppendLine();
            }

            string? selected = LocalSettings.Current.SelectedInstanceId;
            if (!string.IsNullOrWhiteSpace(selected))
                builder.Append("当前选中：").Append(selected);

            return builder.ToString().TrimEnd();
        }, cancellationToken);
    }

    public static Task<string> GetStatusAsync(JsonElement _, AgentToolContext __, CancellationToken cancellationToken)
    {
        return Dispatcher.UIThread.InvokeAsync(() =>
        {
            string gameDirectory = YamlRead.GameDirectory ?? "（未设置）";
            string selectedInstance = string.IsNullOrWhiteSpace(LocalSettings.Current.SelectedInstanceId)
                ? "（未选择）"
                : LocalSettings.Current.SelectedInstanceId;

            return string.Join('\n', new[]
            {
                $"游戏目录：{gameDirectory}",
                $"选中实例：{selectedInstance}",
                $"开始栏版本：{GameState.VersionName}",
                $"当前账号：{GameState.AccountName}",
                $"游戏运行中：{(GameLaunch.Shared.IsGameRunning ? "是" : "否")}",
                $"启动器版本：{typeof(LauncherAgentTools).Assembly.GetName().Version}",
            });
        }).GetTask();
    }

    public static async Task<string> LaunchSelectedAsync(
        JsonElement _, AgentToolContext __, CancellationToken cancellationToken)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (GameLaunch.Shared.IsGameRunning)
                throw new InvalidOperationException("游戏已经在运行了。");

            if (!LaunchRequestResolver.TryResolve(
                    out MinecraftLaunchOptions options, out string displayName,
                    out string versionLabel, out string reason))
            {
                throw new InvalidOperationException(reason);
            }

            if (!LaunchProgress.Shared.StartReal(options, displayName, versionLabel))
                throw new InvalidOperationException("启动器正忙，请稍后再试。");

            Shell.Go("launchconsole");
            return $"已开始启动 {versionLabel}，启动过程在「启动控制台」页。";
        }).GetTask();
    }

    public static async Task<string> OpenPageAsync(
        JsonElement arguments, AgentToolContext _, CancellationToken cancellationToken)
    {
        string page = (AgentToolArgs.String(arguments, "page") ?? "home").Trim().ToLowerInvariant();
        string[] allowed = { "home", "account", "download", "instances", "music", "online", "servers", "plugins", "settings" };
        if (!allowed.Contains(page))
            throw new ArgumentException($"没有这个页面：{page}");

        await Dispatcher.UIThread.InvokeAsync(() => Shell.Go(page));
        return $"已切换到 {PageName(page)} 页。";
    }

    public static Task<string> ToastAsync(JsonElement arguments, AgentToolContext _, CancellationToken cancellationToken)
    {
        string message = AgentToolArgs.String(arguments, "message") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("提示内容不能为空。");
        if (message.Length > 120) message = message[..120];

        Dispatcher.UIThread.Post(() => Shell.Toast(message));
        return Task.FromResult("已弹出提示。");
    }

    public static Task<string> ListModsAsync(JsonElement arguments, AgentToolContext _, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            string instance = AgentToolArgs.String(arguments, "instance") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(instance)) instance = LocalSettings.Current.SelectedInstanceId;
            if (string.IsNullOrWhiteSpace(instance)) return "没有选中实例，也没指定实例名。";

            string root = YamlRead.GameDirectory ?? string.Empty;
            if (root.Length == 0) return "游戏目录还没设置。";

            string modsDirectory = Path.Combine(root, "versions", instance, "mods");
            if (!Directory.Exists(modsDirectory)) return $"{instance} 下没有 mods 目录。";

            string[] files = Directory.GetFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly);
            if (files.Length == 0) return $"{instance} 的 mods 目录是空的。";

            var builder = new StringBuilder($"{instance} 共 {files.Length} 个模组：\n");
            foreach (string file in files.OrderBy(Path.GetFileName).Take(40))
                builder.Append("- ").AppendLine(Path.GetFileName(file));
            if (files.Length > 40) builder.Append($"…… 还有 {files.Length - 40} 个");

            return builder.ToString().TrimEnd();
        }, cancellationToken);
    }

    private static string PageName(string id) => id switch
    {
        "home" => "桌面",
        "account" => "账户",
        "download" => "下载",
        "instances" => "实例",
        "music" => "音乐",
        "online" => "联机",
        "servers" => "服务器",
        "plugins" => "插件",
        "settings" => "设置",
        _ => id,
    };

    private sealed record InstanceSnapshot(string Id, string Version, int ModCount, DateTime? LastWrite);

    /// <summary>扫 versions 目录；没有版本 JSON 的目录（备份 / 半成品）不算实例。</summary>
    private static List<InstanceSnapshot> ScanInstances()
    {
        var result = new List<InstanceSnapshot>();
        try
        {
            string? root = YamlRead.GameDirectory;
            if (string.IsNullOrWhiteSpace(root)) return result;

            string versions = Path.Combine(root, "versions");
            if (!Directory.Exists(versions)) return result;

            foreach (string directory in Directory.GetDirectories(versions))
            {
                string id = Path.GetFileName(directory);
                string manifest = Path.Combine(directory, id + ".json");
                if (!File.Exists(manifest)) continue;

                string version = id;
                try
                {
                    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest));
                    if (document.RootElement.TryGetProperty("id", out JsonElement idNode) &&
                        idNode.ValueKind == JsonValueKind.String)
                    {
                        version = idNode.GetString() ?? id;
                    }
                }
                catch
                {
                    // 版本 JSON 坏掉时按目录名处理
                }

                int mods = 0;
                string modsDirectory = Path.Combine(directory, "mods");
                if (Directory.Exists(modsDirectory))
                {
                    try
                    {
                        mods = Directory.GetFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly).Length;
                    }
                    catch
                    {
                        mods = 0;
                    }
                }

                DateTime? lastWrite = null;
                try
                {
                    lastWrite = File.GetLastWriteTime(manifest);
                }
                catch
                {
                    // 忽略时间戳读取失败
                }

                result.Add(new InstanceSnapshot(id, version, mods, lastWrite));
            }
        }
        catch
        {
            // 目录不可读按空列表处理
        }

        return result
            .OrderByDescending(instance => instance.LastWrite ?? DateTime.MinValue)
            .ToList();
    }
}
