using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Notchify.Core;
using Notchify.Services;
using static Notchify.Views.ViewKit;

namespace Notchify.Modules;

/// <summary>Search & Translate: send a query to any engine, translate as you type, quick sums, save a video from a link.</summary>
public sealed class SearchModule : NotchModule
{
    public ObservableCollection<string> CalcHistory { get; } = new();

    public override string Id => "search";
    public override string Title => "Search";
    public override string Glyph => Glyphs.Search;
    public override string Description => "Search nine engines (or your own), live translation, a calculator with history, and video saving via yt-dlp.";

    public static void Search(string query, string? engineName)
    {
        var s = SettingsStore.Current.Search;
        var engine = s.Engines.FirstOrDefault(e => e.Name == (engineName ?? s.DefaultEngine)) ?? s.Engines.FirstOrDefault();
        if (engine == null || string.IsNullOrWhiteSpace(query)) return;
        Ui.OpenUrl(engine.Url.Replace("%s", Uri.EscapeDataString(query.Trim())));
        Notch.Shell.Collapse();
    }

    public static async Task<string?> TranslateAsync(string text, string target, CancellationToken ct)
    {
        var s = SettingsStore.Current.Search;
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (s.TranslateProvider == "libre")
        {
            var body = JsonSerializer.Serialize(new { q = text, source = "auto", target, format = "text", api_key = s.LibreTranslateKey });
            using var res = await Http.Client.PostAsync(s.LibreTranslateUrl, new StringContent(body, Encoding.UTF8, "application/json"), ct);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("translatedText", out var t) ? t.GetString() : null;
        }
        // Keyless public endpoint used by many open-source translators. Swap to LibreTranslate in Settings if you prefer.
        var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={Uri.EscapeDataString(target)}&dt=t&q={Uri.EscapeDataString(text)}";
        var json = await Http.Client.GetStringAsync(url, ct);
        using var d = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        foreach (var seg in d.RootElement[0].EnumerateArray())
            if (seg[0].ValueKind == JsonValueKind.String) sb.Append(seg[0].GetString());
        return sb.ToString();
    }

    public static bool LooksLikeVideoUrl(string s) =>
        Uri.TryCreate(s.Trim(), UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https") &&
        new[] { "youtube.com", "youtu.be", "vimeo.com", "x.com", "twitter.com", "instagram.com", "tiktok.com", "reddit.com", "twitch.tv", "dailymotion.com" }
            .Any(h => u.Host.EndsWith(h, StringComparison.OrdinalIgnoreCase));

    /// <summary>Saves a video with yt-dlp (must be on PATH or configured in Settings).</summary>
    public static void DownloadVideo(string url)
    {
        var exe = SettingsStore.Current.Search.VideoDownloader;
        var id = "video:" + Guid.NewGuid().ToString("N")[..6];
        try
        {
            var psi = new ProcessStartInfo(exe, $"--newline -o \"{Paths.Downloads}\\%(title)s.%(ext)s\" \"{url}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            var p = Process.Start(psi)!;
            Notch.Hub.Upsert(id, a => { a.Glyph = Glyphs.Download; a.Accent = Ui.Red; a.Text = "0%"; a.Progress = 0; a.Priority = 45; });
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                var m = System.Text.RegularExpressions.Regex.Match(e.Data, @"(\d+(?:\.\d+)?)%");
                if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                    Ui.Post(() => Notch.Hub.Upsert(id, a => { a.Text = $"{pct:0}%"; a.Progress = pct / 100; }));
            };
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) => Ui.Post(() =>
            {
                Notch.Hub.Remove(id);
                Notch.Hub.Notify(Glyphs.Download, p.ExitCode == 0 ? "Video saved" : "Video download failed",
                    p.ExitCode == 0 ? "In your Downloads folder" : "Check the link or update yt-dlp", p.ExitCode == 0 ? Ui.Green : Ui.Red);
            });
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }
        catch
        {
            Notch.Hub.Notify(Glyphs.Warning, "yt-dlp not found", "Install it (winget install yt-dlp) or set its path in Settings", Ui.Orange, IslandPriority.Normal, 6);
        }
    }

    public override FrameworkElement CreateView()
    {
        var s = SettingsStore.Current.Search;
        var box = new TextBox { Tag = "Search, translate, calculate — or paste a video link", FontSize = 15 };
        var engines = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var e in s.Engines)
        {
            var name = e.Name;
            var chip = Chip(name, (_, _) => Search(box.Text, name));
            chip.Margin = new Thickness(0, 0, 6, 6);
            if (name == s.DefaultEngine) chip.Style = S("AccentButton");
            engines.Children.Add(chip);
        }

        var calcResult = Text("", "Title", 18);
        var translation = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 56, VerticalContentAlignment = VerticalAlignment.Top, Tag = "Translation appears here" };
        CancellationTokenSource? cts = null;
        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        debounce.Tick += async (_, _) =>
        {
            debounce.Stop();
            cts?.Cancel();
            cts = new CancellationTokenSource();
            var text = box.Text;
            if (text.Length < 2 || MathEval.TryEvaluate(text, out _) || LooksLikeVideoUrl(text)) { translation.Text = ""; return; }
            try { translation.Text = await TranslateAsync(text, s.TranslateTo, cts.Token) ?? ""; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { translation.Text = "Translation unavailable: " + ex.Message; }
        };
        void Kick() { debounce.Stop(); debounce.Start(); }
        var langs = new ComboBox { Width = 90, ItemsSource = new[] { "en", "es", "fr", "de", "it", "pt", "nl", "pl", "tr", "ru", "uk", "ar", "hi", "pa", "ur", "bn", "zh-CN", "zh-TW", "ja", "ko", "vi", "th", "id" }, SelectedItem = s.TranslateTo };
        langs.SelectionChanged += (_, _) => { s.TranslateTo = (string)langs.SelectedItem; SettingsStore.Save(); Kick(); };
        var videoButton = Chip("Save video", (_, _) => DownloadVideo(box.Text), Glyphs.Download, accent: true);
        videoButton.Visibility = Visibility.Collapsed;

        var history = new ListBox { ItemsSource = CalcHistory, MaxHeight = 90 };
        history.MouseDoubleClick += (_, _) => { if (history.SelectedItem is string h) Notch.Clipboard.SetTextSilently(h.Split('=').Last().Trim()); };


        box.TextChanged += (_, _) =>
        {
            var t = box.Text.Trim();
            calcResult.Text = MathEval.TryEvaluate(t, out var v) ? "= " + MathEval.Format(v) : "";
            videoButton.Visibility = LooksLikeVideoUrl(t) ? Visibility.Visible : Visibility.Collapsed;
            Kick();
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            var t = box.Text.Trim();
            if (MathEval.TryEvaluate(t, out var v))
            {
                var r = MathEval.Format(v);
                CalcHistory.Insert(0, $"{t} = {r}");
                while (CalcHistory.Count > 20) CalcHistory.RemoveAt(CalcHistory.Count - 1);
                Notch.Clipboard.SetTextSilently(r);
                Notch.Hub.Notify(Glyphs.Calculator, r, "Copied", Ui.Accent, IslandPriority.Low, 1.2);
            }
            else if (LooksLikeVideoUrl(t)) DownloadVideo(t);
            else Search(t, Keyboard.Modifiers == ModifierKeys.Shift ? s.Engines.Skip(1).FirstOrDefault()?.Name : null);
            e.Handled = true;
        };

        var left = new StackPanel();
        left.Children.Add(box);
        left.Children.Add(engines);
        var calcRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(videoButton, Dock.Right);
        calcRow.Children.Add(videoButton);
        calcRow.Children.Add(calcResult);
        left.Children.Add(calcRow);
        left.Children.Add(history);

        var right = new DockPanel();
        var th = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(langs, Dock.Right);
        th.Children.Add(langs);
        th.Children.Add(Text("TRANSLATE", "SectionHeader"));
        DockPanel.SetDock(th, Dock.Top);
        right.Children.Add(th);
        right.Children.Add(translation);

        var view = Columns((left, Star(1.4)), (new Border(), Px(14)), (right, Star(1)));
        view.IsVisibleChanged += (_, _) => { if (view.IsVisible) { box.Focus(); box.SelectAll(); } };
        return view;
    }

    public override IEnumerable<PaletteCommand> Commands =>
        SettingsStore.Current.Search.Engines.Select(e => new PaletteCommand($"Search with {e.Name}", Glyphs.Globe, () => Notch.Shell.OpenTab(Id), null, "web find"));
}

/// <summary>Tiny calculator: + - * / ^ %, parentheses, sqrt sin cos tan log ln abs round, pi, e.</summary>
public static class MathEval
{
    public static string Format(double v) =>
        Math.Abs(v) >= 1e15 || (Math.Abs(v) < 1e-6 && v != 0) ? v.ToString("G10", CultureInfo.InvariantCulture) : Math.Round(v, 10).ToString("#,0.##########", CultureInfo.CurrentCulture);

    public static bool TryEvaluate(string input, out double value)
    {
        value = 0;
        var s = input.Replace(" ", "").Replace("×", "*").Replace("÷", "/").Replace(",", "");
        // Require at least one operator or function so plain numbers and words aren't treated as sums.
        if (s.Length == 0 || !s.Any(c => "+-*/^%(".Contains(c)) || s.All(char.IsLetter)) return false;
        try
        {
            var p = new Parser(s);
            value = p.ParseExpression();
            return p.AtEnd && !double.IsNaN(value) && !double.IsInfinity(value);
        }
        catch { return false; }
    }

    private sealed class Parser(string s)
    {
        private int _i;
        public bool AtEnd => _i >= s.Length;
        private char Peek => _i < s.Length ? s[_i] : '\0';

        public double ParseExpression()
        {
            var v = ParseTerm();
            while (Peek is '+' or '-') v = s[_i++] == '+' ? v + ParseTerm() : v - ParseTerm();
            return v;
        }

        private double ParseTerm()
        {
            var v = ParsePower();
            while (Peek is '*' or '/' or '%')
            {
                var op = s[_i++];
                var r = ParsePower();
                v = op == '*' ? v * r : op == '/' ? v / r : v % r;
            }
            return v;
        }

        private double ParsePower()
        {
            var v = ParseUnary();
            if (Peek == '^') { _i++; v = Math.Pow(v, ParsePower()); }
            return v;
        }

        private double ParseUnary()
        {
            if (Peek == '-') { _i++; return -ParseUnary(); }
            if (Peek == '+') { _i++; return ParseUnary(); }
            return ParsePrimary();
        }

        private double ParsePrimary()
        {
            if (Peek == '(')
            {
                _i++;
                var v = ParseExpression();
                if (Peek != ')') throw new FormatException();
                _i++;
                return v;
            }
            if (char.IsLetter(Peek))
            {
                var start = _i;
                while (char.IsLetter(Peek)) _i++;
                var name = s[start.._i].ToLowerInvariant();
                if (name == "pi") return Math.PI;
                if (name == "e") return Math.E;
                var arg = ParsePrimary();
                return name switch
                {
                    "sqrt" => Math.Sqrt(arg), "sin" => Math.Sin(arg), "cos" => Math.Cos(arg), "tan" => Math.Tan(arg),
                    "log" => Math.Log10(arg), "ln" => Math.Log(arg), "abs" => Math.Abs(arg), "round" => Math.Round(arg),
                    _ => throw new FormatException(),
                };
            }
            var st = _i;
            while (char.IsDigit(Peek) || Peek == '.') _i++;
            if (st == _i) throw new FormatException();
            var num = double.Parse(s[st.._i], CultureInfo.InvariantCulture);
            if (Peek == '%' && (_i + 1 >= s.Length || "+-*/)".Contains(s[_i + 1]))) { _i++; num /= 100; }
            return num;
        }
    }
}
