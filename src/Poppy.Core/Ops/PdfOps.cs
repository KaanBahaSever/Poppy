using System.IO.Compression;
using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Poppy.Core.Engines;
using Windows.Storage;
using Windows.Storage.Streams;
using WinPdf = Windows.Data.Pdf;

namespace Poppy.Core.Ops;

/// <summary>PDF tools (PDFsharp) and page rendering (the PDF renderer built into Windows).</summary>
public static class PdfOps
{
    static PdfDocument Open(string path, PdfDocumentOpenMode mode)
    {
        try
        {
            return PdfReader.Open(path, mode);
        }
        catch (PdfReaderException e)
        {
            throw new OpException("PDF: " + e.Message);
        }
    }

    // ---------- Render ----------

    /// <summary>Renders every page into <paramref name="folder"/> as "name-1.png", "name-2.png"...</summary>
    public static async Task<IReadOnlyList<string>> RenderPagesAsync(
        string pdf, string folder, string baseName, string ext, Action<double>? progress, CancellationToken ct, double scale = 2)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdf));
        WinPdf.PdfDocument doc;
        try
        {
            doc = await WinPdf.PdfDocument.LoadFromFileAsync(file);
        }
        catch (Exception e)
        {
            throw new OpException("PDF: " + e.Message);
        }
        var outputs = new List<string>();
        var count = (int)doc.PageCount;
        var digits = count.ToString().Length;
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = await RenderPageAsync(doc, i, scale);
            var path = Path.Combine(folder, $"{baseName}-{(i + 1).ToString().PadLeft(digits, '0')}.{ext}");
            using (var img = new MagickImage(bytes))
            {
                if (ext == "jpg") { img.Alpha(AlphaAction.Remove); img.Quality = 90; }
                img.Write(path, ImageOps.FormatFor(ext));
            }
            outputs.Add(path);
            progress?.Invoke((i + 1.0) / count);
        }
        return outputs;
    }

    static async Task<byte[]> RenderPageAsync(WinPdf.PdfDocument doc, int index, double scale)
    {
        using var page = doc.GetPage((uint)index);
        using var stream = new InMemoryRandomAccessStream();
        var options = new WinPdf.PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, page.Size.Width * scale),
            DestinationHeight = (uint)Math.Max(1, page.Size.Height * scale),
            BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
        };
        await page.RenderToStreamAsync(stream, options);
        using var net = stream.AsStreamForRead();
        using var ms = new MemoryStream();
        net.Position = 0;
        await net.CopyToAsync(ms);
        return ms.ToArray();
    }

    public static async Task<string> ToImagesAsync(string input, string ext, Action<double>? progress, CancellationToken ct)
    {
        if (ext == "tiff")
        {
            // One multi-page TIFF, the format fax machines and archives expect.
            using var tmp = new TempDir();
            var pages = await RenderPagesAsync(input, tmp.Path, "p", "png", progress, ct);
            var output = OutputPaths.For(input, "tiff");
            using var all = new MagickImageCollection();
            foreach (var p in pages) all.Add(new MagickImage(p));
            foreach (var img in all) img.Settings.Compression = CompressionMethod.LZW;
            all.Write(output, MagickFormat.Tiff);
            return output;
        }
        var count = PageCount(input);
        if (count == 1)
        {
            using var tmp = new TempDir();
            var page = (await RenderPagesAsync(input, tmp.Path, "p", ext, progress, ct))[0];
            var output = OutputPaths.For(input, ext);
            File.Move(page, output);
            return output;
        }
        var folder = OutputPaths.NewFolder(input, "pages");
        await RenderPagesAsync(input, folder, OutputPaths.BaseName(input), ext, progress, ct);
        return folder;
    }

    static readonly Lazy<bool> ocrAvailable = new(() =>
    {
        try { return Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages() != null; }
        catch { return false; }
    });

    /// <summary>Windows has an OCR language installed for the user's languages.</summary>
    public static bool OcrAvailable => ocrAvailable.Value;

    /// <summary>
    /// PDF to plain text with the OCR engine built into Windows. Reading the rendered page
    /// works the same for digital and scanned PDFs, and needs no Office.
    /// </summary>
    public static async Task<string> ToTextAsync(string input, Action<double>? progress, CancellationToken ct)
    {
        var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages() ?? throw new OpException(L.T("need.ocr"));
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(input));
        var doc = await WinPdf.PdfDocument.LoadFromFileAsync(file);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < (int)doc.PageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var page = doc.GetPage((uint)i);
            var max = Windows.Media.Ocr.OcrEngine.MaxImageDimension;
            var scale = Math.Min(2.5, max / Math.Max(page.Size.Width, page.Size.Height));
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new WinPdf.PdfPageRenderOptions
            {
                DestinationWidth = (uint)(page.Size.Width * scale),
                DestinationHeight = (uint)(page.Size.Height * scale),
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            });
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(bitmap);
            if (i > 0) sb.Append("\r\n\f\r\n");
            foreach (var line in result.Lines) sb.Append(line.Text).Append("\r\n");
            progress?.Invoke((i + 1.0) / doc.PageCount);
        }
        var output = OutputPaths.For(input, "txt");
        await File.WriteAllTextAsync(output, sb.ToString(), new System.Text.UTF8Encoding(true), ct);
        return output;
    }

    public static int PageCount(string path)
    {
        using var doc = Open(path, PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    // ---------- Tools ----------

    public static string Merge(IReadOnlyList<string> inputs)
    {
        if (inputs.Count < 2) throw new OpException(L.T("need.multiple"));
        var output = OutputPaths.For(inputs[0], "pdf", "merged");
        using var target = new PdfDocument();
        foreach (var path in inputs)
        {
            using var src = Open(path, PdfDocumentOpenMode.Import);
            foreach (var page in src.Pages) target.AddPage(page);
        }
        target.Save(output);
        return output;
    }

    public static string Split(string input)
    {
        using var src = Open(input, PdfDocumentOpenMode.Import);
        var folder = OutputPaths.NewFolder(input, "pages");
        var digits = src.PageCount.ToString().Length;
        for (int i = 0; i < src.PageCount; i++)
        {
            using var one = new PdfDocument();
            one.AddPage(src.Pages[i]);
            one.Save(Path.Combine(folder, $"{OutputPaths.BaseName(input)}-{(i + 1).ToString().PadLeft(digits, '0')}.pdf"));
        }
        return folder;
    }

    /// <summary>Every page 90° clockwise.</summary>
    public static string Rotate(string input)
    {
        var output = OutputPaths.For(input, "pdf", "rotated");
        using var doc = Open(input, PdfDocumentOpenMode.Modify);
        foreach (var page in doc.Pages) page.Rotate = (page.Rotate + 90) % 360;
        doc.Save(output);
        return output;
    }

    /// <summary>"pages" like "1-3, 5, 8-" (1-based, open ranges allowed).</summary>
    public static string Extract(string input, OpOptions o)
    {
        using var src = Open(input, PdfDocumentOpenMode.Import);
        var pages = ParseRange(o.Get("pages", "1"), src.PageCount);
        if (pages.Count == 0) throw new OpException("No pages in range");
        var output = OutputPaths.For(input, "pdf", "pages");
        using var doc = new PdfDocument();
        foreach (var p in pages) doc.AddPage(src.Pages[p - 1]);
        doc.Save(output);
        return output;
    }

    public static List<int> ParseRange(string spec, int pageCount)
    {
        var result = new List<int>();
        foreach (var raw in spec.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            int a, b;
            var dash = part.IndexOf('-');
            if (dash < 0)
            {
                if (!int.TryParse(part, out a)) continue;
                b = a;
            }
            else
            {
                var left = part[..dash];
                var right = part[(dash + 1)..];
                a = left.Length == 0 ? 1 : int.TryParse(left, out var x) ? x : -1;
                b = right.Length == 0 ? pageCount : int.TryParse(right, out var y) ? y : -1;
                if (a < 0 || b < 0) continue;
            }
            if (a > b) (a, b) = (b, a);
            for (int p = Math.Max(1, a); p <= Math.Min(pageCount, b); p++)
                if (!result.Contains(p)) result.Add(p);
        }
        return result;
    }

    public static string Protect(string input, OpOptions o)
    {
        var password = o.Get("password");
        if (password.Length == 0) throw new OpException("Empty password");
        var output = OutputPaths.For(input, "pdf", "protected");
        using var doc = Open(input, PdfDocumentOpenMode.Modify);
        doc.SecuritySettings.UserPassword = password;
        doc.SecuritySettings.OwnerPassword = password;
        doc.SecurityHandler.SetEncryptionToV5(); // AES-256
        doc.Save(output);
        return output;
    }

    public static string StripMetadata(string input)
    {
        var output = OutputPaths.For(input, "pdf", "clean");
        using var doc = Open(input, PdfDocumentOpenMode.Modify);
        doc.Info.Elements.Clear();
        doc.Internals.Catalog.Elements.Remove("/Metadata");
        doc.Save(output);
        return output;
    }

    /// <summary>
    /// Recompresses big photos inside the PDF (the usual reason a PDF is large).
    /// Uses Ghostscript when it is installed. Returns null if the file cannot get smaller.
    /// </summary>
    public static async Task<string?> CompressAsync(string input, CancellationToken ct)
    {
        var output = OutputPaths.For(input, "pdf", "compressed");
        var gs = Engines.Engines.Ghostscript;
        if (gs != null)
        {
            var r = await ProcessRunner.RunAsync(gs,
                ["-sDEVICE=pdfwrite", "-dCompatibilityLevel=1.6", "-dPDFSETTINGS=/ebook", "-dNOPAUSE", "-dQUIET", "-dBATCH", "-sOutputFile=" + output, input],
                null, ct, TimeSpan.FromMinutes(10));
            if (!r.Ok && File.Exists(output)) File.Delete(output);
        }
        if (!File.Exists(output))
        {
            await Task.Run(() =>
            {
                using var doc = Open(input, PdfDocumentOpenMode.Modify);
                var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
                foreach (var page in doc.Pages)
                {
                    ct.ThrowIfCancellationRequested();
                    var xobjects = page.Resources?.Elements.GetDictionary("/XObject");
                    if (xobjects == null) continue;
                    foreach (var key in xobjects.Elements.Keys.ToList())
                    {
                        if (xobjects.Elements.GetObject(key) is PdfDictionary x && seen.Add(x)
                            && x.Elements.GetName("/Subtype") == "/Image" && x.Stream != null)
                            RecompressImage(x);
                    }
                }
                doc.Options.CompressContentStreams = true;
                doc.Options.NoCompression = false;
                doc.Save(output);
            }, ct);
        }

        if (new FileInfo(output).Length >= new FileInfo(input).Length)
        {
            File.Delete(output);
            return null;
        }
        return output;
    }

    const int MaxSide = 1800;

    static void RecompressImage(PdfDictionary x)
    {
        var filter = x.Elements["/Filter"];
        var filterName = filter is PdfName n ? n.Value : filter is PdfArray { Elements.Count: 1 } a && a.Elements[0] is PdfName n1 ? n1.Value : null;
        var raw = x.Stream!.Value;
        if (raw.Length < 60_000) return;
        var colorSpace = x.Elements.GetName("/ColorSpace");
        if (x.Elements.ContainsKey("/Decode") || x.Elements.ContainsKey("/Mask") && filterName != "/DCTDecode") return;

        MagickImage? img = null;
        try
        {
            if (filterName == "/DCTDecode")
            {
                if (colorSpace is not ("/DeviceRGB" or "/DeviceGray")) return; // CMYK JPEGs are often inverted; leave them
                img = new MagickImage(raw);
            }
            else if (filterName == "/FlateDecode")
            {
                if (x.Elements.ContainsKey("/DecodeParms") || x.Elements.GetInteger("/BitsPerComponent") != 8) return;
                var channels = colorSpace switch { "/DeviceRGB" => 3, "/DeviceGray" => 1, _ => 0 };
                if (channels == 0) return;
                int w = x.Elements.GetInteger("/Width"), h = x.Elements.GetInteger("/Height");
                var pixels = Inflate(raw);
                if (pixels.Length != w * h * channels) return;
                img = new MagickImage();
                img.ReadPixels(pixels, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, channels == 3 ? "RGB" : "R"));
                if (channels == 1) img.ColorType = ColorType.Grayscale;
            }
            else return;

            if (img.Width > MaxSide || img.Height > MaxSide) img.Resize(new MagickGeometry(MaxSide, MaxSide));
            img.Strip();
            img.Quality = 65;
            img.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", "4:2:0");
            var jpeg = img.ToByteArray(MagickFormat.Jpeg);
            if (jpeg.Length >= raw.Length * 0.9) return;

            x.Stream.Value = jpeg;
            x.Elements["/Filter"] = new PdfName("/DCTDecode");
            x.Elements.Remove("/DecodeParms");
            x.Elements["/Width"] = new PdfInteger((int)img.Width);
            x.Elements["/Height"] = new PdfInteger((int)img.Height);
            x.Elements["/BitsPerComponent"] = new PdfInteger(8);
            x.Elements["/ColorSpace"] = new PdfName(img.ChannelCount <= 2 ? "/DeviceGray" : "/DeviceRGB");
            x.Elements["/Length"] = new PdfInteger(jpeg.Length);
        }
        catch (MagickException) { }
        finally
        {
            img?.Dispose();
        }
    }

    static byte[] Inflate(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        z.CopyTo(output);
        return output.ToArray();
    }
}
