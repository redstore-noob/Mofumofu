namespace Mofumofu.ExtensionAPI.Data;

/// <summary>一个已安装 / 刚装好的 Java 运行时。</summary>
/// <param name="JavaPath">java 可执行文件完整路径</param>
/// <param name="HomePath">运行时根目录（bin 的上一级）</param>
/// <param name="Component">Mojang 组件名（如 java-runtime-gamma）；供应商安装的为空</param>
/// <param name="MajorVersion">主版本号；无法识别为 null</param>
public sealed record InstalledJavaInfo(
    string JavaPath,
    string HomePath,
    string Component,
    int? MajorVersion);
