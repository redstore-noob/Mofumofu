namespace Mofumofu.ExtensionAPI.Data;

/// <summary>
/// 插件分类：决定插件在启动器中显示的类型标签（比如功能扩展/主题美化）。
/// 在插件入口中必填。
/// </summary>
/// <remarks>序号显式固定，禁止依赖隐式序号；插件按整数持久化时不要假设成员顺序。</remarks>
public enum PluginTypeEnum
{
    Tool = 0,
    Theme = 1,
    General = 2,
    Optimize = 3,
    Patch = 4,
    Function = 5
}
