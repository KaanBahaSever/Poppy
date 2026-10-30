using System.Runtime.InteropServices;
using Poppy.Core.Engines;
using E = Poppy.Core.Engines.Engines;

namespace Poppy.Core.Ops;

/// <summary>
/// Documents, slides, spreadsheets and e-books. Each pair (source, target) is routed to the
/// best engine present: Microsoft Office, then Pandoc / built-in code, then LibreOffice.
/// </summary>
public static class DocOps
{
    delegate Task<string> Job(string input, string output, CancellationToken ct);

    // Formats each engine opens and saves.
    // Not PDF: Word's PDF reflow hangs when Word runs hidden.
    static readonly HashSet<string> WordIn = ["docx", "doc", "odt", "rtf", "txt", "html", "htm"];
    static readonly Dictionary<string, (int Format, int Encoding)> WordOut = new()
    {
        ["pdf"] = (17, 0), ["docx"] = (16, 0), ["odt"] = (23, 0), ["rtf"] = (6, 0), ["html"] = (10, 65001), ["txt"] = (2, 65001),
    };
    static readonly HashSet<string> PandocIn = ["docx", "odt", "rtf", "md", "markdown", "html", "htm", "txt", "epub"];
    static readonly HashSet<string> PandocOut = ["docx", "odt", "rtf", "html", "md", "txt", "epub"];
    static readonly HashSet<string> LibreIn = ["docx", "doc", "odt", "rtf", "txt", "html", "htm", "pptx", "ppt", "odp", "xlsx", "xls", "ods", "csv"];
    static readonly HashSet<string> LibreOut = ["pdf", "docx", "odt", "rtf", "html", "txt", "pptx", "odp", "xlsx", "xls", "ods", "csv"];
    static readonly HashSet<string> SlidesIn = ["pptx", "ppt", "odp"];
    static readonly Dictionary<string, int> PowerPointOut = new() { ["pdf"] = 32, ["pptx"] = 24, ["odp"] = 35 };
    static readonly HashSet<string> ExcelIn = ["xlsx", "xls", "ods", "csv", "tsv"];
    static readonly Dictionary<string, int> ExcelOut = new() { ["xlsx"] = 51, ["xls"] = 56, ["ods"] = 60, ["csv"] = 62, ["html"] = 44 };

    static string Norm(string ext) => ext switch { "htm" => "html", "markdown" => "md", _ => ext };

    /// <summary>The engine that will do this conversion, or null if nothing installed can.</summary>
    static Job? Route(string src, string target)
    {
        src = Norm(src);
        var cat = FileKinds.Detect("x." + src);

        if (cat == FileCategory.Slides)
        {
            if (E.PowerPoint)
            {
                if (PowerPointOut.TryGetValue(target, out var fmt)) return (i, o, ct) => PowerPointSave(i, o, fmt, ct);
                if (target is "png" or "jpg") return (i, o, ct) => SlidesToImages(i, o, target, ct);
                if (target == "mp4") return PowerPointVideo;
            }
            if (E.LibreOffice != null)
            {
                if (LibreOut.Contains(target)) return Libre;
                if (target is "png" or "jpg") return (i, o, ct) => SlidesToImages(i, o, target, ct);
            }
            return null;
        }

        if (cat == FileCategory.Spreadsheet)
        {
            if (SheetOps.CanConvert(src, target)) return SheetOps.ConvertAsync;
            if (E.Excel && ExcelIn.Contains(src))
            {
                if (target == "pdf") return ExcelPdf;
                if (ExcelOut.TryGetValue(target, out var fmt)) return (i, o, ct) => ExcelSave(i, o, fmt, ct);
            }
            if (E.Excel && target is "json" or "tsv")
                return (i, o, ct) => ViaTemp(i, o, "xlsx", ExcelOutJob(51), SheetOps.ConvertAsync, ct);
            if (E.LibreOffice != null && LibreIn.Contains(src) && LibreOut.Contains(target)) return Libre;
            return null;
        }

        // Documents and e-books.
        if (E.Word && WordIn.Contains(src) && WordOut.ContainsKey(target) && src != "md") return WordJob(target);
        if (E.Pandoc != null && PandocIn.Contains(src))
        {
            if (PandocOut.Contains(target)) return Pandoc;
            if (target == "pdf")
            {
                if (E.Edge != null) return PandocToPdfViaEdge;
                if (E.Word) return (i, o, ct) => ViaTemp(i, o, "docx", Pandoc, WordJob("pdf"), ct);
                if (E.LibreOffice != null) return (i, o, ct) => ViaTemp(i, o, "docx", Pandoc, Libre, ct);
            }
        }
        if (src == "html" && target == "pdf" && E.Edge != null) return EdgePdf;
        if (E.LibreOffice != null && LibreIn.Contains(src) && LibreOut.Contains(target)) return Libre;
        if (src == "txt" && target == "md") return CopyAs;
        return null;
    }

    public static string? Unavailable(string path, string target)
    {
        var src = FileKinds.Ext(path);
        if (Route(src, target) != null) return null;
        if (FileKinds.Detect(path) is FileCategory.Document or FileCategory.Ebook && src is "md" or "markdown" or "epub")
            return L.T("need.pandoc");
        return L.T("need.office");
    }

    public static async Task<string> ConvertAsync(string input, string target, CancellationToken ct)
    {
        var job = Route(FileKinds.Ext(input), target) ?? throw new OpException(Unavailable(input, target)!);
        var multi = target is "png" or "jpg";
        var output = multi ? OutputPaths.NewFolder(input, target == "png" ? "slides" : "slides-jpg") : OutputPaths.For(input, target);
        try
        {
            await job(Path.GetFullPath(input), output, ct);
        }
        catch
        {
            try
            {
                if (multi) Directory.Delete(output, true);
                else if (File.Exists(output)) File.Delete(output);
            }
            catch { }
            throw;
        }
        if (!multi && (!File.Exists(output) || new FileInfo(output).Length == 0))
            throw new OpException("Conversion produced no file");
        return output;
    }

    static async Task<string> ViaTemp(string input, string output, string midExt, Job first, Job second, CancellationToken ct)
    {
        using var tmp = new TempDir();
        var mid = tmp.File(OutputPaths.BaseName(input) + "." + midExt);
        await first(input, mid, ct);
        return await second(mid, output, ct);
    }

    static Task<string> CopyAs(string input, string output, CancellationToken ct)
    {
        File.Copy(input, output);
        return Task.FromResult(output);
    }

    // ---------- Pandoc ----------

    static string PandocFormat(string ext) => Norm(ext) switch
    {
        "md" => "gfm",
        "txt" => "markdown",
        var e => e,
    };

    static async Task<string> Pandoc(string input, string output, CancellationToken ct)
    {
        var to = FileKinds.Ext(output) switch { "md" => "gfm", "txt" => "plain", var e => e };
        var args = new List<string> { "-f", PandocFormat(FileKinds.Ext(input)), "-t", to, "-o", output, "--wrap=none" };
        if (to is "html" or "epub" or "rtf") args.AddRange(["-s", "--metadata", "title=" + OutputPaths.BaseName(input)]);
        if (to == "html") args.Add("--embed-resources");
        args.Add(input);
        var r = await ProcessRunner.RunAsync(E.Pandoc!, args, null, ct, TimeSpan.FromMinutes(5), Path.GetDirectoryName(input));
        if (!r.Ok) throw new OpException(r.Tail());
        return output;
    }

    const string PrintCss = """
        <style>
        body{font-family:"Segoe UI",Arial,sans-serif;font-size:11pt;line-height:1.55;max-width:46em;margin:2em auto;color:#1b1b1b}
        h1,h2,h3{line-height:1.25} img{max-width:100%} pre,code{font-family:Consolas,monospace;background:#f4f4f4}
        pre{padding:.75em;overflow-x:auto} table{border-collapse:collapse} td,th{border:1px solid #ccc;padding:.3em .6em}
        blockquote{border-left:3px solid #ddd;margin-left:0;padding-left:1em;color:#555}
        </style>
        """;

    static async Task<string> PandocToPdfViaEdge(string input, string output, CancellationToken ct)
    {
        using var tmp = new TempDir();
        var css = tmp.File("print.html");
        await File.WriteAllTextAsync(css, PrintCss, ct);
        var html = tmp.File("doc.html");
        var args = new List<string>
        {
            "-f", PandocFormat(FileKinds.Ext(input)), "-t", "html5", "-s", "--embed-resources", "--wrap=none",
            "--metadata", "title=" + OutputPaths.BaseName(input), "-H", css, "-o", html, input,
        };
        var r = await ProcessRunner.RunAsync(E.Pandoc!, args, null, ct, TimeSpan.FromMinutes(5), Path.GetDirectoryName(input));
        if (!r.Ok) throw new OpException(r.Tail());
        return await EdgePdf(html, output, ct);
    }

    /// <summary>HTML to PDF with headless Edge, which every Windows 10/11 machine has.</summary>
    static async Task<string> EdgePdf(string input, string output, CancellationToken ct)
    {
        var url = new Uri(Path.GetFullPath(input)).AbsoluteUri;
        string error = "";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var profile = new TempDir();
            var r = await ProcessRunner.RunAsync(E.Edge!,
                ["--headless", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--no-pdf-header-footer",
                 "--user-data-dir=" + profile.Path, "--print-to-pdf=" + output, url], null, ct, TimeSpan.FromMinutes(3));
            error = r.Tail();
            // The launcher can exit a moment before the renderer has written the file.
            for (int i = 0; i < 60 && !IsComplete(output); i++) await Task.Delay(250, ct);
            if (IsComplete(output)) return output;
        }
        throw new OpException("Edge could not print the page. " + error);
    }

    static bool IsComplete(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0) return false;
            using var f = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException) { return false; }
    }

    // ---------- LibreOffice ----------

    static async Task<string> Libre(string input, string output, CancellationToken ct)
    {
        using var tmp = new TempDir();
        using var profile = new TempDir();
        var target = FileKinds.Ext(output);
        var filter = target == "txt" ? "txt:Text (encoded):UTF8" : target == "csv" ? "csv:Text - txt - csv (StarCalc):44,34,76" : target;
        var r = await ProcessRunner.RunAsync(E.LibreOffice!,
            ["-env:UserInstallation=" + new Uri(profile.Path).AbsoluteUri, "--headless", "--convert-to", filter, "--outdir", tmp.Path, input],
            null, ct, TimeSpan.FromMinutes(5));
        var produced = Directory.GetFiles(tmp.Path).FirstOrDefault();
        if (produced == null) throw new OpException("LibreOffice failed. " + r.Tail());
        File.Move(produced, output);
        return output;
    }

    // ---------- Microsoft Office (late-bound COM, no interop assemblies needed) ----------

    // A dummy password makes Office fail fast on protected files instead of showing a prompt nobody can see.
    const string NoPassword = "__poppy_no_password__";

    // PowerPoint is single-instance: two jobs at once would close each other's presentation.
    static readonly SemaphoreSlim OfficeLock = new(1, 1);

    static async Task<string> Office(Func<string> work, TimeSpan? timeout = null)
    {
        await OfficeLock.WaitAsync();
        try
        {
            var before = OfficeProcessIds();
            var job = Sta.Run(work);
            if (await Task.WhenAny(job, Task.Delay(timeout ?? TimeSpan.FromMinutes(3))) != job)
            {
                // A hidden dialog nobody can answer. Kill only the Office processes this job started.
                foreach (var id in OfficeProcessIds().Except(before))
                    try { System.Diagnostics.Process.GetProcessById(id).Kill(); } catch { }
                _ = job.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                throw new OpException("Microsoft Office did not respond");
            }
            return await job;
        }
        finally { OfficeLock.Release(); }
    }

    static HashSet<int> OfficeProcessIds() =>
        new[] { "WINWORD", "POWERPNT", "EXCEL" }.SelectMany(System.Diagnostics.Process.GetProcessesByName).Select(p => p.Id).ToHashSet();

    static dynamic CreateCom(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId) ?? throw new OpException(L.T("need.office")))!;

    static void Release(object? o)
    {
        if (o != null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o);
    }

    static Job WordJob(string target) => (input, output, ct) => Office(() =>
    {
        ct.ThrowIfCancellationRequested();
        var (fmt, enc) = WordOut[target];
        dynamic? word = null, doc = null;
        try
        {
            word = CreateCom("Word.Application");
            word.Visible = false;
            word.DisplayAlerts = 0;
            word.Options.ConfirmConversions = false;
            doc = word.Documents.Open(input, false, true, false, NoPassword, NoPassword, false, NoPassword, NoPassword, Type.Missing, Type.Missing, false);
            if (target == "pdf")
                doc.ExportAsFixedFormat(output, 17);
            else if (enc != 0)
                doc.SaveAs2(output, fmt, false, "", false, "", false, false, false, false, false, enc);
            else
                doc.SaveAs2(output, fmt);
            return output;
        }
        catch (COMException e)
        {
            throw new OpException("Word: " + e.Message);
        }
        finally
        {
            try { doc?.Close(0); } catch { }
            try { if (word != null && word.Documents.Count == 0) word.Quit(0); } catch { }
            Release(doc);
            Release(word);
        }
    });

    static dynamic OpenPresentation(dynamic app, string input) =>
        app.Presentations.Open(input + "::" + NoPassword, -1 /*ReadOnly*/, 0 /*Untitled*/, 0 /*WithWindow*/);

    static Task<string> PowerPointSave(string input, string output, int fmt, CancellationToken ct) => Office(() =>
    {
        ct.ThrowIfCancellationRequested();
        dynamic? app = null, pres = null;
        try
        {
            app = CreateCom("PowerPoint.Application");
            app.DisplayAlerts = 1; // ppAlertsNone
            pres = OpenPresentation(app, input);
            pres.SaveAs(output, fmt);
            return output;
        }
        catch (COMException e)
        {
            throw new OpException("PowerPoint: " + e.Message);
        }
        finally
        {
            try { pres?.Close(); } catch { }
            try { if (app != null && app.Presentations.Count == 0) app.Quit(); } catch { }
            Release(pres);
            Release(app);
        }
    });

    static Task<string> PowerPointVideo(string input, string output, CancellationToken ct) => Office(() =>
    {
        dynamic? app = null, pres = null;
        try
        {
            app = CreateCom("PowerPoint.Application");
            app.DisplayAlerts = 1;
            pres = OpenPresentation(app, input);
            pres.CreateVideo(output, true, 3, 1080, 30, 85);
            // ppMediaTaskStatus: 1 in progress, 2 queued, 3 done, 4 failed
            var deadline = DateTime.UtcNow.AddMinutes(30);
            while (true)
            {
                int status = pres.CreateVideoStatus;
                if (status == 3) break;
                if (status == 4) throw new OpException("PowerPoint could not create the video");
                if (ct.IsCancellationRequested || DateTime.UtcNow > deadline) throw new OperationCanceledException(ct);
                Thread.Sleep(500);
            }
            // The file appears a moment after the status flips.
            for (int i = 0; i < 40 && !IsReadable(output); i++) Thread.Sleep(250);
            return output;
        }
        catch (COMException e)
        {
            throw new OpException("PowerPoint: " + e.Message);
        }
        finally
        {
            try { pres?.Close(); } catch { }
            try { if (app != null && app.Presentations.Count == 0) app.Quit(); } catch { }
            Release(pres);
            Release(app);
        }
    }, TimeSpan.FromMinutes(30));

    static bool IsReadable(string path)
    {
        try
        {
            using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Slides to images: export to PDF, then render each page at slide resolution.</summary>
    static async Task<string> SlidesToImages(string input, string outputFolder, string ext, CancellationToken ct)
    {
        using var tmp = new TempDir();
        var pdf = tmp.File("deck.pdf");
        if (E.PowerPoint) await PowerPointSave(input, pdf, 32, ct);
        else await Libre(input, pdf, ct);
        await PdfOps.RenderPagesAsync(pdf, outputFolder, OutputPaths.BaseName(input), ext, null, ct);
        return outputFolder;
    }

    static Job ExcelOutJob(int fmt) => (i, o, ct) => ExcelSave(i, o, fmt, ct);

    static Task<string> ExcelSave(string input, string output, int fmt, CancellationToken ct) => ExcelRun(input, wb =>
    {
        ct.ThrowIfCancellationRequested();
        wb.SaveAs(output, fmt);
    }, output);

    static Task<string> ExcelPdf(string input, string output, CancellationToken ct) => ExcelRun(input, wb =>
    {
        ct.ThrowIfCancellationRequested();
        wb.ExportAsFixedFormat(0, output);
    }, output);

    static Task<string> ExcelRun(string input, Action<dynamic> work, string output) => Office(() =>
    {
        dynamic? app = null, wb = null;
        try
        {
            app = CreateCom("Excel.Application");
            app.Visible = false;
            app.DisplayAlerts = false;
            app.ScreenUpdating = false;
            wb = app.Workbooks.Open(input, 0, true, Type.Missing, NoPassword);
            work(wb);
            return output;
        }
        catch (COMException e)
        {
            throw new OpException("Excel: " + e.Message);
        }
        finally
        {
            try { wb?.Close(false); } catch { }
            try { if (app != null && app.Workbooks.Count == 0) app.Quit(); } catch { }
            Release(wb);
            Release(app);
        }
    });
}
