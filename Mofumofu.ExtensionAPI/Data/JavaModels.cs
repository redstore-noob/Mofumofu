namespace Mofumofu.ExtensionAPI.Data;

/// <summary>
/// 一个已安装 / 刚装好的 Java 运行时。Component 为 Mojang 组件名（如 java-runtime-gamma），
/// 供应商安装的为空；MajorVersion 无法识别为 null。
/// </summary>
public sealed record InstalledJavaInfo(
    string JavaPath,
    string HomePath,
    string Component,
    int? MajorVersion);
