using System.IO.Compression;
using System.Text.Json;
using ImageMagick;
using PdfSharp.Pdf.IO;
using Poppy.Core.Ops;
using SharpCompress.Archives;

namespace Poppy.Core.Tests;

/// <summary>Opens a result with a real decoder to prove it is a valid file of the expected type.</summary>
public static class Check
{
    public static void Valid(string path, string ext)
    {
        if (Directory.Exists(path))
        {
            var files = Directory.GetFiles(path);
            Assert.NotEmpty(files);
            foreach (var f in files) Valid(f, ext);
            return;
        }
        Assert.True(File.Exists(path), "missing output " + path);
        Assert.True(new FileInfo(path).Length > 0, "empty output " + path);
        Assert.Equal(ext, FileKinds.Ext(path));

        switch (ext)
        {
            case "heic":
                // A real HEIF container ("ftyp" box with a HEIC brand), decodable by Poppy's own loader.
                var head12 = new byte[12];
                using (var fs = File.OpenRead(path)) fs.ReadExactly(head12);
                Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(head12, 4, 4));
                Assert.Contains(System.Text.Encoding.ASCII.GetString(head12, 8, 4), new[] { "heic", "heix", "mif1", "msf1", "hevc" });
                var (_, w, h) = ImageOps.LoadOriented(path, 64);
                Assert.True(w > 0 && h > 0);
                break;
            case "png" or "jpg" or "webp" or "avif" or "tiff" or "bmp" or "gif" or "ico":
                using (var img = new MagickImage(path))
                {
                    Assert.True(img.Width > 0 && img.Height > 0);
                    var expected = ImageOps.FormatFor(ext);
                    Assert.True(img.Format == expected || (ext == "tiff" && img.Format == MagickFormat.Tif) || (ext == "ico" && img.Format == MagickFormat.Ico) || (ext == "heic" && img.Format is MagickFormat.Heif or MagickFormat.Heic) || (ext == "avif" && img.Format is MagickFormat.Heif or MagickFormat.Avif),
                        $"{path}: format {img.Format}, expected {expected}");
                }
                break;
            case "pdf":
                Assert.True(PdfPages(path) > 0);
                break;
            case "mp4" or "mkv" or "webm" or "mov" or "avi":
                var v = MediaOps.ProbeAsync(path).GetAwaiter().GetResult();
                Assert.True(v.HasVideo, path + " has no video");
                Assert.True(v.Duration > 0);
                break;
            case "mp3" or "wav" or "flac" or "m4a" or "ogg" or "opus" or "aiff" or "wma":
                var a = MediaOps.ProbeAsync(path).GetAwaiter().GetResult();
                Assert.True(a.HasAudio, path + " has no audio");
                Assert.True(a.Duration > 0);
                break;
            case "docx": ZipHas(path, "word/document.xml"); break;
            case "pptx": ZipHas(path, "ppt/presentation.xml"); break;
            case "xlsx": ZipHas(path, "xl/workbook.xml"); break;
            case "odt" or "ods" or "odp": ZipHas(path, "content.xml"); break;
            case "epub": ZipHas(path, "mimetype"); break;
            case "xls":
                var head = new byte[4];
                using (var fs = File.OpenRead(path)) fs.ReadExactly(head);
                Assert.Equal(new byte[] { 0xD0, 0xCF, 0x11, 0xE0 }, head);
                break;
            case "rtf":
                Assert.StartsWith("{\\rtf", File.ReadAllText(path).TrimStart('\uFEFF'));
                break;
            case "html":
                var html = File.ReadAllText(path);
                Assert.True(html.Contains("<html", StringComparison.OrdinalIgnoreCase) || html.Contains("<table", StringComparison.OrdinalIgnoreCase) || html.Contains("<body", StringComparison.OrdinalIgnoreCase));
                break;
            case "json":
                using (JsonDocument.Parse(File.ReadAllText(path))) { }
                break;
            case "txt" or "md" or "csv" or "tsv" or "srt" or "vtt" or "ass":
                Assert.False(string.IsNullOrWhiteSpace(File.ReadAllText(path)));
                break;
            case "tar.gz":
                using (var fs = File.OpenRead(path))
                using (var reader = SharpCompress.Readers.ReaderFactory.OpenReader(fs))
                {
                    var files = 0;
                    while (reader.MoveToNextEntry()) if (!reader.Entry.IsDirectory) files++;
                    Assert.True(files > 0, "empty tar.gz");
                }
                break;
            case "zip" or "7z" or "tar":
                using (var arc = ArchiveFactory.OpenArchive(path))
                    Assert.Contains(arc.Entries, e => !e.IsDirectory);
                break;
            default:
                throw new Exception("No validator for " + ext);
        }
    }

    public static int PdfPages(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    static void ZipHas(string path, string entry)
    {
        using var zip = ZipFile.OpenRead(path);
        Assert.Contains(zip.Entries, e => e.FullName == entry);
    }
}
