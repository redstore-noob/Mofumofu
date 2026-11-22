namespace Mofumofu.ExtensionAPI.Actions;

/// <summary>
/// 一次启动的上下文：宿主在启动前装好递给钩子。
/// </summary>
public interface ILaunchContext
{
    string InstanceId { get; }

    string VersionId { get; }

    string GameDirectory { get; }

    /// <summary>
    /// 只读视图：即便强转成 <c>IList&lt;string&gt;</c>，宿主返回的守卫列表也会对写操作
    /// 再做一次 <see cref="JvmArgumentPolicy"/> 校验。
    /// </summary>
    IReadOnlyList<string> JvmArguments { get; }

    IReadOnlyList<string> JvmArgumentsSnapshot =>
        Array.AsReadOnly(JvmArguments.ToArray());

    /// <summary>
    /// 追加 JVM 参数的受控入口：按 <see cref="JvmArgumentPolicy"/> 白名单校验，
    /// 拒绝空白与控制字符注入，被拒绝时不改动列表。
    /// </summary>
    bool TryAddJvmArgument(string argument)
    {
        if (!JvmArgumentPolicy.IsAllowed(argument))
            return false;
        if (JvmArguments is IList<string> writable)
        {
            writable.Add(argument);
            return true;
        }
        return false;
    }

    void Cancel(string reason);
}

/// <summary>
/// 采用「前缀白名单 + 高危子串黑名单」双重约束，默认拒绝一切未显式放行的参数。
/// </summary>
public static class JvmArgumentPolicy
{
    /// <summary>大小写敏感。这些是标准 JVM 调优参数，不能改写 classpath / 挂载代理。</summary>
    private static readonly string[] AllowedPrefixes =
    {
        "-Xms", "-Xmx", "-Xss", "-Xmn",
        "-XX:",
        "-D",
        "-ea", "-da", "-esa", "-dsa",
        "-verbose:",
        "--add-opens=", "--add-exports=",
    };

    /// <summary>
    /// 必须与 MofuCore 里 MinecraftArgumentBuilder 的黑名单保持一致，
    /// 否则插件能注入内核拒绝的东西，且两处策略不一致会让人误判"内核已经拦住了"。
    /// </summary>
    private static readonly string[] ForbiddenSubstrings =
    {
        "javaagent", "agentlib", "agentpath",
        "bootclasspath", "patch-module", "module-path", "class-path",
        "-cp", "-jar",
        "runjdwp", "Xdebug",
        "OnError", "OnOutOfMemoryError",
        "StartFlightRecording", "FlightRecorderOptions", "CompilerCommandFile",
        "java.system.class.loader", "com.sun.management",
        "java.rmi.server.codebase", "java.security.policy",
    };

    public static bool IsAllowed(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
            return false;

        // 拒绝空白 / 控制字符，避免一次 Add 注入额外参数或换行。
        foreach (char c in argument)
            if (char.IsWhiteSpace(c) || char.IsControl(c))
                return false;

        foreach (string forbidden in ForbiddenSubstrings)
            if (argument.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                return false;

        foreach (string prefix in AllowedPrefixes)
            if (argument.StartsWith(prefix, StringComparison.Ordinal))
                return true;

        return false;
    }
}
