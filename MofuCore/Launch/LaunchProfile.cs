using System.Text.Json;

namespace MofuCore.Launch;

/// <summary>
/// 这只是一个启动时要准备的数据处理的东西🤔
/// 合并 inheritsFrom 继承链后的最终启动档案：主类、资源索引、Java 要求、参数与依赖库。
/// </summary>
public class LaunchProfile
{
    /// <summary>实例目录名（启动请求的版本 id）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>version.json 中声明的原始 id（如 NeoForge 的 "neoforge-21.8.54"）。</summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>继承链上的父版本 id（原版实例为空）。</summary>
    public string InheritsFrom { get; set; } = string.Empty;

    public string MainClass { get; set; } = string.Empty;

    /// <summary>提供客户端 JAR 的版本 id（显式 jar 字段优先于含 downloads.client 的链节点）。</summary>
    public string ClientJarVersionId { get; set; } = string.Empty;

    public string AssetsId { get; set; } = string.Empty;

    /// <summary>logging.client.argument 模板（原版为 -Dlog4j.configurationFile=${path}）。</summary>
    public string LoggingArgument { get; set; } = string.Empty;

    /// <summary>logging.client.file.id（log4j 配置文件名，如 client-1.12.xml）。</summary>
    public string LoggingFileId { get; set; } = string.Empty;

    public string VersionType { get; set; } = string.Empty;

    /// <summary>版本要求的最低 Java 主版本；null 表示未声明。</summary>
    public int? RequiredJavaMajorVersion { get; set; }

    /// <summary>旧版 minecraftArguments（新版格式之前）。</summary>
    public string LegacyGameArguments { get; set; } = string.Empty;

    /// <summary>新版 arguments.jvm 原始元素（字符串或带 rules 的对象）。</summary>
    public IReadOnlyList<JsonElement> JvmArguments { get; set; } = [];

    /// <summary>新版 arguments.game 原始元素。</summary>
    public IReadOnlyList<JsonElement> GameArguments { get; set; } = [];

    /// <summary>依赖库原始条目（去重后，保持首次出现顺序）。</summary>
    public IReadOnlyList<JsonElement> Libraries { get; set; } = [];

    /// <summary>实例所在的 Minecraft 根目录（不含 versions 子目录）。</summary>
    public string MinecraftDirectory { get; set; } = string.Empty;

    /// <summary>true = 继承链或字段不完整，档案只含基本信息（扫描列表仍可展示，不可直接启动）。</summary>
    public bool IsPartial { get; set; }

    /// <summary>客户端 JAR 的绝对路径。</summary>
    public string ClientJarPath => string.IsNullOrWhiteSpace(MinecraftDirectory) || string.IsNullOrWhiteSpace(ClientJarVersionId)
        ? string.Empty
        : Path.Combine(MinecraftDirectory, "versions", ClientJarVersionId, ClientJarVersionId + ".jar");

    /// <summary>依赖库目录的绝对路径。</summary>
    public string LibrariesDirectory => string.IsNullOrWhiteSpace(MinecraftDirectory)
        ? string.Empty
        : Path.Combine(MinecraftDirectory, "libraries");

    /// <summary>资源目录的绝对路径。</summary>
    public string AssetsDirectory => string.IsNullOrWhiteSpace(MinecraftDirectory)
        ? string.Empty
        : Path.Combine(MinecraftDirectory, "assets");
}
