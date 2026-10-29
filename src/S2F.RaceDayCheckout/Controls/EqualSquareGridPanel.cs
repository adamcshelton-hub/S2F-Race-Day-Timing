using System.Windows;
using System.Windows.Controls;

namespace S2F.RaceDayCheckout.Controls;

/// <summary>
/// Equal <b>square</b> clock cells that auto-fit columns to the available width (no scroll).
/// Column count = how many <see cref="MaxSide"/> tiles fit across (min 1). Cards only shrink
/// after width drops below one full tile. Extra space stays empty (top-left packed).
/// </summary>
public sealed class EqualSquareGridPanel : Panel
{
    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns),
        typeof(int),
        typeof(EqualSquareGridPanel),
        new FrameworkPropertyMetadata(16, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange),
        v => v is int i && i >= 1);

    public static readonly DependencyProperty MaxSideProperty = DependencyProperty.Register(
        nameof(MaxSide),
        typeof(double),
        typeof(EqualSquareGridPanel),
        new FrameworkPropertyMetadata(260.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange),
        v => v is double d && d > 0);

    /// <summary>Upper bound on column count (auto layout never exceeds this).</summary>
    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    /// <summary>
    /// Preferred / max cell side. Drives how many columns fit; cells shrink only when a
    /// single column is narrower than this.
    /// </summary>
    public double MaxSide
    {
        get => (double)GetValue(MaxSideProperty);
        set => SetValue(MaxSideProperty, value);
    }

    private double PreferredSide()
    {
        var max = MaxSide;
        if (double.IsInfinity(max) || double.IsNaN(max) || max <= 0)
            return 260;
        return max;
    }

    private double CapSide(double side)
    {
        var max = PreferredSide();
        if (side > max)
            side = max;
        return side < 0 ? 0 : side;
    }

    /// <summary>
    /// Columns from width: floor(width / preferred), at least 1, at most child count / MaxColumns.
    /// When width &lt; preferred → 1 column (caller then shrinks the cell).
    /// </summary>
    private int ColumnsFor(double width, int count)
    {
        if (count <= 0)
            return 1;

        var preferred = PreferredSide();
        var byWidth = 1;
        if (!double.IsInfinity(width) && width > 0)
            byWidth = Math.Max(1, (int)Math.Floor(width / preferred));

        return Math.Min(count, Math.Min(MaxColumns, byWidth));
    }

    private static double SideFor(double width, double height, int cols, int rows)
    {
        var finiteW = !double.IsInfinity(width) && width > 0;
        var finiteH = !double.IsInfinity(height) && height > 0;

        if (finiteW && finiteH)
            return Math.Min(width / cols, height / rows);
        if (finiteW)
            return width / cols;
        if (finiteH)
            return height / rows;
        return 200;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = InternalChildren.Count;
        if (count == 0)
            return new Size(0, 0);

        var cols = ColumnsFor(availableSize.Width, count);
        var rows = (count + cols - 1) / cols;
        var side = CapSide(SideFor(availableSize.Width, availableSize.Height, cols, rows));

        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(side, side));

        // Packed grid size only — arrange still uses full finalSize; leftover stays empty.
        return new Size(side * cols, side * rows);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = InternalChildren.Count;
        if (count == 0)
            return finalSize;

        var cols = ColumnsFor(finalSize.Width, count);
        var rows = (count + cols - 1) / cols;
        var side = CapSide(SideFor(finalSize.Width, finalSize.Height, cols, rows));

        for (var i = 0; i < count; i++)
        {
            var col = i % cols;
            var row = i / cols;
            InternalChildren[i].Arrange(new Rect(col * side, row * side, side, side));
        }

        return finalSize;
    }
}
