using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Notchify.Core;
using Notchify.Modules;
using Notchify.Services;

namespace Notchify.Views;

public partial class ShelfView : UserControl
{
    private Point _dragStart;

    public ShelfView()
    {
        InitializeComponent();
        Tiles.ItemsSource = Notch.Shelf.Items;
        Notch.Shelf.Items.CollectionChanged += (_, _) => UpdateEmpty();
        Drop += OnDrop;
        UpdateEmpty();
    }

    private void UpdateEmpty() => Empty.Visibility = Notch.Shelf.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            Notch.Shelf.Add(files);
            e.Handled = true;
        }
    }

    private List<ShelfItem> Selected() =>
        Tiles.SelectedItems.Count > 0 ? Tiles.SelectedItems.Cast<ShelfItem>().ToList() : Notch.Shelf.Items.ToList();

    // ----- drag out -----

    private void Tiles_MouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);

    private void Tiles_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(null) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = (e.OriginalSource as FrameworkElement)?.DataContext as ShelfItem;
        if (item == null) return;
        var items = Tiles.SelectedItems.Contains(item) ? Tiles.SelectedItems.Cast<ShelfItem>().ToList() : new List<ShelfItem> { item };
        var paths = items.Where(i => i.Exists).Select(i => i.Path).ToArray();
        if (paths.Length == 0) return;
        var data = new DataObject(DataFormats.FileDrop, paths);
        DragDrop.DoDragDrop(Tiles, data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    private void Tiles_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Tiles.SelectedItem is ShelfItem i) Ui.OpenUrl(i.Path);
    }

    // ----- toolbar -----

    private async void Zip_Click(object sender, RoutedEventArgs e)
    {
        var items = Selected();
        if (items.Count == 0) return;
        try
        {
            var path = await Notch.Shelf.ZipAsync(items);
            if (path != null) Notch.Hub.Notify(Glyphs.Package, "Zipped", Path.GetFileName(path), Ui.Green, IslandPriority.Low);
        }
        catch (Exception ex) { Notch.Hub.Notify(Glyphs.Warning, "Zip failed", ex.Message, Ui.Red); }
    }

    private async void Unzip_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Selected().Where(i => i.IsZip).ToList())
        {
            try
            {
                var dir = await Notch.Shelf.UnzipAsync(item);
                if (dir != null) Notch.Hub.Notify(Glyphs.Folder, "Unzipped", Path.GetFileName(dir), Ui.Green, IslandPriority.Low);
            }
            catch (Exception ex) { Notch.Hub.Notify(Glyphs.Warning, "Unzip failed", ex.Message, Ui.Red); }
        }
    }

    private void Convert_Click(object sender, RoutedEventArgs e)
    {
        ConvertButton.ContextMenu.PlacementTarget = ConvertButton;
        ConvertButton.ContextMenu.IsOpen = true;
    }

    private async void ConvertTo_Click(object sender, RoutedEventArgs e)
    {
        var format = (string)((MenuItem)sender).Tag;
        var items = Selected().Where(i => i.IsImage || i.IsPdf).ToList();
        if (items.Count == 0) { Notch.Hub.Notify(Glyphs.Info, "Select an image or PDF first", null, Ui.Gray); return; }
        var count = 0;
        foreach (var item in items)
        {
            try { count += (await Notch.Shelf.ConvertAsync(item, format)).Count; }
            catch (Exception ex)
            {
                var hint = item.Path.EndsWith(".heic", StringComparison.OrdinalIgnoreCase)
                    ? "Install the free “HEIF Image Extensions” from the Microsoft Store." : ex.Message;
                Notch.Hub.Notify(Glyphs.Warning, $"Couldn't convert {item.Name}", hint, Ui.Red, IslandPriority.Normal, 6);
            }
        }
        if (count > 0) Notch.Hub.Notify(Glyphs.Photo, $"Converted to {format.ToUpperInvariant()}", $"{count} file(s) on the shelf", Ui.Green, IslandPriority.Low);
    }

    private void Capture_Click(object sender, RoutedEventArgs e) => ToolsModule.ScreenCapture();
    private void Clear_Click(object sender, RoutedEventArgs e) => Notch.Shelf.Clear();

    private ShortcutPicker? _picker;

    /// <summary>Type an app, a folder (Downloads…), a drive or a path. Folders and files go on as they are; apps as a shortcut.</summary>
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_picker == null)
        {
            _picker = new ShortcutPicker();
            _picker.Picked += t =>
            {
                if (!t.IsApp) { Notch.Shelf.Add(new[] { t.Path }); return; }
                // The shelf holds files, so an app goes on as a shortcut file (with the app's own icon).
                var link = ShortcutCatalog.CreateShortcutFile(t, Path.Combine(Paths.Shelf, "Shortcuts"));
                if (link == null) return;
                Notch.Shelf.Add(new[] { link });
                if (Notch.Shelf.Items.FirstOrDefault(i => string.Equals(i.Path, link, StringComparison.OrdinalIgnoreCase)) is { } item &&
                    ShortcutCatalog.SaveIcon(t) is { } icon)
                    Notch.Shelf.SetIcon(item, icon);
            };
            PickerHost.Content = _picker;
        }
        _picker.Show();
    }

    // ----- context menu -----

    private static ShelfItem? Item(object sender) => (sender as FrameworkElement)?.DataContext as ShelfItem;
    private void Open_Click(object sender, RoutedEventArgs e) { if (Item(sender) is { } i) Ui.OpenUrl(i.Path); }
    private void Reveal_Click(object sender, RoutedEventArgs e) { if (Item(sender) is { } i) Ui.RevealInExplorer(i.Path); }
    private void CopyPath_Click(object sender, RoutedEventArgs e) { if (Item(sender) is { } i) Notch.Clipboard.SetTextSilently(i.Path); }
    private void Remove_Click(object sender, RoutedEventArgs e) { if (Item(sender) is { } i) Notch.Shelf.Remove(i); }
    private void ChangeIcon_Click(object sender, RoutedEventArgs e) { if (Item(sender) is { } i && Ui.PickIcon() is { } icon) Notch.Shelf.SetIcon(i, icon); }
    private void ResetIcon_Click(object sender, RoutedEventArgs e) { if (Item(sender) is { } i) Notch.Shelf.SetIcon(i, null); }
}
