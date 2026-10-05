using System.Windows;
using System.Windows.Input;
using Notchify.Core;
using Notchify.Modules;

namespace Notchify.Shell;

/// <summary>
/// Global command palette: every Notchify action one keystroke away. Fuzzy-matches tabs,
/// module commands and snippets, and evaluates sums inline.
/// </summary>
public partial class CommandPalette : Window
{
    private static CommandPalette? _open;
    private List<PaletteCommand> _all = new();

    public static void Toggle()
    {
        if (_open != null) { _open.Close(); return; }
        _open = new CommandPalette();
        _open.Show();
        _open.Activate();
        _open.Query.Focus();
    }

    private CommandPalette()
    {
        InitializeComponent();
        PositionOnActiveScreen();
        _all = Notch.Modules.AllCommands().ToList();
        _all.Add(new PaletteCommand("Settings", Glyphs.Settings, () => Notch.Shell.ShowSettings(), null, "preferences options"));
        _all.Add(new PaletteCommand("Quit NotchX", Glyphs.Close, () => Application.Current.Shutdown(), null, "exit"));
        Filter("");

        Query.TextChanged += (_, _) => Filter(Query.Text);
        PreviewKeyDown += OnKey;
        // Closing deactivates the window, which would ask it to close again mid-close (and throw).
        var closing = false;
        Closing += (_, _) => closing = true;
        Deactivated += (_, _) => { if (!closing) Close(); };
        Closed += (_, _) => _open = null;
        Results.MouseUp += (_, _) => Run();
    }

    private void PositionOnActiveScreen()
    {
        Native.GetCursorPos(out var p);
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y));
        var source = PresentationSource.FromVisual(Application.Current.MainWindow);
        var scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        Left = screen.WorkingArea.Left / scale + (screen.WorkingArea.Width / scale - Width) / 2;
        Top = screen.WorkingArea.Top / scale + screen.WorkingArea.Height / scale * 0.18;
    }

    private void Filter(string q)
    {
        q = q.Trim();
        var items = new List<PaletteCommand>();

        if (q.Length > 0 && MathEval.TryEvaluate(q, out var value))
        {
            var text = MathEval.Format(value);
            items.Add(new PaletteCommand($"= {text}", Glyphs.Calculator, () => Notch.Clipboard.SetTextSilently(text), "Copy result"));
        }

        items.AddRange(_all
            .Select(c => (c, score: Score(c, q)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Select(x => x.c));

        if (q.Length > 1)
            items.Add(new PaletteCommand($"Search the web for “{q}”", Glyphs.Globe, () => SearchModule.Search(q, null), "Enter"));

        Results.ItemsSource = items.Take(60).ToList();
        if (Results.Items.Count > 0) Results.SelectedIndex = 0;
    }

    /// <summary>Subsequence fuzzy match with bonuses for prefix and word starts.</summary>
    private static int Score(PaletteCommand c, string q)
    {
        if (q.Length == 0) return 1;
        var hay = (c.Title + " " + c.Keywords).ToLowerInvariant();
        var needle = q.ToLowerInvariant();
        if (hay.StartsWith(needle)) return 1000;
        var idx = hay.IndexOf(needle, StringComparison.Ordinal);
        if (idx >= 0) return 800 - idx + (idx > 0 && hay[idx - 1] == ' ' ? 100 : 0);
        int score = 0, hi = 0;
        foreach (var ch in needle)
        {
            var found = hay.IndexOf(ch, hi);
            if (found < 0) return 0;
            score += found == hi ? 10 : 1;
            hi = found + 1;
        }
        return score;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); e.Handled = true; break;
            case Key.Down: Move(1); e.Handled = true; break;
            case Key.Up: Move(-1); e.Handled = true; break;
            case Key.Enter: Run(); e.Handled = true; break;
        }
    }

    private void Move(int d)
    {
        if (Results.Items.Count == 0) return;
        Results.SelectedIndex = Math.Clamp(Results.SelectedIndex + d, 0, Results.Items.Count - 1);
        Results.ScrollIntoView(Results.SelectedItem);
    }

    private void Run()
    {
        if (Results.SelectedItem is not PaletteCommand cmd) return;
        Close();
        try { cmd.Run(); }
        catch (Exception ex) { Log.Error("palette command", ex); }
    }
}
