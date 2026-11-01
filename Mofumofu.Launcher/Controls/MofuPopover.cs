using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Mofumofu.Launcher.Controls;

/// <summary>
/// 窗口级浮层：一层整窗遮罩 + 贴着触发控件弹出的面板（打开时从控件位置缩放"蹦"出来）。
/// <para>
/// 之前小组件里的遮罩是画在卡片内部的，卡片一小就容易被裁、也压不住窗口；
/// 这里把遮罩和面板都挂到窗口级的 <see cref="MofuDialogHost"/> 上，任何控件都能用：
/// <code>MofuPopover.Show(触发控件, 面板);</code>
/// </para>
/// </summary>
public static class MofuPopover
{
    /// <summary>从 <paramref name="anchor"/> 旁弹出 <paramref name="card"/>；找不到窗口级宿主时返回 false。</summary>
    public static bool Show(Control anchor, Control card, bool dismissOnBackdrop = true) =>
        FindHost(anchor)?.PresentPopover(anchor, card, dismissOnBackdrop) ?? false;

    /// <summary>关闭 <paramref name="card"/> 所在的浮层（点取消 / 选完版本时调用）。</summary>
    public static bool Close(Control card)
    {
        var host = card.FindAncestorOfType<MofuDialogHost>();
        if (host is null) return false;

        host.Manager.DismissDialog();
        return true;
    }

    /// <summary>卡片是否还挂在展开中的浮层上（异步加载版本列表回来时要先判一下）。</summary>
    public static bool IsOpen(Control card) =>
        card.FindAncestorOfType<MofuDialogHost>() is { IsOpen: true };

    private static MofuDialogHost? FindHost(Control anchor) =>
        TopLevel.GetTopLevel(anchor)?.GetVisualDescendants().OfType<MofuDialogHost>().FirstOrDefault();
}
