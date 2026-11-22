namespace Mofumofu.ExtensionAPI.Data;

/// <summary>Mojang 官方运行时清单里的一条；安装时按主版本匹配，Component 仅供展示。</summary>
public sealed record JavaRuntimeChoice(
    string Component,
    string VersionName,
    int? MajorVersion,
    DateTimeOffset Released);
