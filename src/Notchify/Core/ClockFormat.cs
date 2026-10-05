using System.Globalization;

namespace Notchify.Core;

/// <summary>Formats the time and date the way the user set up in Settings › Appearance › Clock.</summary>
public static class ClockFormat
{
    private static ClockSettings C => SettingsStore.Current.Clock;

    public static string Time(DateTime t)
    {
        var c = C;
        string pattern;
        switch (c.TimeFormat)
        {
            case "12h":
                pattern = "h:mm" + (c.ShowSeconds ? ":ss" : "") + (c.ShowAmPm ? " tt" : "");
                break;
            case "24h":
                pattern = "HH:mm" + (c.ShowSeconds ? ":ss" : "");
                break;
            default:
                var f = CultureInfo.CurrentCulture.DateTimeFormat;
                pattern = c.ShowSeconds ? f.LongTimePattern : f.ShortTimePattern;
                if (!c.ShowAmPm) pattern = pattern.Replace("tt", "").Trim();
                break;
        }
        // Some cultures have no AM/PM designator; don't leave a dangling space.
        return t.ToString(pattern).Trim();
    }

    public static string Date(DateTime t) => C.DateStyle switch
    {
        "None" => "",
        "Day" => t.ToString("ddd"),
        "Long" => t.ToString("dddd, d MMMM"),
        "Numeric" => t.ToString(CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern),
        _ => t.ToString("ddd d MMM"),
    };

    /// <summary>Date and time for the open notch's header.</summary>
    public static string Header(DateTime t)
    {
        var date = Date(t);
        return date.Length == 0 ? Time(t) : $"{date}  {Time(t)}";
    }

    /// <summary>How often a clock needs to redraw.</summary>
    public static TimeSpan TickInterval => C.ShowSeconds ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5);
}
