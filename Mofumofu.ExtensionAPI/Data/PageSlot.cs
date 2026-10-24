namespace Mofumofu.ExtensionAPI.Data;

/// <summary>
/// 插件内容可以插到页面的哪一层边缘。
/// 页面本体始终在中间，这三层都包在页面外面。
/// </summary>
public enum PageSlot
{
    /// <summary>页面顶部，占布局高度。</summary>
    Banner,

    /// <summary>页面底部，占布局高度。</summary>
    Footer,

    /// <summary>悬浮在页面之上，不占布局高度，适合浮球、角标一类内容。</summary>
    Overlay,
}
