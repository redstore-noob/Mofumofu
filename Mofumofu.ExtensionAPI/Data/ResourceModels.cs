namespace Mofumofu.ExtensionAPI.Data;

/// <summary>可下载资源的种类（对应 Modrinth 的 project_type）。</summary>
public enum ResourceKind
{
    /// <summary>模组（装进实例的 mods/ 目录，支持依赖自动补齐）；显式固定序号，禁止依赖隐式序号</summary>
    Mod = 0,

    /// <summary>光影（shaderpacks/ 目录）</summary>
    Shader = 1,

    /// <summary>资源包（resourcepacks/ 目录）</summary>
    ResourcePack = 2,
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
/// <param name="InstalledFiles">
/// 本次实际写入的文件完整路径（含自动补齐的依赖）。宿主应传入不可变快照
/// （如 <c>Array.AsReadOnly(...)</c> / <c>ToArray()</c>），不要把内部可变 List 直接塞进来；
/// 插件也不要把本属性强转回 List 修改。需要保证只读的读取方请用 <see cref="InstalledFilesSnapshot"/>。
/// </param>
/// <remarks>本类型是位置参数 record，构造函数 / Deconstruct / with 签名保持不变以兼容已编译插件。</remarks>
public sealed record ResourceDownloadResult(
    string TargetDirectory,
    IReadOnlyList<string> InstalledFiles)
{
    /// <summary>写入文件列表的只读安全快照（每次生成新副本，强转也无法改动宿主内部集合）。</summary>
    public IReadOnlyList<string> InstalledFilesSnapshot =>
        Array.AsReadOnly(InstalledFiles is null ? Array.Empty<string>() : InstalledFiles.ToArray());
}
