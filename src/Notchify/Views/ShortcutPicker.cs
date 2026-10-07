using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Notchify.Core;
using Notchify.Services;
using static Notchify.Views.ViewKit;

namespace Notchify.Views;

/// <summary>
/// "Type to add" panel for the Launcher and the Shelf: type an app, a folder (Downloads, Documents…), a drive or
/// a path and press Enter. Browse… still picks any file.
/// </summary>
public sealed class ShortcutPicker : Border
{
    private const int MaxResults = 60;
    private readonly TextBox _search = new() { Tag = "Type an app, a folder like Downloads, or a path…" };
    private readonly ListBox _results = new() { Margin = new Thickness(0, 8, 0, 0) };
    private List<ShortcutTarget> _apps = new();
    private bool _loading = true;

    /// <summary>Something was chosen. Close the panel afterwards with <see cref="Hide"/>.</summary>
    public event Action<ShortcutTarget>? Picked;

    public ShortcutPicker()
    {
        Visibility = Visibility.Collapsed;
        Padding = new Thickness(10);
        CornerRadius = new CornerRadius(12);
        SetResourceReference(BackgroundProperty, "PanelBrush");

        var close = IconButton(Glyphs.Close, "Close", (_, _) => Hide());
        var browse = Chip("Browse…", (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Apps and files|*.exe;*.lnk;*.bat;*.cmd;*.url|All files|*.*" };
            if (dlg.ShowDialog() == true)
                Pick(new ShortcutTarget(System.IO.Path.GetFileNameWithoutExtension(dlg.FileName), dlg.FileName, "File"));
        });
        var top = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(browse, Dock.Right);
        top.Children.Add(close);
        top.Children.Add(browse);
        top.Children.Add(_search);
        var panel = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        panel.Children.Add(top);
        panel.Children.Add(_results);
        Child = panel;

        _search.TextChanged += (_, _) => Refresh();
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _results.Items.Count > 0) { Pick((ShortcutTarget)((ListBoxItem)_results.Items[_results.SelectedIndex < 0 ? 0 : _results.SelectedIndex]).Tag); e.Handled = true; }
            else if (e.Key == Key.Down && _results.Items.Count > 0) { _results.SelectedIndex = Math.Min(_results.Items.Count - 1, _results.SelectedIndex + 1); e.Handled = true; }
            else if (e.Key == Key.Up && _results.Items.Count > 0) { _results.SelectedIndex = Math.Max(0, _results.SelectedIndex - 1); e.Handled = true; }
            else if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
        };
        _results.MouseDoubleClick += (_, _) => { if (_results.SelectedItem is ListBoxItem { Tag: ShortcutTarget t }) Pick(t); };
        _results.KeyDown += (_, e) => { if (e.Key == Key.Enter && _results.SelectedItem is ListBoxItem { Tag: ShortcutTarget t }) Pick(t); };
    }

    public void Show()
    {
        Visibility = Visibility.Visible;
        _search.Clear();
        Refresh();
        Dispatcher.BeginInvoke(() => _search.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        if (_apps.Count == 0)
        {
            _loading = true;
            ShortcutCatalog.AppsAsync().ContinueWith(t => Ui.Post(() =>
            {
                _apps = t.Result;
                _loading = false;
                if (IsVisible) Refresh();
            }));
        }
        else _loading = false;
    }

    public void Hide() => Visibility = Visibility.Collapsed;

    private void Pick(ShortcutTarget target)
    {
        Picked?.Invoke(target);
        Hide();
    }

    private void Refresh()
    {
        _results.Items.Clear();
        var found = ShortcutCatalog.Search(_apps, _search.Text);
        foreach (var t in found.Take(MaxResults)) _results.Items.Add(Row(t));
        if (_loading)
            _results.Items.Add(new ListBoxItem { IsEnabled = false, Content = Text("Loading installed apps…", "Caption") });
        else if (found.Count == 0)
            _results.Items.Add(new ListBoxItem { IsEnabled = false, Content = Text("Nothing found. Paste a full path, or use Browse…", "Caption") });
        if (_results.Items.Count > 0 && _results.Items[0] is ListBoxItem { IsEnabled: true }) _results.SelectedIndex = 0;
    }

    private static ListBoxItem Row(ShortcutTarget t)
    {
        var icon = new Image { Width = 20, Height = 20, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        // App icons take a moment each; load them once the row is on screen.
        icon.Loaded += (_, _) => { if (icon.Source == null) icon.Source = ShortcutCatalog.Icon(t); };
        var name = Text(t.Name, "Body");
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.VerticalAlignment = VerticalAlignment.Center;
        var kind = Text(t.Kind == "Folder" || t.Kind == "File" ? $"{t.Kind} · {t.Path}" : t.Kind, "Caption");
        kind.TextTrimming = TextTrimming.CharacterEllipsis;
        kind.VerticalAlignment = VerticalAlignment.Center;
        kind.Margin = new Thickness(10, 0, 0, 0);
        kind.MaxWidth = 260;
        var row = Columns((icon, Auto), (name, Auto), (kind, Star()));
        return new ListBoxItem { Content = row, Tag = t, ToolTip = t.IsApp ? t.Name : t.Path };
    }
}
