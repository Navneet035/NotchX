using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Notchify.Core;

public enum IslandPriority { Low, Normal, High, Critical }

/// <summary>
/// A persistent item shown on the collapsed pill (music, running timer, build, download...).
/// Several can be visible at once; the pill widens to fit and orders them by priority.
/// </summary>
public sealed class LiveActivity : ObservableObject
{
    private string _glyph = Glyphs.Info;
    private string _text = "";
    private string? _detail;
    private Brush _accent = Brushes.White;
    private ImageSource? _image;
    private double? _progress;
    private int _priority;

    public LiveActivity(string id) => Id = id;

    public string Id { get; }
    /// <summary>Module id to open when the activity is clicked.</summary>
    public string? OpenTab { get; set; }
    public string Glyph { get => _glyph; set => Set(ref _glyph, value); }
    public string Text { get => _text; set => Set(ref _text, value); }
    public string? Detail { get => _detail; set => Set(ref _detail, value); }
    public Brush Accent { get => _accent; set => Set(ref _accent, value); }
    public ImageSource? Image { get => _image; set { if (Set(ref _image, value)) Raise(nameof(HasImage)); } }
    public bool HasImage => _image != null;
    public double? Progress { get => _progress; set { if (Set(ref _progress, value)) Raise(nameof(HasProgress)); } }
    public bool HasProgress => _progress.HasValue;
    /// <summary>Higher = more important = shown first ("what costs most to miss").</summary>
    public int Priority { get => _priority; set => Set(ref _priority, value); }
    /// <summary>Optional custom compact view (e.g. live spectrum bars).</summary>
    public FrameworkElement? CompactContent { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

public sealed record IslandAction(string Label, Action Run, bool Primary = false, string? Glyph = null);

/// <summary>A transient notification / HUD that briefly expands the pill.</summary>
public sealed class Island : ObservableObject
{
    private string _title = "";
    private string? _message;
    private double? _progress;
    private string _glyph = Glyphs.Info;
    private ImageSource? _image;

    /// <summary>Islands with the same key replace each other in place (e.g. repeated volume changes).</summary>
    public string Key { get; init; } = Guid.NewGuid().ToString("N");
    public string Glyph { get => _glyph; set => Set(ref _glyph, value); }
    public string Title { get => _title; set => Set(ref _title, value); }
    public string? Message { get => _message; set { if (Set(ref _message, value)) Raise(nameof(HasMessage)); } }
    public bool HasMessage => !string.IsNullOrEmpty(_message);
    public Brush Accent { get; set; } = Brushes.White;
    public ImageSource? Image { get => _image; set { if (Set(ref _image, value)) Raise(nameof(HasImage)); } }
    public bool HasImage => _image != null;
    /// <summary>0..1 bar for HUDs like volume or brightness.</summary>
    public double? Progress { get => _progress; set { if (Set(ref _progress, value)) Raise(nameof(HasProgress)); } }
    public bool HasProgress => _progress.HasValue;
    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(3);
    public bool Sticky { get; set; }
    public IslandPriority Priority { get; set; } = IslandPriority.Normal;
    public List<IslandAction> Actions { get; init; } = new();
    public bool HasActions => Actions.Count > 0;
    public string? OpenTab { get; set; }
    /// <summary>Lets HUDs be dragged/tweaked: when set it replaces the default island layout.</summary>
    public FrameworkElement? CustomContent { get; set; }
    /// <summary>File paths that can be dragged out of the island (screenshots, downloads).</summary>
    public string[]? DragFiles { get; set; }

    public event Action<Island>? Dismissed;
    internal void OnDismissed() => Dismissed?.Invoke(this);
}

/// <summary>Central place every feature uses to put things on the notch.</summary>
public sealed class ActivityHub : ObservableObject
{
    private readonly List<Island> _queue = new();
    private readonly DispatcherTimer _islandTimer;
    private readonly DispatcherTimer _expiryTimer;
    private Island? _current;

    public ActivityHub()
    {
        _islandTimer = new DispatcherTimer();
        _islandTimer.Tick += (_, _) => { _islandTimer.Stop(); Dismiss(_current); };
        _expiryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _expiryTimer.Tick += (_, _) => PurgeExpired();
        _expiryTimer.Start();
    }

    /// <summary>Activities on the collapsed pill, highest priority first.</summary>
    public ObservableCollection<LiveActivity> Activities { get; } = new();

    public Island? Current
    {
        get => _current;
        private set => Set(ref _current, value);
    }

    /// <summary>True while a Focus / Do-Not-Disturb mode is on; low-priority islands are suppressed.</summary>
    public bool QuietMode { get; set; }

    /// <summary>Set by the shell while the user hovers an island so it doesn't vanish under the cursor.</summary>
    public bool HoldCurrent
    {
        get => _hold;
        set
        {
            _hold = value;
            if (value) _islandTimer.Stop();
            else if (_current is { Sticky: false }) RestartTimer(_current);
        }
    }
    private bool _hold;

    /// <summary>Add or update a pill activity. Must be called on the UI thread (use Ui.Post from services).</summary>
    public LiveActivity Upsert(string id, Action<LiveActivity> configure)
    {
        var a = Activities.FirstOrDefault(x => x.Id == id);
        var isNew = a is null;
        a ??= new LiveActivity(id);
        configure(a);
        if (isNew) Activities.Add(a);
        Sort();
        return a;
    }

    public void Remove(string id)
    {
        var a = Activities.FirstOrDefault(x => x.Id == id);
        if (a != null) Activities.Remove(a);
    }

    public bool Has(string id) => Activities.Any(x => x.Id == id);

    private void Sort()
    {
        var sorted = Activities.OrderByDescending(a => a.Priority).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var oldIndex = Activities.IndexOf(sorted[i]);
            if (oldIndex != i) Activities.Move(oldIndex, i);
        }
    }

    private void PurgeExpired()
    {
        var now = DateTime.Now;
        foreach (var a in Activities.Where(a => a.ExpiresAt < now).ToList()) Activities.Remove(a);
    }

    public void Show(Island island)
    {
        if (QuietMode && island.Priority == IslandPriority.Low) return;

        if (_current != null && _current.Key == island.Key)
        {
            // Update in place, e.g. volume HUD while the user holds the key.
            _current.Title = island.Title;
            _current.Message = island.Message;
            _current.Progress = island.Progress;
            _current.Glyph = island.Glyph;
            _current.Image = island.Image;
            if (island.CustomContent != null && island.CustomContent != _current.CustomContent)
            {
                _current.CustomContent = island.CustomContent;
                Raise(nameof(Current)); // ask the shell to re-present the new content
            }
            RestartTimer(_current);
            return;
        }

        _queue.RemoveAll(q => q.Key == island.Key);

        if (_current == null || (island.Priority > _current.Priority && !_current.Sticky))
        {
            if (_current != null && _current.Sticky) _queue.Insert(0, _current);
            Present(island);
        }
        else
        {
            _queue.Add(island);
            _queue.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }
    }

    /// <summary>Convenience for one-line notifications.</summary>
    public Island Notify(string glyph, string title, string? message = null, Brush? accent = null,
        IslandPriority priority = IslandPriority.Normal, double seconds = 3, string? key = null, string? openTab = null)
    {
        var island = new Island
        {
            Key = key ?? Guid.NewGuid().ToString("N"),
            Glyph = glyph,
            Title = title,
            Message = message,
            Accent = accent ?? Ui.Accent,
            Priority = priority,
            Duration = TimeSpan.FromSeconds(seconds),
            OpenTab = openTab,
        };
        Show(island);
        return island;
    }

    public void Dismiss(Island? island)
    {
        if (island is null) return;
        if (island == _current)
        {
            _islandTimer.Stop();
            Current = null;
            island.OnDismissed();
            if (_queue.Count > 0)
            {
                var next = _queue[0];
                _queue.RemoveAt(0);
                Present(next);
            }
        }
        else if (_queue.Remove(island))
        {
            island.OnDismissed();
        }
    }

    public void DismissByKey(string key)
    {
        if (_current?.Key == key) Dismiss(_current);
        foreach (var q in _queue.Where(q => q.Key == key).ToList()) Dismiss(q);
    }

    private void Present(Island island)
    {
        Current = island;
        RestartTimer(island);
    }

    private void RestartTimer(Island island)
    {
        _islandTimer.Stop();
        if (island.Sticky || _hold) return;
        _islandTimer.Interval = island.Duration;
        _islandTimer.Start();
    }
}
