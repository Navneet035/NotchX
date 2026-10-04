using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Notchify.Core;
using Notchify.Services;

namespace Notchify.Controls;

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var b = value switch { bool x => x, null => false, string s => !string.IsNullOrEmpty(s), int i => i != 0, _ => true };
        return b ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Shared converter instances for XAML built at runtime.</summary>
public static class Converters
{
    public static readonly BoolToVisibility Visible = new();
    public static readonly BoolToVisibility Hidden = new() { Invert = true };
}

public sealed class TimeSpanText : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is TimeSpan ts ? Ui.FormatSpan(ts) : "";
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>
/// Audio visualizer bars. Uses the real loopback FFT when available; otherwise a gentle synthetic
/// wobble while music plays. Only renders (and only captures audio) while visible.
/// </summary>
public sealed class SpectrumBars : FrameworkElement
{
    public static readonly DependencyProperty BarCountProperty =
        DependencyProperty.Register(nameof(BarCount), typeof(int), typeof(SpectrumBars), new FrameworkPropertyMetadata(5, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty =
        DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(SpectrumBars), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public int BarCount { get => (int)GetValue(BarCountProperty); set => SetValue(BarCountProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    /// <summary>False = always use the cheap synthetic animation (e.g. on the collapsed pill).</summary>
    public bool UseLiveAudio { get; set; } = true;

    private readonly double[] _levels = new double[SpectrumService.Bands];
    private bool _live;
    private DateTime _lastFrame;
    private readonly Random _rng = new();

    public SpectrumBars()
    {
        IsVisibleChanged += (_, _) => { if (IsVisible) Start(); else Stop(); };
        Unloaded += (_, _) => Stop();
    }

    private bool _rendering;

    private void Start()
    {
        if (_rendering) return;
        if (UseLiveAudio && SettingsStore.Current.Media.LiveSpectrum) { Notch.Spectrum.Acquire(); _live = true; }
        CompositionTarget.Rendering += OnFrame;
        _rendering = true;
    }

    private void Stop()
    {
        if (!_rendering) return;
        if (_live) Notch.Spectrum.Release();
        _live = false;
        CompositionTarget.Rendering -= OnFrame;
        _rendering = false;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if ((DateTime.Now - _lastFrame).TotalMilliseconds < 33) return; // ~30 fps is plenty
        _lastFrame = DateTime.Now;
        if (!IsVisible) { Stop(); return; }
        var playing = Notch.Media.IsPlaying;
        var src = Notch.Spectrum.Levels;
        var useReal = _live && Notch.Spectrum.Available && src.Any(v => v > 0.02f);
        for (var i = 0; i < _levels.Length; i++)
        {
            double target = useReal ? src[i] : playing ? 0.25 + _rng.NextDouble() * 0.6 : 0.08;
            _levels[i] = _levels[i] * 0.55 + target * 0.45;
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var n = Math.Max(1, BarCount);
        var w = ActualWidth; var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var gap = Math.Max(1.5, w / n * 0.35);
        var barW = (w - gap * (n - 1)) / n;
        for (var i = 0; i < n; i++)
        {
            // Map n bars across the band range, skewing toward the more musical low/mid bands.
            var band = (int)Math.Min(_levels.Length - 1, Math.Pow((double)i / n, 1.3) * _levels.Length);
            var v = Math.Clamp(_levels[band], 0.06, 1);
            var bh = Math.Max(barW, v * h);
            dc.DrawRoundedRectangle(Fill, null, new Rect(i * (barW + gap), (h - bh) / 2, barW, bh), barW / 2, barW / 2);
        }
    }
}

/// <summary>Tiny line chart for stats history.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    public IReadOnlyList<double>? Values { get; set; }
    public double Max { get; set; } = 100;

    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var v = Values;
        if (v == null || v.Count < 2 || ActualWidth <= 0) return;
        var max = Max > 0 ? Max : Math.Max(1, v.Max());
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            for (var i = 0; i < v.Count; i++)
            {
                var p = new Point(ActualWidth * i / (v.Count - 1), ActualHeight - ActualHeight * Math.Clamp(v[i] / max, 0, 1));
                if (i == 0) ctx.BeginFigure(p, false, false); else ctx.LineTo(p, true, true);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, new Pen(Stroke, 1.5) { LineJoin = PenLineJoin.Round }, geo);
    }
}

/// <summary>Seven-day (or any) bar chart with day labels.</summary>
public sealed class MiniBarChart : FrameworkElement
{
    public static readonly DependencyProperty FillProperty =
        DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(MiniBarChart), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public IReadOnlyList<double>? Values { get; set; }
    public IReadOnlyList<string>? Labels { get; set; }

    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var v = Values;
        if (v == null || v.Count == 0 || ActualWidth <= 0) return;
        var labelH = Labels != null ? 14 : 0;
        var max = Math.Max(1e-9, v.Max());
        var slot = ActualWidth / v.Count;
        var barW = Math.Min(18, slot * 0.6);
        var dim = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (var i = 0; i < v.Count; i++)
        {
            var h = (ActualHeight - labelH) * v[i] / max;
            var x = i * slot + (slot - barW) / 2;
            dc.DrawRoundedRectangle(dim, null, new Rect(x, 0, barW, ActualHeight - labelH), 4, 4);
            if (h > 0.5) dc.DrawRoundedRectangle(Fill, null, new Rect(x, ActualHeight - labelH - h, barW, h), 4, 4);
            if (Labels != null && i < Labels.Count)
            {
                var ft = new FormattedText(Labels[i], CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 9, new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), dpi);
                dc.DrawText(ft, new Point(i * slot + (slot - ft.Width) / 2, ActualHeight - 12));
            }
        }
    }
}

/// <summary>Circular progress ring (timer countdown, usage windows, gauge HUD).</summary>
public sealed class Ring : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(Ring), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Ring), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThicknessProperty =
        DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(Ring), new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0..1</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var r = (size - Thickness) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), Thickness), c, r, r);
        var v = Math.Clamp(Value, 0, 0.9999);
        if (v <= 0) return;
        var angle = v * 2 * Math.PI;
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Sin(angle), c.Y - r * Math.Cos(angle));
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, v > 0.5, SweepDirection.Clockwise, true, true);
        }
        geo.Freeze();
        dc.DrawGeometry(null, new Pen(Stroke, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geo);
    }
}
