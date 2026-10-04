using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Notchify.Core;

namespace Notchify.Services;

/// <summary>
/// Word ⇄ PDF conversion. Uses Microsoft Word when it's installed (best results, especially PDF → Word),
/// otherwise the free LibreOffice in headless mode. Everything runs on this PC; no file is uploaded.
/// </summary>
public static class DocumentService
{
    /// <summary>Files that can be turned into a PDF. Word handles the first group; LibreOffice handles them all.</summary>
    public static readonly HashSet<string> ToPdfExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".doc", ".docx", ".docm", ".dot", ".dotx", ".odt", ".rtf", ".txt", ".xls", ".xlsx", ".ods", ".ppt", ".pptx", ".odp" };

    private static readonly HashSet<string> WordReadable = new(StringComparer.OrdinalIgnoreCase)
        { ".doc", ".docx", ".docm", ".dot", ".dotx", ".odt", ".rtf", ".txt" };

    public static bool HasWord => Type.GetTypeFromProgID("Word.Application") != null;

    public static string? LibreOffice
    {
        get
        {
            var custom = SettingsStore.Current.Documents.LibreOfficePath;
            if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom)) return custom;
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                var p = Path.Combine(root, "LibreOffice", "program", "soffice.exe");
                if (File.Exists(p)) return p;
            }
            return null;
        }
    }

    /// <summary>Which engine a conversion will use, or null when none is available.</summary>
    public static string? Engine(bool forWordInput = true)
    {
        var choice = SettingsStore.Current.Documents.Converter;
        var word = HasWord && forWordInput;
        var lo = LibreOffice != null;
        return choice switch
        {
            "Word" when word => "Microsoft Word",
            "LibreOffice" when lo => "LibreOffice",
            _ when word => "Microsoft Word",
            _ when lo => "LibreOffice",
            _ => null,
        };
    }

    public const string InstallHint = "Install the free LibreOffice (winget install TheDocumentFoundation.LibreOffice) or Microsoft Word.";

    // ---------------- Public conversions ----------------

    /// <summary>Word (or Excel / PowerPoint / text) → PDF. Returns the new file.</summary>
    public static async Task<string> ToPdfAsync(string input)
    {
        var target = TargetFor(input, ".pdf");
        var engine = Engine(WordReadable.Contains(Path.GetExtension(input))) ?? throw new InvalidOperationException(InstallHint);
        if (engine == "Microsoft Word") await OnSta(() => WordConvert(input, target, toPdf: true));
        else await LibreConvertAsync(input, target, "pdf", null);
        return Finish(target);
    }

    /// <summary>PDF → editable Word document (.docx).</summary>
    public static async Task<string> ToWordAsync(string pdf)
    {
        var target = TargetFor(pdf, ".docx");
        var engine = Engine() ?? throw new InvalidOperationException(InstallHint);
        if (engine == "Microsoft Word") await OnSta(() => WordConvert(pdf, target, toPdf: false));
        else await LibreConvertAsync(pdf, target, "docx:MS Word 2007 XML", "writer_pdf_import");
        return Finish(target);
    }

    // ---------------- Output ----------------

    private static string TargetFor(string input, string ext)
    {
        var dir = SettingsStore.Current.Documents.SaveTo == "Documents"
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : ShelfService.OutputDir(input);
        return ShelfService.Unique(Path.Combine(dir, Path.GetFileNameWithoutExtension(input) + ext));
    }

    private static string Finish(string target)
    {
        if (!File.Exists(target)) throw new IOException("The converter finished but didn't produce a file.");
        var s = SettingsStore.Current.Documents;
        Ui.Post(() =>
        {
            if (s.AddToShelf) Notch.Shelf.Add(new[] { target });
            if (s.OpenWhenDone) Ui.OpenUrl(target);
        });
        return target;
    }

    // ---------------- Microsoft Word (COM) ----------------

    private static Task OnSta(Action work)
    {
        var tcs = new TaskCompletionSource();
        var t = new Thread(() =>
        {
            try { work(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    private static void WordConvert(string input, string output, bool toPdf)
    {
        dynamic word = Activator.CreateInstance(Type.GetTypeFromProgID("Word.Application")!)!;
        try
        {
            word.Visible = false;
            word.DisplayAlerts = 0; // wdAlertsNone
            // PDFs open through Word's "PDF Reflow", which turns them into editable documents.
            dynamic doc = word.Documents.Open(FileName: input, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false);
            try
            {
                if (toPdf) doc.ExportAsFixedFormat(OutputFileName: output, ExportFormat: 17 /* wdExportFormatPDF */);
                else doc.SaveAs2(FileName: output, FileFormat: 16 /* wdFormatXMLDocument */);
            }
            finally { doc.Close(SaveChanges: false); }
        }
        finally
        {
            try { word.Quit(SaveChanges: false); } catch { }
            Marshal.FinalReleaseComObject(word);
        }
    }

    // ---------------- LibreOffice (headless) ----------------

    private static async Task LibreConvertAsync(string input, string output, string convertTo, string? inFilter)
    {
        var soffice = LibreOffice ?? throw new InvalidOperationException(InstallHint);
        // A private profile, so a LibreOffice window the user has open doesn't block (or get hijacked by) the conversion.
        var profile = new Uri(Path.Combine(Paths.Cache, "libreoffice-profile")).AbsoluteUri;
        var work = Path.Combine(Paths.Cache, "convert", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var psi = new ProcessStartInfo(soffice)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add($"-env:UserInstallation={profile}");
            psi.ArgumentList.Add("--headless");
            psi.ArgumentList.Add("--norestore");
            psi.ArgumentList.Add("--nologo");
            if (inFilter != null) psi.ArgumentList.Add($"--infilter={inFilter}");
            psi.ArgumentList.Add("--convert-to");
            psi.ArgumentList.Add(convertTo);
            psi.ArgumentList.Add("--outdir");
            psi.ArgumentList.Add(work);
            psi.ArgumentList.Add(input);

            using var p = Process.Start(psi) ?? throw new IOException("Couldn't start LibreOffice.");
            var stderr = p.StandardError.ReadToEndAsync();
            _ = p.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                throw new TimeoutException("LibreOffice took too long and was stopped.");
            }
            var produced = Directory.GetFiles(work).FirstOrDefault();
            if (produced == null)
            {
                var err = (await stderr).Trim();
                throw new IOException(string.IsNullOrEmpty(err) ? "LibreOffice couldn't convert this file." : err);
            }
            File.Move(produced, output);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }
}
