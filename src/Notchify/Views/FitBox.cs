using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Notchify.Views;

/// <summary>
/// Makes a Home card's content follow the card's size. The content is laid out at the card's real width (so text
/// wraps or trims instead of running off the edge); if it's still taller than the card, everything is scaled down
/// evenly — re-laid out wider at the smaller scale — until it fits. Very narrow cards lay out at a readable minimum
/// width and scale to fit. Cards with their own scrolling list keep scrolling instead of shrinking.
/// </summary>
public sealed class FitBox : Decorator
{
    private double _scale = 1;

    public FitBox() => ClipToBounds = true;

    /// <summary>Narrower than this, the content is laid out at this width and scaled down.</summary>
    public double MinContentWidth { get; set; } = 130;

    /// <summary>Never shrink below this, so text stays readable; past it, long text trims with "…" instead.</summary>
    public double MinScale { get; set; } = 0.8;

    /// <summary>The content scrolls by itself (a list): only the narrow-width rule applies.</summary>
    public bool Scrolls { get; set; }

    protected override Size MeasureOverride(Size available)
    {
        var child = Child;
        if (child == null) return new Size();
        if (double.IsInfinity(available.Width) || double.IsInfinity(available.Height) || available.Width <= 0 || available.Height <= 0)
        {
            _scale = 1;
            child.Measure(available);
            return child.DesiredSize;
        }

        var w = available.Width;
        var h = available.Height;
        var widest = Math.Clamp(w / MinContentWidth, MinScale, 1);
        var s = widest;
        if (!Scrolls && !Fits(child, w, h, s))
        {
            // Largest scale that fits. Smaller scale = wider layout = less wrapping, so this isn't linear; bisect.
            double lo = MinScale, hi = s;
            for (var i = 0; i < 8; i++)
            {
                var mid = (lo + hi) / 2;
                if (Fits(child, w, h, mid)) lo = mid; else hi = mid;
            }
            s = lo;
        }
        _scale = s;
        // Still too tall at the smallest scale: lay it out at its full height so the top shows and only the
        // bottom is cut, instead of centred content losing both ends.
        _height = h / s;
        if (!Scrolls)
        {
            child.Measure(new Size(w / s, double.PositiveInfinity));
            _height = Math.Max(_height, child.DesiredSize.Height);
        }
        child.Measure(new Size(w / s, _height));
        return new Size(w, h);
    }

    private double _height;

    private static bool Fits(UIElement child, double w, double h, double s)
    {
        child.Measure(new Size(w / s, double.PositiveInfinity));
        return child.DesiredSize.Height * s <= h + 0.5;
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (Child is { } child)
        {
            child.Arrange(new Rect(0, 0, final.Width / _scale, Math.Max(final.Height / _scale, _height)));
            child.RenderTransform = _scale < 0.999 ? new ScaleTransform(_scale, _scale) : Transform.Identity;
        }
        return final;
    }

    // Cards whose list only ever has a few rows: shrink to show them all, like any other card.
    private static readonly HashSet<string> ShortLists = new() { "world", "screentime", "battery" };

    /// <summary>Put a FitBox inside a card (a Border), once.</summary>
    public static void Wrap(FrameworkElement card, string? type)
    {
        if (card is not Border { Child: FrameworkElement content } border || content is FitBox) return;
        border.Child = null;
        var shortList = type != null && ShortLists.Contains(type);
        border.Child = new FitBox { Child = content, Scrolls = !shortList && HasList(content) };
        TrimText(content);
    }

    /// <summary>One-line text that runs out of room ends in "…" rather than being sliced mid-letter.</summary>
    private static void TrimText(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is TextBlock { TextTrimming: TextTrimming.None, TextWrapping: TextWrapping.NoWrap } t &&
                t.ReadLocalValue(TextBlock.TextTrimmingProperty) == DependencyProperty.UnsetValue)
                t.TextTrimming = TextTrimming.CharacterEllipsis;
            TrimText(child);
        }
    }

    private static bool HasList(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is ScrollViewer || (child is ItemsControl and not ComboBox and not MenuItem)) return true;
            if (HasList(child)) return true;
        }
        return false;
    }
}
