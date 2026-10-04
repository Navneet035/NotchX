using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Notchify.Core;

namespace Notchify.Services;

public sealed class ShelfItem : ObservableObject
{
    private bool _selected;
    public string Path { get; set; } = "";
    public DateTime Added { get; set; } = DateTime.Now;

    [JsonIgnore] public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\'));
    [JsonIgnore] public bool Exists => File.Exists(Path) || Directory.Exists(Path);
    [JsonIgnore] public bool IsImage => ShelfService.ImageExtensions.Contains(System.IO.Path.GetExtension(Path).ToLowerInvariant());
    [JsonIgnore] public bool IsZip => System.IO.Path.GetExtension(Path).Equals(".zip", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool IsPdf => System.IO.Path.GetExtension(Path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public ImageSource? Thumbnail => IsImage ? Ui.LoadImage(Path, 120) ?? Ui.FileIcon(Path) : Ui.FileIcon(Path);
    [JsonIgnore] public bool Selected { get => _selected; set => Set(ref _selected, value); }
    [JsonIgnore]
    public string SizeText
    {
        get
        {
            try { return File.Exists(Path) ? Ui.FormatBytes(new FileInfo(Path).Length) : "Folder"; }
            catch { return ""; }
        }
    }
}

/// <summary>
/// The file shelf: drag files onto the notch to stash them, drag them out wherever you need.
/// Survives restarts. Also hosts zip/unzip and offline image conversion.
/// </summary>
public sealed class ShelfService
{
    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".heic", ".heif", ".webp", ".ico", ".jxr" };

    public ObservableCollection<ShelfItem> Items { get; } = new();

    public void Load()
    {
        foreach (var i in JsonStore.Load<List<ShelfItem>>("shelf").Where(i => i.Exists)) Items.Add(i);
    }

    private void Save() => JsonStore.Save("shelf", Items.ToList());

    public void Add(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (Items.Any(i => string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
            Items.Insert(0, new ShelfItem { Path = p });
        }
        Save();
    }

    public void Remove(ShelfItem item)
    {
        Items.Remove(item);
        // Files Notchify created itself (captures, conversions) are cleaned up with the shelf entry.
        if (item.Path.StartsWith(Paths.Shelf, StringComparison.OrdinalIgnoreCase))
            try { File.Delete(item.Path); } catch { }
        Save();
    }

    public void Clear()
    {
        foreach (var i in Items.ToList()) Remove(i);
    }

    public IEnumerable<ShelfItem> Selection => Items.Any(i => i.Selected) ? Items.Where(i => i.Selected) : Items;

    internal static string OutputDir(string source)
    {
        var dir = System.IO.Path.GetDirectoryName(source);
        if (dir != null)
        {
            try
            {
                var probe = System.IO.Path.Combine(dir, ".notchify-probe");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return dir;
            }
            catch { }
        }
        return Paths.Shelf;
    }

    internal static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = System.IO.Path.GetDirectoryName(path)!;
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var ext = System.IO.Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var candidate = System.IO.Path.Combine(dir, $"{name} ({n}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    public async Task<string?> ZipAsync(IReadOnlyList<ShelfItem> items)
    {
        if (items.Count == 0) return null;
        var first = items[0].Path;
        var baseName = items.Count == 1 ? System.IO.Path.GetFileNameWithoutExtension(first) : "Archive";
        var target = Unique(System.IO.Path.Combine(OutputDir(first), baseName + ".zip"));
        await Task.Run(() =>
        {
            using var zip = ZipFile.Open(target, ZipArchiveMode.Create);
            foreach (var item in items)
            {
                if (File.Exists(item.Path))
                    zip.CreateEntryFromFile(item.Path, System.IO.Path.GetFileName(item.Path), CompressionLevel.Optimal);
                else if (Directory.Exists(item.Path))
                {
                    var root = System.IO.Path.GetDirectoryName(item.Path.TrimEnd('\\'))!;
                    foreach (var f in Directory.EnumerateFiles(item.Path, "*", SearchOption.AllDirectories))
                        zip.CreateEntryFromFile(f, System.IO.Path.GetRelativePath(root, f).Replace('\\', '/'), CompressionLevel.Optimal);
                }
            }
        });
        Add(new[] { target });
        return target;
    }

    public async Task<string?> UnzipAsync(ShelfItem item)
    {
        if (!item.IsZip) return null;
        var target = Unique(System.IO.Path.Combine(OutputDir(item.Path), System.IO.Path.GetFileNameWithoutExtension(item.Path)));
        await Task.Run(() => ZipFile.ExtractToDirectory(item.Path, target));
        Add(new[] { target });
        return target;
    }

    /// <summary>
    /// Offline image conversion via WIC. HEIC input works when the free "HEIF Image Extensions"
    /// from the Microsoft Store is installed (it ships with most Windows 11 PCs).
    /// PDFs are rendered page by page with Windows.Data.Pdf.
    /// </summary>
    public async Task<List<string>> ConvertAsync(ShelfItem item, string format)
    {
        var outputs = new List<string>();
        var dir = OutputDir(item.Path);
        var name = System.IO.Path.GetFileNameWithoutExtension(item.Path);
        var ext = format.ToLowerInvariant() switch { "jpeg" or "jpg" => ".jpg", "tiff" => ".tiff", "bmp" => ".bmp", "gif" => ".gif", _ => ".png" };

        if (item.IsPdf)
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(item.Path);
            var pdf = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
            for (uint i = 0; i < pdf.PageCount; i++)
            {
                using var page = pdf.GetPage(i);
                using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(ras, new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = (uint)(page.Size.Width * 2) });
                using var s = ras.AsStreamForRead();
                var frame = BitmapFrame.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var target = Unique(System.IO.Path.Combine(dir, $"{name} p{i + 1}{ext}"));
                Encode(frame, target, ext);
                outputs.Add(target);
            }
        }
        else
        {
            await Task.Run(() =>
            {
                var decoder = BitmapDecoder.Create(new Uri(item.Path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var target = Unique(System.IO.Path.Combine(dir, name + ext));
                Encode(decoder.Frames[0], target, ext);
                outputs.Add(target);
            });
        }
        Add(outputs);
        return outputs;
    }

    private static void Encode(BitmapSource frame, string path, string ext)
    {
        BitmapEncoder enc = ext switch
        {
            ".jpg" => new JpegBitmapEncoder { QualityLevel = 92 },
            ".tiff" => new TiffBitmapEncoder(),
            ".bmp" => new BmpBitmapEncoder(),
            ".gif" => new GifBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };
        // JPEG/BMP don't support alpha; flatten onto white.
        if (ext is ".jpg" or ".bmp" && frame.Format != PixelFormats.Bgr24)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new System.Windows.Rect(0, 0, frame.PixelWidth, frame.PixelHeight));
                dc.DrawImage(frame, new System.Windows.Rect(0, 0, frame.PixelWidth, frame.PixelHeight));
            }
            var rtb = new RenderTargetBitmap(frame.PixelWidth, frame.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            frame = rtb;
        }
        enc.Frames.Add(BitmapFrame.Create(frame));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
