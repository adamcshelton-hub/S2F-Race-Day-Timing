using System.Windows;
using System.Windows.Controls;

namespace S2F.RaceDayCheckout.Controls;

/// <summary>
/// Single horizontal row: children share width equally and stretch to the panel height.
/// Used by start clocks so adding more shrinks each card instead of wrapping / scrolling.
/// </summary>
public sealed class EqualWidthRowPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var count = InternalChildren.Count;
        if (count == 0)
            return new Size(0, 0);

        var finiteWidth = !double.IsInfinity(availableSize.Width) && availableSize.Width > 0;
        var finiteHeight = !double.IsInfinity(availableSize.Height) && availableSize.Height > 0;
        var childWidth = finiteWidth ? availableSize.Width / count : availableSize.Width;
        var childHeight = finiteHeight ? availableSize.Height : availableSize.Height;

        double maxDesiredHeight = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(childWidth, childHeight));
            maxDesiredHeight = Math.Max(maxDesiredHeight, child.DesiredSize.Height);
        }

        var width = finiteWidth ? availableSize.Width : childWidth * count;
        var height = finiteHeight ? availableSize.Height : maxDesiredHeight;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = InternalChildren.Count;
        if (count == 0)
            return finalSize;

        var childWidth = finalSize.Width / count;
        for (var i = 0; i < count; i++)
        {
            InternalChildren[i].Arrange(new Rect(i * childWidth, 0, childWidth, finalSize.Height));
        }

        return finalSize;
    }
}
