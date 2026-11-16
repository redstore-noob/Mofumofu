namespace Mofumofu.ExtensionAPI.Actions;

/// <summary>
/// 一次启动的上下文：宿主在启动前装好递给钩子，插件据此做检查与注入。
/// </summary>
public interface ILaunchContext
{
    /// <summary>即将启动的实例目录名。</summary>
    string InstanceId { get; }

    /// <summary>游戏版本号。</summary>
    string VersionId { get; }

    /// <summary>实例所在游戏目录。</summary>
    string GameDirectory { get; }

    /// <summary>
    /// JVM 参数的只读视图。契约层不再暴露可写 <c>IList</c>：插件无法经属性直接
    /// Add / Clear / Remove 绕过白名单；即便把返回值强转回 <c>IList&lt;string&gt;</c>，
    /// 宿主返回的守卫列表也会对所有写操作再做一次 <see cref="JvmArgumentPolicy"/> 校验。
    /// 新增参数请用 <see cref="TryAddJvmArgument"/>，只读快照用 <see cref="JvmArgumentsSnapshot"/>。
    /// </summary>
    IReadOnlyList<string> JvmArguments { get; }

    /// <summary>
    /// JVM 参数的只读快照（每次读取生成新副本，插件即便强转也无法改动宿主内部集合）。
    /// 其它插件此后追加的参数不会反映到已取到的快照里。
    /// </summary>
    IReadOnlyList<string> JvmArgumentsSnapshot =>
        Array.AsReadOnly(JvmArguments.ToArray());

    /// <summary>
    /// 追加一条 JVM 参数（新增参数的推荐、受控入口）。会先按 <see cref="JvmArgumentPolicy"/>
    /// 白名单校验：仅放行常见调优参数，拒绝改写类路径 / 挂载代理 / 触发外部命令的高危参数，
    /// 也拒绝空白与控制字符注入。通过校验返回 true，否则返回 false 且不改动列表。
    /// </summary>
    /// <param name="argument">单条 JVM 参数（如 <c>-Xmx4G</c>、<c>-Dkey=value</c>）</param>
    /// <returns>true = 已通过白名单并追加；false = 被拒绝（空值 / 非法字符 / 不在白名单内）</returns>
    bool TryAddJvmArgument(string argument)
    {
        if (!JvmArgumentPolicy.IsAllowed(argument))
            return false;
        // 属性是只读视图；宿主实现返回的对象同时实现可写 IList，写入再次经守卫白名单。
        if (JvmArguments is IList<string> writable)
        {
            writable.Add(argument);
            return true;
        }
        return false;
    }

    /// <summary>阻止本次启动；reason 会展示给玩家。调用后宿主短路后续钩子。</summary>
    void Cancel(string reason);
}

/// <summary>
/// 插件可注入的 JVM 参数白名单校验（供 <see cref="ILaunchContext.TryAddJvmArgument"/> 使用）。
/// 采用「前缀白名单 + 高危子串黑名单」双重约束，默认拒绝一切未显式放行的参数。
/// </summary>
public static class JvmArgumentPolicy
{
    /// <summary>放行的安全前缀（大小写敏感）。这些是标准 JVM 调优参数，不能改写 classpath / 挂载代理。</summary>
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
    /// 即便命中前缀也必须拒绝的高危子串：改写类路径 / 挂载代理 / 引导类路径 / 触发外部命令 /
    /// 替换类加载器或安全策略。必须与 MofuCore 里 MinecraftArgumentBuilder 的黑名单保持一致，
    /// 否则插件能注入内核拒绝的东西（两处策略不一致本身也会让人误判"内核已经拦住了"）。
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

    /// <summary>校验一条待注入的 JVM 参数是否落在白名单内。</summary>
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
