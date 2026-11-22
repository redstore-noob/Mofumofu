namespace Mofumofu.ExtensionAPI.Data;

/// <summary>
/// 实例 mods/ 目录里的一个模组文件。FileName 是启用态名字（如 "sodium.jar"），
/// 停用的文件在磁盘上是 .jar.disabled。
/// </summary>
public sealed record ModFileInfo(
    string FileName,
    bool Enabled,
    long SizeBytes);
