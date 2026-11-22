namespace Mofumofu.ExtensionAPI.Data;

/// <summary>可下载资源的种类（对应 Modrinth 的 project_type）。</summary>
public enum ResourceKind
{
    /// <summary>装进实例 mods/ 目录，支持依赖自动补齐；显式固定序号，禁止依赖隐式序号</summary>
    Mod = 0,

    Shader = 1,

    ResourcePack = 2,
}

/// <summary>资源搜索结果里的一条项目。</summary>
public sealed record ResourceProject(
    string ProjectId,
    string Title,
    string Description,
    string Author,
    long Downloads,
    string? IconUrl);

/// <summary>某个项目在指定游戏版本 / 加载器下的一个可下载版本。</summary>
public sealed record ResourceFileInfo(
    string VersionId,
    string VersionNumber,
    string FileName,
    long SizeBytes);

/// <summary>
/// 资源下载的结果。InstalledFiles 应传入不可变快照（如 <c>ToArray()</c>），
/// 插件不要强转回 List 修改；本类型是位置参数 record，签名保持不变以兼容已编译插件。
/// </summary>
public sealed record ResourceDownloadResult(
    string TargetDirectory,
    IReadOnlyList<string> InstalledFiles)
{
    public IReadOnlyList<string> InstalledFilesSnapshot =>
        Array.AsReadOnly(InstalledFiles is null ? Array.Empty<string>() : InstalledFiles.ToArray());
}
