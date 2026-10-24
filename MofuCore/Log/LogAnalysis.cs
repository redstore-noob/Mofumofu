using System.Text;
using System.Text.RegularExpressions;

namespace MofuCore.Log;

/// <summary>单条分析结论的严重程度。</summary>
public enum LogAnalysisSeverity
{
    /// <summary>背景信息（如手动触发的调试崩溃、崩溃报告位置）。</summary>
    Info = 0,

    /// <summary>很可能参与了崩溃，但没有直接因果证据。</summary>
    Warning = 1,

    /// <summary>有直接证据的崩溃原因。</summary>
    Critical = 2,
}

/// <summary>模组依赖问题的种类。</summary>
public enum ModIssueKind
{
    /// <summary>缺少前置模组。</summary>
    MissingDependency,

    /// <summary>前置/游戏版本与要求不匹配。</summary>
    VersionMismatch,

    /// <summary>重复安装（同 mod id 出现多份 jar）。</summary>
    Duplicate,

    /// <summary>模组与游戏本体版本不兼容。</summary>
    GameIncompatible,
}

/// <summary>一条具体的模组依赖 / 冲突问题（由 Fabric / Forge / NeoForge 的报错块解析而来）。</summary>
public sealed class ModDependencyIssue
{
    /// <summary>提出依赖要求的模组 id（如 examplemod）；未知为空串。</summary>
    public string SourceMod { get; init; } = string.Empty;

    /// <summary>被依赖的模组 id 或 minecraft。</summary>
    public string Dependency { get; init; } = string.Empty;

    /// <summary>要求的版本 / 版本区间原文（如 ^1.2.0、[1.20.1,1.21)、any version）。</summary>
    public string RequiredVersion { get; init; } = string.Empty;

    /// <summary>实际安装的版本；缺失时为空串。</summary>
    public string ActualVersion { get; init; } = string.Empty;

    public ModIssueKind Kind { get; init; }

    /// <summary>触发这条结论的原始报错行。</summary>
    public string EvidenceLine { get; init; } = string.Empty;

    /// <summary>该问题来自哪个加载器的报错格式。</summary>
    public string Loader { get; init; } = string.Empty;

    /// <summary>人话描述：谁、要求什么、现状是什么。</summary>
    public override string ToString()
    {
        string source = SourceMod.Length > 0 ? SourceMod : "某模组";
        return Kind switch
        {
            ModIssueKind.MissingDependency =>
                $"{source} 缺少前置 {Dependency}（要求 {DisplayRequired}，当前没有安装）",
            ModIssueKind.VersionMismatch =>
                $"{source} 的前置 {Dependency} 版本不匹配（要求 {DisplayRequired}，实际为 {ActualVersion}）",
            ModIssueKind.Duplicate =>
                $"模组 {Dependency} 被安装了多份（{ActualVersion}）",
            ModIssueKind.GameIncompatible =>
                $"{source} 与当前游戏版本不兼容（要求 {DisplayRequired}，实际为 {ActualVersion}）",
            _ => $"{SourceMod} 与 {Dependency} 之间存在依赖问题（要求 {DisplayRequired}，实际为 {ActualVersion}）",
        };
    }

    private string DisplayRequired =>
        string.IsNullOrWhiteSpace(RequiredVersion) ? "任意版本" : RequiredVersion;
}

/// <summary>一条崩溃分析结论：原因 + 证据 + 决策。</summary>
public sealed class LogAnalysisFinding
{
    /// <summary>结论类别码（如 jvm.arg、memory.heap、mod.mixin），供程序区分，不用于展示。</summary>
    public string Category { get; init; } = string.Empty;

    public LogAnalysisSeverity Severity { get; init; }

    /// <summary>一句话原因，必须包含解析出的具体对象（模组名 / 版本 / 参数 / 路径）。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>命中的原始日志行（最多 3 条，已去重截断）。</summary>
    public IReadOnlyList<string> Evidence { get; init; } = [];

    /// <summary>对应的处置决策，按顺序执行即可。</summary>
    public IReadOnlyList<string> Decisions { get; init; } = [];

    public string SeverityDisplay => Severity switch
    {
        LogAnalysisSeverity.Critical => "严重",
        LogAnalysisSeverity.Warning => "可疑",
        _ => "信息",
    };
}

/// <summary>崩溃日志分析的输入。</summary>
public sealed class LogAnalysisInput
{
    /// <summary>启动日志全文（含游戏进程 stdout/stderr），分析时只看末尾。</summary>
    public string LogText { get; set; } = string.Empty;

    /// <summary>实例游戏目录下的 crash-reports 目录。</summary>
    public string CrashReportsDirectory { get; set; } = string.Empty;

    /// <summary>实例游戏目录（用于查找 JVM 崩溃产生的 hs_err_pid*.log 与相对路径的崩溃报告）。</summary>
    public string GameDirectory { get; set; } = string.Empty;

    /// <summary>游戏进程退出码；启动前失败（进程没拉起）时为 null。</summary>
    public int? ExitCode { get; set; }

    /// <summary>当前实例的版本 id（用于结论里引用）。</summary>
    public string MinecraftVersion { get; set; } = string.Empty;

    /// <summary>当前使用的 Java 版本描述（如 17.0.8）。</summary>
    public string JavaVersion { get; set; } = string.Empty;

    /// <summary>当前实例的最大内存 MiB（0 表示未知）。</summary>
    public int MemoryMb { get; set; }

    /// <summary>本次启动开始时间：用于只采信本次运行生成的崩溃报告 / hs_err 文件，避免把旧报告当新崩溃。</summary>
    public DateTimeOffset? GameStartedAt { get; set; }
}

/// <summary>崩溃日志分析结果。</summary>
public sealed class LogAnalysisResult
{
    /// <summary>全部结论（已按严重程度降序，去重）。</summary>
    public IReadOnlyList<LogAnalysisFinding> Findings { get; set; } = [];

    /// <summary>解析出的模组依赖 / 冲突问题明细。</summary>
    public IReadOnlyList<ModDependencyIssue> ModIssues { get; set; } = [];

    /// <summary>本次崩溃对应的崩溃报告路径；没有为空串。</summary>
    public string CrashReportPath { get; set; } = string.Empty;

    /// <summary>崩溃报告的 Description 字段（如 Ticking entity）。</summary>
    public string CrashDescription { get; set; } = string.Empty;

    /// <summary>崩溃报告 / 日志里的首个异常行（含 Caused by 链首条）。</summary>
    public string CrashException { get; set; } = string.Empty;

    /// <summary>崩溃报告 System Details 里的 Is Modded 行。</summary>
    public string IsModdedLine { get; set; } = string.Empty;

    /// <summary>从 Is Modded / 日志推断出的加载器：vanilla / fabric / forge / neoforge / quilt / unknown。</summary>
    public string ModLoader { get; set; } = "unknown";

    /// <summary>JVM 原生崩溃日志（hs_err_pid*.log）路径；没有为空串。</summary>
    public string HsErrPath { get; set; } = string.Empty;

    /// <summary>hs_err 中的 Problematic frame（如 C [aticfx64.dll+0x1a2b3]）。</summary>
    public string HsErrProblematicFrame { get; set; } = string.Empty;

    /// <summary>hs_err 中的信号（如 SIGSEGV / EXCEPTION_ACCESS_VIOLATION）。</summary>
    public string HsErrSignal { get; set; } = string.Empty;

    public int? ExitCode { get; set; }

    /// <summary>退出码含义（映射 Windows NT 状态码 / Unix 信号）；未知为空串。</summary>
    public string ExitCodeMeaning { get; set; } = string.Empty;

    /// <summary>一句话结论，直接作为弹窗正文开头。</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>是否识别出了至少一条确定性或可疑原因。</summary>
    public bool HasFindings => Findings.Count > 0;

    /// <summary>分析引用的版本信息（由分析器回填，供文本导出使用）。</summary>
    public string MinecraftVersion { get; set; } = string.Empty;

    /// <summary>分析引用的 Java 版本描述（由分析器回填）。</summary>
    public string JavaVersion { get; set; } = string.Empty;

    /// <summary>分析引用的内存上限 MiB（由分析器回填）。</summary>
    public int MemoryMb { get; set; }

    /// <summary>生成可直接复制上报 / 搜索的纯文本诊断。</summary>
    public string BuildPlainText()
    {
        StringBuilder text = new();
        text.AppendLine("== Mofumofu 崩溃分析 ==");
        if (MinecraftVersion.Length > 0)
        {
            text.Append("版本：").Append(MinecraftVersion).AppendLine();
        }

        text.Append("加载器：").AppendLine(ModLoader);
        if (JavaVersion.Length > 0)
        {
            text.Append("Java：").AppendLine(JavaVersion);
        }

        if (MemoryMb > 0)
        {
            text.Append("内存上限：").Append(MemoryMb).AppendLine(" MiB");
        }

        if (ExitCode is int exit)
        {
            text.Append("退出码：").Append(exit);
            if (ExitCodeMeaning.Length > 0)
            {
                text.Append("（").Append(ExitCodeMeaning).Append('）');
            }

            text.AppendLine();
        }

        if (CrashDescription.Length > 0)
        {
            text.Append("崩溃场景：").AppendLine(CrashDescription);
        }

        if (CrashException.Length > 0)
        {
            text.Append("首个异常：").AppendLine(CrashException);
        }

        if (IsModdedLine.Length > 0)
        {
            text.Append(IsModdedLine).AppendLine();
        }

        if (CrashReportPath.Length > 0)
        {
            text.Append("崩溃报告：").AppendLine(CrashReportPath);
        }

        if (HsErrPath.Length > 0)
        {
            text.Append("JVM 错误文件：").AppendLine(HsErrPath);
            if (HsErrSignal.Length > 0 || HsErrProblematicFrame.Length > 0)
            {
                text.Append("  信号：").Append(HsErrSignal)
                    .Append("  问题帧：").AppendLine(HsErrProblematicFrame);
            }
        }

        if (ModIssues.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("-- 模组依赖 / 冲突 --");
            foreach (ModDependencyIssue issue in ModIssues)
            {
                text.Append("  * ").AppendLine(issue.ToString());
            }
        }

        if (Findings.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("-- 分析结论 --");
            for (int index = 0; index < Findings.Count; index++)
            {
                LogAnalysisFinding finding = Findings[index];
                text.AppendLine()
                    .Append('[').Append(index + 1).Append("] ")
                    .Append('(').Append(finding.SeverityDisplay).Append(") ")
                    .AppendLine(finding.Title);
                foreach (string line in finding.Evidence)
                {
                    text.Append("    证据：").AppendLine(line);
                }

                for (int step = 0; step < finding.Decisions.Count; step++)
                {
                    text.Append("    决策 ").Append(step + 1).Append("：").AppendLine(finding.Decisions[step]);
                }
            }
        }

        return text.ToString();
    }
}

/// <summary>
/// 崩溃日志自动分析引擎：游戏非零退出后，把三路证据跑一遍正则规则库——
/// 1) 启动日志末尾（进程退出前的最后几屏）；
/// 2) 实例 crash-reports 里本次运行新生成的崩溃报告（含 Description / 堆栈 / Is Modded）；
/// 3) 游戏根目录的 hs_err_pid*.log（JVM 原生崩溃时 Minecraft 不写 crash-reports，只写这个）。
/// 规则覆盖 Minecraft Wiki「Crash」条目归纳的崩溃成因类别：模组（缺前置 / 冲突 / 重复 /
/// 版本不匹配）、Java 版本不匹配、硬件问题（内存 / 显卡）、软件拦截（杀毒软件 / 权限）、
/// JVM 原生崩溃、内存不足、世界文件损坏与手动触发（F3+C）。
/// 每条结论都提取具体对象（模组 id、版本区间、JVM 参数、文件名、显卡驱动 DLL），
/// 不输出笼统的「未知错误」。
/// </summary>
public static class LogAnalysis
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    // ------------------------------------------------------------------
    // 公共入口
    // ------------------------------------------------------------------

    /// <summary>分析一次崩溃，永不抛异常（内部异常吞掉并反映为空结论）。</summary>
    public static LogAnalysisResult Analyze(LogAnalysisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        try
        {
            return AnalyzeCore(input);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or RegexMatchTimeoutException)
        {
            return new LogAnalysisResult
            {
                Summary = "崩溃日志读取失败：" + e.Message,
            };
        }
    }

    private static LogAnalysisResult AnalyzeCore(LogAnalysisInput input)
    {
        Evidence evidence = CollectEvidence(input);
        string haystack = evidence.CombinedText;

        List<LogAnalysisFinding> findings = [];
        List<ModDependencyIssue> modIssues = [];

        AnalyzeExitCode(input, findings);
        AnalyzeJvmStartup(haystack, input, findings);
        AnalyzeJavaVersion(haystack, input, findings);
        AnalyzeMemory(haystack, input, evidence, findings);
        AnalyzeGraphics(haystack, findings);
        AnalyzeHsErr(evidence, findings);
        AnalyzeLoaderErrors(haystack, modIssues, findings);
        AnalyzeClassNotFound(haystack, findings);
        AnalyzeAuth(haystack, findings);
        AnalyzeFiles(haystack, findings);
        AnalyzeWorld(evidence, findings);
        AnalyzeGameSpecific(haystack, findings);
        InferSuspectMods(evidence, findings);

        // 同名结论去重（多条规则命中同一现象时保留证据最全的一条），再按严重程度降序
        List<LogAnalysisFinding> ordered = findings
            .GroupBy(finding => finding.Title, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(finding => finding.Evidence.Count).First())
            .OrderByDescending(finding => (int)finding.Severity)
            .ToList();

        LogAnalysisResult result = new()
        {
            Findings = ordered,
            ModIssues = modIssues
                .DistinctBy(issue => issue.ToString(), StringComparer.Ordinal)
                .ToList(),
            CrashReportPath = evidence.ReportPath,
            CrashDescription = evidence.ReportDescription,
            CrashException = evidence.ReportException,
            IsModdedLine = evidence.IsModdedLine,
            ModLoader = evidence.ModLoader,
            HsErrPath = evidence.HsErrPath,
            HsErrProblematicFrame = evidence.HsErrProblematicFrame,
            HsErrSignal = evidence.HsErrSignal,
            ExitCode = input.ExitCode,
            ExitCodeMeaning = input.ExitCode is int code ? DescribeExitCode(code) ?? string.Empty : string.Empty,
            MinecraftVersion = input.MinecraftVersion,
            JavaVersion = input.JavaVersion.Length > 0 ? input.JavaVersion : evidence.ReportJavaVersion,
            MemoryMb = input.MemoryMb > 0 ? input.MemoryMb : evidence.ReportMemoryMb,
        };
        result.Summary = BuildSummary(result);
        return result;
    }

    private static string BuildSummary(LogAnalysisResult result)
    {
        LogAnalysisFinding? top = result.Findings.FirstOrDefault();
        if (result.ModIssues.Count > 0)
        {
            return $"共 {result.Findings.Count} 条结论，其中 {result.ModIssues.Count} 条模组依赖/冲突问题，" +
                   "首要问题：" + result.ModIssues[0];
        }

        if (top is { Severity: LogAnalysisSeverity.Critical })
        {
            return top.Title;
        }

        if (top is not null)
        {
            return "共 " + result.Findings.Count + " 条结论：" + top.Title;
        }

        if (result.CrashReportPath.Length > 0)
        {
            string scene = result.CrashDescription.Length > 0 ? "（" + result.CrashDescription + "）" : string.Empty;
            return "已生成崩溃报告" + scene + "，但未匹配到已知崩溃模式；可把诊断信息发给开发者进一步分析。";
        }

        if (result.HsErrPath.Length > 0)
        {
            return "JVM 原生崩溃（不生成 crash-reports），详见 hs_err 日志：" + result.HsErrPath;
        }

        return "未找到崩溃报告，也未识别出已知崩溃模式；可把诊断信息发给开发者进一步分析。";
    }

    // ------------------------------------------------------------------
    // 证据采集
    // ------------------------------------------------------------------

    private sealed class Evidence
    {
        public string LogTailText { get; init; } = string.Empty;

        public string ReportPath { get; init; } = string.Empty;

        public string ReportContent { get; init; } = string.Empty;

        public string ReportDescription { get; init; } = string.Empty;

        public string ReportException { get; init; } = string.Empty;

        public string ReportJavaVersion { get; init; } = string.Empty;

        public int ReportMemoryMb { get; init; }

        public string IsModdedLine { get; init; } = string.Empty;

        public string ModLoader { get; init; } = "unknown";

        public string HsErrPath { get; init; } = string.Empty;

        public string HsErrContent { get; init; } = string.Empty;

        public string HsErrSignal { get; init; } = string.Empty;

        public string HsErrProblematicFrame { get; init; } = string.Empty;

        public string CombinedText =>
            LogTailText + "\n" + ReportContent + "\n" + HsErrContent;
    }

    private static Evidence CollectEvidence(LogAnalysisInput input)
    {
        string logTail = TailText(input.LogText, 400, 48 * 1024);

        // 崩溃发生时 stdout 会打出报告路径（相对实例目录），比按时间扫目录更可靠
        string reportPathFromLog = FirstMatch(logTail,
            Rx(@"This crash report has been saved to:\s*(?<value>.+?)\s*$"), "value");
        string? reportPath = ResolveWithinDirectory(reportPathFromLog, input.GameDirectory);
        if (reportPath is null && !string.IsNullOrWhiteSpace(input.CrashReportsDirectory))
        {
            reportPath = NewestMatchingFile(input.CrashReportsDirectory, "*.txt", input.GameStartedAt);
        }

        string reportContent = ReadBounded(reportPath, 64 * 1024);
        string? hsErrPath = NewestMatchingFileInRoot(input.GameDirectory, "hs_err_pid*.log", input.GameStartedAt);
        string hsErrContent = ReadBounded(hsErrPath, 32 * 1024);

        string isModdedLine = FirstMatch(reportContent, Rx(@"(?m)^Is Modded:\s*(?<value>.+?)\s*$"), "value");
        return new Evidence
        {
            LogTailText = logTail,
            ReportPath = reportPath ?? string.Empty,
            ReportContent = reportContent,
            ReportDescription = FirstMatch(reportContent,
                Rx(@"(?m)^\s*Description:\s*(?<value>.+?)\s*$"), "value"),
            ReportException = FirstException(reportContent),
            ReportJavaVersion = FirstMatch(reportContent,
                Rx(@"(?m)^Java Version:\s*(?<value>.+?)\s*$"), "value"),
            ReportMemoryMb = TryParseInt(FirstMatch(reportContent,
                Rx(@"(?m)^Memory:.*?up to \d+ bytes \((?<value>\d+) MiB\)"), "value")),
            IsModdedLine = isModdedLine,
            ModLoader = DetectModLoader(isModdedLine, logTail + "\n" + reportContent),
            HsErrPath = hsErrPath ?? string.Empty,
            HsErrContent = hsErrContent,
            HsErrSignal = FirstMatch(hsErrContent, Rx(@"#\s*(SIGSEGV|SIGBUS|SIGILL|SIGFPE|EXCEPTION_\w+)"), null),
            HsErrProblematicFrame = FirstMatch(hsErrContent,
                Rx(@"(?m)^#\s*[CJ]\s+\[(?<value>[\w.-]+\.(?:dll|so|dylib))[^\]]*\]"), "value"),
        };
    }

    /// <summary>Is Modded 行最可信；没有报告时再从日志里的加载器横幅推断。</summary>
    private static string DetectModLoader(string isModdedLine, string haystack)
    {
        string text = isModdedLine + "\n" + haystack;
        if (text.Contains("neoforge", StringComparison.OrdinalIgnoreCase) || text.Contains("NeoForge", StringComparison.Ordinal))
        {
            return "neoforge";
        }

        if (text.Contains("forge", StringComparison.OrdinalIgnoreCase))
        {
            return "forge";
        }

        if (text.Contains("quilt", StringComparison.OrdinalIgnoreCase))
        {
            return "quilt";
        }

        if (text.Contains("fabric", StringComparison.OrdinalIgnoreCase))
        {
            return "fabric";
        }

        if (isModdedLine.Contains("Definitely not", StringComparison.OrdinalIgnoreCase) ||
            isModdedLine.Contains("Probably not", StringComparison.OrdinalIgnoreCase))
        {
            return "vanilla";
        }

        return "unknown";
    }

    // ------------------------------------------------------------------
    // 退出码
    // ------------------------------------------------------------------

    private static void AnalyzeExitCode(
        LogAnalysisInput input, List<LogAnalysisFinding> findings)
    {
        if (input.ExitCode is not int code)
        {
            return;
        }

        string? meaning = DescribeExitCode(code);
        if (meaning is null)
        {
            return;
        }

        List<string> decisions = [];
        if ((uint)code is 0xC0000005 or 0xC0000374 or 0xC0000409 or 0xC00000FD)
        {
            decisions.Add("这是 JVM 原生崩溃：Minecraft 不会生成 crash-reports 报告，而是在游戏根目录写 hs_err_pid*.log（本次已解析）。");
            decisions.Add("多半与显卡驱动、LWJGL natives 或内存稳定性有关：优先更新显卡驱动，再校验游戏文件完整性。");
            decisions.Add("若问题稳定复现且与渲染无关，运行内存检测（Windows 内存诊断）排查物理内存故障。");
        }
        else if (code is 137 or 143)
        {
            decisions.Add("进程被系统强制终止：Linux/macOS 下常见于物理内存耗尽触发 OOM-Killer，请调小 -Xmx 或关闭其他占用内存的程序。");
        }
        else if (code == 1)
        {
            decisions.Add("游戏内部抛出了未捕获的 Java 异常，具体原因以「分析结论」里的堆栈为准。");
        }

        if (decisions.Count == 0)
        {
            return;
        }

        findings.Add(new LogAnalysisFinding
        {
            Category = "process.exitcode",
            Severity = code == 1 ? LogAnalysisSeverity.Info : LogAnalysisSeverity.Critical,
            Title = $"进程以退出码 {code} 结束：{meaning}",
            Evidence = ["exit code: " + code.ToString()],
            Decisions = decisions,
        });
    }

    /// <summary>Windows NT 状态码 / Unix 信号 → 含义；未知返回 null。</summary>
    private static string? DescribeExitCode(int code)
    {
        return code switch
        {
            0 => "正常退出",
            1 => "Java 未捕获异常退出（游戏内部报错）",
            134 => "SIGABRT：进程主动中止（Unix）",
            137 => "SIGKILL：进程被强制杀死（Unix，常见于系统内存耗尽）",
            139 => "SIGSEGV：段错误（Unix）",
            143 => "SIGTERM：进程被终止（Unix）",
            _ when (uint)code == 0xC0000005 =>
                "STATUS_ACCESS_VIOLATION：原生内存访问冲突（JVM 原生崩溃）",
            _ when (uint)code == 0xC0000374 =>
                "STATUS_HEAP_CORRUPTION：原生堆损坏（JVM 原生崩溃）",
            _ when (uint)code == 0xC0000409 =>
                "STATUS_STACK_BUFFER_OVERRUN：栈缓冲区溢出（JVM 原生崩溃）",
            _ when (uint)code == 0xC00000FD =>
                "STATUS_STACK_OVERFLOW：原生栈溢出",
            _ when (uint)code == 0xC000013A =>
                "STATUS_CONTROL_C_EXIT：进程被控制台 / Ctrl+C 终止",
            _ when (uint)code == 0x40000015 =>
                "STATUS_FATAL_APP_EXIT：应用致命错误退出",
            _ => null,
        };
    }

    // ------------------------------------------------------------------
    // JVM 启动失败（进程一闪而过 / 根本没起来）
    // ------------------------------------------------------------------

    private static void AnalyzeJvmStartup(
        string haystack, LogAnalysisInput input, List<LogAnalysisFinding> findings)
    {
        Match argument = Rx(@"Unrecognized (?:VM )?option[:\s]+'?(?<value>-?[\w:+=\[\]{}.%]+)")
            .Match(haystack);
        if (argument.Success)
        {
            string option = argument.Groups["value"].Value;
            findings.Add(new LogAnalysisFinding
            {
                Category = "jvm.arg",
                Severity = LogAnalysisSeverity.Critical,
                Title = $"当前 Java 不认识 JVM 参数 {option}，虚拟机在游戏启动前就退出了",
                Evidence = [TrimLine(argument.Value)],
                Decisions =
                [
                    $"在「实例设置 → JVM 参数」里删掉 {option}。",
                    $"该参数可能是为新版 Java 准备的：改用版本要求的 Java（当前 Java {FallbackText(input.JavaVersion)}）再试。",
                    "若参数来自启动器默认优化且无法删除，切换 Java 大版本通常即可解决。",
                ],
            });
        }

        Match experimental = Rx(@"(?<value>-XX:\+\w+)\s+is experimental and must be enabled").Match(haystack);
        if (experimental.Success)
        {
            findings.Add(new LogAnalysisFinding
            {
                Category = "jvm.arg.experimental",
                Severity = LogAnalysisSeverity.Critical,
                Title = $"实验性 JVM 参数 {experimental.Groups["value"].Value} 未开启 UnlockExperimentalVMOptions",
                Evidence = [TrimLine(experimental.Value)],
                Decisions =
                [
                    "确认 JVM 参数里 -XX:+UnlockExperimentalVMOptions 排在实验性参数之前。",
                    "或直接删除这条实验性参数。",
                ],
            });
        }

        Match heap = Rx(@"Could not reserve enough space for (?<kb>\d+)K?B? object heap").Match(haystack);
        if (heap.Success && int.TryParse(heap.Groups["kb"].Value, out int kb))
        {
            double wantedGb = Math.Round(kb / 1024.0 / 1024.0, 1);
            findings.Add(new LogAnalysisFinding
            {
                Category = "jvm.heap.reserve",
                Severity = LogAnalysisSeverity.Critical,
                Title = $"JVM 无法为对象堆保留 {wantedGb:0.#} GB 内存（当前实例上限 {FallbackMemory(input.MemoryMb)}）",
                Evidence = [TrimLine(heap.Value)],
                Decisions =
                [
                    "把实例的最大内存调小：系统可用内存不足时 JVM 初始化阶段直接失败。",
                    "如果用的是 32 位 Java，堆无论如何不能超过约 1.5 GB，请改用 64 位 Java。",
                    "关闭其他占用内存的程序后重试。",
                ],
            });
        }

        foreach ((Regex pattern, string title, string[] decisions) tuple in new[]
                 {
                     (Rx(@"Invalid (?:maximum|minimum) heap size"),
                         "启动参数里的 -Xmx/-Xms 数值非法",
                         new[]
                         {
                             "检查「实例设置 → JVM 参数」里手写的 -Xmx/-Xms 格式（如 4096M、4G），修正后重试。",
                         }),
                     (Rx(@"Could not find or load main class (?<value>[\w.$]+)"),
                         "找不到主类：版本 libraries / 客户端 jar 缺失或损坏",
                         new[]
                         {
                             "在实例详情里执行文件校验，或删除该版本目录后重新下载版本。",
                         }),
                     (Rx(@"(?m)(?:Error: )?[Uu]nable to access jarfile (?<value>[^\r\n]+)"),
                         "Java 无法访问游戏 jar 文件",
                         new[]
                         {
                             "客户端 jar 路径无效或文件缺失：重新下载 / 校验该版本文件。",
                         }),
                     (Rx("Multiple garbage collectors selected"),
                         "同时启用了多个垃圾回收器",
                         new[]
                         {
                             "JVM 参数里只保留一个 GC 选择（如 -XX:+UseG1GC），删除其余 -XX:+Use*GC。",
                         }),
                 })
        {
            Match match = tuple.pattern.Match(haystack);
            if (!match.Success)
            {
                continue;
            }

            string detail = match.Groups["value"].Success ? "：" + match.Groups["value"].Value : string.Empty;
            findings.Add(new LogAnalysisFinding
            {
                Category = "jvm.startup",
                Severity = LogAnalysisSeverity.Critical,
                Title = tuple.title + detail,
                Evidence = [TrimLine(match.Value)],
                Decisions = tuple.decisions,
            });
        }
    }

    // ------------------------------------------------------------------
    // Java 版本不匹配
    // ------------------------------------------------------------------

    /// <summary>class 文件主版本号 → Java 大版本。</summary>
    private static int? JavaMajorFromClassFile(int classFileVersion)
    {
        return classFileVersion is >= 45 and <= 76 ? classFileVersion - 44 : null;
    }

    private static void AnalyzeJavaVersion(
        string haystack, LogAnalysisInput input, List<LogAnalysisFinding> findings)
    {
        // 新版 JVM 报文：class file version 65.0 ... up to 61.0
        Match newer = Rx(
                @"class file version (?<needed>\d+)\.\d+\).*?up to (?<current>\d+)\.\d+")
            .Match(haystack);
        // 旧版 JVM 报文：UnsupportedClassVersionError: ... (class file version 61.0), Java 11
        Match legacy = Rx(
                @"class file version (?<needed>\d+)\.\d+\), Java (?<current>\d+)")
            .Match(haystack);
        Match compiled = newer.Success ? newer : legacy;
        if (!compiled.Success)
        {
            // 最老的 Java 8 报文只给需要的版本，不给当前版本
            Match majorOnly = Rx(@"Unsupported major\.minor version (?<needed>\d+)").Match(haystack);
            if (majorOnly.Success && int.TryParse(majorOnly.Groups["needed"].Value, out int onlyNeeded) &&
                JavaMajorFromClassFile(onlyNeeded) is int onlyJava)
            {
                findings.Add(new LogAnalysisFinding
                {
                    Category = "java.version.low",
                    Severity = LogAnalysisSeverity.Critical,
                    Title = $"Java 版本过低：实例需要 Java {onlyJava}（class file {onlyNeeded}.0），当前 Java 不支持",
                    Evidence = [TrimLine(majorOnly.Value)],
                    Decisions =
                    [
                        $"在「设置 → Java 运行时」切换到 Java {onlyJava}；没有的话保持联网重新启动，启动器会自动下载。",
                    ],
                });
            }
        }
        else if (compiled.Success &&
            int.TryParse(compiled.Groups["needed"].Value, out int needed) &&
            int.TryParse(compiled.Groups["current"].Value, out int current))
        {
            int? neededJava = JavaMajorFromClassFile(needed);
            int? currentJava = JavaMajorFromClassFile(current);
            string versionDetail = (neededJava is int need && currentJava is int have)
                ? $"需要 Java {need}，当前 Java {have}"
                : $"需要 class file {needed}.0，当前支持到 {current}.0";
            findings.Add(new LogAnalysisFinding
            {
                Category = "java.version.low",
                Severity = LogAnalysisSeverity.Critical,
                Title = "Java 版本过低：" + versionDetail +
                        $"（当前实例记录的 Java：{FallbackText(input.JavaVersion)}）",
                Evidence = [TrimLine(compiled.Value)],
                Decisions =
                [
                    neededJava is int major
                        ? $"在「设置 → Java 运行时」切换到 Java {major}；没有的话保持联网重新启动，启动器会自动下载。"
                        : "在「设置 → Java 运行时」切换到版本要求的 Java。",
                    "注意：模组 / 加载器对 Java 大版本敏感，不要长期用与版本要求不一致的 Java。",
                ],
            });
        }

        // 反向：组件太旧，跑在新 Java 上（javassist、guava、旧 Forge 常见）
        Match unsupportedMajor = Rx(@"Unsupported class file major version (?<value>\d+)").Match(haystack);
        if (unsupportedMajor.Success &&
            int.TryParse(unsupportedMajor.Groups["value"].Value, out int fileMajor) &&
            JavaMajorFromClassFile(fileMajor) is int fileJava)
        {
            findings.Add(new LogAnalysisFinding
            {
                Category = "java.version.high",
                Severity = LogAnalysisSeverity.Critical,
                Title = $"Java 版本过高：实例里的某个组件不认识 Java {fileJava} 的类文件（class file major {fileMajor}）",
                Evidence = [TrimLine(unsupportedMajor.Value)],
                Decisions =
                [
                    "旧版本 / 旧模组通常要求 Java 8 或 17：在「设置 → Java 运行时」切换到版本要求的 Java。",
                    "若是自己注入的库（如旧 javassist）报的，把对应组件升级到支持新 Java 的版本。",
                ],
            });
        }

        Match baseMethod = Rx(
                @"NoSuchMethodError: .*?java\.base/[\w.$]+\.[\w$<>]+\([^)]*\)")
            .Match(haystack);
        if (baseMethod.Success)
        {
            findings.Add(new LogAnalysisFinding
            {
                Category = "java.version.method",
                Severity = LogAnalysisSeverity.Critical,
                Title = "JDK 核心类缺少方法（java.base 抛 NoSuchMethodError）：几乎必然是 Java 大版本与实例要求不符",
                Evidence = [TrimLine(baseMethod.Value)],
                Decisions =
                [
                    "旧版本游戏 / 旧加载器跑到新 Java 上会出现这个错误：切换到版本要求的 Java（多数旧版本要求 Java 8，1.17 要求 16，1.18–1.20.4 要求 17，1.20.5+ 要求 21）。",
                ],
            });
        }

        Match requiresJava = Rx(@"requires Java (?<value>\d+)").Match(haystack);
        if (requiresJava.Success && int.TryParse(requiresJava.Groups["value"].Value, out int required))
        {
            findings.Add(new LogAnalysisFinding
            {
                Category = "java.version.declared",
                Severity = LogAnalysisSeverity.Critical,
                Title = $"加载器 / 模组声明需要 Java {required}，与当前运行时不符",
                Evidence = [TrimLine(requiresJava.Value)],
                Decisions = [$"在「设置 → Java 运行时」切换到 Java {required} 后重新启动。"],
            });
        }
    }

    // ------------------------------------------------------------------
    // 内存
    // ------------------------------------------------------------------

    private static void AnalyzeMemory(
        string haystack, LogAnalysisInput input, Evidence evidence, List<LogAnalysisFinding> findings)
    {
        string currentMemory = FallbackMemory(input.MemoryMb > 0 ? input.MemoryMb : evidence.ReportMemoryMb);

        if (Rx("OutOfMemoryError: Java heap space").IsMatch(haystack) ||
            Rx("GC overhead limit exceeded").IsMatch(haystack))
        {
            findings.Add(new LogAnalysisFinding
            {
                Category = "memory.heap",
                Severity = LogAnalysisSeverity.Critical,
                Title = $"Java 堆内存耗尽（OutOfMemoryError: Java heap space / GC overhead limit exceeded，当前上限 {currentMemory}）",
                Evidence = CollectEvidenceLines(haystack, "OutOfMemoryError", "GC overhead limit exceeded"),
                Decisions =
                [
                    $"把实例最大内存调大（当前 {currentMemory}）：1.20.x 原版建议 2048–4096 MiB，中等模组包 4096–6144 MiB，大型整合包 6144–8192 MiB。",
                    "如果内存已到系统上限：减少模组 / 光影 / 资源包规模，或换用更省内存的模组。",
                    "32 位 Java 最大只能用到约 1.5 GB，请确认使用的是 64 位 Java。",
                ],
            });
        }

        Match nativeThread = Rx("OutOfMemoryError: unable to create new native thread").Match(haystack);
        if (nativeThread.Success)
        {
            findings.Add(new LogAnalysisFinding
            {
                Category = "memory.nativeThread",
                Severity = LogAnalysisSeverity.Critical,
                Title = "无法创建新的原生线程（OutOfMemoryError: unable to create new native thread）",
                Evidence = [TrimLine(nativeThread.Value)],
                Decisions =
                [
                    "系统线程或内存资源不足：把 -Xmx 调小一些，给 JVM 栈和系统留出空间。",
                    "检查是否有自定义 -XX:ThreadStackSize 或并行度过高的参数。",
                ],
            });
        }

        if (Rx("OutOfMemoryError: Metaspace").IsMatch(haystack))
        {
            findings.Add(new LogAnalysisFinding
            {
                Category = "memory.metaspace",
                Severity = LogAnalysisSeverity.Critical,
                Title = "Metaspace 耗尽：加载的类总量超出元空间上限",
                Evidence = CollectEvidenceLines(haystack, "Metaspace"),
                Decisions =
                [
                    "在 JVM 参数里加 -XX:MaxMetaspaceSize=1024M（大整合包可到 1536M）。",
                    "或减少模组数量。",
                ],
            });
        }

        if (Rx("StackOverflowError").IsMatch(haystack))
        {
            findings.Add(new LogAnalysisFinding
            {
                Category = "memory.stack",
                Severity = LogAnalysisSeverity.Critical,
                Title = "栈溢出（StackOverflowError）：几乎总是某个模组 / 数据包递归调用自己",
                Evidence = CollectEvidenceLines(haystack, "StackOverflowError"),
                Decisions =
                [
                    "看下方堆栈证据里第一个非 net.minecraft 开头的包名，那就是递归发生的位置，更新或移除对应模组。",
                    "数据包 / 合成表循环也会触发：回想崩溃前刚装了什么。",
                    "临时加 -Xss4M 只能缓解，不建议长期使用。",
                ],
            });
        }

        if (Rx("(?i)insufficient memory for the Java Runtime|Native memory allocation \\(mmap\\) failed").IsMatch(haystack))
        {
            findings.Add(new LogAnalysisFinding
            {
                Category = "memory.system",
                Severity = LogAnalysisSeverity.Critical,
                Title = "系统物理内存不足（JVM 无法向操作系统申请内存）",
                Evidence = CollectEvidenceLines(haystack, "insufficient memory", "Native memory allocation"),
                Decisions =
                [
                    $"把 -Xmx 调小（当前 {currentMemory}），为系统和其他进程留出内存。",
                    "关闭浏览器等其他占内存程序；Windows 可适当调大虚拟内存（页面文件）。",
                ],
            });
        }
    }

    // ------------------------------------------------------------------
    // 显卡 / 渲染
    // ------------------------------------------------------------------

    private static void AnalyzeGraphics(string haystack, List<LogAnalysisFinding> findings)
    {
        if (Rx("Pixel format not accelerated").IsMatch(haystack))
        {
            findings.Add(new LogAnalysisFinding
            {
                Category = "graphics.driver.opengl",
                Severity = LogAnalysisSeverity.Critical,
                Title = "显卡驱动不支持硬件 OpenGL（Pixel format not accelerated）",
                Evidence = CollectEvidenceLines(haystack, "Pixel format not accelerated"),
                Decisions =
                [
                    "去显卡官网更新驱动（NVIDIA / AMD / Intel 官网，不要用系统自带的通用驱动）。",
                    "确认使用 64 位 Java：32 位 Java 在 64 位系统上常触发此错误。",
                    "远程桌面会话里没有 GPU 加速，请在本机桌面会话中运行游戏。",
                    "Intel GMA 等老核显本身不支持所需的 OpenGL 版本，只能换显卡或换轻量渲染方案。",
                ],
            });
        }

        Match glfw = Rx(@"GLFW error (?<code>\d+): (?<message>[^\r\n]+)").Match(haystack);
        if (glfw.Success)
        {
            string code = glfw.Groups["code"].Value;
            string message = glfw.Groups["message"].Value;
            string title = code is "65543" or "65542"
                ? $"显卡驱动不支持所需 OpenGL（GLFW error {code}）"
                : $"窗口 / OpenGL 上下文创建失败（GLFW error {code}）";
            findings.Add(new LogAnalysisFinding
            {
                Category = "graphics.glfw",
                Severity = LogAnalysisSeverity.Critical,
                Title = title,
                Evidence = [TrimLine(glfw.Value)],
                Decisions =
                [
                    "更新显卡驱动后重试。",
                    "多个显卡的机器：在显卡控制面板里把 javaw.exe 指定为高性能独显。",
                    "显示驱动异常时重启电脑也能恢复 OpenGL 上下文。",
                ],
            });
        }

        Match glError = Rx(@"OpenGL Error: (?<value>\d+)|GL_INVALID_(?<name>\w+)").Match(haystack);
        if (glError.Success && Rx("GL_INVALID|OpenGL Error").IsMatch(haystack))
        {
            findings.Add(new LogAnalysisFinding
            {
                Category = "graphics.opengl",
                Severity = LogAnalysisSeverity.Warning,
                Title = "OpenGL 报错（" + TrimLine(glError.Value) + "）：驱动或模组渲染路径问题",
                Evidence = CollectEvidenceLines(haystack, "GL_INVALID", "OpenGL Error"),
                Decisions =
                [
                    "更新显卡驱动；若只在高强度场景出现，多为某个渲染类模组 / 光影触发。",
                    "关掉光影与高分辨率资源包再试，可以定位是不是渲染负载问题。",
                ],
            });
        }

        if (Rx("(?i)DXGI_ERROR_DEVICE_REMOVED|device instance has been suspended|display driver (?:stopped responding|has stopped)|nvlddmkm|atikmdag").IsMatch(haystack))
        {
            findings.Add(new LogAnalysisFinding
            {
                Category = "graphics.tdr",
                Severity = LogAnalysisSeverity.Critical,
                Title = "显卡驱动崩溃并重置（设备被移除 / 驱动停止响应）",
                Evidence = CollectEvidenceLines(haystack, "DXGI_ERROR", "stopped responding", "nvlddmkm", "atikmdag"),
                Decisions =
                [
                    "更新 / 干净重装显卡驱动（用 DDU 卸载后安装官网版本）。",
                    "降低游戏画质与光影负载；检查显卡温度与供电，取消超频。",
                    "若只在这一台机器复现，倾向硬件或驱动问题而非模组。",
                ],
            });
        }

        Match atlas = Rx(@"Unable to fit: (?<value>\w+)[^\r\n]*").Match(haystack);
        if (atlas.Success)
        {
            findings.Add(new LogAnalysisFinding
            {
                Category = "graphics.atlas",
                Severity = LogAnalysisSeverity.Critical,
                Title = $"材质图集无法装下贴图（Unable to fit: {atlas.Groups["value"].Value}）：资源包分辨率过高或内存不足",
                Evidence = [TrimLine(atlas.Value)],
                Decisions =
                [
                    "换用更低分辨率的资源包（高分辨率包建议 512M 以上空闲堆且数量不要叠加）。",
                    "同时调大实例内存。",
                ],
            });
        }

        if (Rx("(?i)Couldn't initialize GlProgram|Failed to compile (?:gl)?shader|shader(?:s)? failed to (?:compile|load)").IsMatch(haystack))
        {
            findings.Add(new LogAnalysisFinding
            {
                Category = "graphics.shader",
                Severity = LogAnalysisSeverity.Critical,
                Title = "光影 / 着色器编译失败（Iris / OptiFine 常见）",
                Evidence = CollectEvidenceLines(haystack, "GlProgram", "shader", "Shader"),
                Decisions =
                [
                    "在光影设置里切换到其他光影包，或更新当前光影到适配版本的发行版。",
                    "更新显卡驱动；旧核显可能不支持新光影所需的特性。",
                    "Iris / OptiFine 与部分优化模组不兼容：与 Sodium 等同时安装时确认版本匹配。",
                ],
            });
        }
    }

    // ------------------------------------------------------------------
    // JVM 原生崩溃（hs_err_pid*.log）
    // ------------------------------------------------------------------

    private static void AnalyzeHsErr(Evidence evidence, List<LogAnalysisFinding> findings)
    {
        if (evidence.HsErrContent.Length == 0)
        {
            return;
        }

        string frame = evidence.HsErrProblematicFrame;
        string moduleDetail = frame.Length > 0 ? $"（问题帧：{frame}）" : string.Empty;
        string driver = DescribeNativeModule(frame);
        List<string> decisions =
        [
            "JVM 原生崩溃不生成 crash-reports：完整原因在游戏根目录的 hs_err_pid*.log 里，已解析关键帧。",
            "更新显卡驱动并校验游戏文件完整性（LWJGL natives 损坏也会这样崩）。",
        ];
        if (driver.Length > 0)
        {
            decisions.Insert(0, "问题帧指向" + driver + "：优先更新对应驱动。");
        }

        findings.Add(new LogAnalysisFinding
        {
            Category = "jvm.native",
            Severity = LogAnalysisSeverity.Critical,
            Title = $"JVM 原生崩溃：{FallbackText(evidence.HsErrSignal)}{moduleDetail}",
            Evidence = [.. evidence.HsErrContent
                .Split('\n')
                .Select(TrimLine)
                .Where(line => line.StartsWith('#'))
                .Take(3)],
            Decisions = decisions,
        });
    }

    /// <summary>从 Problematic frame 的 DLL 名推断出问题的组件。</summary>
    private static string DescribeNativeModule(string frame)
    {
        string module = (frame ?? string.Empty).ToLowerInvariant();
        if (module.Length == 0)
        {
            return string.Empty;
        }

        if (module.StartsWith("ati") || module.StartsWith("amd") || module.Contains("amdogl"))
        {
            return "AMD 显卡驱动";
        }

        if (module.StartsWith("nv") || module.Contains("nvcuda") || module.Contains("nvwgf"))
        {
            return "NVIDIA 显卡驱动";
        }

        if (module.StartsWith("ig") || module.Contains("intel"))
        {
            return "Intel 核显驱动";
        }

        if (module.Contains("lwjgl") || module.Contains("opengl") || module.Contains("gl") ||
            module.Contains("dxgi") || module.Contains("d3d"))
        {
            return "图形库（LWJGL / OpenGL / D3D）";
        }

        if (module.Contains("jvm") || module.Contains("java"))
        {
            return "JVM 本体（考虑更换 Java 发行版 / 版本）";
        }

        return string.Empty;
    }

    // ------------------------------------------------------------------
    // 模组加载器：缺前置 / 版本冲突 / 重复（核心）
    // ------------------------------------------------------------------

    private static void AnalyzeLoaderErrors(
        string haystack, List<ModDependencyIssue> modIssues, List<LogAnalysisFinding> findings)
    {
        bool fabricContext = Rx("ModResolutionException|fabricmc\\.loader").IsMatch(haystack);
        bool forgeContext = Rx(
                "MissingModsException|Missing or unsupported mandatory dependencies|fml\\.common|neoforged|Duplicate Mods? (?:Found|Set)|ModLoadingException|Incompatible mods?")
            .IsMatch(haystack);

        // ---- Fabric：「- modid ver requires X of mod dep, which is missing / loaded at Y」
        if (fabricContext)
        {
            foreach (Match match in Rx(
                         @"(?m)^[\s>\[\]-]*(?<src>[\w$. -]+?)\s+(?:[\w.+-]+\s+)?requires\s+(?<req>[^,\r\n]+?)\s+of\s+(?:mod|package)\s+(?<dep>[\w$.-]+?)\s*,\s*(?:which is missing|which is loaded at version (?<actual>[\w.+-]+)|but only (?<actual2>[\w.+-]+) is \w+)")
                         .Matches(haystack))
            {
                modIssues.Add(new ModDependencyIssue
                {
                    SourceMod = match.Groups["src"].Value.Trim(),
                    Dependency = match.Groups["dep"].Value,
                    RequiredVersion = match.Groups["req"].Value.Trim(),
                    ActualVersion = FirstNonEmpty(match.Groups["actual"].Value, match.Groups["actual2"].Value),
                    Kind = match.Groups["actual"].Success || match.Groups["actual2"].Success
                        ? ModIssueKind.VersionMismatch
                        : ModIssueKind.MissingDependency,
                    EvidenceLine = TrimLine(match.Value),
                    Loader = "fabric",
                });
            }

            foreach (Match match in Rx(
                         @"(?m)^[\s>\[\]-]*(?<src>[\w$. -]+?)\s+(?:[\w.+-]+\s+)?requires\s+minecraft\s+(?<req>[=<>^~\[\]][\w.[\]-]+|[=<>]?[\w.]+)\s+but\s+(?<actual>[\w.+-]+)\s+is\s+(?:loaded|present)")
                         .Matches(haystack))
            {
                modIssues.Add(new ModDependencyIssue
                {
                    SourceMod = match.Groups["src"].Value.Trim(),
                    Dependency = "minecraft",
                    RequiredVersion = match.Groups["req"].Value,
                    ActualVersion = match.Groups["actual"].Value,
                    Kind = ModIssueKind.GameIncompatible,
                    EvidenceLine = TrimLine(match.Value),
                    Loader = "fabric",
                });
            }

            foreach (Match match in Rx(
                         @"(?i)duplicate mod ids?[: ]+'?(?<id>[\w-]+)|Found (?<count>\d+) mods with duplicate id:? '?(?<id2>[\w-]+)")
                         .Matches(haystack))
            {
                modIssues.Add(new ModDependencyIssue
                {
                    Dependency = FirstNonEmpty(match.Groups["id"].Value, match.Groups["id2"].Value),
                    ActualVersion = "存在多份同 id 的 jar",
                    Kind = ModIssueKind.Duplicate,
                    EvidenceLine = TrimLine(match.Value),
                    Loader = "fabric",
                });
            }
        }

        // ---- Forge / NeoForge：「dep [range] required by: src」依赖块
        if (forgeContext)
        {
            // Maven 版本区间右界可能是 ) （半开区间 [1.20.1,) ）也可能是 ]
            foreach (Match match in Rx(
                         @"(?m)^\s*[-*>]*(?<dep>[\w-]+)\s+\[(?<range>[^\]\s]*)[^\r\n]*required by:?\s*(?<src>[\w-]+)")
                         .Matches(haystack))
            {
                string range = match.Groups["range"].Value;
                if (!range.EndsWith(')') && !range.EndsWith(']'))
                {
                    range += "]";
                }

                modIssues.Add(new ModDependencyIssue
                {
                    SourceMod = match.Groups["src"].Value,
                    Dependency = match.Groups["dep"].Value,
                    RequiredVersion = "[" + range,
                    Kind = ModIssueKind.MissingDependency,
                    EvidenceLine = TrimLine(match.Value),
                    Loader = "forge",
                });
            }

            foreach (Match match in Rx(
                         @"(?m)^[\s>\[\]-]*(?<src>[\w$. -]+?)\s+(?:[\w.+-]+\s+)?requires\s+(?<dep>[\w-]+)\s+(?<req>[=<>^~\[\]][\w.[\]-]+)\s+but\s+(?<actual>[\w.+-]+)\s+is\s+(?:loaded|present)")
                         .Matches(haystack))
            {
                modIssues.Add(new ModDependencyIssue
                {
                    SourceMod = match.Groups["src"].Value.Trim(),
                    Dependency = match.Groups["dep"].Value,
                    RequiredVersion = match.Groups["req"].Value,
                    ActualVersion = match.Groups["actual"].Value,
                    Kind = match.Groups["dep"].Value is "minecraft"
                        ? ModIssueKind.GameIncompatible
                        : ModIssueKind.VersionMismatch,
                    EvidenceLine = TrimLine(match.Value),
                    Loader = "forge",
                });
            }

            // Duplicate Mods Found:  Mod ID: 'x' from mod files: a.jar, b.jar
            foreach (Match match in Rx(
                         @"Mod ID:\s*'(?<id>[\w-]+)'[^\r\n]*from mod files:\s*(?<files>[^\r\n]+)")
                         .Matches(haystack))
            {
                modIssues.Add(new ModDependencyIssue
                {
                    Dependency = match.Groups["id"].Value,
                    ActualVersion = match.Groups["files"].Value.Trim(),
                    Kind = ModIssueKind.Duplicate,
                    EvidenceLine = TrimLine(match.Value),
                    Loader = "forge",
                });
            }
        }

        if (modIssues.Count > 0)
        {
            // 同一条依赖问题可能同时命中 Fabric / Forge 两种报错格式，先去重再计数展示
            List<ModDependencyIssue> distinctIssues = modIssues
                .DistinctBy(issue => issue.ToString(), StringComparer.Ordinal)
                .ToList();
            modIssues.Clear();
            modIssues.AddRange(distinctIssues);

            List<string> decisions =
            [
                "按下面的明细逐条处理：缺什么装什么，版本不匹配的更新到要求区间内。",
                "改完模组后重启实例；如仍崩溃，用二分法（一次禁用一半模组）定位冲突项。",
            ];
            ModDependencyIssue[] duplicates = [.. modIssues.Where(issue => issue.Kind == ModIssueKind.Duplicate)];
            if (duplicates.Length > 0)
            {
                decisions.Insert(1,
                    "重复模组：删除 mods 目录里多余的 jar（明细里列出了冲突文件名），每种只保留一份。");
            }

            findings.Add(new LogAnalysisFinding
            {
                Category = "mod.dependencies",
                Severity = LogAnalysisSeverity.Critical,
                Title = $"模组依赖 / 冲突问题，共 {modIssues.Count} 条（加载器：{modIssues[0].Loader}）",
                Evidence = [.. modIssues.Select(issue => issue.EvidenceLine).Distinct().Take(3)],
                Decisions = decisions,
            });
        }

        // ---- Mixin 注入失败：能直接点名模组
        Match mixinApply = Rx(
                @"Mixin apply failed: (?<cfg>\S+) from mod (?<mod>\S+)")
            .Match(haystack);
        Match criticalInjection = Rx(
                @"Critical injection failure: (?<method>\S+) in (?<cfg>\S+)").Match(haystack);
        Match invalidMixin = Rx(
                @"InvalidMixinException(?:\([^)]*\))?:\s*(?<msg>[^\r\n]+)").Match(haystack);
        Match mixinConfig = Rx(
                @"[Tt]he mixin config (?<cfg>\S+?\.mixins\.json)[^\r\n]*").Match(haystack);
        Match mixinMatch = mixinApply.Success ? mixinApply
            : criticalInjection.Success ? criticalInjection
            : invalidMixin.Success ? invalidMixin
            : mixinConfig;
        if (mixinMatch.Success || Rx("MixinTransformerError|mixin transformation").IsMatch(haystack))
        {
            string modName = mixinApply.Success ? mixinApply.Groups["mod"].Value : string.Empty;
            string configName = FirstNonEmpty(
                mixinApply.Groups["cfg"].Value,
                criticalInjection.Groups["cfg"].Value,
                mixinConfig.Groups["cfg"].Value);
            string target = modName.Length > 0 ? $"（来自模组 {modName}，配置 {configName}）" :
                configName.Length > 0 ? $"（配置 {configName}）" : string.Empty;
            findings.Add(new LogAnalysisFinding
            {
                Category = "mod.mixin",
                Severity = LogAnalysisSeverity.Critical,
                Title = "Mixin 注入失败" + target + "：模组与当前加载器 / MC 版本不匹配",
                Evidence = CollectEvidenceLines(haystack, "Mixin", "mixin"),
                Decisions =
                [
                    modName.Length > 0
                        ? $"把 {modName} 更新到与当前 MC / 加载器版本匹配的发行版，或暂时从 mods 目录移除。"
                        : "根据证据行里的 mixins.json 名称定位模组，更新或移除后重试。",
                    "同一模组不同加载器版本（Forge / Fabric / NeoForge）的 jar 不能混装。",
                ],
            });
        }

        // ---- 加载器通用：模组文件缺失 / 加载失败
        Match modFile = Rx(@"Mod file (?<value>\S+) (?:is missing|lacks|could not be)").Match(haystack);
        if (modFile.Success)
        {
            findings.Add(new LogAnalysisFinding
            {
                Category = "mod.file",
                Severity = LogAnalysisSeverity.Critical,
                Title = $"模组文件无法加载：{modFile.Groups["value"].Value}",
                Evidence = [TrimLine(modFile.Value)],
                Decisions =
                [
                    "该 jar 可能下载不完整或与本加载器不匹配：重新下载对应加载器版本的模组。",
                    "确认 jar 没有被杀毒软件隔离或破坏。",
                ],
            });
        }
    }

    // ------------------------------------------------------------------
    // 类缺失 → 反推所属模组 / 前置
    // ------------------------------------------------------------------

    private static readonly (string Prefix, string Owner)[] KnownPackageOwners =
    [
        ("net.minecraft.", "Minecraft 原版"),
        ("com.mojang.", "Minecraft 原版"),
        ("net.fabricmc.", "Fabric Loader / Fabric API"),
        ("net.minecraftforge.", "Forge"),
        ("net.neoforged.", "NeoForge"),
        ("org.quiltmc.", "Quilt"),
        ("org.spongepowered.asm.", "Mixin 库（随加载器分发）"),
        ("io.netty.", "Netty 网络库（随游戏分发）"),
        ("com.google.common.", "Guava（随游戏分发）"),
    ];

    private static void AnalyzeClassNotFound(string haystack, List<LogAnalysisFinding> findings)
    {
        // NoClassDefFoundError 打印内部名（斜杠分隔），ClassNotFoundException 打印全限定名（点分隔），统一成点分隔
        Match match = Rx(
                @"(?:NoClassDefFoundError|ClassNotFoundException):\s*(?<cls>[\w.$/]+)")
            .Match(haystack);
        if (!match.Success)
        {
            return;
        }

        string className = match.Groups["cls"].Value.Replace('/', '.');
        string? knownOwner = null;
        foreach ((string prefix, string ownerName) in KnownPackageOwners)
        {
            if (className.StartsWith(prefix, StringComparison.Ordinal))
            {
                knownOwner = ownerName;
                break;
            }
        }

        bool isVanillaOrLibrary = knownOwner is not null;
        string owner = knownOwner ?? GuessModFromClassName(className);
        findings.Add(new LogAnalysisFinding
        {
            Category = "mod.classnotfound",
            Severity = LogAnalysisSeverity.Critical,
            Title = $"运行时找不到类 {className}（属于：{owner}）",
            Evidence = CollectEvidenceLines(haystack, "NoClassDefFoundError", "ClassNotFoundException"),
            Decisions = isVanillaOrLibrary
                ?
                [
                    "核心类缺失说明版本文件不完整：在实例详情执行文件校验，或删除该版本目录重新下载。",
                    "加载器类缺失也可能是加载器安装损坏：重装 Fabric / Forge / NeoForge。",
                ]
                :
                [
                    $"缺失的类属于第三方模组（{owner}）：它的前置没装、或文件只下载了一半。",
                    $"重新下载 {owner} 及其声明的前置模组，确认与当前 MC / 加载器版本匹配。",
                ],
        });
    }

    /// <summary>com.simibubi.create.Create → create；取包名末段或驼峰首段作模组 id 线索。</summary>
    private static string GuessModFromClassName(string className)
    {
        string[] segments = className.Split('.');
        if (segments.Length >= 2)
        {
            return segments[0] + "." + segments[1] + "（疑似模组 " + segments[1] + "）";
        }

        return segments[0];
    }

    // ------------------------------------------------------------------
    // 堆栈里出现的第三方包 → 可疑模组清单
    // ------------------------------------------------------------------

    private static readonly HashSet<string> VanillaPackages = new(StringComparer.Ordinal)
    {
        "net.minecraft", "com.mojang", "java", "javax", "jdk", "sun", "io.netty",
        "com.google", "org.apache", "org.slf4j", "it.unimi", "net.fabricmc",
        "org.quiltmc", "net.minecraftforge", "net.neoforged", "org.spongepowered",
        "org.lwjgl", "com.ibm", "jdk.nashorn", "javassist", "org.objectweb",
    };

    /// <summary>
    /// 从崩溃堆栈提取非原厂包名，得到「崩溃发生在哪些模组的代码路径里」。
    /// 这是嫌疑清单而非定罪：Mixin/反射调用会让原版堆栈夹着模组帧。
    /// </summary>
    private static void InferSuspectMods(Evidence evidence, List<LogAnalysisFinding> findings)
    {
        string stackSource = evidence.ReportContent + "\n" + evidence.LogTailText;
        if (stackSource.Length == 0)
        {
            return;
        }

        Dictionary<string, int> counts = [];
        foreach (Match frame in Rx(@"(?m)^\s*at (?<pkg>(?:\w[\w$]*\.){1,7}\w[\w$]*)\.[\w$<>]+\(").Matches(stackSource))
        {
            string[] parts = frame.Groups["pkg"].Value.Split('.');
            if (parts.Length < 2)
            {
                continue;
            }

            // 原版 / 随游戏分发的库不进嫌疑清单；命中 2 段前缀（net.minecraft）或首段（java）
            bool vanilla = VanillaPackages.Contains(parts[0]) ||
                           VanillaPackages.Contains(parts[0] + "." + parts[1]);
            if (vanilla)
            {
                continue;
            }

            // 完整前缀（最多三段）作为嫌疑标识，出现越频繁越可疑
            string suspect = string.Join('.', parts.Length >= 3 ? parts[..3] : parts);
            counts[suspect] = counts.TryGetValue(suspect, out int value) ? value + 1 : 1;
        }

        string[] top = [.. counts.OrderByDescending(pair => pair.Value).Take(3).Select(pair => pair.Key)];
        if (top.Length == 0)
        {
            return;
        }

        findings.Add(new LogAnalysisFinding
        {
            Category = "mod.suspects",
            Severity = LogAnalysisSeverity.Warning,
            Title = "崩溃堆栈经过以下第三方代码（嫌疑模组，非定罪）：" + string.Join("、", top),
            Evidence = [.. top.Select(name => $"at {name}.*（出现 {counts[name]} 帧）")],
            Decisions =
            [
                "优先更新 / 移除清单里的第一个模组后再试。",
                "多个嫌疑时用二分法：一次禁用一半模组，快速缩小到冲突项。",
            ],
        });
    }

    // ------------------------------------------------------------------
    // 登录会话
    // ------------------------------------------------------------------

    private static void AnalyzeAuth(string haystack, List<LogAnalysisFinding> findings)
    {
        if (Rx("Failed to verify username|not authenticated with Minecraft").IsMatch(haystack))
        {
            findings.Add(new LogAnalysisFinding
            {
                Category = "auth.verify",
                Severity = LogAnalysisSeverity.Critical,
                Title = "服务器无法验证用户名 / 会话（Failed to verify username）",
                Evidence = CollectEvidenceLines(haystack, "verify username", "authenticated"),
                Decisions =
                [
                    "离线 / 皮肤站账号无法通过正版验证服务器：进入正版服需要正版账号。",
                    "皮肤站账号：确认服务器与皮肤站是同一家，且已重新登录刷新令牌。",
                ],
            });
        }

        if (Rx("Invalid session|Session ID is invalid").IsMatch(haystack))
        {
            findings.Add(new LogAnalysisFinding
            {
                Category = "auth.session",
                Severity = LogAnalysisSeverity.Critical,
                Title = "登录会话已失效（Invalid session）",
                Evidence = CollectEvidenceLines(haystack, "Invalid session", "Session ID"),
                Decisions =
                [
                    "在账号页退出并重新登录该账号，再重新启动游戏（官方建议就是重启游戏刷新会话）。",
                    "同一账号在别处重复登录也会使旧会话失效。",
                ],
            });
        }

        if (Rx("Authentication servers are down for maintenance").IsMatch(haystack))
        {
            findings.Add(new LogAnalysisFinding
            {
                Category = "auth.maintenance",
                Severity = LogAnalysisSeverity.Warning,
                Title = "Mojang 认证服务器维护中（Authentication servers are down for maintenance）",
                Evidence = CollectEvidenceLines(haystack, "maintenance"),
                Decisions = ["稍等片刻再试，这是官方服务端临时状态，与本机无关。"],
            });
        }
    }

    // ------------------------------------------------------------------
    // 文件完整性 / 系统环境
    // ------------------------------------------------------------------

    private static void AnalyzeFiles(string haystack, List<LogAnalysisFinding> findings)
    {
        if (Rx("zip END header not found|error in opening zip file|[Ii]nvalid or corrupt jarfile|is not a zip file").IsMatch(haystack))
        {
            Match nearby = Rx(
                    @"(?:zip END header not found|error in opening zip file|[Ii]nvalid or corrupt jarfile)\S*[:\s]*(?<file>[\w:.\\/ -]+\.jar)?")
                .Match(haystack);
            string file = nearby.Groups["file"].Success ? "：" + nearby.Groups["file"].Value.Trim() : string.Empty;
            findings.Add(new LogAnalysisFinding
            {
                Category = "file.corrupt",
                Severity = LogAnalysisSeverity.Critical,
                Title = "jar / zip 文件损坏（无法解析压缩包）" + file,
                Evidence = CollectEvidenceLines(haystack, "zip END header", "opening zip file", "corrupt jarfile"),
                Decisions =
                [
                    file.Length > 0
                        ? $"删除损坏的文件 {file.TrimStart('：')} 后重新下载对应内容。"
                        : "对实例执行文件校验或重装版本 / 模组，替换损坏文件。",
                    "多半是下载中断或杀毒软件改写了文件：把游戏目录加入杀毒白名单。",
                ],
            });
        }

        // Java 的 FileNotFoundException 把路径放在 "(Access is denied)" 前面，路径与提示在同一条消息里
        Match denied = Rx(@"(?m)^(?<line>[^\r\n]*(?:Access is denied|Permission denied)[^\r\n]*)$").Match(haystack);
        if (denied.Success)
        {
            Match pathMatch = Rx(@"(?<path>(?:[A-Za-z]:\\|/)[^\s:]+)").Match(denied.Groups["line"].Value);
            string path = pathMatch.Success ? "：" + pathMatch.Groups["path"].Value : string.Empty;
            findings.Add(new LogAnalysisFinding
            {
                Category = "file.permission",
                Severity = LogAnalysisSeverity.Critical,
                Title = "没有文件访问权限（Access is denied）" + path,
                Evidence = CollectEvidenceLines(haystack, "Access is denied", "Permission denied"),
                Decisions =
                [
                    "把游戏目录移出 OneDrive / 网盘同步目录，这些软件会锁文件。",
                    "在杀毒软件里把游戏目录加白（实时扫描拦截是 Minecraft Wiki 明确列出的崩溃来源）。",
                    "必要时以管理员身份运行启动器；Windows 用户账户控制（UAC）拦截时也用这招。",
                ],
            });
        }

        if (Rx("because it is being used by another process").IsMatch(haystack))
        {
            findings.Add(new LogAnalysisFinding
            {
                Category = "file.locked",
                Severity = LogAnalysisSeverity.Critical,
                Title = "文件被其他进程占用（常见于两个启动器 / 两个实例共用一个游戏目录）",
                Evidence = CollectEvidenceLines(haystack, "being used by another process"),
                Decisions =
                [
                    "关闭其他正在运行的游戏实例或其他启动器后重试。",
                    "游戏目录尽量每个实例独占，不要多个实例指向同一个 GameDirectory。",
                ],
            });
        }

        if (Rx("No space left on device|磁盘空间不足").IsMatch(haystack))
        {
            findings.Add(new LogAnalysisFinding
            {
                Category = "file.disk",
                Severity = LogAnalysisSeverity.Critical,
                Title = "磁盘空间不足（No space left on device）",
                Evidence = CollectEvidenceLines(haystack, "No space left", "磁盘空间不足"),
                Decisions =
                [
                    "清理游戏目录所在磁盘（世界存档、日志、旧版本与崩溃报告通常占不少空间）。",
                    "必要时把 .minecraft 迁移到剩余空间更大的磁盘。",
                ],
            });
        }
    }

    // ------------------------------------------------------------------
    // 世界 / 存档
    // ------------------------------------------------------------------

    private static void AnalyzeWorld(Evidence evidence, List<LogAnalysisFinding> findings)
    {
        string haystack = evidence.CombinedText;

        if (evidence.ReportDescription.Contains("Ticking entity", StringComparison.OrdinalIgnoreCase) ||
            Rx("Ticking entity").IsMatch(evidence.LogTailText))
        {
            string entity = FirstMatch(evidence.ReportContent + "\n" + evidence.LogTailText,
                Rx(@"(?m)^Entity:\s*(?<value>\w+)"), "value");
            string detail = entity.Length > 0 ? $"，出问题的实体是 {entity}" : "（报告未给出实体名）";
            List<string> entityEvidence = [];
            if (entity.Length > 0)
            {
                entityEvidence.Add("Entity: " + entity);
            }

            entityEvidence.AddRange(CollectEvidenceLines(evidence.LogTailText, "Ticking entity"));
            findings.Add(new LogAnalysisFinding
            {
                Category = "world.tickingEntity",
                Severity = LogAnalysisSeverity.Critical,
                Title = "实体 tick 时崩溃（Ticking entity" + detail + "）",
                Evidence = entityEvidence,
                Decisions =
                [
                    entity.Length > 0
                        ? $"世界能打开时用 /kill @e[type={entity}] 清除该实体；打不开就用 NBT 编辑器（如 NBTExplorer/Amulet）删除对应实体。"
                        : "用 NBT 编辑器检查存档里异常实体并删除。",
                    "模组实体高频崩溃时，更新或移除提供该实体的模组。",
                ],
            });
        }

        if (evidence.ReportDescription.Contains("Ticking block entity", StringComparison.OrdinalIgnoreCase) ||
            Rx("Ticking block entity").IsMatch(evidence.LogTailText))
        {
            string block = FirstMatch(evidence.ReportContent + "\n" + evidence.LogTailText,
                Rx(@"(?m)^Block:\s*(?<value>\S+)"), "value");
            string detail = block.Length > 0 ? $"，出问题的方块是 {block}" : "（报告未给出方块名）";
            List<string> blockEvidence = [];
            if (block.Length > 0)
            {
                blockEvidence.Add("Block: " + block);
            }

            findings.Add(new LogAnalysisFinding
            {
                Category = "world.tickingBlock",
                Severity = LogAnalysisSeverity.Critical,
                Title = "方块实体 tick 时崩溃（Ticking block entity" + detail + "）",
                Evidence = blockEvidence,
                Decisions =
                [
                    block.Length > 0
                        ? $"进入世界后用 /setblock <坐标> air 清除该方块（坐标见崩溃报告 Affected block 段）。"
                        : "用地图编辑器删除对应方块实体。",
                    "模组方块高频崩溃时，更新或移除提供该方块的模组。",
                ],
            });
        }

        if (Rx("Failed to check session lock|session\\.lock").IsMatch(haystack))
        {
            findings.Add(new LogAnalysisFinding
            {
                Category = "world.sessionLock",
                Severity = LogAnalysisSeverity.Critical,
                Title = "世界被另一个进程占用（session.lock 校验失败）",
                Evidence = CollectEvidenceLines(haystack, "session lock", "session.lock"),
                Decisions =
                [
                    "同一个世界不能被两个实例同时打开：关闭另一个实例 / 服务器后重试。",
                    "确认崩溃后没有残留的 javaw.exe 进程（任务管理器结束它）。",
                ],
            });
        }

        if (Rx("Failed to load level|Exception loading level|We were unable to load this world|Description: (?:Reading|Loading) NBT data|corrupt(?:ed)? chunk|Chunk IO").IsMatch(haystack))
        {
            findings.Add(new LogAnalysisFinding
            {
                Category = "world.corrupt",
                Severity = LogAnalysisSeverity.Critical,
                Title = "世界存档 / 区块数据损坏（NBT 或区块文件无法解析）",
                Evidence = CollectEvidenceLines(haystack, "load level", "NBT", "chunk", "Chunk"),
                Decisions =
                [
                    "有备份就用备份恢复（定期备份是官方对区块损坏的标准建议）。",
                    "没有备份：用 NBT 编辑器修复 level.dat，或用 Amulet/MCASelector 删除损坏区块。",
                    "服务器侧损坏时，从世界备份中只恢复损坏的区域文件（.mca）。",
                ],
            });
        }
    }

    // ------------------------------------------------------------------
    // 其他游戏内崩溃场景
    // ------------------------------------------------------------------

    private static void AnalyzeGameSpecific(string haystack, List<LogAnalysisFinding> findings)
    {
        if (Rx("Watching server").IsMatch(haystack))
        {
            findings.Add(new LogAnalysisFinding
            {
                Category = "game.watchdog",
                Severity = LogAnalysisSeverity.Warning,
                Title = "服务器主线程卡死超过 60 秒（Watching server）：单 tick 计算量过大",
                Evidence = CollectEvidenceLines(haystack, "Watching server", "Can't keep up"),
                Decisions =
                [
                    "生成 / 红石 / 实体堆积都会拖死主线程：回想崩溃前在做什么。",
                    "联机的话适当调大内存并更新性能类模组（如 Lithium / Starlight 系）。",
                ],
            });
        }

        if (Rx("Ticking player|Ticking memory connection").IsMatch(haystack))
        {
            findings.Add(new LogAnalysisFinding
            {
                Category = "game.connection",
                Severity = LogAnalysisSeverity.Warning,
                Title = "处理网络包 / 玩家数据时崩溃：客户端与服务端模组不一致的典型症状",
                Evidence = CollectEvidenceLines(haystack, "Ticking player", "Ticking memory connection"),
                Decisions =
                [
                    "进服前保证客户端模组与服务端一致（版本也要一致）。",
                    "单人游戏出现时，检查最近装的网络同步类模组。",
                ],
            });
        }

        if (Rx("Description: Manually triggered debug crash|Manually triggered debug crash").IsMatch(haystack))
        {
            findings.Add(new LogAnalysisFinding
            {
                Category = "game.manual",
                Severity = LogAnalysisSeverity.Info,
                Title = "手动触发的调试崩溃（F3+C），游戏本身没有问题",
                Evidence = CollectEvidenceLines(haystack, "Manually triggered"),
                Decisions = ["忽略即可；按 F3+C 前记得先松开 Shift 的提示是有意为之。"],
            });
        }
    }

    // ------------------------------------------------------------------
    // 小工具
    // ------------------------------------------------------------------

    private static Regex Rx(string expression) =>
        new(expression, RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout);

    private static string TrimLine(string line) =>
        (line ?? string.Empty).Trim().TrimEnd('\r');

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string FallbackText(string value) =>
        string.IsNullOrWhiteSpace(value) ? "未知" : value;

    private static string FallbackMemory(int memoryMb) => memoryMb > 0 ? memoryMb + " MiB" : "未知";

    private static int TryParseInt(string value) =>
        int.TryParse(value, out int parsed) ? parsed : 0;

    private static string FirstException(string text)
    {
        Match match = Rx(
                @"(?m)^\s*(?:Caused by:\s*)?([\w.$]*(?:Exception|Error|Throwable)[^\r\n]*)$")
            .Match(text);
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    private static string FirstMatch(string text, Regex pattern, string? groupName)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        Match match = pattern.Match(text);
        if (!match.Success)
        {
            return string.Empty;
        }

        return groupName is null ? match.Value.Trim() : match.Groups[groupName].Value.Trim();
    }

    /// <summary>收集包含任一关键词的原始行（去重，最多 3 条，单行截 240 字符）。</summary>
    private static IReadOnlyList<string> CollectEvidenceLines(string haystack, params string[] keywords)
    {
        List<string> lines = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string raw in haystack.Split('\n'))
        {
            if (lines.Count >= 3)
            {
                break;
            }

            string line = TrimLine(raw);
            if (line.Length == 0 || !seen.Add(line))
            {
                continue;
            }

            if (keywords.Any(keyword => line.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                lines.Add(line.Length > 240 ? line[..240] + "…" : line);
            }
        }

        return lines;
    }

    /// <summary>取文本末尾 maxLines 行；不足 minChars 时改为取末尾 minChars 字节并对齐行首。</summary>
    private static string TailText(string text, int maxLines, int minChars)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string[] lines = text.Split('\n');
        string tail = lines.Length > maxLines
            ? string.Join("\n", lines[^maxLines..])
            : string.Join("\n", lines);
        if (tail.Length >= minChars || text.Length <= minChars)
        {
            return tail;
        }

        string cut = text[^minChars..];
        int index = cut.IndexOf('\n');
        return index >= 0 ? cut[(index + 1)..] : cut;
    }

    /// <summary>日志里的崩溃报告路径是相对实例目录的，尝试解析成绝对路径；失败返回 null。</summary>
    private static string? ResolveWithinDirectory(string relativePath, string directory)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        try
        {
            string candidate = Path.IsPathRooted(relativePath)
                ? relativePath
                : Path.GetFullPath(Path.Combine(directory ?? string.Empty, relativePath));
            return File.Exists(candidate) ? candidate : null;
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? NewestMatchingFile(string directory, string pattern, DateTimeOffset? sinceUtc)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(directory, pattern)
                .Select(path => new FileInfo(path))
                .Where(file => sinceUtc is null || file.LastWriteTimeUtc >= sinceUtc.Value.UtcDateTime.AddSeconds(-5))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>只在目录根找（hs_err_pid*.log 写在游戏根目录，不递归）。</summary>
    private static string? NewestMatchingFileInRoot(string directory, string pattern, DateTimeOffset? sinceUtc)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        try
        {
            // EnumerateFiles 默认 TopDirectoryOnly，正好只看根目录
            return Directory.EnumerateFiles(directory, pattern)
                .Select(path => new FileInfo(path))
                .Where(file => sinceUtc is null || file.LastWriteTimeUtc >= sinceUtc.Value.UtcDateTime.AddSeconds(-5))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ReadBounded(string? path, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            using StreamReader reader = new(path, Encoding.UTF8);
            char[] buffer = new char[maxChars];
            int read = reader.Read(buffer, 0, buffer.Length);
            return new string(buffer, 0, read);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
