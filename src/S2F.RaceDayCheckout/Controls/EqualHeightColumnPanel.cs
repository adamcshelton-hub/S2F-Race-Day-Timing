using System.Windows;
using System.Windows.Controls;

namespace S2F.RaceDayCheckout.Controls;

/// <summary>
/// Single vertical column: children share height equally and stretch to the panel width.
/// Used by start clocks beside Manual Backup so they fit without scrolling.
/// </summary>
public sealed class EqualHeightColumnPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var count = InternalChildren.Count;
        if (count == 0)
            return new Size(0, 0);

        var finiteWidth = !double.IsInfinity(availableSize.Width) && availableSize.Width > 0;
        var finiteHeight = !double.IsInfinity(availableSize.Height) && availableSize.Height > 0;
        var childWidth = finiteWidth ? availableSize.Width : availableSize.Width;
        var childHeight = finiteHeight ? availableSize.Height / count : availableSize.Height;

        double maxDesiredWidth = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(childWidth, childHeight));
            maxDesiredWidth = Math.Max(maxDesiredWidth, child.DesiredSize.Width);
        }

        var width = finiteWidth ? availableSize.Width : maxDesiredWidth;
        var height = finiteHeight ? availableSize.Height : childHeight * count;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = InternalChildren.Count;
        if (count == 0)
            return finalSize;

        var childHeight = finalSize.Height / count;
        for (var i = 0; i < count; i++)
        {
            InternalChildren[i].Arrange(new Rect(0, i * childHeight, finalSize.Width, childHeight));
        }

        return finalSize;
    }
}
