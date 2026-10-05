using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Notchify.Controls;
using Notchify.Core;
using Notchify.Services;
using static Notchify.Views.ViewKit;

namespace Notchify.Modules;

/// <summary>AI Usage: Claude Code, Codex, Copilot (and Cursor) windows, tokens and cost, with alerts before a limit.</summary>
public sealed class AiUsageModule : NotchModule
{
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMinutes(2) };

    public AiUsageModule() => _refresh.Tick += (_, _) => _ = Notch.AiUsage.RefreshAsync();

    public override string Id => "ai";
    public override string Title => "AI Usage";
    public override string ShortTitle => "AI";
    public override string Glyph => Glyphs.Robot;
    public override string Description => "Rate-limit windows, tokens and cost for Claude Code, Codex and Copilot — from local logs or an API token.";

    protected override void Start()
    {
        _refresh.Start();
        SettingsStore.Changed += OnSettingsChanged;
        _ = Notch.AiUsage.RefreshAsync();
    }

    protected override void Stop()
    {
        _refresh.Stop();
        SettingsStore.Changed -= OnSettingsChanged;
        Ui.Post(Notch.AiUsage.RemoveNotchChips);
    }

    private static void OnSettingsChanged() => Ui.Post(Notch.AiUsage.UpdateNotchChips);

    public override FrameworkElement CreateView()
    {
        var stack = new StackPanel();
        var updated = Text("", "Caption");

        void Render()
        {
            stack.Children.Clear();
            var order = SettingsStore.Current.AiUsage.Sections;
            foreach (var id in order)
            {
                var p = Notch.AiUsage.Providers.FirstOrDefault(x => x.Id == id);
                if (p == null) continue;
                stack.Children.Add(Card(BuildCard(p)));
            }
            updated.Text = $"Updated {DateTime.Now:t}";
        }

        Notch.AiUsage.Updated += () => Ui.Post(Render);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var refresh = IconButton(Glyphs.Refresh, "Refresh now", (_, _) => _ = Notch.AiUsage.RefreshAsync());
        DockPanel.SetDock(refresh, Dock.Right);
        header.Children.Add(refresh);
        header.Children.Add(updated);
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(Scroll(stack));
        dock.IsVisibleChanged += (_, _) => { if (dock.IsVisible) Render(); };
        Render();
        return dock;
    }

    private static UIElement BuildCard(ProviderUsage p)
    {
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(Icon(p.Glyph, 14));
        var name = Text(p.Name, "Title");
        name.Margin = new Thickness(8, 0, 0, 0);
        title.Children.Add(name);

        if (!p.Available)
            return new StackPanel { Children = { title, WithMargin(Text(p.Status, "Caption"), 0, 4) } };

        var ring = new Ring { Width = 54, Height = 54, Thickness = 5, Value = p.WindowUsed ?? 0 };
        ring.Stroke = (p.WindowUsed ?? 0) >= 0.9 ? Ui.Red : (p.WindowUsed ?? 0) >= 0.7 ? Ui.Orange : (System.Windows.Media.Brush)Application.Current.FindResource("AccentBrush");
        var pct = Text(p.HasWindow ? $"{p.WindowPercent:0}%" : "—", "Body", 12);
        pct.HorizontalAlignment = HorizontalAlignment.Center;
        pct.VerticalAlignment = VerticalAlignment.Center;
        var ringGrid = new Grid { Children = { ring, pct } };

        var info = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        info.Children.Add(title);
        info.Children.Add(WithMargin(Text(p.WindowLabel, "Caption"), 0, 2));
        if (!string.IsNullOrEmpty(p.WindowResets)) info.Children.Add(Text(p.WindowResets, "Caption"));
        if (p.HasWeekly)
        {
            var bar = new ProgressBar { Maximum = 100, Value = p.WeeklyPercent, Margin = new Thickness(0, 6, 0, 2) };
            info.Children.Add(bar);
            info.Children.Add(Text(p.WeeklyLabel, "Caption"));
        }

        var nums = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        nums.Children.Add(Text($"Today  {p.TokensTodayText} · {p.CostTodayText}", "Caption"));
        nums.Children.Add(Text($"7 days {p.TokensWeekText} · {p.CostWeekText}", "Caption"));
        var chart = new MiniBarChart { Height = 34, Width = 120, Values = p.Daily, Margin = new Thickness(0, 4, 0, 0) };
        chart.SetResourceReference(MiniBarChart.FillProperty, "AccentBrush");
        nums.Children.Add(chart);

        var row = Columns((ringGrid, Auto), (info, Star()), (nums, Auto));
        var foot = Text(p.Status, "Caption", 10);
        foot.Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("FaintTextBrush");
        foot.Margin = new Thickness(0, 6, 0, 0);
        return new StackPanel { Children = { row, foot } };
    }

    private static FrameworkElement WithMargin(FrameworkElement el, double top, double bottom)
    {
        el.Margin = new Thickness(0, top, 0, bottom);
        return el;
    }

    public override IEnumerable<PaletteCommand> Commands => new[]
    {
        new PaletteCommand("Refresh AI usage", Glyphs.Robot, () => _ = Notch.AiUsage.RefreshAsync(), null, "claude codex copilot tokens cost limit"),
    };
}
