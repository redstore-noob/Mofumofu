namespace Mofumofu.ExtensionAPI.Data;

/// <summary>实例 mods/ 目录里的一个模组文件。</summary>
/// <param name="FileName">启用态文件名（如 "sodium.jar"，停用的为 "sodium.jar" + Enabled=false）</param>
/// <param name="Enabled">是否启用（.jar 为启用，.jar.disabled 为停用）</param>
/// <param name="SizeBytes">文件大小（字节）</param>
public sealed record ModFileInfo(
    string FileName,
    bool Enabled,
    long SizeBytes);
