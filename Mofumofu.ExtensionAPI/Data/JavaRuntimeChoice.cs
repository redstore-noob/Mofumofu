namespace Mofumofu.ExtensionAPI.Data;

/// <summary>Mojang 官方运行时清单里的一条。</summary>
/// <param name="Component">组件名，如 java-runtime-gamma（安装时按主版本即可，此字段供展示）</param>
/// <param name="VersionName">完整版本号，如 17.0.8</param>
/// <param name="MajorVersion">主版本号（8 / 11 / 17 / 21 / 25）</param>
/// <param name="Released">发布时间</param>
public sealed record JavaRuntimeChoice(
    string Component,
    string VersionName,
    int? MajorVersion,
    DateTimeOffset Released);
