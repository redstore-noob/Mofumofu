using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

using MofuCore.Config;
namespace MofuCore.Java;

public class JavaPathSearch
{
    /// <summary>优先级高于自动探测。</summary>
    public const string JavaPathVariable = "MOFUMOFU_JAVA";

    public const string JavaRuntimeVariable = "MOFUMOFU_JAVA_RUNTIME";

    private const int MaximumScanDepth = 6;
    private static readonly TimeSpan DetectTimeout = NetworkDefaults.QuickProbeTimeout;
    private static readonly Regex VersionPattern = new(
        "version\\s+\"?(\\d+)(?:\\.(\\d+))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UnixVariablePattern = new(
        "\\$(?:\\{(?<braced>\\w+)\\}|(?<plain>\\w+))",
        RegexOptions.Compiled);

    public static string JavaExecutableName => OperatingSystem.IsWindows() ? "java.exe" : "java";

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

    public static JavaRuntime? Detect(string javaExecutable)
    {
        return DetectAsync(javaExecutable).GetAwaiter().GetResult();
    }

    public static async Task<int?> DetectMajorVersionAsync(string javaExecutable, CancellationToken cancellationToken = default)
    {
        JavaRuntime? runtime = await DetectAsync(javaExecutable, cancellationToken).ConfigureAwait(false);
        return runtime?.MajorVersion;
    }

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

    public static IReadOnlyList<string> CollectCandidates(string? configuredPath = null, string? runtimeDirectory = null)
    {
        return BuildCandidates(configuredPath, runtimeDirectory)
            .Select(candidate => candidate.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();
    }

    /// <summary>要求父目录为 bin。</summary>
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

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidates.Add(new JavaCandidate(configuredPath.Trim(), true));
        }

        string? environmentJava = Environment.GetEnvironmentVariable(JavaPathVariable);
        if (!string.IsNullOrWhiteSpace(environmentJava))
        {
            candidates.Add(new JavaCandidate(environmentJava.Trim(), true));
        }

        foreach (Config.JavaPathItem item in Config.YamlRead.GetJavaPaths())
        {
            if (!string.IsNullOrWhiteSpace(item.Path))
            {
                candidates.Add(new JavaCandidate(item.Path.Trim(), false));
            }
        }

        foreach (string variable in new[] { "JAVA_HOME", "JDK_HOME" })
        {
            string? home = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(home))
            {
                candidates.Add(new JavaCandidate(Path.Combine(home.Trim(), "bin", JavaExecutableName), false));
            }
        }

        string? configuredRuntime = runtimeDirectory;
        if (string.IsNullOrWhiteSpace(configuredRuntime))
        {
            configuredRuntime = Environment.GetEnvironmentVariable(JavaRuntimeVariable);
        }

        candidates.AddRange(EnumerateRuntimeJavaExecutables(configuredRuntime));

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

        candidates.AddRange(RegistryJavaHomes().Select(home => new JavaCandidate(Path.Combine(home, "bin", JavaExecutableName), false)));

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
        if (OperatingSystem.IsWindows())
        {
            return WindowsInstallDirectories();
        }

        if (OperatingSystem.IsMacOS())
        {
            return MacOsInstallDirectories();
        }

        return LinuxInstallDirectories();
    }

    private static IEnumerable<string> WindowsInstallDirectories()
    {
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
                }
            }
        }

        return result;
    }

    private static IEnumerable<string> LinuxInstallDirectories()
    {
        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] roots =
        [
            "/usr/lib/jvm", "/usr/java", "/opt/java", "/opt",
            Path.Combine(userHome, ".jdks"),
            Path.Combine(userHome, ".sdkman", "candidates", "java"),
        ];

        List<string> result = [];
        foreach (string root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            result.Add(root);
            try
            {
                result.AddRange(Directory.GetDirectories(root));
            }
            catch (Exception)
            {
            }
        }

        return result;
    }

    private static IEnumerable<string> MacOsInstallDirectories()
    {
        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] bases =
        [
            "/Library/Java/JavaVirtualMachines",
            Path.Combine(userHome, "Library", "Java", "JavaVirtualMachines"),
        ];

        List<string> result = [];
        foreach (string baseDirectory in bases)
        {
            if (!Directory.Exists(baseDirectory))
            {
                continue;
            }

            try
            {
                foreach (string bundle in Directory.GetDirectories(baseDirectory))
                {
                    // macOS 的 JDK home 在 <bundle>.jdk/Contents/Home
                    string home = Path.Combine(bundle, "Contents", "Home");
                    if (Directory.Exists(home))
                    {
                        result.Add(home);
                    }
                }
            }
            catch (Exception)
            {
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
            // 拒绝展开后仍含控制字符/空字符的路径，避免环境变量或 PATH 注入破坏后续命令行与路径解析
            foreach (char character in expanded)
            {
                if (character == '\0' || char.IsControl(character))
                {
                    return null;
                }
            }

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
        // Windows 路径允许字面量 '$'，禁用 Unix 风格 $VAR / ${VAR} 展开，避免环境变量劫持候选路径
        if (OperatingSystem.IsWindows())
        {
            return expanded;
        }

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

public class JavaRuntime
{
    public string JavaPath { get; set; } = string.Empty;

    public string VersionString { get; set; } = string.Empty;

    /// <summary>旧式 1.8 记为 8。</summary>
    public int? MajorVersion { get; set; }

    public JavaVendor Vendor { get; set; } = JavaVendor.Unknown;

    public string Architecture { get; set; } = string.Empty;

    public string HomePath { get; set; } = string.Empty;

    public string VendorName => JavaVendors.DisplayName(Vendor);

    public string DisplayName => $"{(MajorVersion is null ? "Java" : "Java " + MajorVersion)} · {VendorName}";
}
