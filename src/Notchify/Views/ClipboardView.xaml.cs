using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Notchify.Core;
using Notchify.Services;

namespace Notchify.Views;

public partial class ClipboardView : UserControl
{
    private readonly ICollectionView _view;

    public ClipboardView()
    {
        InitializeComponent();
        _view = new CollectionViewSource { Source = Notch.Clipboard.Items }.View;
        // Pins first, then most recent.
        _view.SortDescriptions.Add(new SortDescription(nameof(ClipItem.Pinned), ListSortDirection.Descending));
        _view.SortDescriptions.Add(new SortDescription(nameof(ClipItem.Created), ListSortDirection.Descending));
        _view.Filter = o => o is ClipItem c && c.Matches(Search.Text);
        List.ItemsSource = _view;
        Notch.Clipboard.Items.CollectionChanged += (_, _) => UpdateEmpty();
        IsVisibleChanged += (_, _) => { if (IsVisible) { _view.Refresh(); UpdateEmpty(); } };
        UpdateEmpty();
    }

    private void UpdateEmpty() => Empty.Visibility = Notch.Clipboard.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => _view.Refresh();

    private static ClipItem? ItemOf(object sender) => (sender as FrameworkElement)?.Tag as ClipItem;

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } i)
        {
            Notch.Clipboard.Copy(i);
            Notch.Hub.Notify(Glyphs.Copy, "Copied", null, Ui.Accent, IslandPriority.Low, 1);
        }
    }

    private void Paste_Click(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } i) Notch.Clipboard.Paste(i); }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } i) return;
        Notch.Clipboard.TogglePin(i);
        _view.Refresh();
    }

    private void Delete_Click(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } i) Notch.Clipboard.Remove(i); }
    private void Smart_Click(object sender, RoutedEventArgs e) => ItemOf(sender)?.RunSmartAction();
    private void Clear_Click(object sender, RoutedEventArgs e) => Notch.Clipboard.Clear();

    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is ClipItem i) Notch.Clipboard.Paste(i);
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (List.SelectedItem is not ClipItem i) return;
        if (e.Key == Key.Enter) Notch.Clipboard.Paste(i);
        else if (e.Key == Key.Delete) Notch.Clipboard.Remove(i);
    }
}
