using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using Notchify.Core;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Notchify.Services;

/// <summary>One page in the editor: where it comes from, how it's turned, and its thumbnail.</summary>
public sealed class PdfPageItem : ObservableObject
{
    private int _rotation;
    private int _number;
    private ImageSource? _thumbnail;

    public string Source { get; init; } = "";
    public int Index { get; init; }
    /// <summary>Extra clockwise rotation added in the editor (0, 90, 180, 270).</summary>
    public int Rotation { get => _rotation; set => Set(ref _rotation, ((value % 360) + 360) % 360); }
    public int Number { get => _number; set => Set(ref _number, value); }
    public ImageSource? Thumbnail { get => _thumbnail; set => Set(ref _thumbnail, value); }
    public string SourceName => Path.GetFileName(Source);
}

/// <summary>Text added to pages when the PDF is saved.</summary>
public sealed record PdfStamp(string Text, string Position, bool SelectedOnly, HashSet<PdfPageItem> Pages);

/// <summary>
/// A PDF being edited: reorder, rotate, delete, merge other PDFs, extract or split pages, add text,
/// page numbers and a password. Nothing changes on disk until you save, and saving never overwrites
/// the original — it writes "name (edited).pdf" next to it unless you choose otherwise.
/// </summary>
public sealed class PdfEditSession
{
    static PdfEditSession()
    {
        // PDFsharp 6 needs to be told where fonts come from: read them straight from C:\Windows\Fonts.
        try { GlobalFontSettings.FontResolver ??= new WindowsFontResolver(); } catch { }
    }

    /// <summary>Maps the fonts the editor uses to their files in the Windows font folder.</summary>
    private sealed class WindowsFontResolver : IFontResolver
    {
        private static readonly string Fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
        {
            string[] candidates = bold ? new[] { "segoeuib.ttf", "arialbd.ttf" } : new[] { "segoeui.ttf", "arial.ttf" };
            var file = candidates.FirstOrDefault(f => File.Exists(Path.Combine(Fonts, f)));
            return file == null ? null : new FontResolverInfo(file);
        }

        public byte[]? GetFont(string faceName)
        {
            var path = Path.Combine(Fonts, faceName);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
    }

    public string FilePath { get; }
    public ObservableCollection<PdfPageItem> Pages { get; } = new();
    public List<PdfStamp> Stamps { get; } = new();
    public bool PageNumbers { get; set; }
    public string Password { get; set; } = "";
    public bool Dirty { get; set; }

    private PdfEditSession(string path) => FilePath = path;

    public static async Task<PdfEditSession> OpenAsync(string path)
    {
        var s = new PdfEditSession(path);
        await s.AppendAsync(path);
        s.Dirty = false;
        return s;
    }

    /// <summary>Add every page of another PDF at the end (merge).</summary>
    public async Task AppendAsync(string path)
    {
        int count;
        try
        {
            using var probe = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            count = probe.PageCount;
        }
        catch (Exception ex)
        {
            throw new IOException($"Couldn't open {Path.GetFileName(path)} — it may be password-protected or damaged. ({ex.Message})");
        }
        var added = new List<PdfPageItem>();
        for (var i = 0; i < count; i++)
        {
            var item = new PdfPageItem { Source = path, Index = i };
            Pages.Add(item);
            added.Add(item);
        }
        Renumber();
        Dirty = true;
        await RenderThumbnailsAsync(path, added);
    }

    /// <summary>Small page previews, rendered by Windows' own PDF engine.</summary>
    private static async Task RenderThumbnailsAsync(string path, List<PdfPageItem> items)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var pdf = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
            foreach (var item in items)
            {
                if (item.Index >= pdf.PageCount) continue;
                using var page = pdf.GetPage((uint)item.Index);
                using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(ras, new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = 160 });
                ras.Seek(0);
                var ms = new MemoryStream();
                await ras.AsStreamForRead().CopyToAsync(ms);
                ms.Position = 0;
                item.Thumbnail = Ui.LoadImage(ms);
            }
        }
        catch (Exception ex) { Log.Info("pdf thumbnails: " + ex.Message); }
    }

    public void Renumber()
    {
        for (var i = 0; i < Pages.Count; i++) Pages[i].Number = i + 1;
    }

    public void Rotate(IEnumerable<PdfPageItem> items, int degrees)
    {
        foreach (var p in items) p.Rotation += degrees;
        Dirty = true;
    }

    public void Delete(IEnumerable<PdfPageItem> items)
    {
        foreach (var p in items.ToList()) Pages.Remove(p);
        Renumber();
        Dirty = true;
    }

    /// <summary>Move the selection one place earlier (-1) or later (+1), keeping it together.</summary>
    public void Move(IList<PdfPageItem> items, int delta)
    {
        var ordered = items.OrderBy(Pages.IndexOf).ToList();
        if (delta > 0) ordered.Reverse();
        foreach (var p in ordered)
        {
            var i = Pages.IndexOf(p);
            var j = i + delta;
            if (j < 0 || j >= Pages.Count || items.Contains(Pages[j])) continue;
            Pages.Move(i, j);
        }
        Renumber();
        Dirty = true;
    }

    public void MoveTo(PdfPageItem item, PdfPageItem target)
    {
        var from = Pages.IndexOf(item);
        var to = Pages.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;
        Pages.Move(from, to);
        Renumber();
        Dirty = true;
    }

    // ---------------- Saving ----------------

    public string DefaultSavePath(string suffix = " (edited)") =>
        ShelfService.Unique(Path.Combine(ShelfService.OutputDir(FilePath), Path.GetFileNameWithoutExtension(FilePath) + suffix + ".pdf"));

    /// <summary>Write the edited document (all pages, or just <paramref name="only"/>) to <paramref name="target"/>.</summary>
    public Task SaveAsync(string target, IReadOnlyCollection<PdfPageItem>? only = null)
    {
        var pages = (only ?? Pages).OrderBy(Pages.IndexOf).ToList();
        var stamps = Stamps.ToList();
        var numbers = PageNumbers;
        var password = Password;
        return Task.Run(() => Write(pages, stamps, numbers, password, target));
    }

    /// <summary>Each page as its own PDF in a new folder next to the original.</summary>
    public async Task<string> SplitAsync()
    {
        var dir = ShelfService.Unique(Path.Combine(ShelfService.OutputDir(FilePath), Path.GetFileNameWithoutExtension(FilePath) + " pages"));
        Directory.CreateDirectory(dir);
        var name = Path.GetFileNameWithoutExtension(FilePath);
        foreach (var p in Pages.ToList())
            await SaveAsync(Path.Combine(dir, $"{name} p{p.Number}.pdf"), new[] { p });
        return dir;
    }

    private static void Write(List<PdfPageItem> pages, List<PdfStamp> stamps, bool pageNumbers, string password, string target)
    {
        var sources = new Dictionary<string, PdfDocument>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var output = new PdfDocument();
            foreach (var item in pages)
            {
                if (!sources.TryGetValue(item.Source, out var src))
                    sources[item.Source] = src = PdfReader.Open(item.Source, PdfDocumentOpenMode.Import);
                var page = output.AddPage(src.Pages[item.Index]);
                if (item.Rotation != 0) page.Rotate = (page.Rotate + item.Rotation) % 360;
            }

            var total = output.PageCount;
            for (var i = 0; i < total; i++)
            {
                var item = pages[i];
                var mine = stamps.Where(s => !s.SelectedOnly || s.Pages.Contains(item)).ToList();
                if (mine.Count == 0 && !pageNumbers) continue;
                var page = output.Pages[i];
                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                // Draw in the orientation the reader sees: a turned page turns its drawing too, so undo that.
                var pw = gfx.PageSize.Width;
                var ph = gfx.PageSize.Height;
                var rot = ((page.Rotate % 360) + 360) % 360;
                switch (rot)
                {
                    case 90: gfx.TranslateTransform(0, ph); gfx.RotateTransform(-90); break;
                    case 180: gfx.TranslateTransform(pw, ph); gfx.RotateTransform(180); break;
                    case 270: gfx.TranslateTransform(pw, 0); gfx.RotateTransform(90); break;
                }
                var w = rot is 90 or 270 ? ph : pw;
                var h = rot is 90 or 270 ? pw : ph;
                foreach (var s in mine) DrawStamp(gfx, s, w, h);
                if (pageNumbers)
                {
                    var font = new XFont("Segoe UI", 9);
                    gfx.DrawString($"{i + 1} / {total}", font, XBrushes.DimGray, new XRect(0, h - 28, w, 20), XStringFormats.Center);
                }
            }

            if (!string.IsNullOrEmpty(password))
            {
                output.SecuritySettings.UserPassword = password;
                output.SecuritySettings.OwnerPassword = password;
            }

            // Write to a temporary file first so a failure never leaves half a PDF behind.
            var tmp = target + ".tmp";
            output.Save(tmp);
            File.Move(tmp, target, true);
        }
        finally
        {
            foreach (var d in sources.Values) d.Dispose();
        }
    }

    private static void DrawStamp(XGraphics gfx, PdfStamp s, double w, double h)
    {
        switch (s.Position)
        {
            case "Watermark":
            {
                var size = Math.Clamp(w / Math.Max(4, s.Text.Length) * 1.4, 20, 120);
                var font = new XFont("Segoe UI", size, XFontStyleEx.Bold);
                var brush = new XSolidBrush(XColor.FromArgb(55, 120, 120, 120));
                var state = gfx.Save();
                gfx.TranslateTransform(w / 2, h / 2);
                gfx.RotateTransform(-45);
                gfx.DrawString(s.Text, font, brush, new XRect(-w, -size, 2 * w, 2 * size), XStringFormats.Center);
                gfx.Restore(state);
                break;
            }
            case "Bottom":
                gfx.DrawString(s.Text, new XFont("Segoe UI", 11), XBrushes.Black, new XRect(36, h - 52, w - 72, 20), XStringFormats.Center);
                break;
            default: // Top
                gfx.DrawString(s.Text, new XFont("Segoe UI", 11), XBrushes.Black, new XRect(36, 24, w - 72, 20), XStringFormats.Center);
                break;
        }
    }
}
