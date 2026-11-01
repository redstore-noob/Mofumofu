using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Mofumofu.Launcher.Services;

namespace Mofumofu.Launcher.Controls;

/// <summary>
/// 遮罩层内层卡片（居中弹窗底板 / 贴控件浮层面板）的统一 Border 工厂。
/// <para>
/// 以前 DialogBuilder 和各浮层调用点各自手搓 Border——圆角 16 的、12 的，
/// 投影有 24/64 的、14/36 的，描边深浅也不一。这里收敛成同一套外观令牌：
/// 主题实心底板 + 1px 描边 + 双层投影，只按「居中弹窗 / 贴控件浮层」两种语义
/// 分圆角和投影权重。新弹层一律从这里取卡片，别再手搓。
/// </para>
/// </summary>
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
            // 双层投影：一层大而散撑场面，一层小而紧把卡片"钉"在遮罩上
            // （BoxShadows 构造是 (first, rest[])：第一个单放，其余进数组）
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

    /// <summary>
    /// 贴触发控件的浮层面板：小一号的圆角和投影，避免跟居中弹窗抢视觉重量。
    /// 宽高由调用点按内容设定（工厂不管），内边距走 <paramref name="padding"/>。
    /// </summary>
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
