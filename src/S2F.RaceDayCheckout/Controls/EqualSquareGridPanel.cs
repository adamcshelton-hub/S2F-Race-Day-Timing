using System.Windows;
using System.Windows.Controls;

namespace S2F.RaceDayCheckout.Controls;

/// <summary>
/// Fixed-column grid of equal <b>square</b> cells that fit the available area (no scroll).
/// Extra space stays empty (top-left packed) so one clock stays square instead of stretching tall.
/// </summary>
public sealed class EqualSquareGridPanel : Panel
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns),
        typeof(int),
        typeof(EqualSquareGridPanel),
        new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange),
        v => v is int i && i >= 1);

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = InternalChildren.Count;
        if (count == 0)
            return new Size(0, 0);

        var cols = Columns;
        var rows = (count + cols - 1) / cols;
        var finiteW = !double.IsInfinity(availableSize.Width) && availableSize.Width > 0;
        var finiteH = !double.IsInfinity(availableSize.Height) && availableSize.Height > 0;

        double side;
        if (finiteW && finiteH)
            side = Math.Min(availableSize.Width / cols, availableSize.Height / rows);
        else if (finiteW)
            side = availableSize.Width / cols;
        else if (finiteH)
            side = availableSize.Height / rows;
        else
            side = 200;

        if (side < 0)
            side = 0;

        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(side, side));

        // Report the packed square grid size (not the full available height) so parents
        // that size-to-content don't reserve a tall empty band — arrange still fits in finalSize.
        return new Size(side * cols, side * rows);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = InternalChildren.Count;
        if (count == 0)
            return finalSize;

        var cols = Columns;
        var rows = (count + cols - 1) / cols;
        var side = Math.Min(finalSize.Width / cols, finalSize.Height / rows);
        if (side < 0)
            side = 0;

        // Pack top-left so the first clock sits under Add New Clock (not vertically centered).
        for (var i = 0; i < count; i++)
        {
            var col = i % cols;
            var row = i / cols;
            InternalChildren[i].Arrange(new Rect(col * side, row * side, side, side));
        }

        return finalSize;
    }
}
