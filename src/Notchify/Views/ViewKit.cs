using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Notchify.Views;

/// <summary>Small helpers for views that are built in code rather than XAML.</summary>
public static class ViewKit
{
    public static Style S(string key) => (Style)Application.Current.FindResource(key);

    public static TextBlock Text(string text, string style = "Body", double? size = null)
    {
        var t = new TextBlock { Text = text, Style = S(style) };
        if (size.HasValue) t.FontSize = size.Value;
        return t;
    }

    public static TextBlock Icon(string glyph, double size = 14, Brush? color = null)
    {
        var t = new TextBlock { Text = glyph, Style = S("Icon"), FontSize = size };
        if (color != null) t.Foreground = color;
        return t;
    }

    public static Button IconButton(string glyph, string tip, RoutedEventHandler click)
    {
        var b = new Button { Style = S("IconButton"), Content = glyph, ToolTip = tip };
        b.Click += click;
        return b;
    }

    public static Button Chip(string label, RoutedEventHandler click, string? glyph = null, bool accent = false)
    {
        object content = label;
        if (glyph != null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = new TextBlock { Text = glyph, Style = S("Icon"), FontSize = 12, Margin = new Thickness(0, 0, 6, 0) };
            // Setting Foreground to null would make the icon invisible; only override it on accent buttons.
            if (accent) icon.Foreground = Brushes.White;
            else icon.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) });
            sp.Children.Add(icon);
            sp.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            content = sp;
        }
        var b = new Button { Style = S(accent ? "AccentButton" : "ChipButton"), Content = content, Margin = new Thickness(0, 0, 6, 0) };
        b.Click += click;
        return b;
    }

    public static CheckBox Switch(string label, bool value, Action<bool> changed, string? tip = null)
    {
        var c = new CheckBox { Style = S("Switch"), Content = label, IsChecked = value, ToolTip = tip };
        c.Click += (_, _) => changed(c.IsChecked == true);
        return c;
    }

    public static Border Card(UIElement child, Thickness? margin = null) =>
        new() { Style = S("Card"), Child = child, Margin = margin ?? new Thickness(0, 0, 0, 8) };

    /// <summary>Header + content in a scrollable column.</summary>
    public static ScrollViewer Scroll(UIElement content) =>
        new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

    public static Grid Columns(params (UIElement el, GridLength width)[] cols)
    {
        var g = new Grid();
        for (var i = 0; i < cols.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = cols[i].width });
            Grid.SetColumn(cols[i].el, i);
            g.Children.Add(cols[i].el);
        }
        return g;
    }

    public static GridLength Star(double v = 1) => new(v, GridUnitType.Star);
    public static GridLength Auto => GridLength.Auto;
    public static GridLength Px(double v) => new(v);
}
