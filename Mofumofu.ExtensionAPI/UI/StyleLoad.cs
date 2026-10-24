using Avalonia.Styling;

namespace Mofumofu.ExtensionAPI.UI;

/// <summary>
/// 加载axaml，覆盖启动器原本的主题
/// 这个修改功能多个插件叠一起可能会爆💥
/// 资源键请加插件 Id 前缀（如 MyPlugin.BackgroundBrush），避免多个插件互相覆盖。
/// </summary>
public interface IStyleLoad
{
    /// <summary>
    /// 加载已打进 DLL 的 axaml 资源流（AvaloniaResource / EmbeddedResource），适合正式发布的插件。
    /// 加载失败只影响本次调用（抛异常），不会影响宿主与其他插件。
    /// </summary>
    /// <param name="xamlStream">axaml 资源流</param>
    /// <returns>撤销句柄：调用后撤下这批样式；插件停用/卸载时宿主也会代为调用</returns>
    IDisposable StyleEdit(Stream xamlStream);

    /// <summary>
    /// 加载插件目录下的散装 axaml 文件（相对插件目录，如 "theme.axaml"），适合开发调试期随改随看。
    /// </summary>
    /// <param name="xamlPath">axaml 文件路径，相对插件目录</param>
    /// <returns>撤销句柄：调用后撤下这批样式；插件停用/卸载时宿主也会代为调用</returns>
    IDisposable StyleEdit(string xamlPath);

    /// <summary>
    /// 直接应用一个现成的样式/主题对象——主题库（如 Material.Avalonia）的标准用法：
    /// 插件自带主题库 DLL，实例化它的主题类（实现了 IStyle）后从这里交给宿主合入全局。
    /// 加载后宿主的标准控件会换成该主题的观感；宿主自绘部分不受影响。
    /// </summary>
    /// <param name="styles">主题库的样式对象（IStyle，例如 MaterialTheme 实例）</param>
    /// <returns>撤销句柄：撤下后恢复宿主原有观感</returns>
    IDisposable StyleEdit(IStyle styles);
}
