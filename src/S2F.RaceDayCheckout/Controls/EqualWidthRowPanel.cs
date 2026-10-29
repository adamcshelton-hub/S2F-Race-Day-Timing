using System.Windows;
using System.Windows.Controls;

namespace S2F.RaceDayCheckout.Controls;

/// <summary>
/// Single horizontal row: children share width equally and stretch to the panel height.
/// Optional <see cref="MinChildWidth"/> keeps each column at least that wide (desired size).
/// </summary>
public sealed class EqualWidthRowPanel : Panel
{
    public static readonly DependencyProperty MinChildWidthProperty = DependencyProperty.Register(
        nameof(MinChildWidth),
        typeof(double),
        typeof(EqualWidthRowPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange),
        v => v is double d && d >= 0);

    public double MinChildWidth
    {
        get => (double)GetValue(MinChildWidthProperty);
        set => SetValue(MinChildWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = InternalChildren.Count;
        if (count == 0)
            return new Size(0, 0);

        var finiteWidth = !double.IsInfinity(availableSize.Width) && availableSize.Width > 0;
        var finiteHeight = !double.IsInfinity(availableSize.Height) && availableSize.Height > 0;
        var minTotal = MinChildWidth * count;
        var childWidth = finiteWidth
            ? Math.Max(availableSize.Width / count, MinChildWidth)
            : (MinChildWidth > 0 ? MinChildWidth : 200);
        var childHeight = finiteHeight ? availableSize.Height : availableSize.Height;

        double maxDesiredHeight = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(childWidth, childHeight));
            maxDesiredHeight = Math.Max(maxDesiredHeight, child.DesiredSize.Height);
        }

        var width = finiteWidth ? Math.Max(availableSize.Width, minTotal) : childWidth * count;
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
