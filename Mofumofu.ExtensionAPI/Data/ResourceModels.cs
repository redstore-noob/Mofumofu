namespace Mofumofu.ExtensionAPI.Data;

/// <summary>可下载资源的种类（对应 Modrinth 的 project_type）。</summary>
public enum ResourceKind
{
    /// <summary>模组（装进实例的 mods/ 目录，支持依赖自动补齐）</summary>
    Mod,

    /// <summary>光影（shaderpacks/ 目录）</summary>
    Shader,

    /// <summary>资源包（resourcepacks/ 目录）</summary>
    ResourcePack,
}

/// <summary>资源搜索结果里的一条项目。</summary>
/// <param name="ProjectId">项目唯一 id（查文件 / 下载时回传）</param>
/// <param name="Title">项目名</param>
/// <param name="Description">简介</param>
/// <param name="Author">作者</param>
/// <param name="Downloads">下载量</param>
/// <param name="IconUrl">图标地址；无图标为 null</param>
public sealed record ResourceProject(
    string ProjectId,
    string Title,
    string Description,
    string Author,
    long Downloads,
    string? IconUrl);

/// <summary>某个项目在指定游戏版本 / 加载器下的一个可下载版本。</summary>
/// <param name="VersionId">版本唯一 id（下载时回传）</param>
/// <param name="VersionNumber">版本号显示串</param>
/// <param name="FileName">主文件名</param>
/// <param name="SizeBytes">主文件大小（字节）</param>
public sealed record ResourceFileInfo(
    string VersionId,
    string VersionNumber,
    string FileName,
    long SizeBytes);

/// <summary>资源下载的结果。</summary>
/// <param name="TargetDirectory">文件落地的目录</param>
/// <param name="InstalledFiles">本次实际写入的文件完整路径（含自动补齐的依赖）</param>
public sealed record ResourceDownloadResult(
    string TargetDirectory,
    IReadOnlyList<string> InstalledFiles);
