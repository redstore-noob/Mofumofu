using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MofuCore.Java;

/// <summary>
/// 本机 Java 运行时搜索：环境变量、注册表、常见安装目录与 Mojang runtime 目录，
/// 并解析 java 的主版本、供应商与架构。
/// </summary>
public class JavaPathSearch
{
    /// <summary>显式指定 java 可执行文件的环境变量（优先级高于自动探测）。</summary>
    public const string JavaPathVariable = "MOFUMOFU_JAVA";

    /// <summary>Minecraft runtime 根目录的环境变量（不传 runtimeDirectory 时使用）。</summary>
    public const string JavaRuntimeVariable = "MOFUMOFU_JAVA_RUNTIME";

    private const int MaximumScanDepth = 6;
    private static readonly TimeSpan DetectTimeout = TimeSpan.FromSeconds(3);
    private static readonly Regex VersionPattern = new(
        "version\\s+\"?(\\d+)(?:\\.(\\d+))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UnixVariablePattern = new(
        "\\$(?:\\{(?<braced>\\w+)\\}|(?<plain>\\w+))",
        RegexOptions.Compiled);

    /// <summary>当前平台的 java 可执行文件名。</summary>
    public static string JavaExecutableName => OperatingSystem.IsWindows() ? "java.exe" : "java";

    /// <summary>
    /// 探测指定 java 可执行文件的主版本、供应商与架构；无法执行或解析失败时返回 null。
    /// </summary>
    public static async Task<JavaRuntime?> DetectAsync(string javaExecutable, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(javaExecutable))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(ExpandPath(javaExecutable));
        }
        catch (Exception)
        {
            return null;
        }

        if (!File.Exists(fullPath))
        {
            return null;
        }

        string? output = await RunJavaVersionAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (output is null)
        {
            return null;
        }

        return BuildRuntime(fullPath, output);
    }

    /// <summary>
    /// 同步探测指定 java 可执行文件；失败返回 null。
    /// </summary>
    public static JavaRuntime? Detect(string javaExecutable)
    {
        return DetectAsync(javaExecutable).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 探测指定 java 可执行文件的主版本号；失败返回 null。
    /// </summary>
    public static async Task<int?> DetectMajorVersionAsync(string javaExecutable, CancellationToken cancellationToken = default)
    {
        JavaRuntime? runtime = await DetectAsync(javaExecutable, cancellationToken).ConfigureAwait(false);
        return runtime?.MajorVersion;
    }

    /// <summary>
    /// 查找 java 可执行文件。
    /// </summary>
    /// <param name="configuredPath">用户显式配置的 java 路径（最高优先）。</param>
    /// <param name="requiredMajorVersion">Minecraft 版本要求的最低 Java 主版本；为空时不校验版本。</param>
    /// <param name="runtimeDirectory">Minecraft runtime 根目录；为空时读取 MOFUMOFU_JAVA_RUNTIME。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<string?> FindJavaExecutableAsync(
        string? configuredPath = null,
        int? requiredMajorVersion = null,
        string? runtimeDirectory = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<JavaCandidate> candidates = BuildCandidates(configuredPath, runtimeDirectory);

        // 不要求特定版本时，直接返回第一个找到的 java
        if (requiredMajorVersion is null)
        {
            foreach (JavaCandidate candidate in candidates)
            {
                string? resolved = ResolveJavaFile(candidate.Path);
                if (resolved is not null)
                {
                    return resolved;
                }
            }

            return null;
        }

        List<ProbedCandidate> probed = [];
        HashSet<string> visitedPaths = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < candidates.Count; index++)
        {
            JavaCandidate candidate = candidates[index];
            string? resolved = ResolveJavaFile(candidate.Path);
            if (resolved is null || !visitedPaths.Add(resolved))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            JavaRuntime? runtime = await DetectAsync(resolved, cancellationToken).ConfigureAwait(false);
            if (runtime?.MajorVersion is int majorVersion)
            {
                probed.Add(new ProbedCandidate(resolved, majorVersion, index, candidate.IsPreferred));
            }
        }

        int requiredVersion = requiredMajorVersion.Value;

        // 主版本精确匹配优先：避免用 Java 25 启动要求 Java 17 的旧加载器
        List<ProbedCandidate> ordered = probed
            .OrderBy(candidate => candidate.MajorVersion)
            .ThenByDescending(candidate => candidate.IsPreferred)
            .ThenBy(candidate => candidate.Index)
            .ToList();
        foreach (ProbedCandidate candidate in ordered)
        {
            if (candidate.MajorVersion == requiredVersion)
            {
                return candidate.Path;
            }
        }

        // 次选：显式配置且不低于最低要求
        foreach (ProbedCandidate candidate in ordered)
        {
            if (candidate.IsPreferred && candidate.MajorVersion >= requiredVersion)
            {
                return candidate.Path;
            }
        }

        // 再次：自动探测中最低且不低于最低要求
        foreach (ProbedCandidate candidate in ordered)
        {
            if (candidate.MajorVersion >= requiredVersion)
            {
                return candidate.Path;
            }
        }

        return null;
    }

    /// <summary>
    /// 查找主版本精确匹配的 Java；找不到返回 null。
    /// </summary>
    public static async Task<string?> FindExactMatchJavaAsync(
        string? configuredPath,
        int requiredMajorVersion,
        string? runtimeDirectory = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<JavaCandidate> candidates = BuildCandidates(configuredPath, runtimeDirectory);
        HashSet<string> visitedPaths = new(StringComparer.OrdinalIgnoreCase);
        string? preferredExact = null;

        foreach (JavaCandidate candidate in candidates)
        {
            string? resolved = ResolveJavaFile(candidate.Path);
            if (resolved is null || !visitedPaths.Add(resolved))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            JavaRuntime? runtime = await DetectAsync(resolved, cancellationToken).ConfigureAwait(false);
            if (runtime?.MajorVersion == requiredMajorVersion)
            {
                if (candidate.IsPreferred)
                {
                    return resolved;
                }

                preferredExact ??= resolved;
            }
        }

        return preferredExact;
    }

    /// <summary>
    /// 列出所有可用的 Java 运行时（去重、存在性过滤、带版本与供应商），按主版本降序。
    /// </summary>
    public static async Task<IReadOnlyList<JavaRuntime>> FindAllJavaAsync(
        string? runtimeDirectory = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<JavaCandidate> candidates = BuildCandidates(null, runtimeDirectory);
        List<JavaRuntime> result = [];
        HashSet<string> visitedPaths = new(StringComparer.OrdinalIgnoreCase);
        using SemaphoreSlim gate = new(4, 4);

        List<Task<JavaRuntime?>> tasks = [];
        List<string> resolvedPaths = [];
        foreach (JavaCandidate candidate in candidates)
        {
            string? resolved = ResolveJavaFile(candidate.Path);
            if (resolved is null || !visitedPaths.Add(resolved))
            {
                continue;
            }

            resolvedPaths.Add(resolved);
            tasks.Add(DetectWithGateAsync(resolved, gate, cancellationToken));
        }

        JavaRuntime?[] detected = await Task.WhenAll(tasks).ConfigureAwait(false);
        for (int index = 0; index < detected.Length; index++)
        {
            // 探测失败也保留路径（设置页"自动检索"需要列出全部找到的 java）
            result.Add(detected[index] ?? new JavaRuntime { JavaPath = resolvedPaths[index] });
        }

        return result
            .OrderByDescending(runtime => runtime.MajorVersion ?? 0)
            .ThenBy(runtime => runtime.JavaPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 收集全部候选 java 路径（未做存在性过滤）。
    /// </summary>
    public static IReadOnlyList<string> CollectCandidates(string? configuredPath = null, string? runtimeDirectory = null)
    {
        return BuildCandidates(configuredPath, runtimeDirectory)
            .Select(candidate => candidate.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();
    }

    /// <summary>
    /// 在目录树中查找 java 可执行文件（要求父目录为 bin）；找不到返回 null。
    /// </summary>
    public static string? FindJavaExecutableInDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        List<JavaCandidate> found = [];
        try
        {
            WalkForJava(Path.GetFullPath(directory.Trim()), found, 0);
        }
        catch (Exception)
        {
            return null;
        }

        return found.Count == 0 ? null : found[0].Path;
    }

    private static async Task<JavaRuntime?> DetectWithGateAsync(
        string javaExecutable,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await DetectAsync(javaExecutable, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static List<JavaCandidate> BuildCandidates(string? configuredPath, string? runtimeDirectory)
    {
        List<JavaCandidate> candidates = [];

        // 1. 用户显式指定的路径
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidates.Add(new JavaCandidate(configuredPath.Trim(), true));
        }

        // 2. MOFUMOFU_JAVA 环境变量
        string? environmentJava = Environment.GetEnvironmentVariable(JavaPathVariable);
        if (!string.IsNullOrWhiteSpace(environmentJava))
        {
            candidates.Add(new JavaCandidate(environmentJava.Trim(), true));
        }

        // 3. 启动器中已保存的 Java（设置页添加 / 自动检索的结果）
        foreach (Config.JavaPathItem item in Config.YamlRead.GetJavaPaths())
        {
            if (!string.IsNullOrWhiteSpace(item.Path))
            {
                candidates.Add(new JavaCandidate(item.Path.Trim(), false));
            }
        }

        // 4. JAVA_HOME / JDK_HOME
        foreach (string variable in new[] { "JAVA_HOME", "JDK_HOME" })
        {
            string? home = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(home))
            {
                candidates.Add(new JavaCandidate(Path.Combine(home.Trim(), "bin", JavaExecutableName), false));
            }
        }

        // 5. Minecraft runtime 目录（递归扫描）
        string? configuredRuntime = runtimeDirectory;
        if (string.IsNullOrWhiteSpace(configuredRuntime))
        {
            configuredRuntime = Environment.GetEnvironmentVariable(JavaRuntimeVariable);
        }

        candidates.AddRange(EnumerateRuntimeJavaExecutables(configuredRuntime));

        // 6. PATH 中的每个目录
        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (string directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = directory.Trim().Trim('"');
                if (trimmed.Length > 0)
                {
                    candidates.Add(new JavaCandidate(Path.Combine(trimmed, JavaExecutableName), false));
                }
            }
        }

        // 7. Windows 注册表登记的 JavaHome
        candidates.AddRange(RegistryJavaHomes().Select(home => new JavaCandidate(Path.Combine(home, "bin", JavaExecutableName), false)));

        // 8. Windows 常见安装目录
        candidates.AddRange(CommonInstallDirectories().Select(directory => new JavaCandidate(Path.Combine(directory, "bin", JavaExecutableName), false)));

        return candidates;
    }

    private static IEnumerable<JavaCandidate> EnumerateRuntimeJavaExecutables(string? runtimeDirectory)
    {
        if (string.IsNullOrWhiteSpace(runtimeDirectory))
        {
            return [];
        }

        string expanded = ExpandPath(runtimeDirectory.Trim().Trim('"'));
        if (!Directory.Exists(expanded))
        {
            return [];
        }

        List<JavaCandidate> result = [];
        try
        {
            WalkForJava(expanded, result, 0);
        }
        catch (Exception)
        {
            // 目录不可读时忽略
        }

        return result;
    }

    private static void WalkForJava(string directory, List<JavaCandidate> result, int depth)
    {
        if (depth > MaximumScanDepth)
        {
            return;
        }

        string directJava = Path.Combine(directory, JavaExecutableName);
        if (File.Exists(directJava) && string.Equals(Path.GetFileName(directory), "bin", StringComparison.OrdinalIgnoreCase))
        {
            result.Add(new JavaCandidate(directJava, false));
            return;
        }

        string[] subdirectories;
        try
        {
            subdirectories = Directory.GetDirectories(directory);
        }
        catch (Exception)
        {
            return;
        }

        foreach (string subdirectory in subdirectories)
        {
            WalkForJava(subdirectory, result, depth + 1);
        }
    }

    private static IEnumerable<string> CommonInstallDirectories()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        List<string> roots = [];
        foreach (string variable in new[] { "ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA" })
        {
            string? root = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(root))
            {
                roots.Add(root);
            }
        }

        string[] vendorDirectories =
        [
            "Java", "Eclipse Adoptium", "Eclipse Foundation", "Microsoft", "Zulu",
            "Amazon Corretto", "BellSoft", "Semeru", "JetBrains", "GraalVM",
            Path.Combine("Programs", "Eclipse Adoptium"),
        ];

        List<string> result = [];
        foreach (string root in roots)
        {
            foreach (string vendor in vendorDirectories)
            {
                string vendorRoot = Path.Combine(root, vendor);
                if (!Directory.Exists(vendorRoot))
                {
                    continue;
                }

                result.Add(vendorRoot);
                try
                {
                    foreach (string subdirectory in Directory.GetDirectories(vendorRoot))
                    {
                        result.Add(subdirectory);
                    }
                }
                catch (Exception)
                {
                    // 目录不可读时忽略
                }
            }
        }

        return result;
    }

    private static List<string> RegistryJavaHomes()
    {
        List<string> homes = [];
        if (!OperatingSystem.IsWindows())
        {
            return homes;
        }

        string[] subKeys =
        [
            @"SOFTWARE\JavaSoft\Java Development Kit",
            @"SOFTWARE\JavaSoft\Java Runtime Environment",
            @"SOFTWARE\JavaSoft\JRE",
            @"SOFTWARE\Eclipse Adoptium\JDK",
            @"SOFTWARE\Eclipse Foundation\JDK",
            @"SOFTWARE\Microsoft\JDK",
            @"SOFTWARE\Azul Systems\Zulu",
            @"SOFTWARE\Amazon Corretto",
            @"SOFTWARE\BellSoft\Liberica",
            @"SOFTWARE\Semeru",
        ];

        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            foreach (string subKey in subKeys)
            {
                try
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using RegistryKey? root = baseKey.OpenSubKey(subKey);
                    if (root is null)
                    {
                        continue;
                    }

                    foreach (string versionName in root.GetSubKeyNames())
                    {
                        using RegistryKey? versionKey = root.OpenSubKey(versionName);
                        if (versionKey is null)
                        {
                            continue;
                        }

                        foreach (string valueName in new[] { "JavaHome", "InstallationPath", "Path", "Home" })
                        {
                            if (versionKey.GetValue(valueName) is string home && home.Trim().Length > 0)
                            {
                                homes.Add(home.Trim());
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // 注册表不可读（权限/平台）时忽略
                }
            }
        }

        return homes
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ResolveJavaFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            string expanded = ExpandPath(path.Trim().Trim('"'));
            if (Directory.Exists(expanded))
            {
                string insideDirectory = Path.Combine(expanded, "bin", JavaExecutableName);
                return File.Exists(insideDirectory) ? Path.GetFullPath(insideDirectory) : null;
            }

            return File.Exists(expanded) ? Path.GetFullPath(expanded) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ExpandPath(string path)
    {
        string expanded = Environment.ExpandEnvironmentVariables(path);
        return UnixVariablePattern.Replace(expanded, match =>
            Environment.GetEnvironmentVariable(match.Groups["braced"].Success
                ? match.Groups["braced"].Value
                : match.Groups["plain"].Value) ?? string.Empty);
    }

    private static async Task<string?> RunJavaVersionAsync(string javaExecutable, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = javaExecutable,
            Arguments = "-version",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };

        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return null;
            }
        }
        catch (Exception)
        {
            return null;
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DetectTimeout);
        try
        {
            // 先等待退出（超时则强杀）再读流，避免同步读取永久阻塞
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 进程可能已经退出
            }

            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        try
        {
            string stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            string stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            return stderr + "\n" + stdout;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static JavaRuntime BuildRuntime(string javaExecutable, string output)
    {
        JavaRuntime runtime = new()
        {
            JavaPath = javaExecutable,
            HomePath = Path.GetDirectoryName(Path.GetDirectoryName(javaExecutable)) ?? string.Empty,
        };

        Match match = VersionPattern.Match(output);
        if (match.Success && int.TryParse(match.Groups[1].Value, out int major))
        {
            // 旧式版本号 "1.8.0_202"：主版本取次段
            if (major == 1 && match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out int legacyMajor))
            {
                major = legacyMajor;
            }

            runtime.MajorVersion = major;
            int versionStart = output.IndexOf(match.Groups[1].Value, StringComparison.Ordinal);
            if (versionStart >= 0)
            {
                int versionEnd = versionStart;
                while (versionEnd < output.Length && (char.IsDigit(output[versionEnd]) || output[versionEnd] is '.' or '_' or '+' or '-'))
                {
                    versionEnd++;
                }

                runtime.VersionString = output[versionStart..versionEnd];
            }
        }

        runtime.Vendor = DetectVendor(output);
        runtime.Architecture = DetectArchitecture(output);
        return runtime;
    }

    private static JavaVendor DetectVendor(string output)
    {
        string text = output.ToLowerInvariant();
        if (text.Contains("temurin", StringComparison.Ordinal) || text.Contains("adoptium", StringComparison.Ordinal))
        {
            return JavaVendor.EclipseAdoptium;
        }

        if (text.Contains("zulu", StringComparison.Ordinal) || text.Contains("azul", StringComparison.Ordinal))
        {
            return JavaVendor.Azul;
        }

        if (text.Contains("microsoft", StringComparison.Ordinal))
        {
            return JavaVendor.Microsoft;
        }

        if (text.Contains("corretto", StringComparison.Ordinal) || text.Contains("amazon", StringComparison.Ordinal))
        {
            return JavaVendor.Amazon;
        }

        if (text.Contains("liberica", StringComparison.Ordinal) || text.Contains("bellsoft", StringComparison.Ordinal))
        {
            return JavaVendor.BellSoft;
        }

        if (text.Contains("graalvm", StringComparison.Ordinal))
        {
            return JavaVendor.GraalVm;
        }

        if (text.Contains("jetbrains", StringComparison.Ordinal))
        {
            return JavaVendor.JetBrains;
        }

        if (text.Contains("dragonwell", StringComparison.Ordinal) || text.Contains("alibaba", StringComparison.Ordinal))
        {
            return JavaVendor.Alibaba;
        }

        if (text.Contains("bisheng", StringComparison.Ordinal) || text.Contains("huawei", StringComparison.Ordinal))
        {
            return JavaVendor.Huawei;
        }

        if (text.Contains("semeru", StringComparison.Ordinal) || text.Contains("ibm", StringComparison.Ordinal))
        {
            return JavaVendor.Ibm;
        }

        if (text.Contains("sapmachine", StringComparison.Ordinal) || text.Contains("sap se", StringComparison.Ordinal))
        {
            return JavaVendor.Sap;
        }

        if (text.Contains("tencent", StringComparison.Ordinal))
        {
            return JavaVendor.Tencent;
        }

        if (text.Contains("mojang", StringComparison.Ordinal))
        {
            return JavaVendor.Mojang;
        }

        if (text.Contains("oracle", StringComparison.Ordinal)
            || text.Contains("java(tm) se runtime", StringComparison.Ordinal))
        {
            return JavaVendor.Oracle;
        }

        return text.Contains("openjdk", StringComparison.Ordinal) || text.Contains("open jdk", StringComparison.Ordinal)
            ? JavaVendor.OpenJdk
            : JavaVendor.Unknown;
    }

    private static string DetectArchitecture(string output)
    {
        string text = output.ToLowerInvariant();
        if (text.Contains("aarch64", StringComparison.Ordinal) || text.Contains("arm64", StringComparison.Ordinal))
        {
            return "aarch64";
        }

        if (text.Contains("x86_64", StringComparison.Ordinal) || text.Contains("amd64", StringComparison.Ordinal) || text.Contains("64-bit", StringComparison.Ordinal))
        {
            return "x64";
        }

        return text.Contains("32-bit", StringComparison.Ordinal) ? "x86" : string.Empty;
    }

    private sealed record JavaCandidate(string Path, bool IsPreferred);

    private sealed record ProbedCandidate(string Path, int MajorVersion, int Index, bool IsPreferred);
}

/// <summary>
/// Java 供应商。
/// </summary>
public enum JavaVendor
{
    Unknown,
    Oracle,
    OpenJdk,
    EclipseAdoptium,
    Azul,
    Microsoft,
    Amazon,
    BellSoft,
    GraalVm,
    Ibm,
    JetBrains,
    Tencent,
    Alibaba,
    Huawei,
    Sap,
    Mojang,
}

/// <summary>
/// 供应商显示名。
/// </summary>
public static class JavaVendors
{
    public static string DisplayName(JavaVendor vendor)
    {
        return vendor switch
        {
            JavaVendor.Oracle => "Oracle",
            JavaVendor.OpenJdk => "OpenJDK",
            JavaVendor.EclipseAdoptium => "Temurin",
            JavaVendor.Azul => "Zulu",
            JavaVendor.Microsoft => "Microsoft",
            JavaVendor.Amazon => "Corretto",
            JavaVendor.BellSoft => "Liberica",
            JavaVendor.GraalVm => "GraalVM",
            JavaVendor.Ibm => "Semeru",
            JavaVendor.JetBrains => "JetBrains",
            JavaVendor.Tencent => "Tencent",
            JavaVendor.Alibaba => "Dragonwell",
            JavaVendor.Huawei => "BiSheng",
            JavaVendor.Sap => "SapMachine",
            JavaVendor.Mojang => "Mojang",
            _ => "Unknown",
        };
    }
}

/// <summary>
/// 一个已探测到的 Java 运行时。
/// </summary>
public class JavaRuntime
{
    /// <summary>java 可执行文件路径。</summary>
    public string JavaPath { get; set; } = string.Empty;

    /// <summary>java 版本号，如 21.0.1 或 1.8.0_202。</summary>
    public string VersionString { get; set; } = string.Empty;

    /// <summary>主版本号（旧式 1.8 记为 8）。</summary>
    public int? MajorVersion { get; set; }

    public JavaVendor Vendor { get; set; } = JavaVendor.Unknown;

    /// <summary>架构：x64 / aarch64 / x86，未知为空串。</summary>
    public string Architecture { get; set; } = string.Empty;

    /// <summary>JDK/JRE 根目录（java 所在 bin 的上一级）。</summary>
    public string HomePath { get; set; } = string.Empty;

    public string VendorName => JavaVendors.DisplayName(Vendor);

    public string DisplayName => $"{(MajorVersion is null ? "Java" : "Java " + MajorVersion)} · {VendorName}";
}
