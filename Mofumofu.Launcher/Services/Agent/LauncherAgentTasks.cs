using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MofuCore.Config;
using MofuCore.Launch;
using MofuCore.Log;
using MofuCore.Server;
using Mofumofu.Launcher.Models;
using L = Mofumofu.Launcher.Localization.Localization;

namespace Mofumofu.Launcher.Services.Agent;

internal static class LauncherAgentTasks
{
    public static AgentTaskDraft InspectInstance(InstanceInfo instance, string minecraftDirectory, string gameDirectory)
    {
        LaunchProfile? profile = null;
        try { profile = new VersionLoader().Load(minecraftDirectory, instance.Id); }
        catch (Exception error) when (error is MinecraftLaunchException or JsonException or IOException or UnauthorizedAccessException) { }

        GlobalLaunchSettings global = YamlRead.LoadGlobalLaunchSettings();
        InstanceJavaConfig config = YamlRead.LoadInstanceJavaConfig(Path.Combine(minecraftDirectory, "versions", instance.Id));
        int modCount = instance.ModCount;
        try
        {
            string mods = Path.Combine(gameDirectory, "mods");
            if (Directory.Exists(mods)) modCount = Directory.EnumerateFiles(mods, "*.jar").Count();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        MemoryAllocationResult memory = MemoryAllocation.Resolve(global, config, modCount,
            !string.Equals(instance.Loader, "Vanilla", StringComparison.OrdinalIgnoreCase));
        int? expectedJava = ServerManage.RequiredJavaMajorVersion(instance.Version);
        string javaRequirement = profile?.RequiredJavaMajorVersion is int requiredJava ? requiredJava.ToString()
            : expectedJava is int estimate ? L.T("Agent.Task.Java.Estimated", "{0}（按游戏版本估算）", estimate) : "—";
        string javaStatus = LaunchRequestResolver.ResolveJavaExecutable(global.JavaExecutable, instance.JavaPath).Length > 0
            ? L.T("Agent.Task.Java.Configured", "已找到候选 Java；未运行程序验证版本")
            : string.IsNullOrWhiteSpace(global.JavaExecutable) && string.IsNullOrWhiteSpace(instance.JavaPath)
                ? L.T("Agent.Task.Java.Auto", "未找到指定的 Java，启动时自动选择")
                : L.T("Agent.Task.Java.Missing", "配置的 Java 文件不可访问；启动时尝试自动选择");
        var lines = new[]
        {
            L.T("Agent.Task.Game", "游戏 {0} · 加载器 {1}", SafeToken(instance.Version), SafeToken(instance.Loader)),
            profile is not null ? L.T("Agent.Task.Instance.MetadataReady", "版本配置及继承链可读取。")
                : L.T("Agent.Task.Instance.MetadataMissing", "版本配置或继承链不可读取，可能需要修复实例。"),
            L.T("Agent.Task.ModCount", "模组数量：{0}", modCount),
            L.T("Agent.Task.Java", "所需 Java：{0}；{1}", javaRequirement, javaStatus),
            L.T("Agent.Task.Memory", "启动内存：{0}–{1} MiB（{2}）", memory.MinimumMb, memory.MaximumMb,
                memory.Automatic ? L.T("Agent.Task.Memory.Auto", "自动") : L.T("Agent.Task.Memory.Manual", "手动")),
            memory.TotalMb > 0 && memory.MaximumMb >= memory.TotalMb
                ? L.T("Agent.Task.Memory.Exceeds", "内存上限达到或超过物理内存，建议调低。")
                : memory.BelowRecommendation
                    ? L.T("Agent.Task.Memory.Limited", "自动内存低于实例建议值 {0} MiB，当前主机预算有限。", memory.RecommendedMb)
                    : string.Empty,
        };
        return new AgentTaskDraft(AgentTaskKind.InstanceCheck,
            L.T("Agent.Task.Instance.Prompt", "检查这个 Minecraft 实例的启动准备情况。按优先级列出我该检查或调整的项目，区分已确认事实和待验证项。"),
            string.Join('\n', lines.Where(line => line.Length > 0)));
    }

    // Some analyzer titles interpolate report text (including full filesystem paths).
    // Share only known rule IDs and narrow tokens, not titles, evidence or free text.
    private static readonly HashSet<string> CrashRules = new(StringComparer.Ordinal)
    {
        "process.exitcode", "jvm.arg", "jvm.arg.experimental", "jvm.heap.reserve", "jvm.startup", "jvm.native",
        "java.version.low", "java.version.high", "java.version.method", "java.version.declared",
        "memory.heap", "memory.nativeThread", "memory.metaspace", "memory.stack", "memory.system",
        "graphics.driver.opengl", "graphics.glfw", "graphics.opengl", "graphics.tdr", "graphics.atlas", "graphics.shader",
        "mod.dependencies", "mod.mixin", "mod.file", "mod.classnotfound", "mod.suspects",
        "auth.verify", "auth.session", "auth.maintenance", "file.corrupt", "file.permission", "file.locked", "file.disk",
        "world.tickingEntity", "world.tickingBlock", "world.sessionLock", "world.corrupt",
        "game.watchdog", "game.connection", "game.manual",
    };

    public static AgentTaskDraft DiagnoseCrash(LogAnalysisResult result)
    {
        var lines = new List<string>
        {
            L.T("Agent.Task.Game", "游戏 {0} · 加载器 {1}", SafeToken(result.MinecraftVersion), SafeToken(result.ModLoader)),
            L.T("Agent.Task.Crash.Runtime", "Java {0} · 内存上限 {1} MiB · 退出码 {2}",
                SafeToken(result.JavaVersion, version: true), result.MemoryMb, result.ExitCode?.ToString() ?? "—"),
        };
        lines.AddRange(result.Findings.Where(finding => CrashRules.Contains(finding.Category)).Take(8)
            .Select(finding => $"- [{finding.Severity}] {finding.Category}"));
        if (lines.Count == 2 && result.ModIssues.Count == 0)
            lines.Add(L.T("Agent.Task.Crash.NoFindings", "本地分析未识别到可分享的诊断线索。"));
        lines.AddRange(result.ModIssues.Take(5).Select(issue =>
            L.T("Agent.Task.Crash.Dependency", "- [{0}] 模组 {1} → 依赖 {2}（要求 {3}，实际 {4}）",
                issue.Kind, SafeToken(issue.SourceMod), SafeToken(issue.Dependency),
                SafeToken(issue.RequiredVersion, version: true), SafeToken(issue.ActualVersion, version: true))));
        return new AgentTaskDraft(AgentTaskKind.CrashDiagnosis,
            L.T("Agent.Task.Crash.Prompt", "根据这些本地诊断结论排查 Minecraft 崩溃。按可能性列出原因和可逆的排查步骤；缺少证据时说明还需要哪类信息，不要假设已经看到了原始日志。"),
            string.Join('\n', lines));
    }

    private static string SafeToken(string value, bool version = false)
    {
        string token = value.Trim();
        if (token.Length is < 1 or > 64 || token.Contains("..", StringComparison.Ordinal)) return "—";
        return token.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' ||
            (version && c is ('+' or '=' or '>' or '<' or '~' or '^' or '*'))) ? token : "—";
    }

}
