namespace Notchify.Core;

/// <summary>A kind of card that can be put on the Home page.</summary>
public sealed record CardInfo(string Type, string Name, string Glyph, string Description, int Cols, int Rows, int MinCols = 2, int MinRows = 1);

/// <summary>
/// "Edit layout" mode inside the open notch, and the Home page's card list. In edit mode tabs and Home
/// cards show move / resize / remove controls and can be dragged into a new order. Every change is saved
/// to settings.json (Behavior.HomeCards) straight away.
/// </summary>
public static class LayoutEditor
{
    public const int GridColumns = 12;
    public const int MaxRows = 4;

    private static bool _editing;

    public static event Action? Changed;

    public static bool IsEditing
    {
        get => _editing;
        set
        {
            if (_editing == value) return;
            _editing = value;
            if (!value) SettingsStore.Save();
            Changed?.Invoke();
        }
    }

    public static void Toggle() => IsEditing = !IsEditing;

    /// <summary>The header's "Add card" button asks the Home page to show its card gallery.</summary>
    public static event Action? AddCardRequested;
    public static void RequestAddCard() => AddCardRequested?.Invoke();

    // ---------- Card catalogue ----------

    /// <summary>Every card the Home page can show, with its default and minimum size.</summary>
    public static readonly CardInfo[] Catalog =
    {
        new("clock", "Clock", Glyphs.Clock, "Time and date, with today's weather", 3, 2),
        new("weather", "Weather", Glyphs.Sun, "Temperature, conditions and today's high / low", 3, 1),
        new("nowplaying", "Now Playing", Glyphs.Music, "Album art, track and playback controls", 4, 2, 3, 1),
        new("volume", "Volume & output", Glyphs.Volume, "Volume, mute and the sound output picker", 3, 1),
        new("brightness", "Brightness", Glyphs.Sun, "Screen brightness (built-in panel or monitor over DDC/CI)", 3, 1),
        new("toggles", "Quick toggles", Glyphs.Bolt, "Caffeine, mic mute, capture, eyedropper, palette and lock", 3, 1),
        new("controls", "Sound & brightness", Glyphs.Volume, "Volume, brightness, output, lock and quick toggles in one card", 3, 2, 3, 2),
        new("bluetooth", "Bluetooth", Glyphs.Bluetooth, "Paired devices, connection and battery", 2, 2),
        new("battery", "Battery", Glyphs.Battery(80, false), "This PC's battery and your devices' batteries", 2, 2),
        new("reminders", "Reminders", Glyphs.Bell, "Add a reminder in plain words and see what's due", 3, 2),
        new("calendar", "Calendar", Glyphs.Calendar, "Your next events", 3, 2),
        new("notes", "Notes", Glyphs.Note, "Jot a note and see your latest ones", 3, 2),
        new("clipboard", "Clipboard", Glyphs.Clipboard, "Recent copies — click one to copy it again", 3, 2),
        new("shelf", "Shelf", Glyphs.Package, "Files you've stashed; drop more on it, drag them out", 3, 2),
        new("camera", "Camera", Glyphs.Camera, "A mirror from your webcam (only while visible)", 3, 2),
        new("screentime", "Screen time", Glyphs.Chart, "Time on this PC today, and your top apps", 3, 2),
        new("spaces", "Spaces", Glyphs.Apps, "Virtual desktops: see where you are, switch, add", 3, 1),
        new("world", "World clocks", Glyphs.Globe, "Time and weather in your places (Settings › Weather), with how far ahead or behind they are", 3, 2),
        new("windows", "Open windows", Glyphs.TaskView, "Apps and windows open on this desktop — click one to jump to it", 3, 2),
        new("timer", "Timer", Glyphs.Stopwatch, "Pomodoro countdown with start / pause", 2, 2),
        new("system", "System", Glyphs.Chart, "CPU, memory and network", 3, 1),
        new("launcher", "Apps", Glyphs.Apps, "Your pinned apps from the Launcher", 3, 1),
    };

    public static CardInfo? Info(string type) => Catalog.FirstOrDefault(c => c.Type == type);

    // ---------- Home cards ----------

    private static List<HomeCard> Cards => SettingsStore.Current.Behavior.HomeCards;

    /// <summary>The cards on Home, in order (unknown types dropped).</summary>
    public static List<HomeCard> HomeCards() => Cards.Where(c => Info(c.Type) != null).ToList();

    /// <summary>Card types not on Home yet.</summary>
    public static IEnumerable<CardInfo> Available() => Catalog.Where(i => Cards.All(c => c.Type != i.Type));

    private static void Save() => SettingsStore.NotifyChanged();

    public static void Add(string type)
    {
        if (Info(type) is not { } info || Cards.Any(c => c.Type == type)) return;
        Cards.Add(new HomeCard { Type = type, Cols = info.Cols, Rows = info.Rows });
        Save();
    }

    public static void Remove(string type)
    {
        if (Cards.RemoveAll(c => c.Type == type) > 0) Save();
    }

    public static void Move(string type, int delta)
    {
        var i = Cards.FindIndex(c => c.Type == type);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Cards.Count) return;
        (Cards[i], Cards[j]) = (Cards[j], Cards[i]);
        Save();
    }

    /// <summary>Drop <paramref name="type"/> where <paramref name="targetType"/> is.</summary>
    public static void MoveTo(string type, string targetType)
    {
        if (type == targetType) return;
        var from = Cards.FindIndex(c => c.Type == type);
        var to = Cards.FindIndex(c => c.Type == targetType);
        if (from < 0 || to < 0) return;
        var card = Cards[from];
        Cards.RemoveAt(from);
        Cards.Insert(to, card);
        Save();
    }

    public static void Resize(string type, int cols, int rows)
    {
        var card = Cards.FirstOrDefault(c => c.Type == type);
        if (card == null || Info(type) is not { } info) return;
        cols = Math.Clamp(cols, info.MinCols, GridColumns);
        rows = Math.Clamp(rows, info.MinRows, MaxRows);
        if (card.Cols == cols && card.Rows == rows) return;
        card.Cols = cols;
        card.Rows = rows;
        Save();
    }

    public static void ResetHome()
    {
        SettingsStore.Current.Behavior.HomeCards = SettingsStore.DefaultHomeCards();
        Save();
    }

    /// <summary>
    /// Pack cards onto a 12-column grid in order, each at the first spot it fits ("dense" packing),
    /// so a short card can slide in under another. Returns (column, row) per card.
    /// </summary>
    public static List<(HomeCard Card, int Col, int Row)> Pack(IReadOnlyList<HomeCard> cards)
    {
        var used = new List<bool[]>();
        bool Free(int col, int row, int w, int h)
        {
            for (var r = row; r < row + h; r++)
            {
                while (used.Count <= r) used.Add(new bool[GridColumns]);
                for (var c = col; c < col + w; c++) if (used[r][c]) return false;
            }
            return true;
        }
        var result = new List<(HomeCard, int, int)>();
        foreach (var card in cards)
        {
            var w = Math.Clamp(card.Cols, 1, GridColumns);
            var h = Math.Clamp(card.Rows, 1, MaxRows);
            for (var row = 0; ; row++)
            {
                var placed = false;
                for (var col = 0; col + w <= GridColumns; col++)
                {
                    if (!Free(col, row, w, h)) continue;
                    for (var r = row; r < row + h; r++)
                        for (var c = col; c < col + w; c++) used[r][c] = true;
                    result.Add((card, col, row));
                    placed = true;
                    break;
                }
                if (placed) break;
            }
        }
        return result;
    }
}
