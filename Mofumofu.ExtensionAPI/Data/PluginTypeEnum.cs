namespace Mofumofu.ExtensionAPI.Data;

/// <summary>
/// 插件分类：决定插件在启动器中显示的类型标签（比如功能扩展/主题美化）。
/// 在插件入口中必填。
/// </summary>
public enum PluginTypeEnum
{
    Tool,
    Theme,
    General,
    Optimize,
    Patch,
    Function
}
