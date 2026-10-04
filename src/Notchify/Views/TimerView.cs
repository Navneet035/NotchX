using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Notchify.Controls;
using Notchify.Core;
using Notchify.Modules;

namespace Notchify.Views;

/// <summary>Timer tab, built in code to stay compact.</summary>
public sealed class TimerView : Grid
{
    private readonly TimerModule _m;
    private readonly Ring _ring = new() { Width = 150, Height = 150, Thickness = 7 };
    private readonly TextBlock _time = new() { FontSize = 34, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _phase = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button _startPause;
    private readonly MiniBarChart _chart = new() { Height = 80 };
    private readonly TextBlock _stats = new();
    private readonly List<ToggleButton> _phaseButtons = new();
    private readonly TextBox _custom = new() { Width = 54, Text = "10", Tag = "min", HorizontalContentAlignment = HorizontalAlignment.Center };

    public TimerView(TimerModule module)
    {
        _m = module;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _time.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        _time.FontFamily = (System.Windows.Media.FontFamily)FindResource("DisplayFont");
        _phase.Style = (Style)FindResource("Caption");
        _stats.Style = (Style)FindResource("Caption");
        _ring.SetResourceReference(Ring.StrokeProperty, "AccentBrush");
        _chart.SetResourceReference(MiniBarChart.FillProperty, "AccentBrush");

        // Left: phase selector, ring, controls
        var left = new DockPanel();
        var phases = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
        foreach (var (label, phase) in new[] { ("Focus", TimerPhase.Focus), ("Short", TimerPhase.ShortBreak), ("Long", TimerPhase.LongBreak), ("Timer", TimerPhase.Custom), ("Stopwatch", TimerPhase.Stopwatch) })
        {
            var b = new ToggleButton { Content = label, Style = (Style)FindResource("TileToggle"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(2, 0, 2, 0), Tag = phase };
            b.Click += (_, _) =>
            {
                var p = (TimerPhase)b.Tag;
                _m.Select(p, p == TimerPhase.Custom && double.TryParse(_custom.Text, out var mins) ? TimeSpan.FromMinutes(Math.Clamp(mins, 0.1, 24 * 60)) : null);
            };
            _phaseButtons.Add(b);
            phases.Children.Add(b);
        }
        DockPanel.SetDock(phases, Dock.Top);
        left.Children.Add(phases);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        var reset = new Button { Style = (Style)FindResource("IconButton"), Content = Glyphs.Refresh, ToolTip = "Reset" };
        reset.Click += (_, _) => _m.Reset();
        _startPause = new Button { Style = (Style)FindResource("BigIconButton"), Content = Glyphs.Play, Margin = new Thickness(12, 0, 12, 0) };
        _startPause.Click += (_, _) => _m.StartPause();
        var skip = new Button { Style = (Style)FindResource("IconButton"), Content = Glyphs.Next, ToolTip = "Skip to next session" };
        skip.Click += (_, _) => _m.Skip();
        controls.Children.Add(_custom);
        controls.Children.Add(reset);
        controls.Children.Add(_startPause);
        controls.Children.Add(skip);
        DockPanel.SetDock(controls, Dock.Bottom);
        left.Children.Add(controls);

        var ringGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        ringGrid.Children.Add(_ring);
        var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        center.Children.Add(_time);
        center.Children.Add(_phase);
        ringGrid.Children.Add(center);
        left.Children.Add(ringGrid);
        Children.Add(left);

        // Right: streak + 7-day chart
        var right = new Border { Style = (Style)FindResource("Card") };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "FOCUS · LAST 7 DAYS", Style = (Style)FindResource("SectionHeader") });
        stack.Children.Add(_chart);
        _stats.Margin = new Thickness(0, 10, 0, 0);
        _stats.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(_stats);
        right.Child = stack;
        SetColumn(right, 2);
        Children.Add(right);

        _m.Changed += Refresh;
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); };
        Refresh();
    }

    private void Refresh()
    {
        if (!IsVisible) return;
        _time.Text = Ui.FormatSpan(_m.Remaining);
        _phase.Text = TimerModule.PhaseName(_m.Phase) + (_m.Running ? "" : " · paused");
        _ring.Value = _m.Progress;
        _startPause.Content = _m.Running ? Glyphs.Pause : Glyphs.Play;
        _custom.Visibility = _m.Phase == TimerPhase.Custom ? Visibility.Visible : Visibility.Collapsed;
        foreach (var b in _phaseButtons) b.IsChecked = (TimerPhase)b.Tag == _m.Phase;

        var days = _m.Last7Days;
        _chart.Values = days;
        _chart.Labels = Enumerable.Range(0, 7).Select(i => DateTime.Today.AddDays(i - 6).ToString("ddd")[..2]).ToList();
        _chart.Refresh();
        _stats.Text = $"Today: {_m.TodayMinutes:0} min focused\nStreak: {_m.Streak} day(s) 🔥\nSessions this run: {_m.CompletedFocusSessions}\nWeek total: {days.Sum() / 60:0.#} h";
    }
}
