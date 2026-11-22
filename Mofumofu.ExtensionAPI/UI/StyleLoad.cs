using Avalonia.Styling;

namespace Mofumofu.ExtensionAPI.UI;

/// <summary>
/// 加载 axaml，覆盖启动器原本的主题。
/// </summary>
/// <remarks>
/// 需要 <see cref="PluginPermissionZones.Widget"/> 权限。多个插件的样式会互相覆盖，
/// 资源键请加插件 Id 前缀（如 MyPlugin.BackgroundBrush）；插件停用 / 卸载时宿主撤销本次样式。
/// </remarks>
public interface IStyleLoad
{
    /// <summary>
    /// 读已打进 DLL 的 axaml 资源流（AvaloniaResource / EmbeddedResource），适合正式发布的插件；
    /// 加载失败抛异常，只影响本次调用，不影响宿主与其他插件。
    /// </summary>
    IDisposable StyleEdit(Stream xamlStream);

    /// <summary>读插件目录下的散装 axaml（相对路径），适合开发调试期随改随看。</summary>
    IDisposable StyleEdit(string xamlPath);

    /// <summary>
    /// 应用现成的样式/主题对象，主题库（如 Material.Avalonia）的标准用法：
    /// 实例化实现了 IStyle 的主题类后交给宿主合入全局；宿主自绘部分不受影响。
    /// </summary>
    IDisposable StyleEdit(IStyle styles);
}
