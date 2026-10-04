using System.Windows;
using System.Windows.Threading;
using Notchify.Core;
using Notchify.Views;

namespace Notchify.Modules;

public enum TimerPhase { Focus, ShortBreak, LongBreak, Custom, Stopwatch }

/// <summary>
/// Pomodoro with session chaining, a free countdown, and a drift-free wall-clock stopwatch.
/// Keeps per-day focus minutes for streaks and a 7-day chart. Ticks only while something runs.
/// </summary>
public sealed class TimerModule : NotchModule
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private Dictionary<string, double> _log = new();

    public override string Id => "timer";
    public override string Title => "Timer";
    public override string Glyph => Glyphs.Stopwatch;
    public override string Description => "Pomodoro with session chaining, countdowns, a stopwatch, streaks and a 7-day focus chart.";

    public TimerModule() => _tick.Tick += (_, _) => Tick();

    public TimerPhase Phase { get; private set; } = TimerPhase.Focus;
    public bool Running { get; private set; }
    public int CompletedFocusSessions { get; private set; }

    // Countdown state (wall-clock based so it never drifts).
    private DateTime _endsAt;
    private TimeSpan _remainingWhenPaused;
    private TimeSpan _total;

    // Stopwatch state.
    private DateTime _startedAt;
    private TimeSpan _accumulated;

    public event Action? Changed;

    protected override void Start()
    {
        _log = JsonStore.Load<Dictionary<string, double>>("focus");
        Select(TimerPhase.Focus);
    }

    protected override void Stop()
    {
        _tick.Stop();
        Notch.Hub.Remove("timer");
    }

    public TimeSpan Total => _total;

    public TimeSpan Remaining =>
        Phase == TimerPhase.Stopwatch ? Elapsed :
        Running ? Max(TimeSpan.Zero, _endsAt - DateTime.Now) : _remainingWhenPaused;

    public TimeSpan Elapsed => Phase == TimerPhase.Stopwatch
        ? _accumulated + (Running ? DateTime.Now - _startedAt : TimeSpan.Zero)
        : _total - Remaining;

    public double Progress => Phase == TimerPhase.Stopwatch ? (Elapsed.TotalSeconds % 60) / 60 :
        _total.TotalSeconds <= 0 ? 0 : 1 - Remaining.TotalSeconds / _total.TotalSeconds;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    public static string PhaseName(TimerPhase p) => p switch
    {
        TimerPhase.Focus => "Focus",
        TimerPhase.ShortBreak => "Short break",
        TimerPhase.LongBreak => "Long break",
        TimerPhase.Stopwatch => "Stopwatch",
        _ => "Timer",
    };

    public void Select(TimerPhase phase, TimeSpan? custom = null)
    {
        var s = SettingsStore.Current.Timer;
        Running = false;
        Phase = phase;
        _accumulated = TimeSpan.Zero;
        _total = phase switch
        {
            TimerPhase.Focus => TimeSpan.FromMinutes(s.FocusMinutes),
            TimerPhase.ShortBreak => TimeSpan.FromMinutes(s.ShortBreakMinutes),
            TimerPhase.LongBreak => TimeSpan.FromMinutes(s.LongBreakMinutes),
            TimerPhase.Custom => custom ?? TimeSpan.FromMinutes(10),
            _ => TimeSpan.Zero,
        };
        _remainingWhenPaused = _total;
        _tick.Stop();
        UpdatePill();
        Changed?.Invoke();
    }

    public void StartPause()
    {
        if (Running) Pause(); else Resume();
    }

    public void Resume()
    {
        if (Running) return;
        if (Phase == TimerPhase.Stopwatch) _startedAt = DateTime.Now;
        else
        {
            if (_remainingWhenPaused <= TimeSpan.Zero) _remainingWhenPaused = _total;
            _endsAt = DateTime.Now + _remainingWhenPaused;
        }
        Running = true;
        _tick.Start();
        Tick();
    }

    public void Pause()
    {
        if (!Running) return;
        if (Phase == TimerPhase.Stopwatch) _accumulated += DateTime.Now - _startedAt;
        else _remainingWhenPaused = Remaining;
        Running = false;
        _tick.Stop();
        UpdatePill();
        Changed?.Invoke();
    }

    public void Reset() => Select(Phase, Phase == TimerPhase.Custom ? _total : null);

    public void Skip() => Complete(skipped: true);

    private void Tick()
    {
        if (Running && Phase != TimerPhase.Stopwatch && Remaining <= TimeSpan.Zero) { Complete(skipped: false); return; }
        UpdatePill();
        Changed?.Invoke();
    }

    private void Complete(bool skipped)
    {
        var s = SettingsStore.Current.Timer;
        var finished = Phase;
        if (finished == TimerPhase.Focus && !skipped)
        {
            CompletedFocusSessions++;
            LogFocus(_total.TotalMinutes);
        }
        Running = false;
        _tick.Stop();

        if (!skipped)
        {
            if (s.Sound) System.Media.SystemSounds.Asterisk.Play();
            Notch.Hub.Show(new Island
            {
                Key = "timer-done",
                Glyph = Glyphs.Stopwatch,
                Title = $"{PhaseName(finished)} complete",
                Message = finished == TimerPhase.Focus ? $"Session {CompletedFocusSessions} done · {Streak} day streak" : "Back to it",
                Accent = Ui.Orange,
                Priority = IslandPriority.High,
                Duration = TimeSpan.FromSeconds(6),
                OpenTab = Id,
            });
        }

        // Session chaining: focus → break → focus …
        if (finished is TimerPhase.Focus or TimerPhase.ShortBreak or TimerPhase.LongBreak)
        {
            var next = finished == TimerPhase.Focus
                ? CompletedFocusSessions % Math.Max(1, s.SessionsBeforeLongBreak) == 0 ? TimerPhase.LongBreak : TimerPhase.ShortBreak
                : TimerPhase.Focus;
            Select(next);
            if (s.AutoStartNext && !skipped) Resume();
        }
        else Select(finished, _total);
    }

    private void LogFocus(double minutes)
    {
        var key = DateTime.Today.ToString("yyyy-MM-dd");
        _log[key] = _log.GetValueOrDefault(key) + minutes;
        JsonStore.Save("focus", _log);
    }

    public int Streak
    {
        get
        {
            var day = DateTime.Today;
            if (_log.GetValueOrDefault(day.ToString("yyyy-MM-dd")) <= 0) day = day.AddDays(-1);
            var n = 0;
            while (_log.GetValueOrDefault(day.ToString("yyyy-MM-dd")) > 0) { n++; day = day.AddDays(-1); }
            return n;
        }
    }

    public List<double> Last7Days =>
        Enumerable.Range(0, 7).Select(i => _log.GetValueOrDefault(DateTime.Today.AddDays(i - 6).ToString("yyyy-MM-dd"))).ToList();

    public double TodayMinutes => _log.GetValueOrDefault(DateTime.Today.ToString("yyyy-MM-dd"));

    private void UpdatePill()
    {
        var showing = Running || (Phase == TimerPhase.Stopwatch && _accumulated > TimeSpan.Zero) ||
                      (Phase != TimerPhase.Stopwatch && _remainingWhenPaused < _total && _remainingWhenPaused > TimeSpan.Zero);
        if (!showing) { Notch.Hub.Remove("timer"); return; }
        var mascot = SettingsStore.Current.Timer.Mascot;
        Notch.Hub.Upsert("timer", a =>
        {
            a.Glyph = Phase == TimerPhase.Stopwatch ? Glyphs.Stopwatch : Glyphs.Clock;
            a.Accent = Phase == TimerPhase.Focus ? Ui.Orange : Phase == TimerPhase.Stopwatch ? Ui.Teal : Ui.Green;
            // The mascot rides along with the countdown — a deliberately silly touch.
            a.Text = (string.IsNullOrEmpty(mascot) ? "" : mascot + " ") + Ui.FormatSpan(Remaining) + (Running ? "" : " ❚❚");
            a.Progress = Phase == TimerPhase.Stopwatch ? null : Progress;
            a.Priority = 70;
            a.OpenTab = Id;
        });
    }

    public override FrameworkElement CreateView() => new TimerView(this);

    public override IEnumerable<PaletteCommand> Commands => new[]
    {
        new PaletteCommand("Start focus session", Glyphs.Clock, () => { Select(TimerPhase.Focus); Resume(); }, $"{SettingsStore.Current.Timer.FocusMinutes} min", "pomodoro timer"),
        new PaletteCommand("Start short break", Glyphs.Clock, () => { Select(TimerPhase.ShortBreak); Resume(); }, null, "pomodoro timer"),
        new PaletteCommand("Start 5 minute timer", Glyphs.Clock, () => { Select(TimerPhase.Custom, TimeSpan.FromMinutes(5)); Resume(); }, null, "countdown"),
        new PaletteCommand("Start 10 minute timer", Glyphs.Clock, () => { Select(TimerPhase.Custom, TimeSpan.FromMinutes(10)); Resume(); }, null, "countdown"),
        new PaletteCommand("Start stopwatch", Glyphs.Stopwatch, () => { Select(TimerPhase.Stopwatch); Resume(); }, null, "count up"),
        new PaletteCommand(Running ? "Pause timer" : "Resume timer", Glyphs.Pause, StartPause, null, "timer"),
    };
}
