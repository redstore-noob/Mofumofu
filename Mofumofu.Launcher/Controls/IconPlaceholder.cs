using Avalonia.Controls;

namespace Mofumofu.Launcher.Controls;

/// <summary>
/// 懒加载图标与占位图标叠在同一个面板里：占位图标在下、真图标（<see cref="Image"/>）在上。
/// 真图标就绪后必须把下面的占位图标收起来，否则背景透明的图标会把占位字形漏出来。
/// </summary>
internal static class IconPlaceholder
{
    /// <summary>
    /// 显示或隐藏与 <paramref name="image"/> 同层的占位图标。
    /// 找不到占位图标时什么都不做。
    /// </summary>
    public static void SetVisible(Image image, bool visible)
    {
        if (image.Parent is not Panel panel) return;

        foreach (Control child in panel.Children)
        {
            if (ReferenceEquals(child, image)) continue;
            if (child is IconParkIcon placeholder) placeholder.IsVisible = visible;
        }
    }
}
