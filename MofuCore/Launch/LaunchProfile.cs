using System.Text.Json;

namespace MofuCore.Launch;

public class LaunchProfile
{
    public string Id { get; set; } = string.Empty;

    /// <summary>version.json 中声明的原始 id（如 NeoForge 的 "neoforge-21.8.54"）。</summary>
    public string SourceId { get; set; } = string.Empty;

    public string InheritsFrom { get; set; } = string.Empty;

    public string MainClass { get; set; } = string.Empty;

    /// <summary>显式 jar 字段优先于链上含 downloads.client 的节点。</summary>
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

    public IReadOnlyList<JsonElement> JvmArguments { get; set; } = [];

    public IReadOnlyList<JsonElement> GameArguments { get; set; } = [];

    public IReadOnlyList<JsonElement> Libraries { get; set; } = [];

    /// <summary>不含 versions 子目录。</summary>
    public string MinecraftDirectory { get; set; } = string.Empty;

    /// <summary>true = 继承链或字段不完整，档案只含基本信息（可展示，不可直接启动）。</summary>
    public bool IsPartial { get; set; }

    public string ClientJarPath => string.IsNullOrWhiteSpace(MinecraftDirectory) || string.IsNullOrWhiteSpace(ClientJarVersionId)
        ? string.Empty
        : Path.Combine(MinecraftDirectory, "versions", ClientJarVersionId, ClientJarVersionId + ".jar");

    public string LibrariesDirectory => string.IsNullOrWhiteSpace(MinecraftDirectory)
        ? string.Empty
        : Path.Combine(MinecraftDirectory, "libraries");

    public string AssetsDirectory => string.IsNullOrWhiteSpace(MinecraftDirectory)
        ? string.Empty
        : Path.Combine(MinecraftDirectory, "assets");
}
