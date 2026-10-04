using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Notchify.Core;

namespace Notchify.Services;

public enum ClipKind { Text, Image, Files }

public sealed class ClipItem : ObservableObject
{
    private bool _pinned;
    private string? _ocrText;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ClipKind Kind { get; set; }
    public string? Text { get; set; }
    public string? ImagePath { get; set; }
    public List<string>? Files { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    public string? SourceApp { get; set; }
    public bool Pinned { get => _pinned; set { if (Set(ref _pinned, value)) Raise(nameof(PinGlyph)); } }
    public string? OcrText { get => _ocrText; set => Set(ref _ocrText, value); }

    [JsonIgnore] public string PinGlyph => _pinned ? Glyphs.Unpin : Glyphs.Pin;

    [JsonIgnore]
    public string Preview => Kind switch
    {
        ClipKind.Text => (Text ?? "").Trim().Replace("\r", "").Replace("\n", " ⏎ ") is var t && t.Length > 160 ? t[..160] + "…" : (Text ?? "").Trim(),
        ClipKind.Image => string.IsNullOrWhiteSpace(OcrText) ? "Image" : "Image · " + OcrText!.Replace("\n", " ")[..Math.Min(80, OcrText.Replace("\n", " ").Length)],
        ClipKind.Files => Files is { Count: 1 } ? Path.GetFileName(Files[0]) : $"{Files?.Count} files",
        _ => "",
    };

    [JsonIgnore] public ImageSource? Thumbnail => ImagePath != null ? Ui.LoadImage(ImagePath, 160) : null;
    [JsonIgnore] public bool HasThumbnail => Kind == ClipKind.Image;
    [JsonIgnore] public string KindGlyph => Kind switch { ClipKind.Image => Glyphs.Photo, ClipKind.Files => Glyphs.Folder, _ => SmartGlyph };
    [JsonIgnore] public string Age => (DateTime.Now - Created) switch
    {
        var d when d.TotalMinutes < 1 => "now",
        var d when d.TotalHours < 1 => $"{(int)d.TotalMinutes}m",
        var d when d.TotalDays < 1 => $"{(int)d.TotalHours}h",
        var d => $"{(int)d.TotalDays}d",
    };

    // ---------- Smart actions ----------
    private static readonly Regex UrlRx = new(@"^(https?://|www\.)\S+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EmailRx = new(@"^[^@\s]+@[^@\s]+\.[a-z]{2,}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HexRx = new(@"^#?([0-9a-f]{3}|[0-9a-f]{6}|[0-9a-f]{8})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RgbRx = new(@"^rgba?\(\s*\d+\s*,\s*\d+\s*,\s*\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [JsonIgnore] public bool IsUrl => Kind == ClipKind.Text && UrlRx.IsMatch(Text?.Trim() ?? "");
    [JsonIgnore] public bool IsEmail => Kind == ClipKind.Text && EmailRx.IsMatch(Text?.Trim() ?? "");
    [JsonIgnore] public bool IsColor => Kind == ClipKind.Text && (HexRx.IsMatch(Text?.Trim() ?? "") || RgbRx.IsMatch(Text?.Trim() ?? ""));

    [JsonIgnore]
    public Brush? ColorSwatch
    {
        get
        {
            if (!IsColor) return null;
            var t = Text!.Trim();
            if (HexRx.IsMatch(t)) return Ui.Brush(t.StartsWith('#') ? t : "#" + t);
            var nums = Regex.Matches(t, @"\d+").Select(m => byte.Parse(m.Value)).ToArray();
            return nums.Length >= 3 ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(nums[0], nums[1], nums[2])) : null;
        }
    }

    [JsonIgnore] public string SmartGlyph => IsUrl ? Glyphs.Link : IsEmail ? Glyphs.Mail : IsColor ? Glyphs.Color : Glyphs.Text;
    [JsonIgnore] public bool HasSmartAction => IsUrl || IsEmail;
    [JsonIgnore] public string SmartActionLabel => IsUrl ? "Open" : IsEmail ? "Compose" : "";

    public void RunSmartAction()
    {
        var t = Text?.Trim() ?? "";
        if (IsUrl) Ui.OpenUrl(t.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + t : t);
        else if (IsEmail) Ui.OpenUrl("mailto:" + t);
    }

    public bool Matches(string query) =>
        string.IsNullOrWhiteSpace(query) ||
        (Text?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (OcrText?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (Files?.Any(f => f.Contains(query, StringComparison.OrdinalIgnoreCase)) ?? false);
}

/// <summary>
/// Clipboard history. Listens with AddClipboardFormatListener (no polling), respects the
/// "exclude from history" hints password managers set, OCRs images on-device, and keeps pins forever.
/// </summary>
public sealed class ClipboardService
{
    private const string StoreName = "clipboard";
    private bool _ignoreNext;
    private DateTime _lastCapture;

    public ObservableCollection<ClipItem> Items { get; } = new();

    /// <summary>Set by Screen Capture so the next image also lands on the shelf.</summary>
    public bool NextImageToShelf { get; set; }

    public event Action<ClipItem>? Captured;

    public void Load()
    {
        foreach (var item in JsonStore.Load<List<ClipItem>>(StoreName)) Items.Add(item);
        Items.CollectionChanged += (_, e) => { if (e.Action != NotifyCollectionChangedAction.Move) Save(); };
    }

    public void Save() => JsonStore.Save(StoreName, Items.ToList());

    /// <summary>Called by the notch window on WM_CLIPBOARDUPDATE.</summary>
    public void OnClipboardChanged()
    {
        if (_ignoreNext) { _ignoreNext = false; return; }
        var s = SettingsStore.Current.Clipboard;
        if (!s.Enabled) return;
        // Debounce apps that set the clipboard several times in a row.
        if ((DateTime.Now - _lastCapture).TotalMilliseconds < 80) return;
        _lastCapture = DateTime.Now;
        // Let the source app finish writing every format.
        Ui.Dispatcher.BeginInvoke(Capture, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void Capture()
    {
        try
        {
            var s = SettingsStore.Current.Clipboard;
            var data = Clipboard.GetDataObject();
            if (data == null) return;

            // Password managers mark secrets with these formats — never record them.
            if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") ||
                data.GetDataPresent("Clipboard Viewer Ignore")) return;
            if (data.GetDataPresent("CanIncludeInClipboardHistory") &&
                data.GetData("CanIncludeInClipboardHistory") is MemoryStream ms && ms.Length >= 4 &&
                BitConverter.ToInt32(ms.ToArray(), 0) == 0) return;

            var owner = Native.GetClipboardOwner();
            var app = owner != IntPtr.Zero ? Native.GetProcessName(owner) : null;
            if (app != null && s.IgnoredApps.Any(a => app.Contains(a, StringComparison.OrdinalIgnoreCase))) return;

            ClipItem? item = null;
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                item = new ClipItem { Kind = ClipKind.Files, Files = files.ToList() };
            }
            else if (data.GetDataPresent(DataFormats.UnicodeText))
            {
                var text = data.GetData(DataFormats.UnicodeText) as string;
                if (string.IsNullOrEmpty(text)) return;
                item = new ClipItem { Kind = ClipKind.Text, Text = text };
            }
            else if (s.CaptureImages && Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img == null) return;
                var path = Path.Combine(Paths.Clips, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
                SavePng(img, path);
                item = new ClipItem { Kind = ClipKind.Image, ImagePath = path };
                if (NextImageToShelf)
                {
                    NextImageToShelf = false;
                    var shelfCopy = Path.Combine(Paths.Shelf, $"Capture {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
                    File.Copy(path, shelfCopy, true);
                    Notch.Shelf.Add(new[] { shelfCopy });
                }
                if (s.Ocr) _ = OcrAsync(item);
            }
            if (item == null) return;
            item.SourceApp = app;
            Add(item);
            Captured?.Invoke(item);
        }
        catch (Exception ex)
        {
            // The clipboard is often briefly locked by another process; skipping one copy is fine.
            Log.Info("clipboard read: " + ex.Message);
        }
    }

    private void Add(ClipItem item)
    {
        // De-duplicate: re-copying moves the item to the top.
        var dup = Items.FirstOrDefault(i => i.Kind == item.Kind && i.Kind == ClipKind.Text && i.Text == item.Text);
        if (dup != null)
        {
            Items.Move(Items.IndexOf(dup), 0);
            dup.Created = DateTime.Now;
            Save();
            return;
        }
        Items.Insert(0, item);
        Trim();
    }

    private void Trim()
    {
        var max = SettingsStore.Current.Clipboard.MaxItems;
        // Pins survive the cap.
        while (Items.Count(i => !i.Pinned) > max)
        {
            var victim = Items.Last(i => !i.Pinned);
            DeleteFiles(victim);
            Items.Remove(victim);
        }
    }

    public void Remove(ClipItem item)
    {
        DeleteFiles(item);
        Items.Remove(item);
    }

    /// <summary>Clears everything except pinned items.</summary>
    public void Clear()
    {
        foreach (var i in Items.Where(i => !i.Pinned).ToList()) Remove(i);
    }

    public void TogglePin(ClipItem item)
    {
        item.Pinned = !item.Pinned;
        Save();
    }

    private static void DeleteFiles(ClipItem item)
    {
        if (item.ImagePath != null && item.ImagePath.StartsWith(Paths.Clips))
            try { File.Delete(item.ImagePath); } catch { }
    }

    /// <summary>Put an item back on the clipboard without re-recording it.</summary>
    public void Copy(ClipItem item)
    {
        _ignoreNext = true;
        try
        {
            switch (item.Kind)
            {
                case ClipKind.Text: Clipboard.SetText(item.Text ?? ""); break;
                case ClipKind.Image when item.ImagePath != null:
                    var img = Ui.LoadImage(item.ImagePath);
                    if (img != null) Clipboard.SetImage(img);
                    break;
                case ClipKind.Files when item.Files != null:
                    var col = new System.Collections.Specialized.StringCollection();
                    col.AddRange(item.Files.ToArray());
                    Clipboard.SetFileDropList(col);
                    break;
            }
            Items.Move(Items.IndexOf(item), 0);
        }
        catch (Exception ex) { _ignoreNext = false; Log.Error("clipboard write", ex); }
    }

    public void SetTextSilently(string text)
    {
        _ignoreNext = true;
        try { Clipboard.SetText(text); } catch { _ignoreNext = false; }
    }

    /// <summary>Copy then paste into the app that was focused before the notch opened.</summary>
    public async void Paste(ClipItem item)
    {
        Copy(item);
        await PasteIntoPreviousAppAsync();
    }

    public static async Task PasteIntoPreviousAppAsync()
    {
        var target = Notch.Shell.LastExternalWindow;
        Notch.Shell.Collapse();
        if (target == IntPtr.Zero || !SettingsStore.Current.Clipboard.AutoPaste) return;
        Native.SetForegroundWindow(target);
        await Task.Delay(120);
        Native.SendChord(Native.VK_CONTROL, Native.VK_V);
    }

    private async Task OcrAsync(ClipItem item)
    {
        if (item.ImagePath == null) return;
        var text = await OcrService.RecognizeAsync(item.ImagePath);
        if (string.IsNullOrWhiteSpace(text)) return;
        item.OcrText = text;
        Save();
    }

    public static void SavePng(BitmapSource img, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}

/// <summary>On-device OCR with Windows.Media.Ocr — nothing leaves the PC.</summary>
public static class OcrService
{
    public static async Task<string?> RecognizeAsync(string imagePath)
    {
        try
        {
            var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null) return null;
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(imagePath);
            using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            using var bmp = await decoder.GetSoftwareBitmapAsync();
            if (bmp.PixelWidth > Windows.Media.Ocr.OcrEngine.MaxImageDimension ||
                bmp.PixelHeight > Windows.Media.Ocr.OcrEngine.MaxImageDimension) return null;
            var result = await engine.RecognizeAsync(bmp);
            return string.Join("\n", result.Lines.Select(l => l.Text));
        }
        catch (Exception ex)
        {
            Log.Info("ocr: " + ex.Message);
            return null;
        }
    }
}
