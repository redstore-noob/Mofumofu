using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Mofumofu.Launcher.Services;

namespace Mofumofu.Launcher.Controls;

public static class MofuCard
{
    /// <summary>
    /// 居中弹窗的卡片底板：大圆角 + 重投影（窗口级遮罩上唯一的主角）。
    /// 尺寸上限与弹窗宿主保持一致（880×760），超了会被宿主的布局压住。
    /// </summary>
    public static Border Dialog(Control body)
    {
        return new Border
        {
            Background = MofuDialogHost.Res("NekoDialogSolidBrush", Color.FromArgb(0xFF, 0x12, 0x1A, 0x24)),
            BorderBrush = MofuDialogHost.Res("NekoBorderBrush", Color.FromArgb(0xFF, 0x26, 0x30, 0x3D)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            MaxWidth = 880,
            MaxHeight = 760,
            BoxShadow = new BoxShadows(
                new BoxShadow
                {
                    OffsetY = 24,
                    Blur = 64,
                    Color = Color.FromArgb(0x88, 0, 0, 0),
                },
                new BoxShadow[]
                {
                    new()
                    {
                        OffsetY = 2,
                        Blur = 8,
                        Color = Color.FromArgb(0x22, 0, 0, 0),
                    },
                }),
            Child = body,
        };
    }

    public static Border Popover(Control body, Thickness padding)
    {
        return new Border
        {
            Background = MofuDialogHost.Res("NekoDialogSolidBrush", Color.FromArgb(0xFF, 0x12, 0x1A, 0x24)),
            BorderBrush = MofuDialogHost.Res("NekoBorderBrush", Color.FromArgb(0xFF, 0x26, 0x30, 0x3D)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = padding,
            BoxShadow = new BoxShadows(
                new BoxShadow
                {
                    OffsetY = 14,
                    Blur = 36,
                    Color = Color.FromArgb(0x55, 0, 0, 0),
                },
                new BoxShadow[]
                {
                    new()
                    {
                        OffsetY = 1,
                        Blur = 4,
                        Color = Color.FromArgb(0x1A, 0, 0, 0),
                    },
                }),
            Child = body,
        };
    }
}
