using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Mofumofu.Launcher.Controls;

public static class MofuPopover
{
    public static bool Show(Control anchor, Control card, bool dismissOnBackdrop = true) =>
        FindHost(anchor)?.PresentPopover(anchor, card, dismissOnBackdrop) ?? false;

    public static bool Close(Control card)
    {
        var host = card.FindAncestorOfType<MofuDialogHost>();
        if (host is null) return false;

        host.Manager.DismissDialog();
        return true;
    }

    public static bool IsOpen(Control card) =>
        card.FindAncestorOfType<MofuDialogHost>() is { IsOpen: true };

    private static MofuDialogHost? FindHost(Control anchor) =>
        TopLevel.GetTopLevel(anchor)?.GetVisualDescendants().OfType<MofuDialogHost>().FirstOrDefault();
}
