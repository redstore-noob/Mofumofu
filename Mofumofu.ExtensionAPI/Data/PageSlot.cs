namespace Mofumofu.ExtensionAPI.Data;

/// <summary>
/// 插件内容可以插到页面的哪一层边缘；页面本体始终在中间，这三层都包在页面外面。
/// </summary>
public enum PageSlot
{
    /// <summary>占布局高度；显式固定序号，禁止依赖隐式序号</summary>
    Banner = 0,

    /// <summary>占布局高度。</summary>
    Footer = 1,

    /// <summary>不占布局高度，适合浮球、角标一类内容。</summary>
    Overlay = 2,
}
