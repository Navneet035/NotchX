namespace Notchify.Core;

/// <summary>A ready-made look: background, gradient and accent picked to go together. Everything stays adjustable afterwards.</summary>
public sealed record ThemePreset(string Name, string Background, string Gradient, double Angle, string Accent, string Description);

public static class ThemePresets
{
    public static readonly ThemePreset[] All =
    {
        new("Graphite", "#F2141418", "#F2202028", 90, "#FF0A84FF", "Deep grey glass with a blue accent"),
        new("Midnight", "#F20A0E1C", "#F2182040", 120, "#FF6E9BFF", "Inky navy fading to indigo"),
        new("Aurora", "#F20C1717", "#F2241534", 135, "#FF3DDC97", "Teal-black into violet, with mint"),
        new("Ember", "#F2181110", "#F22B1610", 100, "#FFFF9F0A", "Warm charcoal with an amber glow"),
        new("Lime", "#F2111211", "#F21B1F12", 90, "#FFB4E61E", "Near-black with a lime accent"),
        new("Rose", "#F2161012", "#F22A1420", 120, "#FFFF6B9A", "Dark plum with a rose accent"),
        new("Pure black", "#F2000000", "#F2000000", 90, "#FF0A84FF", "The original flat black"),
    };

    public static void Apply(ThemePreset t)
    {
        var a = SettingsStore.Current.Appearance;
        a.BackgroundColor = t.Background;
        a.GradientColor = t.Gradient;
        a.GradientAngle = t.Angle;
        a.AccentColor = t.Accent;
        a.BackgroundType = t.Background == t.Gradient ? "Solid" : "Gradient";
        SettingsStore.NotifyChanged();
    }

    /// <summary>The preset the current colours match, if any.</summary>
    public static ThemePreset? Current
    {
        get
        {
            var a = SettingsStore.Current.Appearance;
            return All.FirstOrDefault(t => t.Accent.Equals(a.AccentColor, StringComparison.OrdinalIgnoreCase)
                && t.Background.Equals(a.BackgroundColor, StringComparison.OrdinalIgnoreCase)
                && (t.Background == t.Gradient ? a.BackgroundType == "Solid" : a.BackgroundType == "Gradient"
                    && t.Gradient.Equals(a.GradientColor, StringComparison.OrdinalIgnoreCase)));
        }
    }
}
