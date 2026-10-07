using System.Windows;
using System.Windows.Controls;

namespace Notchify.Controls;

/// <summary>
/// Lays the tabs out in rows across the whole header. The first row stops short by <see cref="FirstRowReserve"/>
/// (the clock and buttons sit there); every row after it uses the full width, under the clock too.
/// </summary>
public sealed class TabFlowPanel : Panel
{
    public static readonly DependencyProperty FirstRowReserveProperty = DependencyProperty.Register(
        nameof(FirstRowReserve), typeof(double), typeof(TabFlowPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Width kept free at the right end of the first row.</summary>
    public double FirstRowReserve
    {
        get => (double)GetValue(FirstRowReserveProperty);
        set => SetValue(FirstRowReserveProperty, value);
    }

    public static readonly DependencyProperty ReserveHeightProperty = DependencyProperty.Register(
        nameof(ReserveHeight), typeof(double), typeof(TabFlowPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>
    /// How far down the reserved corner goes. Rows that start above it keep clear of it too — the edit-layout
    /// chips are shorter than the buttons there, so two rows of chips sit beside them.
    /// </summary>
    public double ReserveHeight
    {
        get => (double)GetValue(ReserveHeightProperty);
        set => SetValue(ReserveHeightProperty, value);
    }

    protected override Size MeasureOverride(Size available)
    {
        var any = new Size(double.PositiveInfinity, double.PositiveInfinity);
        foreach (UIElement child in InternalChildren) child.Measure(any);
        var (width, height) = Flow(available.Width, null);
        return new Size(double.IsInfinity(available.Width) ? width : available.Width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        Flow(final.Width, (child, rect) => child.Arrange(rect));
        return final;
    }

    /// <summary>Walk the children row by row; returns the widest row and the total height.</summary>
    private (double Width, double Height) Flow(double width, Action<UIElement, Rect>? place)
    {
        double x = 0, y = 0, rowHeight = 0, widest = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var size = child.DesiredSize;
            var limit = width - (y == 0 || y < ReserveHeight - 0.5 ? FirstRowReserve : 0);
            if (x > 0 && x + size.Width > limit)
            {
                y += rowHeight;
                x = 0;
                rowHeight = 0;
            }
            place?.Invoke(child, new Rect(x, y, size.Width, size.Height));
            x += size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
            widest = Math.Max(widest, x);
        }
        return (widest, y + rowHeight);
    }
}
