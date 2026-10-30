namespace Poppy.Core.Ops;

/// <summary>Routes "convert:xxx" to the right engine for each file's category.</summary>
public static class Converter
{
    /// <summary>Why the conversion cannot run on this machine, or null if it can.</summary>
    public static string? Unavailable(string path, string target)
    {
        var cat = FileKinds.Detect(path);
        var src = FileKinds.Ext(path);
        return cat switch
        {
            FileCategory.Image => target == "heic" && !ImageOps.CanWrite("heic") ? L.T("need.hevc")
                : target == "avif" && !ImageOps.CanWrite("avif") && !Engines.Engines.HasFfmpeg ? L.T("need.ffmpeg") : null,
            FileCategory.Video or FileCategory.Audio => Engines.Engines.HasFfmpeg ? null : L.T("need.ffmpeg"),
            FileCategory.Pdf => target == "txt" ? (PdfOps.OcrAvailable ? null : L.T("need.ocr")) : null,
            FileCategory.Document or FileCategory.Slides or FileCategory.Spreadsheet or FileCategory.Ebook => DocOps.Unavailable(path, target),
            FileCategory.Subtitle => SubtitleOps.Unavailable(src, target),
            _ => ArchiveOps.Unavailable(target),
        };
    }

    /// <summary>Which installable engine (EngineInstaller id) would make this conversion possible.</summary>
    public static string? MissingEngine(string path, string target)
    {
        if (Unavailable(path, target) == null) return null;
        return FileKinds.Detect(path) switch
        {
            FileCategory.Image => target == "heic" ? "heic" : "ffmpeg",
            FileCategory.Video or FileCategory.Audio or FileCategory.Subtitle => "ffmpeg",
            FileCategory.Pdf => "ocr",
            FileCategory.Document or FileCategory.Slides or FileCategory.Spreadsheet or FileCategory.Ebook => DocOps.MissingEngine(path, target),
            _ => "7zip",
        };
    }

    public static async Task<IReadOnlyList<string>> ConvertAsync(OpContext ctx, string target)
    {
        var archives = FileKinds.Detect(ctx.Inputs[0]) is FileCategory.Folder or FileCategory.Other
            || (FileKinds.Detect(ctx.Inputs[0]) == FileCategory.Archive);
        if (archives)
            return await ArchiveOps.PackAsync(ctx.Inputs, target, f => ctx.Report(0, f), ctx.Ct);

        // Several photos dropped on PDF are meant to become one document, one photo per page.
        if (target == "pdf" && ctx.Inputs.Count > 1 && ctx.Inputs.All(p => FileKinds.Detect(p) == FileCategory.Image))
            return [await ImageOps.CombineToPdfAsync(ctx.Inputs, f => ctx.Report(0, f), ctx.Ct)];

        var outputs = new List<string>();
        for (int i = 0; i < ctx.Inputs.Count; i++)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            var input = ctx.Inputs[i];
            var index = i;
            void P(double f) => ctx.Report(index, f);
            var output = FileKinds.Detect(input) switch
            {
                FileCategory.Image => await ImageOps.ConvertAsync(input, target, ctx.Ct),
                FileCategory.Video or FileCategory.Audio => await MediaOps.ConvertAsync(input, target, P, ctx.Ct),
                FileCategory.Pdf when target == "txt" => await PdfOps.ToTextAsync(input, P, ctx.Ct),
                FileCategory.Pdf => await PdfOps.ToImagesAsync(input, target, P, ctx.Ct),
                FileCategory.Subtitle => await SubtitleOps.ConvertAsync(input, target, ctx.Ct),
                _ => await DocOps.ConvertAsync(input, target, ctx.Ct),
            };
            outputs.Add(output);
            ctx.Report(i, 1);
        }
        return outputs;
    }
}

/// <summary>Runs a ring item (other than UI-only actions) over the dropped files.</summary>
public static class Runner
{
    public static async Task<OpResult> RunAsync(string id, OpContext ctx)
    {
        if (ctx.Inputs.Count == 0) throw new OpException(L.T("nothing"));
        if (id.StartsWith("convert:"))
            return new OpResult(await Converter.ConvertAsync(ctx, id["convert:".Length..]));

        switch (id)
        {
            case "action.zip":
                return new OpResult(await ArchiveOps.PackAsync(ctx.Inputs, "zip", f => ctx.Report(0, f), ctx.Ct));
            case "pdf.merge":
                return new OpResult([await Task.Run(() => PdfOps.Merge(ctx.Inputs), ctx.Ct)]);
        }

        var outputs = new List<string>();
        var skipped = 0;
        for (int i = 0; i < ctx.Inputs.Count; i++)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            var input = ctx.Inputs[i];
            var index = i;
            void P(double f) => ctx.Report(index, f);
            var o = ctx.Options;
            var ct = ctx.Ct;
            string? output = id switch
            {
                "image.compress" => await ImageOps.CompressAsync(input, ct),
                "image.metadata" => await ImageOps.StripMetadataAsync(input, ct),
                "image.resize" => await ImageOps.ResizeAsync(input, o, ct),
                "image.crop" => await ImageOps.CropAsync(input, o, ct),
                "image.edit" => await ImageOps.AdjustAsync(input, o, ct),
                "image.background" => await ImageOps.BackgroundAsync(input, o, ct),
                "image.annotate" or "image.redact" => ImageOps.SaveEdited(input, o.Get("rendered"), id == "image.redact" ? "redacted" : "annotated"),

                "video.compress" => await MediaOps.VideoCompressAsync(input, P, ct),
                "video.trim" or "audio.trim" => await MediaOps.TrimAsync(input, o, P, ct),
                "video.mute" => await MediaOps.MuteAsync(input, P, ct),
                "video.resize" => await MediaOps.VideoResizeAsync(input, o, P, ct),
                "video.rotate" => await MediaOps.VideoRotateAsync(input, P, ct),
                "video.frame" => await MediaOps.FrameAsync(input, o, ct),
                "video.speed" or "audio.speed" => await MediaOps.SpeedAsync(input, o, P, ct),
                "video.metadata" or "audio.metadata" => await MediaOps.StripMetadataAsync(input, P, ct),

                "audio.compress" => await MediaOps.AudioCompressAsync(input, P, ct),
                "audio.normalize" => await MediaOps.NormalizeAsync(input, P, ct),
                "audio.mono" => await MediaOps.MonoAsync(input, P, ct),
                "audio.fade" => await MediaOps.FadeAsync(input, P, ct),

                "pdf.compress" => await PdfOps.CompressAsync(input, ct),
                "pdf.split" => await Task.Run(() => PdfOps.Split(input, o), ct),
                "pdf.rotate" => await Task.Run(() => PdfOps.Rotate(input), ct),
                "pdf.extract" => await Task.Run(() => PdfOps.Extract(input, o), ct),
                "pdf.protect" => await Task.Run(() => PdfOps.Protect(input, o), ct),
                "pdf.metadata" => await Task.Run(() => PdfOps.StripMetadata(input), ct),

                "subtitle.shift" => await SubtitleOps.ShiftAsync(input, o, ct),
                "archive.extract" => await ArchiveOps.ExtractAsync(input, ct),
                _ => throw new OpException("Unknown item: " + id),
            };
            if (output == null) skipped++;
            else outputs.Add(output);
            ctx.Report(i, 1);
        }
        return new OpResult(outputs, skipped > 0 ? L.T("already.small") : null);
    }
}
