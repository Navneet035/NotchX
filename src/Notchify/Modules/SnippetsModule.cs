using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Notchify.Core;
using Notchify.Services;
using static Notchify.Views.ViewKit;

namespace Notchify.Modules;

public sealed class Snippet
{
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>Text snippets pasted from the command palette — the clipboard history is left untouched.</summary>
public sealed class SnippetsModule : NotchModule
{
    public ObservableCollection<Snippet> Snippets { get; } = new();

    public override string Id => "snippets";
    public override string Title => "Snippets";
    public override string Glyph => Glyphs.Text;
    public override string Description => "Reusable addresses, replies and code blocks, pasted from the command palette.";

    protected override void Start()
    {
        Snippets.Clear();
        foreach (var s in JsonStore.Load<List<Snippet>>("snippets")) Snippets.Add(s);
        if (Snippets.Count == 0)
            Snippets.Add(new Snippet { Name = "Today's date", Text = "{date}" });
    }

    private void Save() => JsonStore.Save("snippets", Snippets.ToList());

    private static string Expand(string text) => text
        .Replace("{date}", DateTime.Now.ToString("d"))
        .Replace("{time}", DateTime.Now.ToString("t"))
        .Replace("{clipboard}", Clipboard.ContainsText() ? Clipboard.GetText() : "");

    /// <summary>Paste a snippet into the previous app, then restore whatever text was on the clipboard.</summary>
    public static async void Paste(Snippet s)
    {
        string? previous = null;
        try { if (Clipboard.ContainsText()) previous = Clipboard.GetText(); } catch { }
        Notch.Clipboard.SetTextSilently(Expand(s.Text));
        await ClipboardService.PasteIntoPreviousAppAsync();
        await Task.Delay(400);
        if (previous != null) Notch.Clipboard.SetTextSilently(previous);
    }

    public override FrameworkElement CreateView()
    {
        var list = new ListBox { ItemsSource = Snippets, DisplayMemberPath = nameof(Snippet.Name) };
        var name = new TextBox { Tag = "Name" };
        var body = new TextBox { Tag = "Snippet text — {date}, {time} and {clipboard} are expanded", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top, MinHeight = 90, FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("MonoFont") };

        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is Snippet s) { name.Text = s.Name; body.Text = s.Text; }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(Chip("Save", (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) return;
            var existing = Snippets.FirstOrDefault(x => x.Name == name.Text);
            if (existing != null) existing.Text = body.Text;
            else Snippets.Add(new Snippet { Name = name.Text.Trim(), Text = body.Text });
            Save();
            list.Items.Refresh();
            Notch.Hub.Notify(Glyphs.Save, "Snippet saved", name.Text, Ui.Green, IslandPriority.Low, 1.2);
        }, Glyphs.Save, accent: true));
        buttons.Children.Add(Chip("New", (_, _) => { list.SelectedItem = null; name.Clear(); body.Clear(); name.Focus(); }, Glyphs.Add));
        buttons.Children.Add(Chip("Paste", (_, _) => { if (list.SelectedItem is Snippet s) Paste(s); }, Glyphs.Clipboard));
        buttons.Children.Add(Chip("Delete", (_, _) =>
        {
            if (list.SelectedItem is Snippet s) { Snippets.Remove(s); Save(); name.Clear(); body.Clear(); }
        }, Glyphs.Delete));

        var editor = new DockPanel();
        DockPanel.SetDock(name, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        editor.Children.Add(name);
        editor.Children.Add(buttons);
        body.Margin = new Thickness(0, 8, 0, 0);
        editor.Children.Add(body);
        return Columns((list, Star(0.8)), (new Border(), Px(14)), (editor, Star(1.4)));
    }

    public override IEnumerable<PaletteCommand> Commands =>
        Snippets.Select(s => new PaletteCommand($"Paste snippet: {s.Name}", Glyphs.Text, () => Paste(s), null, "snippet expand text " + s.Text));
}
