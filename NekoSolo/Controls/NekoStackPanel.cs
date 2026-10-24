using System.Windows;
using System.Windows.Controls;

namespace NekoSolo.Controls;

/// <summary>
/// 带 Spacing 的 StackPanel（WPF 原生没有该属性，界面上大量使用等距排布）。
/// 布局语义与 Avalonia 的 StackPanel.Spacing 一致：只在相邻可见子元素之间插入间隔。
/// </summary>
public class NekoStackPanel : StackPanel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(NekoStackPanel),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        bool horizontal = Orientation == Orientation.Horizontal;
        double main = 0;
        double cross = 0;
        int visible = 0;

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;

            child.Measure(horizontal
                ? new Size(double.PositiveInfinity, availableSize.Height)
                : new Size(availableSize.Width, double.PositiveInfinity));

            if (horizontal)
            {
                main += child.DesiredSize.Width;
                cross = Math.Max(cross, child.DesiredSize.Height);
            }
            else
            {
                main += child.DesiredSize.Height;
                cross = Math.Max(cross, child.DesiredSize.Width);
            }

            visible++;
        }

        if (visible > 1) main += Spacing * (visible - 1);
        return horizontal ? new Size(main, cross) : new Size(cross, main);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        bool horizontal = Orientation == Orientation.Horizontal;
        double offset = 0;

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;

            if (horizontal)
            {
                child.Arrange(new Rect(offset, 0, child.DesiredSize.Width, finalSize.Height));
                offset += child.DesiredSize.Width + Spacing;
            }
            else
            {
                child.Arrange(new Rect(0, offset, finalSize.Width, child.DesiredSize.Height));
                offset += child.DesiredSize.Height + Spacing;
            }
        }

        return finalSize;
    }
}
