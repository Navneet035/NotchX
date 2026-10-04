using System.Windows;
using Notchify.Core;
using Notchify.Services;
using Notchify.Views;

namespace Notchify.Modules;

/// <summary>File Shelf: drop files on the notch, drag them out later. Plus zip/unzip and offline image conversion.</summary>
public sealed class ShelfModule : NotchModule
{
    public override string Id => "shelf";
    public override string Title => "Shelf";
    public override string Glyph => Glyphs.Folder;
    public override string Description => "Drag files onto the notch to stash them. Zip, unzip and convert images (HEIC, PNG, JPEG, TIFF, PDF) offline.";

    public ShelfService Service => Notch.Shelf;

    public override FrameworkElement CreateView() => new ShelfView();

    public override IEnumerable<PaletteCommand> Commands => new[]
    {
        new PaletteCommand("Clear shelf", Glyphs.Delete, Notch.Shelf.Clear, null, "files shelf"),
        new PaletteCommand("Zip everything on the shelf", Glyphs.Package, async () =>
        {
            var path = await Notch.Shelf.ZipAsync(Notch.Shelf.Selection.ToList());
            if (path != null) Notch.Hub.Notify(Glyphs.Package, "Zipped", System.IO.Path.GetFileName(path), Ui.Green);
        }, null, "compress archive"),
    };
}
