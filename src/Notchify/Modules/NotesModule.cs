using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Notchify.Core;
using static Notchify.Views.ViewKit;

namespace Notchify.Modules;

public sealed class Note : ObservableObject
{
    private bool _done;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public bool Done { get => _done; set => Set(ref _done, value); }
}

/// <summary>Quick Notes: jot a thought from the notch. The newest open note rides on the pill until you tick it off.</summary>
public sealed class NotesModule : NotchModule
{
    public ObservableCollection<Note> Notes { get; } = new();

    public override string Id => "notes";
    public override string Title => "Notes";
    public override string Glyph => Glyphs.Note;
    public override string Description => "Jot a thought without switching apps; the latest note stays on the pill until it's done.";

    protected override void Start()
    {
        Notes.Clear();
        foreach (var n in JsonStore.Load<List<Note>>("notes")) Notes.Add(n);
        UpdatePill();
    }

    protected override void Stop() => Notch.Hub.Remove("note");

    internal void Save()
    {
        JsonStore.Save("notes", Notes.ToList());
        UpdatePill();
    }

    public void Add(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Notes.Insert(0, new Note { Text = text.Trim() });
        Save();
    }

    private void UpdatePill()
    {
        var top = Notes.FirstOrDefault(n => !n.Done);
        if (top == null) { Notch.Hub.Remove("note"); return; }
        Notch.Hub.Upsert("note", a =>
        {
            a.Glyph = Glyphs.Note;
            a.Accent = Ui.Yellow;
            a.Text = top.Text.Length > 28 ? top.Text[..28] + "…" : top.Text;
            a.Detail = top.Text;
            a.Priority = 15;
            a.OpenTab = Id;
        });
    }

    public override FrameworkElement CreateView()
    {
        var input = new TextBox { Tag = "Jot something down and press Enter…", AcceptsReturn = false };
        input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Add(input.Text);
            input.Clear();
            e.Handled = true;
        };

        var list = new ListBox { ItemsSource = Notes, Margin = new Thickness(0, 8, 0, 0) };
        list.ItemTemplate = BuildTemplate();

        var dock = new DockPanel();
        DockPanel.SetDock(input, Dock.Top);
        dock.Children.Add(input);
        dock.Children.Add(list);
        dock.IsVisibleChanged += (_, _) => { if (dock.IsVisible) input.Focus(); };
        return dock;
    }

    private DataTemplate BuildTemplate()
    {
        // Built with FrameworkElementFactory so the module stays a single file.
        var grid = new FrameworkElementFactory(typeof(DockPanel));
        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetValue(FrameworkElement.StyleProperty, S("Switch"));
        check.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding(nameof(Note.Done)) { Mode = System.Windows.Data.BindingMode.TwoWay });
        check.SetValue(DockPanel.DockProperty, Dock.Right);
        check.SetValue(FrameworkElement.ToolTipProperty, "Done");
        check.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((_, _) => Save()));
        var delete = new FrameworkElementFactory(typeof(Button));
        delete.SetValue(FrameworkElement.StyleProperty, S("IconButton"));
        delete.SetValue(ContentControl.ContentProperty, Glyphs.Delete);
        delete.SetValue(DockPanel.DockProperty, Dock.Right);
        delete.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((s, _) =>
        {
            if ((s as FrameworkElement)?.DataContext is Note n) { Notes.Remove(n); Save(); }
        }));
        var copy = new FrameworkElementFactory(typeof(Button));
        copy.SetValue(FrameworkElement.StyleProperty, S("IconButton"));
        copy.SetValue(ContentControl.ContentProperty, Glyphs.Copy);
        copy.SetValue(DockPanel.DockProperty, Dock.Right);
        copy.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((s, _) =>
        {
            if ((s as FrameworkElement)?.DataContext is Note n) Notch.Clipboard.SetTextSilently(n.Text);
        }));
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(FrameworkElement.StyleProperty, S("Body"));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Note.Text)));
        grid.AppendChild(check);
        grid.AppendChild(delete);
        grid.AppendChild(copy);
        grid.AppendChild(text);
        return new DataTemplate { VisualTree = grid };
    }

    public override IEnumerable<PaletteCommand> Commands => new[]
    {
        new PaletteCommand("New note", Glyphs.Note, () => Notch.Shell.OpenTab(Id), null, "jot write quick"),
        new PaletteCommand("Note: paste clipboard text as a note", Glyphs.Note, () =>
        {
            if (System.Windows.Clipboard.ContainsText()) Add(System.Windows.Clipboard.GetText());
        }, null, "jot"),
    };
}
