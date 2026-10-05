using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Notchify.Core;
using Notchify.Services;
using static Notchify.Views.ViewKit;

namespace Notchify.Modules;

/// <summary>
/// Documents: Word → PDF, PDF → Word, and a PDF editor (reorder, rotate, delete, merge, extract, split,
/// add text / watermark / page numbers, password). Drop files on a tile or click it to choose.
/// </summary>
public sealed class DocumentsModule : NotchModule
{
    private const string Store = "documents";

    public override string Id => "documents";
    public override string Title => "Documents";
    public override string ShortTitle => "Docs";
    public override string Glyph => Glyphs.Document;
    public override string Description => "Convert Word ⇄ PDF and edit PDFs: reorder, rotate, delete, merge, split, add text, page numbers and a password.";

    /// <summary>Files made here, newest first.</summary>
    public ObservableCollection<string> Recent { get; } = new();

    /// <summary>Set by palette commands so the view opens the right picker once it's shown.</summary>
    internal static Action<string>? PendingAction;

    protected override void Start()
    {
        Recent.Clear();
        foreach (var f in JsonStore.Load<List<string>>(Store).Where(File.Exists).Take(24)) Recent.Add(f);
    }

    internal void AddRecent(string path)
    {
        Recent.Remove(path);
        Recent.Insert(0, path);
        while (Recent.Count > 24) Recent.RemoveAt(Recent.Count - 1);
        JsonStore.Save(Store, Recent.ToList());
    }

    internal void RemoveRecent(string path)
    {
        Recent.Remove(path);
        JsonStore.Save(Store, Recent.ToList());
    }

    public override FrameworkElement CreateView() => new DocumentsView(this);

    public override IEnumerable<PaletteCommand> Commands => new[]
    {
        new PaletteCommand("Convert Word to PDF…", Glyphs.Document, () => Open("topdf"), null, "docx doc pdf export convert"),
        new PaletteCommand("Convert PDF to Word…", Glyphs.Document, () => Open("toword"), null, "docx pdf convert editable"),
        new PaletteCommand("Edit a PDF…", Glyphs.Edit, () => Open("edit"), null, "pdf merge split rotate pages watermark password"),
    };

    private void Open(string action)
    {
        PendingAction?.Invoke(action);
        Notch.Shell.OpenTab(Id);
    }
}

/// <summary>The Documents tab: converter tiles + recent files, or the PDF editor.</summary>
internal sealed class DocumentsView : Grid
{
    private const string PageDrag = "notchx/pdfpage";

    private readonly DocumentsModule _module;
    private readonly Grid _home = new();
    private readonly DockPanel _editor = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _engine = Text("", "Caption");
    private readonly WrapPanel _recent = new();

    // Editor parts
    private PdfEditSession? _session;
    private readonly TextBlock _docTitle = Text("", "Title", 13);
    private readonly TextBlock _docInfo = Text("", "Caption");
    private readonly TextBlock _status = Text("", "Caption");
    private readonly ListBox _pages = new() { SelectionMode = SelectionMode.Extended, AllowDrop = true };
    private readonly StackPanel _textRow = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
    private readonly StackPanel _passwordRow = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
    private readonly WrapPanel _extras = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly ToggleButton _numbers = new() { Style = S("IconToggle"), Content = "#", ToolTip = "Add page numbers (1 / 12) at the bottom", FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.Bold };

    public DocumentsView(DocumentsModule module)
    {
        _module = module;
        BuildHome();
        BuildEditor();
        Children.Add(_home);
        Children.Add(_editor);
        IsVisibleChanged += (_, _) => { if (IsVisible) RefreshEngine(); };
        DocumentsModule.PendingAction = a => Dispatcher.BeginInvoke(() => Pick(a), System.Windows.Threading.DispatcherPriority.Background);
    }

    // =====================================================================
    // Home: three tiles and the recent list
    // =====================================================================

    private void BuildHome()
    {
        var tiles = new UniformGrid { Columns = 3 };
        tiles.Children.Add(Tile("topdf", Glyphs.Document, "Word → PDF", "Drop .docx, .doc, .odt, .rtf, .txt, Excel or PowerPoint"));
        tiles.Children.Add(Tile("toword", Glyphs.Edit, "PDF → Word", "Drop a PDF to get an editable .docx"));
        tiles.Children.Add(Tile("edit", Glyphs.Crop, "Edit PDF", "Reorder, rotate, delete, merge, split, add text, password"));

        var clear = Chip("Clear", (_, _) => { foreach (var f in _module.Recent.ToList()) _module.RemoveRecent(f); }, null);
        var recentHeader = new DockPanel { Margin = new Thickness(0, 10, 0, 4) };
        DockPanel.SetDock(clear, Dock.Right);
        recentHeader.Children.Add(clear);
        recentHeader.Children.Add(Text("RECENT", "SectionHeader"));
        _module.Recent.CollectionChanged += (_, _) => RenderRecent();
        RenderRecent();

        var stack = new DockPanel();
        DockPanel.SetDock(tiles, Dock.Top);
        DockPanel.SetDock(_engine, Dock.Top);
        DockPanel.SetDock(recentHeader, Dock.Top);
        _engine.Margin = new Thickness(2, 6, 0, 0);
        _engine.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(tiles);
        stack.Children.Add(_engine);
        stack.Children.Add(recentHeader);
        stack.Children.Add(Scroll(_recent));
        _home.Children.Add(stack);
    }

    private readonly Dictionary<string, (TextBlock Sub, ProgressBar Bar, string Hint)> _tileParts = new();

    private Button Tile(string action, string glyph, string title, string hint)
    {
        var sub = Text(hint, "Caption", 10);
        sub.TextWrapping = TextWrapping.Wrap;
        sub.TextAlignment = TextAlignment.Center;
        var bar = new ProgressBar { IsIndeterminate = true, Height = 3, Margin = new Thickness(10, 6, 10, 0), Visibility = Visibility.Collapsed };
        var name = Text(title, "Title", 14);
        name.HorizontalAlignment = HorizontalAlignment.Center;
        name.Margin = new Thickness(0, 6, 0, 2);
        var b = new Button
        {
            Style = S("ChipButton"),
            Height = 108,
            Margin = new Thickness(0, 0, 8, 0),
            AllowDrop = true,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ToolTip = "Click to choose a file, or drop files here",
            Content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { Icon(glyph, 22), name, sub, bar } },
        };
        _tileParts[action] = (sub, bar, hint);
        b.Click += (_, _) => Pick(action);
        b.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        b.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) _ = Run(action, files);
            e.Handled = true;
        };
        return b;
    }

    private void RefreshEngine()
    {
        var engine = DocumentService.Engine();
        _engine.Text = engine == null
            ? "⚠ Word ⇄ PDF needs a converter. " + DocumentService.InstallHint + " Editing PDFs works without one."
            : $"Converting with {engine} · files stay on this PC · results go {(SettingsStore.Current.Documents.SaveTo == "Documents" ? "to Documents" : "next to the original")}";
        _engine.Foreground = engine == null ? Ui.Orange : (Brush)FindResource("SubtleTextBrush");
    }

    private void Pick(string action)
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = action != "edit",
            Filter = action == "topdf"
                ? "Documents|*.docx;*.doc;*.docm;*.dotx;*.odt;*.rtf;*.txt;*.xlsx;*.xls;*.ods;*.pptx;*.ppt;*.odp|All files|*.*"
                : "PDF|*.pdf",
        };
        Notch.Shell.KeepOpen = true; // the notch would otherwise close behind the dialog
        try { if (dlg.ShowDialog() == true) _ = Run(action, dlg.FileNames); }
        finally { Notch.Shell.KeepOpen = false; }
    }

    private async Task Run(string action, string[] files)
    {
        if (action == "edit")
        {
            var pdf = files.FirstOrDefault(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
            if (pdf == null) { Notch.Hub.Notify(Glyphs.Info, "Drop a PDF to edit it", null, Ui.Gray); return; }
            await OpenEditor(pdf);
            return;
        }

        var wanted = action == "topdf"
            ? files.Where(f => DocumentService.ToPdfExtensions.Contains(Path.GetExtension(f))).ToList()
            : files.Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToList();
        if (wanted.Count == 0)
        {
            Notch.Hub.Notify(Glyphs.Info, action == "topdf" ? "Drop a Word, Excel, PowerPoint or text file" : "Drop a PDF", null, Ui.Gray);
            return;
        }
        var (sub, bar, hint) = _tileParts[action];
        bar.Visibility = Visibility.Visible;
        Notch.Shell.KeepOpen = true;
        try
        {
            foreach (var f in wanted)
            {
                sub.Text = $"Converting {Path.GetFileName(f)}…";
                try
                {
                    var result = action == "topdf" ? await DocumentService.ToPdfAsync(f) : await DocumentService.ToWordAsync(f);
                    _module.AddRecent(result);
                    Announce(result, action == "topdf" ? "Converted to PDF" : "Converted to Word");
                }
                catch (Exception ex)
                {
                    Log.Error("document convert", ex);
                    Notch.Hub.Notify(Glyphs.Warning, $"Couldn't convert {Path.GetFileName(f)}", ex.Message, Ui.Red, IslandPriority.Normal, 7);
                }
            }
        }
        finally
        {
            sub.Text = hint;
            bar.Visibility = Visibility.Collapsed;
            Notch.Shell.KeepOpen = false;
        }
    }

    private static void Announce(string file, string title)
    {
        Notch.Hub.Show(new Island
        {
            Glyph = Glyphs.Document,
            Title = title,
            Message = Path.GetFileName(file),
            Image = Ui.FileIcon(file),
            Accent = Ui.Green,
            Duration = TimeSpan.FromSeconds(6),
            DragFiles = new[] { file },
            Actions =
            {
                new IslandAction("Open", () => Ui.OpenUrl(file), true),
                new IslandAction("Show in folder", () => Ui.RevealInExplorer(file), false, Glyphs.Folder),
            },
        });
    }

    private void RenderRecent()
    {
        _recent.Children.Clear();
        if (_module.Recent.Count == 0)
        {
            _recent.Children.Add(Text("Converted and edited files show up here. Drag them anywhere.", "Caption"));
            return;
        }
        foreach (var file in _module.Recent)
        {
            var f = file;
            var name = Text(Path.GetFileName(f), "Caption", 10);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.MaxWidth = 92;
            var tile = new StackPanel
            {
                Width = 96, Margin = new Thickness(0, 0, 6, 6), Background = Brushes.Transparent, Cursor = Cursors.Hand,
                ToolTip = f + "\nDrag out · double-click to open",
                Children = { new Image { Source = Ui.FileIcon(f), Width = 32, Height = 32, Margin = new Thickness(0, 2, 0, 4) }, name },
            };
            Point? pressed = null;
            tile.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2) { Ui.OpenUrl(f); e.Handled = true; return; }
                pressed = e.GetPosition(tile);
            };
            tile.MouseLeftButtonUp += (_, _) => pressed = null;
            tile.MouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || pressed is not { } start) return;
                var d = e.GetPosition(tile) - start;
                if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                pressed = null;
                if (File.Exists(f)) DragDrop.DoDragDrop(tile, new DataObject(DataFormats.FileDrop, new[] { f }), DragDropEffects.Copy);
            };
            var menu = new ContextMenu();
            void Item(string header, Action act) { var mi = new MenuItem { Header = header }; mi.Click += (_, _) => act(); menu.Items.Add(mi); }
            Item("Open", () => Ui.OpenUrl(f));
            Item("Show in folder", () => Ui.RevealInExplorer(f));
            if (f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) Item("Edit this PDF", () => _ = OpenEditor(f));
            Item("Put on the Shelf", () => Notch.Shelf.Add(new[] { f }));
            Item("Remove from list", () => _module.RemoveRecent(f));
            tile.ContextMenu = menu;
            _recent.Children.Add(tile);
        }
    }

    // =====================================================================
    // PDF editor
    // =====================================================================

    private void BuildEditor()
    {
        // Row 1: back, title, save
        var back = IconButton(Glyphs.Left, "Back to Documents", (_, _) => CloseEditor());
        var save = Chip("Save", async (_, _) => await Save(null), Glyphs.Save, accent: true);
        save.ToolTip = "Saves a new file next to the original (the original is never changed)";
        var saveAs = Chip("Save as…", async (_, _) =>
        {
            if (_session == null) return;
            var dlg = new SaveFileDialog { Filter = "PDF|*.pdf", FileName = Path.GetFileName(_session.DefaultSavePath()), InitialDirectory = Path.GetDirectoryName(_session.FilePath) };
            Notch.Shell.KeepOpen = true;
            try { if (dlg.ShowDialog() == true) await Save(dlg.FileName); }
            finally { Notch.Shell.KeepOpen = true; }
        }, null);
        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Children = { _docTitle, _docInfo } };
        var top = Columns((back, Auto), (titleStack, Star()), (_status, Auto), (saveAs, Auto), (save, Auto));
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(0, 0, 10, 0);

        // Row 2: page tools
        List<PdfPageItem> Selected() => _pages.SelectedItems.Cast<PdfPageItem>().ToList();
        bool Need(List<PdfPageItem> sel)
        {
            if (sel.Count > 0) return true;
            Hint("Select pages first (click, Ctrl+click, Shift+click or Ctrl+A)");
            return false;
        }
        Button Tool(string glyph, string tip, Action run) => IconButton(glyph, tip, (_, _) => { run(); UpdateInfo(); });
        var tools = new WrapPanel
        {
            Margin = new Thickness(0, 6, 0, 0),
            Children =
            {
                Tool("", "Rotate left", () => { var s = Selected(); if (Need(s)) _session!.Rotate(s, -90); }),
                Tool("", "Rotate right", () => { var s = Selected(); if (Need(s)) _session!.Rotate(s, 90); }),
                Tool(Glyphs.Delete, "Delete selected pages", DeleteSelected),
                Tool(Glyphs.Left, "Move earlier", () => { var s = Selected(); if (Need(s)) _session!.Move(s, -1); Reselect(s); }),
                Tool(Glyphs.Right, "Move later", () => { var s = Selected(); if (Need(s)) _session!.Move(s, 1); Reselect(s); }),
                Separator(),
                Chip("Extract selected", async (_, _) => { var s = Selected(); if (Need(s)) await Extract(s); }, Glyphs.Copy),
                Chip("Split into pages", async (_, _) => await Split(), Glyphs.Crop),
                Chip("Add PDF…", async (_, _) => await AddPdf(), Glyphs.Add),
                Chip("Add text…", (_, _) => Toggle(_textRow), Glyphs.Text),
                Chip("Password…", (_, _) => Toggle(_passwordRow), Glyphs.Lock),
                _numbers,
            },
        };
        // Mirror the left-rotate icon so the two arrows point opposite ways.
        ((Button)tools.Children[0]).RenderTransformOrigin = new Point(0.5, 0.5);
        ((Button)tools.Children[0]).RenderTransform = new ScaleTransform(-1, 1);
        foreach (FrameworkElement c in tools.Children) c.Margin = new Thickness(0, 0, 4, 4);
        _numbers.Click += (_, _) => { if (_session != null) { _session.PageNumbers = _numbers.IsChecked == true; _session.Dirty = true; RenderExtras(); } };

        // "Add text" row
        var text = new TextBox { Width = 220, Tag = "Text to add, e.g. CONFIDENTIAL" };
        var position = new ComboBox { ItemsSource = new[] { "Top", "Bottom", "Watermark" }, SelectedIndex = 2, Width = 110, Margin = new Thickness(6, 0, 6, 0) };
        var onlySelected = new CheckBox { Style = S("Switch"), Content = "Selected pages only", VerticalAlignment = VerticalAlignment.Center };
        var addText = Chip("Add", (_, _) =>
        {
            if (_session == null || string.IsNullOrWhiteSpace(text.Text)) return;
            var sel = onlySelected.IsChecked == true ? Selected().ToHashSet() : new HashSet<PdfPageItem>();
            if (onlySelected.IsChecked == true && sel.Count == 0) { Hint("Select pages first, or untick “Selected pages only”"); return; }
            _session.Stamps.Add(new PdfStamp(text.Text.Trim(), (string)position.SelectedItem, onlySelected.IsChecked == true, sel));
            _session.Dirty = true;
            text.Clear();
            _textRow.Visibility = Visibility.Collapsed;
            RenderExtras();
        }, Glyphs.Add, accent: true);
        addText.Margin = new Thickness(8, 0, 0, 0);
        _textRow.Children.Add(text);
        _textRow.Children.Add(position);
        _textRow.Children.Add(onlySelected);
        _textRow.Children.Add(addText);

        // Password row
        var pwd = new PasswordBox { Width = 200 };
        _passwordRow.Children.Add(Text("Password to open the PDF: ", "Caption"));
        ((FrameworkElement)_passwordRow.Children[0]).VerticalAlignment = VerticalAlignment.Center;
        _passwordRow.Children.Add(pwd);
        var setPwd = Chip("Set", (_, _) =>
        {
            if (_session == null) return;
            _session.Password = pwd.Password;
            _session.Dirty = true;
            pwd.Clear();
            _passwordRow.Visibility = Visibility.Collapsed;
            RenderExtras();
        }, Glyphs.Lock, accent: true);
        setPwd.Margin = new Thickness(8, 0, 0, 0);
        _passwordRow.Children.Add(setPwd);

        // Pages
        _pages.ItemsPanel = (ItemsPanelTemplate)System.Windows.Markup.XamlReader.Parse(
            "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><WrapPanel /></ItemsPanelTemplate>");
        _pages.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(@"
<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
  <StackPanel Width='74' Margin='2'>
    <Border Height='84' CornerRadius='6' Background='#18FFFFFF'>
      <Image Source='{Binding Thumbnail}' Stretch='Uniform' Margin='4'>
        <Image.LayoutTransform><RotateTransform Angle='{Binding Rotation}' /></Image.LayoutTransform>
      </Image>
    </Border>
    <TextBlock Text='{Binding Number}' HorizontalAlignment='Center' Style='{DynamicResource Caption}' Margin='0,2,0,0' />
  </StackPanel>
</DataTemplate>");
        ScrollViewer.SetHorizontalScrollBarVisibility(_pages, ScrollBarVisibility.Disabled);
        _pages.KeyDown += (_, e) => { if (e.Key == Key.Delete) { DeleteSelected(); UpdateInfo(); e.Handled = true; } };
        SetupPageDrag();

        var head = new StackPanel { Children = { top, tools, _textRow, _passwordRow, _extras } };
        DockPanel.SetDock(head, Dock.Top);
        _editor.Children.Add(head);
        _editor.Children.Add(_pages);
    }

    private static Border Separator() => new() { Width = 1, Height = 20, Background = (Brush)Application.Current.FindResource("DividerBrush"), Margin = new Thickness(4, 0, 6, 0) };

    private void Toggle(UIElement row)
    {
        var show = row.Visibility != Visibility.Visible;
        _textRow.Visibility = Visibility.Collapsed;
        _passwordRow.Visibility = Visibility.Collapsed;
        row.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Hint(string message) => Notch.Hub.Notify(Glyphs.Info, message, null, Ui.Gray, IslandPriority.Low, 2.5, "pdf-hint");

    private void Reselect(List<PdfPageItem> items)
    {
        _pages.SelectedItems.Clear();
        foreach (var i in items) _pages.SelectedItems.Add(i);
    }

    private void DeleteSelected()
    {
        if (_session == null) return;
        var sel = _pages.SelectedItems.Cast<PdfPageItem>().ToList();
        if (sel.Count == 0) { Hint("Select pages first (click, Ctrl+click, Shift+click or Ctrl+A)"); return; }
        if (sel.Count == _session.Pages.Count) { Hint("A PDF needs at least one page"); return; }
        _session.Delete(sel);
    }

    /// <summary>Drag a page onto another to move it there; drop PDFs from outside to add their pages.</summary>
    private void SetupPageDrag()
    {
        Point? pressed = null;
        PdfPageItem? pressedItem = null;
        _pages.PreviewMouseLeftButtonDown += (_, e) =>
        {
            pressed = e.GetPosition(_pages);
            pressedItem = (e.OriginalSource as FrameworkElement)?.DataContext as PdfPageItem;
        };
        _pages.PreviewMouseLeftButtonUp += (_, _) => { pressed = null; pressedItem = null; };
        _pages.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || pressed is not { } start || pressedItem == null) return;
            var d = e.GetPosition(_pages) - start;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var item = pressedItem;
            pressed = null;
            pressedItem = null;
            DragDrop.DoDragDrop(_pages, new DataObject(PageDrag, item), DragDropEffects.Move);
        };
        _pages.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(PageDrag) ? DragDropEffects.Move : e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        _pages.Drop += async (_, e) =>
        {
            e.Handled = true;
            if (_session == null) return;
            if (e.Data.GetData(PageDrag) is PdfPageItem dragged)
            {
                if ((e.OriginalSource as FrameworkElement)?.DataContext is PdfPageItem target && target != dragged)
                {
                    _session.MoveTo(dragged, target);
                    _pages.SelectedItem = dragged;
                    UpdateInfo();
                }
                return;
            }
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                foreach (var f in files.Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)))
                    await Append(f);
        };
    }

    private async Task OpenEditor(string pdf)
    {
        try
        {
            _status.Text = "Opening…";
            _session = await PdfEditSession.OpenAsync(pdf);
        }
        catch (Exception ex)
        {
            Notch.Hub.Notify(Glyphs.Warning, "Couldn't open the PDF", ex.Message, Ui.Red, IslandPriority.Normal, 7);
            return;
        }
        finally { _status.Text = ""; }
        _pages.ItemsSource = _session.Pages;
        _docTitle.Text = Path.GetFileName(pdf);
        _numbers.IsChecked = false;
        _textRow.Visibility = Visibility.Collapsed;
        _passwordRow.Visibility = Visibility.Collapsed;
        RenderExtras();
        UpdateInfo();
        _home.Visibility = Visibility.Collapsed;
        _editor.Visibility = Visibility.Visible;
        Notch.Shell.KeepOpen = true; // stay open while editing
    }

    private void CloseEditor()
    {
        if (_session is { Dirty: true } &&
            MessageBox.Show("Leave without saving your changes?", "Edit PDF", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _session = null;
        _pages.ItemsSource = null;
        _editor.Visibility = Visibility.Collapsed;
        _home.Visibility = Visibility.Visible;
        Notch.Shell.KeepOpen = false;
    }

    private void UpdateInfo()
    {
        if (_session == null) return;
        var sources = _session.Pages.Select(p => p.Source).Distinct().Count();
        _docInfo.Text = $"{_session.Pages.Count} page{(_session.Pages.Count == 1 ? "" : "s")}" + (sources > 1 ? $" from {sources} PDFs" : "") + (_session.Dirty ? " · unsaved changes" : "");
    }

    /// <summary>Chips for the text, page numbers and password that will be applied on save.</summary>
    private void RenderExtras()
    {
        _extras.Children.Clear();
        if (_session == null) return;
        Button Pending(string label, Action remove)
        {
            var b = Chip(label + "  ✕", (_, _) => { remove(); _session.Dirty = true; RenderExtras(); UpdateInfo(); }, null);
            b.ToolTip = "Applied when you save · click to remove";
            b.Margin = new Thickness(0, 0, 6, 4);
            return b;
        }
        foreach (var s in _session.Stamps.ToList())
            _extras.Children.Add(Pending($"“{s.Text}” · {s.Position.ToLowerInvariant()} · {(s.SelectedOnly ? $"{s.Pages.Count} page(s)" : "all pages")}", () => _session.Stamps.Remove(s)));
        if (_session.PageNumbers)
            _extras.Children.Add(Pending("Page numbers", () => { _session.PageNumbers = false; _numbers.IsChecked = false; }));
        if (!string.IsNullOrEmpty(_session.Password))
            _extras.Children.Add(Pending("Password protected", () => _session.Password = ""));
        UpdateInfo();
    }

    private async Task AddPdf()
    {
        var dlg = new OpenFileDialog { Filter = "PDF|*.pdf", Multiselect = true };
        Notch.Shell.KeepOpen = true;
        if (dlg.ShowDialog() != true) return;
        foreach (var f in dlg.FileNames) await Append(f);
    }

    private async Task Append(string pdf)
    {
        if (_session == null) return;
        try
        {
            _status.Text = "Adding pages…";
            await _session.AppendAsync(pdf);
        }
        catch (Exception ex) { Notch.Hub.Notify(Glyphs.Warning, "Couldn't add that PDF", ex.Message, Ui.Red, IslandPriority.Normal, 7); }
        finally { _status.Text = ""; UpdateInfo(); }
    }

    private async Task Save(string? target)
    {
        if (_session == null) return;
        target ??= _session.DefaultSavePath();
        try
        {
            _status.Text = "Saving…";
            await _session.SaveAsync(target);
            _session.Dirty = false;
            _module.AddRecent(target);
            Announce(target, "PDF saved");
        }
        catch (Exception ex)
        {
            Log.Error("pdf save", ex);
            Notch.Hub.Notify(Glyphs.Warning, "Couldn't save the PDF", ex.Message, Ui.Red, IslandPriority.Normal, 7);
        }
        finally { _status.Text = ""; UpdateInfo(); }
    }

    private async Task Extract(List<PdfPageItem> pages)
    {
        if (_session == null) return;
        var numbers = pages.Select(p => p.Number).OrderBy(n => n).ToList();
        var label = numbers.Count == 1 ? $"page {numbers[0]}" : $"pages {numbers.First()}-{numbers.Last()}";
        var target = _session.DefaultSavePath($" ({label})");
        try
        {
            _status.Text = "Extracting…";
            await _session.SaveAsync(target, pages);
            _module.AddRecent(target);
            Announce(target, $"Extracted {pages.Count} page{(pages.Count == 1 ? "" : "s")}");
        }
        catch (Exception ex) { Notch.Hub.Notify(Glyphs.Warning, "Couldn't extract pages", ex.Message, Ui.Red, IslandPriority.Normal, 7); }
        finally { _status.Text = ""; }
    }

    private async Task Split()
    {
        if (_session == null) return;
        try
        {
            _status.Text = "Splitting…";
            var dir = await _session.SplitAsync();
            Notch.Hub.Show(new Island
            {
                Glyph = Glyphs.Folder,
                Title = $"Split into {_session.Pages.Count} PDFs",
                Message = Path.GetFileName(dir),
                Accent = Ui.Green,
                Duration = TimeSpan.FromSeconds(6),
                Actions = { new IslandAction("Open folder", () => Ui.OpenUrl(dir), true, Glyphs.Folder) },
            });
        }
        catch (Exception ex) { Notch.Hub.Notify(Glyphs.Warning, "Couldn't split the PDF", ex.Message, Ui.Red, IslandPriority.Normal, 7); }
        finally { _status.Text = ""; }
    }
}
