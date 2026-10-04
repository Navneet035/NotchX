using System.Windows;
using Notchify.Core;
using Notchify.Services;
using Notchify.Views;

namespace Notchify.Modules;

/// <summary>Clipboard history with pins, smart actions (links, colours, emails), OCR search and auto-paste.</summary>
public sealed class ClipboardModule : NotchModule
{
    public override string Id => "clipboard";
    public override string Title => "Clipboard";
    public override string Glyph => Glyphs.Clipboard;
    public override string Description => "Every copy in a searchable history. Pins survive the cap and Clear. Images are OCR'd on-device.";

    public ClipboardService Service => Notch.Clipboard;

    protected override void Start()
    {
        SettingsStore.Current.Clipboard.Enabled = true;
        Notch.Clipboard.Captured += OnCaptured;
    }

    protected override void Stop()
    {
        SettingsStore.Current.Clipboard.Enabled = false;
        Notch.Clipboard.Captured -= OnCaptured;
    }

    private static void OnCaptured(ClipItem item)
    {
        // A copied colour gets its own little island with the swatch.
        if (item.IsColor && !Notch.Shell.IsExpanded)
            Notch.Hub.Show(new Island
            {
                Key = "color",
                Glyph = Glyphs.Color,
                Title = item.Text!.Trim(),
                Message = "Colour copied",
                Accent = item.ColorSwatch ?? Ui.Accent,
                Priority = IslandPriority.Low,
                Duration = TimeSpan.FromSeconds(2),
            });
    }

    public override FrameworkElement CreateView() => new ClipboardView();

    public override IEnumerable<PaletteCommand> Commands
    {
        get
        {
            yield return new PaletteCommand("Clear clipboard history (keeps pins)", Glyphs.Delete, Notch.Clipboard.Clear, null, "clipboard");
            foreach (var item in Notch.Clipboard.Items.Where(i => i.Pinned).Take(20))
            {
                var it = item;
                yield return new PaletteCommand("Paste pinned: " + it.Preview, Glyphs.Pin, () => Notch.Clipboard.Paste(it), null, "clip");
            }
        }
    }
}
