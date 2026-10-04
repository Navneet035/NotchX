using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Notchify.Core;
using Notchify.Modules;
using Notchify.Services;
using static Notchify.Views.ViewKit;

namespace Notchify.Views;

/// <summary>
/// Home: cards on a 12-column grid, in the user's order and sizes (settings.json → Behavior.HomeCards).
/// Two rows fill the open notch; more rows scroll. In edit-layout mode every card gets a frame to drag,
/// move, resize or remove it, and an "Add a card" tile opens the gallery.
/// </summary>
public partial class HomeView : UserControl
{
    private const string DragFormat = "notchify/card";
    private const double Gap = 10;

    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _bluetooth = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly Dictionary<string, FrameworkElement> _cards = new();
    private readonly List<(FrameworkElement Element, int Col, int Row, int Cols, int Rows)> _placed = new();
    private readonly List<ToggleButton> _toggles = new();
    private readonly List<ComboBox> _outputs = new();
    private string _arranged = "";
    private bool _syncingOutput;

    public HomeView()
    {
        InitializeComponent();
        DataContext = Notch.Modules.Get("home");

        // XAML-defined cards, keyed by their Tag (= card type).
        foreach (var card in Pool.Children.OfType<FrameworkElement>().ToList()) _cards[(string)card.Tag] = card;
        foreach (var card in _cards.Values) Collect(card);

        _clock.Tick += (_, _) => Tick();
        _bluetooth.Tick += (_, _) => { if (IsShown("bluetooth")) _ = Notch.Bluetooth.RefreshPairedAsync(); };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Tick();
                _clock.Start();
                _bluetooth.Start();
                if (IsShown("bluetooth")) _ = Notch.Bluetooth.RefreshPairedAsync();
                if (IsShown("volume") || IsShown("controls")) { Notch.Audio.RefreshDevices(); SyncOutputs(); }
            }
            else
            {
                _clock.Stop();
                _bluetooth.Stop();
                Gallery.Visibility = Visibility.Collapsed;
            }
        };

        Notch.Audio.Outputs.CollectionChanged += (_, _) => SyncOutputs();
        BtList.ItemsSource = Notch.Bluetooth.Paired;
        Notch.Bluetooth.Paired.CollectionChanged += (_, _) => UpdateBluetoothEmpty();
        UpdateBluetoothEmpty();

        Scroller.SizeChanged += (_, _) => Position();
        Scroller.PreviewMouseWheel += Scroller_PreviewMouseWheel;
        LayoutEditor.Changed += Arrange;
        LayoutEditor.AddCardRequested += ShowGallery;
        SettingsStore.Changed += () => { if (Signature() != _arranged) Arrange(); };
        Arrange();
    }

    /// <summary>Find the toggles and output pickers inside a card so they stay in sync.</summary>
    private void Collect(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is ToggleButton { Tag: string } t) _toggles.Add(t);
            if (child is ComboBox { Tag: "output" } c)
            {
                c.ItemsSource = Notch.Audio.Outputs;
                c.SelectionChanged += (_, _) =>
                {
                    if (!_syncingOutput && c.SelectedItem is AudioDevice d && !d.IsDefault) Notch.Audio.SetDefaultOutput(d.Id);
                };
                _outputs.Add(c);
            }
            Collect(child);
        }
    }

    private bool IsShown(string type) => _placed.Any(p => Equals(p.Element.Tag, type) || (p.Element is Grid g && Equals(g.Tag, type)));

    // ---------------- Layout ----------------

    private static string Signature() =>
        string.Join(",", SettingsStore.Current.Behavior.HomeCards.Select(c => $"{c.Type}:{c.Cols}x{c.Rows}")) + "|" + LayoutEditor.IsEditing;

    private static void Detach(FrameworkElement el)
    {
        switch (el.Parent)
        {
            case Panel p: p.Children.Remove(el); break;
            case Decorator d: d.Child = null; break;
        }
    }

    private FrameworkElement? Card(string type)
    {
        if (_cards.TryGetValue(type, out var el)) return el;
        try { el = HomeCards.Create(type); }
        catch (Exception ex) { Log.Error($"home card {type}", ex); el = null; }
        if (el == null) return null;
        el.Tag = type;
        _cards[type] = el;
        return el;
    }

    /// <summary>Rebuild which cards are shown (order, sizes, edit frames). Positions come from <see cref="Position"/>.</summary>
    private void Arrange()
    {
        _arranged = Signature();
        var editing = LayoutEditor.IsEditing;
        foreach (var el in _placed.Select(p => p.Element)) Detach(el);
        foreach (var w in _cards.Values) Detach(w);
        Host.Children.Clear();
        _placed.Clear();

        var cards = LayoutEditor.HomeCards();
        // In edit mode an "Add a card" tile joins the packing at the end.
        var packing = cards.ToList();
        if (editing) packing.Add(new HomeCard { Type = "+add", Cols = 2, Rows = 1 });
        foreach (var (card, col, row) in LayoutEditor.Pack(packing))
        {
            FrameworkElement? element;
            if (card.Type == "+add") element = AddTile();
            else
            {
                var widget = Card(card.Type);
                if (widget == null) continue;
                element = editing ? EditFrame(card, widget) : widget;
            }
            element.Tag ??= card.Type;
            Host.Children.Add(element);
            _placed.Add((element, col, row, Math.Clamp(card.Cols, 1, LayoutEditor.GridColumns), Math.Clamp(card.Rows, 1, LayoutEditor.MaxRows)));
        }
        // Cards not on Home go back to the pool (keeps their state for later).
        foreach (var el in _cards.Values)
            if (el.Parent == null) Pool.Children.Add(el);

        EmptyHint.Visibility = cards.Count == 0 && !editing ? Visibility.Visible : Visibility.Collapsed;
        if (!editing) Gallery.Visibility = Visibility.Collapsed;
        Position();
        Tick();
    }

    /// <summary>Grid units → pixels. Two rows fill the visible height; columns split the width twelve ways.</summary>
    private void Position()
    {
        var width = Scroller.ActualWidth;
        var height = Scroller.ActualHeight;
        if (width <= 0 || height <= 0) return;
        var rowsUsed = _placed.Count == 0 ? 0 : _placed.Max(p => p.Row + p.Rows);
        // Leave room for a scrollbar when the cards need more than two rows.
        if (rowsUsed > 2) width -= 8;
        var colW = (width - Gap * (LayoutEditor.GridColumns - 1)) / LayoutEditor.GridColumns;
        var rowH = Math.Max(70, (height - Gap) / 2);
        foreach (var (el, col, row, cols, rows) in _placed)
        {
            Canvas.SetLeft(el, col * (colW + Gap));
            Canvas.SetTop(el, row * (rowH + Gap));
            el.Width = Math.Max(10, cols * colW + (cols - 1) * Gap);
            el.Height = Math.Max(10, rows * rowH + (rows - 1) * Gap);
        }
        Host.Width = width;
        Host.Height = rowsUsed == 0 ? 0 : rowsUsed * rowH + (rowsUsed - 1) * Gap;
    }

    /// <summary>
    /// Cards have their own little lists. The wheel scrolls such a list while it can, and the Home page
    /// otherwise — instead of an empty list swallowing the wheel.
    /// </summary>
    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        static DependencyObject? Up(DependencyObject d) => d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        for (var d = e.OriginalSource as DependencyObject; d != null && d != Scroller; d = Up(d))
        {
            if (d is not ScrollViewer inner) continue;
            var canScroll = e.Delta < 0 ? inner.VerticalOffset < inner.ScrollableHeight - 0.5 : inner.VerticalOffset > 0.5;
            if (canScroll) return;
        }
        Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - e.Delta * 0.6);
        e.Handled = true;
    }

    // ---------------- Edit mode ----------------

    private FrameworkElement AddTile()
    {
        var b = new Button { Style = S("ChipButton"), ToolTip = "Add a card to Home", Cursor = Cursors.Hand };
        b.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { Icon(Glyphs.Add, 14), new TextBlock { Text = "Add a card", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center } },
        };
        b.BorderBrush = Ui.Accent;
        b.BorderThickness = new Thickness(1.5);
        b.Click += (_, _) => ShowGallery();
        return b;
    }

    private void ShowGallery()
    {
        GalleryItems.Children.Clear();
        var available = LayoutEditor.Available().ToList();
        if (available.Count == 0)
            GalleryItems.Children.Add(Text("Every card is already on Home.", "Caption"));
        foreach (var info in available)
        {
            var type = info.Type;
            var desc = Text(info.Description, "Caption", 10);
            desc.TextWrapping = TextWrapping.Wrap;
            var tile = new Button
            {
                Style = S("ChipButton"),
                Width = 180,
                Height = 64,
                Margin = new Thickness(0, 0, 6, 6),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Padding = new Thickness(8, 6, 8, 6),
                ToolTip = $"Add {info.Name} ({info.Cols}×{info.Rows})",
                Content = Columns((Icon(info.Glyph, 16), Px(28)), (new StackPanel { Children = { Text(info.Name, "Title", 12), desc } }, Star())),
            };
            tile.Click += (_, _) =>
            {
                Gallery.Visibility = Visibility.Collapsed;
                LayoutEditor.Add(type);
            };
            GalleryItems.Children.Add(tile);
        }
        Gallery.Visibility = Visibility.Visible;
    }

    private void GalleryClose_Click(object sender, RoutedEventArgs e) => Gallery.Visibility = Visibility.Collapsed;

    /// <summary>Edit-mode wrapper: the card underneath, with a frame on top to drag, move, resize or remove it.</summary>
    private FrameworkElement EditFrame(HomeCard card, FrameworkElement widget)
    {
        var type = card.Type;
        var info = LayoutEditor.Info(type)!;
        var cell = new Grid { AllowDrop = true, Tag = type };
        cell.Children.Add(widget);

        Button B(string glyph, string tip, Action run, bool enabled = true)
        {
            var b = IconButton(glyph, tip, (_, e) => { run(); e.Handled = true; });
            b.Width = 24;
            b.Height = 24;
            b.FontSize = 11;
            b.Margin = new Thickness(1);
            b.IsEnabled = enabled;
            b.Opacity = enabled ? 1 : 0.35;
            b.Background = (Brush)FindResource("CardHoverBrush");
            return b;
        }
        Button T(string label, string tip, Action run, bool enabled)
        {
            var b = B("", tip, run, enabled);
            b.Content = new TextBlock { Text = label, FontFamily = new FontFamily("Segoe UI"), FontSize = 11, FontWeight = FontWeights.SemiBold };
            b.Width = 30;
            return b;
        }
        var title = Text(info.Name, "Title", 11);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.VerticalAlignment = VerticalAlignment.Center;
        var size = Text($"{card.Cols}×{card.Rows}", "Caption", 10);
        size.VerticalAlignment = VerticalAlignment.Center;
        size.Margin = new Thickness(6, 0, 0, 0);
        var remove = B(Glyphs.Close, "Remove from Home", () => LayoutEditor.Remove(type));
        var top = Columns((title, Star()), (size, Auto), (remove, Auto));
        top.VerticalAlignment = VerticalAlignment.Top;

        WrapPanel Row(params UIElement[] buttons)
        {
            var p = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
            foreach (var b in buttons) p.Children.Add(b);
            return p;
        }
        var controls = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Row(B(Glyphs.Left, "Move earlier", () => LayoutEditor.Move(type, -1)),
                    B(Glyphs.Move, "Drag to swap with another card", () => { }),
                    B(Glyphs.Right, "Move later", () => LayoutEditor.Move(type, 1))),
                Row(T("W−", "Narrower", () => LayoutEditor.Resize(type, card.Cols - 1, card.Rows), card.Cols > info.MinCols),
                    T("W+", "Wider", () => LayoutEditor.Resize(type, card.Cols + 1, card.Rows), card.Cols < LayoutEditor.GridColumns),
                    T("H−", "Shorter", () => LayoutEditor.Resize(type, card.Cols, card.Rows - 1), card.Rows > info.MinRows),
                    T("H+", "Taller", () => LayoutEditor.Resize(type, card.Cols, card.Rows + 1), card.Rows < LayoutEditor.MaxRows)),
            },
        };
        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x12, 0x12, 0x16)),
            BorderBrush = Ui.Accent,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8, 4, 4, 6),
            Cursor = Cursors.SizeAll,
            ToolTip = "Drag onto another card to swap places",
            Child = new Grid { Children = { top, controls } },
        };

        Point? pressed = null;
        overlay.PreviewMouseLeftButtonDown += (_, e) => pressed = e.GetPosition(overlay);
        overlay.PreviewMouseLeftButtonUp += (_, _) => pressed = null;
        overlay.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || pressed is not { } start) return;
            var d = e.GetPosition(overlay) - start;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            pressed = null;
            DragDrop.DoDragDrop(overlay, new DataObject(DragFormat, type), DragDropEffects.Move);
        };
        cell.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        cell.Drop += (_, e) =>
        {
            if (e.Data.GetData(DragFormat) is string dragged) Dispatcher.BeginInvoke(() => LayoutEditor.MoveTo(dragged, type));
            e.Handled = true;
        };
        cell.Children.Add(overlay);
        return cell;
    }

    // ---------------- Live bits ----------------

    private void Tick()
    {
        Time.Text = DateTime.Now.ToString("t");
        Date.Text = DateTime.Now.ToString("dddd, d MMMM");
        foreach (var t in _toggles)
            t.IsChecked = (string)t.Tag switch
            {
                "caffeine" => Notch.KeepAwake.IsActive,
                "mic" => Notch.Audio.MicMuted,
                "hide" => SettingsStore.Current.Behavior.HideFromScreenCapture,
                _ => t.IsChecked,
            };
    }

    private void SyncOutputs()
    {
        _syncingOutput = true;
        try
        {
            var current = Notch.Audio.Outputs.FirstOrDefault(o => o.IsDefault);
            foreach (var c in _outputs) c.SelectedItem = current;
        }
        finally { _syncingOutput = false; }
    }

    private void UpdateBluetoothEmpty() =>
        BtEmpty.Visibility = Notch.Bluetooth.Paired.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---------------- Handlers ----------------

    private void Prev_Click(object sender, RoutedEventArgs e) => Notch.Media.Previous();
    private void Play_Click(object sender, RoutedEventArgs e) => Notch.Media.PlayPause();
    private void Next_Click(object sender, RoutedEventArgs e) => Notch.Media.Next();
    private void Caffeine_Click(object sender, RoutedEventArgs e) { Notch.KeepAwake.Toggle(); Tick(); }
    private void Mute_Click(object sender, RoutedEventArgs e) => Notch.Audio.ToggleMute();

    private void Lock_Click(object sender, RoutedEventArgs e)
    {
        Notch.Shell.Collapse();
        Native.LockWorkStation();
    }

    private void Mic_Click(object sender, RoutedEventArgs e)
    {
        Notch.Audio.MicMuted = !Notch.Audio.MicMuted;
        Tick();
        Notch.Hub.Notify(Notch.Audio.MicGlyph, Notch.Audio.MicMuted ? "Microphone muted" : "Microphone on", "All apps",
            Notch.Audio.MicMuted ? Ui.Red : Ui.Green, IslandPriority.Normal, 1.5, "mic");
    }

    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        SettingsStore.Current.Behavior.HideFromScreenCapture = !SettingsStore.Current.Behavior.HideFromScreenCapture;
        SettingsStore.NotifyChanged();
        Tick();
    }

    private void Capture_Click(object sender, RoutedEventArgs e) => ToolsModule.ScreenCapture();
    private void Eyedropper_Click(object sender, RoutedEventArgs e) => ToolsModule.Eyedropper();
    private void Palette_Click(object sender, RoutedEventArgs e) => Shell.CommandPalette.Toggle();

    private void BtDevice_Click(object sender, RoutedEventArgs e) => BluetoothService.OpenQuickConnect();
    private void BtSettings_Click(object sender, RoutedEventArgs e) => BluetoothService.OpenBluetoothSettings();
    private void BtRefresh_Click(object sender, RoutedEventArgs e) => _ = Notch.Bluetooth.RefreshPairedAsync();
}
