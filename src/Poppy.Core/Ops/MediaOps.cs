using System.Globalization;
using System.Text.Json;
using Poppy.Core.Engines;
using E = Poppy.Core.Engines.Engines;

namespace Poppy.Core.Ops;

public sealed record MediaInfo(double Duration, string? VideoCodec, string? AudioCodec, int Width, int Height)
{
    public bool HasVideo => VideoCodec != null;
    public bool HasAudio => AudioCodec != null;
}

/// <summary>Video and audio work, all through FFmpeg.</summary>
public static class MediaOps
{
    static string Ffmpeg => E.Ffmpeg ?? throw new OpException(L.T("need.ffmpeg"));
    static string Ffprobe => E.Ffprobe ?? throw new OpException(L.T("need.ffmpeg"));

    static string Inv(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

    public static async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default)
    {
        var r = await ProcessRunner.RunAsync(Ffprobe,
            ["-v", "error", "-show_entries", "format=duration:stream=codec_type,codec_name,width,height:stream_disposition=attached_pic", "-of", "json", path],
            null, ct, TimeSpan.FromSeconds(30));
        if (!r.Ok) throw new OpException(r.Tail());
        using var doc = JsonDocument.Parse(r.StdOut);
        double duration = 0;
        if (doc.RootElement.TryGetProperty("format", out var f) && f.TryGetProperty("duration", out var d))
            double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
        string? v = null, a = null;
        int w = 0, h = 0;
        if (doc.RootElement.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = s.GetProperty("codec_type").GetString();
                var name = s.TryGetProperty("codec_name", out var n) ? n.GetString() : "unknown";
                var cover = s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                if (type == "video" && v == null && !cover)
                {
                    v = name;
                    w = s.TryGetProperty("width", out var ww) ? ww.GetInt32() : 0;
                    h = s.TryGetProperty("height", out var hh) ? hh.GetInt32() : 0;
                }
                else if (type == "audio" && a == null) a = name;
            }
        }
        return new MediaInfo(duration, v, a, w, h);
    }

    /// <summary>Runs ffmpeg with -progress so the bar moves; duration is the expected output length.</summary>
    static async Task RunAsync(IEnumerable<string> args, double duration, Action<double>? progress, CancellationToken ct)
    {
        var full = new List<string> { "-y", "-hide_banner", "-nostdin", "-progress", "pipe:1", "-nostats" };
        full.AddRange(args);
        var r = await ProcessRunner.RunAsync(Ffmpeg, full, line =>
        {
            if (progress == null || duration <= 0) return;
            if (line.StartsWith("out_time_us=") || line.StartsWith("out_time_ms="))
            {
                if (long.TryParse(line.AsSpan(12), out var us) && us > 0)
                    progress(us / 1_000_000.0 / duration);
            }
        }, ct);
        if (!r.Ok) throw new OpException(r.Tail());
    }

    static string[] H264(int crf, string preset = "veryfast")
    {
        var enc = E.PickEncoder("libx264", "h264_mf", "libopenh264", "mpeg4");
        return enc switch
        {
            "libx264" => ["-c:v", "libx264", "-preset", preset, "-crf", crf.ToString(), "-pix_fmt", "yuv420p"],
            "h264_mf" => ["-c:v", "h264_mf", "-rate_control", "quality", "-quality", (100 - crf * 2).ToString(), "-pix_fmt", "nv12"],
            "libopenh264" => ["-c:v", "libopenh264", "-b:v", crf <= 23 ? "6M" : "2500k", "-pix_fmt", "yuv420p"],
            _ => ["-c:v", "mpeg4", "-q:v", crf <= 23 ? "3" : "6"],
        };
    }

    static string[] Aac(string bitrate = "192k") => ["-c:a", "aac", "-b:a", bitrate];

    /// <summary>Encoder args for re-encoding into the same container type as the input.</summary>
    static string[] ContainerCodecs(string ext, int crf, bool video) => ext switch
    {
        "webm" => [.. (video ? new[] { "-c:v", "libvpx-vp9", "-crf", (crf + 8).ToString(), "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "5" } : []), "-c:a", "libopus", "-b:a", "128k"],
        "avi" => [.. (video ? new[] { "-c:v", "mpeg4", "-q:v", crf <= 23 ? "3" : "6" } : []), "-c:a", "libmp3lame", "-q:a", "3"],
        "ogv" => [.. (video ? new[] { "-c:v", "libtheora", "-q:v", "7" } : []), "-c:a", "libvorbis"],
        "wmv" => [.. (video ? new[] { "-c:v", "wmv2", "-q:v", "4" } : []), "-c:a", "wmav2"],
        "flv" or "3gp" or "mpg" or "mpeg" or "ts" or "mts" => [.. (video ? H264(crf) : []), .. Aac()],
        _ => [.. (video ? H264(crf) : []), .. Aac()],
    };

    static string[] AudioCodec(string ext, bool smaller = false) => ext switch
    {
        "mp3" => ["-c:a", "libmp3lame", .. (smaller ? new[] { "-b:a", "96k" } : ["-q:a", "2"])],
        "wav" => ["-c:a", "pcm_s16le"],
        "flac" => ["-c:a", "flac", "-compression_level", "8"],
        "m4a" or "aac" => ["-c:a", "aac", "-b:a", smaller ? "96k" : "256k"],
        "ogg" or "oga" => ["-c:a", "libvorbis", "-q:a", smaller ? "2" : "5"],
        "opus" => ["-c:a", "libopus", "-b:a", smaller ? "64k" : "160k"],
        "aiff" or "aif" => ["-c:a", "pcm_s16be"],
        "wma" => ["-c:a", "wmav2", "-b:a", smaller ? "96k" : "192k"],
        "amr" => ["-c:a", "libopencore_amrnb", "-ar", "8000", "-ac", "1"],
        _ => ["-c:a", "aac", "-b:a", "192k"],
    };

    static string[] MovFlags(string ext) => ext is "mp4" or "mov" or "m4a" or "m4v" ? ["-movflags", "+faststart"] : [];

    /// <summary>A waveform picture for the trim timeline (PNG in a temp file).</summary>
    public static async Task<string> WaveformAsync(string input, int width, int height, string color, CancellationToken ct)
    {
        var output = Path.Combine(Path.GetTempPath(), "Poppy", "wave-" + Guid.NewGuid().ToString("N")[..8] + ".png");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await RunAsync(["-i", input, "-filter_complex", $"[0:a:0]aformat=channel_layouts=mono,showwavespic=s={width}x{height}:colors={color}", "-frames:v", "1", output], 0, null, ct);
        return output;
    }

    /// <summary>One frame at <paramref name="seconds"/>, scaled to <paramref name="width"/>, as PNG bytes.</summary>
    public static async Task<byte[]> FrameBytesAsync(string input, double seconds, int width, CancellationToken ct)
    {
        var output = Path.Combine(Path.GetTempPath(), "Poppy", "frame-" + Guid.NewGuid().ToString("N")[..8] + ".png");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        try
        {
            await RunAsync(["-ss", Inv(Math.Max(0, seconds)), "-i", input, "-frames:v", "1", "-vf", $"scale={width}:-2", "-update", "1", output], 0, null, ct);
            return await File.ReadAllBytesAsync(output, ct);
        }
        finally
        {
            try { File.Delete(output); } catch { }
        }
    }

    // ---------- Convert ----------

    public static async Task<string> ConvertAsync(string input, string target, Action<double>? progress, CancellationToken ct)
    {
        var info = await ProbeAsync(input, ct);
        var output = OutputPaths.For(input, target);
        var isVideo = FileKinds.Detect(input) == FileCategory.Video && info.HasVideo;

        if (target == "gif")
        {
            const string vf = "fps=12,scale='min(720,iw)':-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4";
            await RunAsync(["-i", input, "-vf", vf, "-loop", "0", output], info.Duration, progress, ct);
            return output;
        }

        if (!isVideo || FileKinds.Detect(output) == FileCategory.Audio)
        {
            if (!info.HasAudio) throw new OpException("No audio track");
            await RunAsync(["-i", input, "-map", "0:a:0", "-vn", .. AudioCodec(target), .. MovFlags(target), output], info.Duration, progress, ct);
            return output;
        }

        // Re-wrap without re-encoding when the codecs already fit the new container: seconds instead of minutes.
        if (CanCopy(info, target))
        {
            try
            {
                await RunAsync(["-i", input, "-map", "0:v:0", "-map", "0:a?", "-c", "copy", .. MovFlags(target), output], info.Duration, progress, ct);
                return output;
            }
            catch (OpException)
            {
                File.Delete(output);
            }
        }

        await RunAsync(["-i", input, "-map", "0:v:0", "-map", "0:a?", .. ContainerCodecs(target, 23, true), .. MovFlags(target), output], info.Duration, progress, ct);
        return output;
    }

    static bool CanCopy(MediaInfo i, string target)
    {
        var v = i.VideoCodec;
        var a = i.AudioCodec;
        return target switch
        {
            "mkv" => true,
            "mp4" or "mov" => v is "h264" or "hevc" or "mpeg4" or "av1" && (a == null || a is "aac" or "mp3" or "ac3" or "alac"),
            "webm" => v is "vp8" or "vp9" or "av1" && (a == null || a is "opus" or "vorbis"),
            _ => false,
        };
    }

    // ---------- Video tools ----------

    static string SameVideoExt(string input)
    {
        var ext = FileKinds.Ext(input);
        return ext is "mp4" or "mov" or "mkv" or "webm" or "avi" ? ext : "mp4";
    }

    public static async Task<string> VideoCompressAsync(string input, Action<double>? progress, CancellationToken ct)
    {
        var info = await ProbeAsync(input, ct);
        var ext = SameVideoExt(input);
        var output = OutputPaths.For(input, ext, "compressed");
        var vf = info.Height > 1080 ? new[] { "-vf", "scale=-2:1080" } : [];
        var codecs = ext == "webm" || ext == "avi" ? ContainerCodecs(ext, 30, true) : [.. H264(28, "medium"), .. Aac("128k")];
        await RunAsync(["-i", input, "-map", "0:v:0", "-map", "0:a?", .. vf, .. codecs, .. MovFlags(ext), output], info.Duration, progress, ct);
        return output;
    }

    /// <summary>"start" and "end" in seconds. Re-encodes so the cut lands on the exact frame.</summary>
    public static async Task<string> TrimAsync(string input, OpOptions o, Action<double>? progress, CancellationToken ct)
    {
        var info = await ProbeAsync(input, ct);
        var start = Math.Max(0, o.GetDouble("start"));
        var end = o.GetDouble("end", info.Duration);
        if (end <= 0 || end > info.Duration) end = info.Duration;
        if (end <= start) throw new OpException("End must be after start");
        var isVideo = FileKinds.Detect(input) == FileCategory.Video && info.HasVideo;
        var ext = isVideo ? SameVideoExt(input) : FileKinds.Ext(input);
        var output = OutputPaths.For(input, ext, "trimmed");
        var codecs = isVideo ? ContainerCodecs(ext, 18, true) : AudioCodec(ext);
        var map = isVideo ? new[] { "-map", "0:v:0", "-map", "0:a?" } : ["-map", "0:a:0"];
        await RunAsync(["-ss", Inv(start), "-to", Inv(end), "-i", input, .. map, .. codecs, .. MovFlags(ext), output], end - start, progress, ct);
        return output;
    }

    public static async Task<string> MuteAsync(string input, Action<double>? progress, CancellationToken ct)
    {
        var info = await ProbeAsync(input, ct);
        var output = OutputPaths.For(input, FileKinds.Ext(input), "muted");
        await RunAsync(["-i", input, "-map", "0:v", "-c", "copy", "-an", .. MovFlags(FileKinds.Ext(input)), output], info.Duration, progress, ct);
        return output;
    }

    /// <summary>"height" in pixels (480, 720, 1080...).</summary>
    public static async Task<string> VideoResizeAsync(string input, OpOptions o, Action<double>? progress, CancellationToken ct)
    {
        var info = await ProbeAsync(input, ct);
        var h = Math.Max(16, o.GetInt("height", 720));
        var ext = SameVideoExt(input);
        var output = OutputPaths.For(input, ext, h + "p");
        await RunAsync(["-i", input, "-map", "0:v:0", "-map", "0:a?", "-vf", $"scale=-2:{h}:flags=lanczos", .. ContainerCodecs(ext, 21, true), .. MovFlags(ext), output], info.Duration, progress, ct);
        return output;
    }

    /// <summary>90° clockwise.</summary>
    public static async Task<string> VideoRotateAsync(string input, Action<double>? progress, CancellationToken ct)
    {
        var info = await ProbeAsync(input, ct);
        var ext = SameVideoExt(input);
        var output = OutputPaths.For(input, ext, "rotated");
        await RunAsync(["-i", input, "-map", "0:v:0", "-map", "0:a?", "-vf", "transpose=1", .. ContainerCodecs(ext, 20, true), .. MovFlags(ext), output], info.Duration, progress, ct);
        return output;
    }

    /// <summary>A still PNG from the middle of the video (or at "time" seconds).</summary>
    public static async Task<string> FrameAsync(string input, OpOptions o, CancellationToken ct)
    {
        var info = await ProbeAsync(input, ct);
        var t = o.ContainsKey("time") ? o.GetDouble("time") : info.Duration / 2;
        var output = OutputPaths.For(input, "png", "frame");
        await RunAsync(["-ss", Inv(Math.Max(0, t)), "-i", input, "-frames:v", "1", "-update", "1", output], 0, null, ct);
        return output;
    }

    /// <summary>"factor" 0.25-4 (2 = twice as fast).</summary>
    public static async Task<string> SpeedAsync(string input, OpOptions o, Action<double>? progress, CancellationToken ct)
    {
        var info = await ProbeAsync(input, ct);
        var f = Math.Clamp(o.GetDouble("factor", 2), 0.25, 4);
        var isVideo = FileKinds.Detect(input) == FileCategory.Video && info.HasVideo;
        var ext = isVideo ? SameVideoExt(input) : FileKinds.Ext(input);
        var output = OutputPaths.For(input, ext, Inv(f) + "x");
        var atempo = Atempo(f);
        var args = new List<string> { "-i", input };
        if (isVideo)
        {
            args.AddRange(["-map", "0:v:0", "-map", "0:a?", "-vf", $"setpts=PTS/{Inv(f)}"]);
            if (info.HasAudio) args.AddRange(["-af", atempo]);
            args.AddRange(ContainerCodecs(ext, 21, true));
        }
        else
        {
            args.AddRange(["-map", "0:a:0", "-af", atempo, .. AudioCodec(ext)]);
        }
        args.AddRange(MovFlags(ext));
        args.Add(output);
        await RunAsync(args, info.Duration / f, progress, ct);
        return output;
    }

    // atempo takes 0.5-2 per stage on older builds; chain stages to stay safe.
    static string Atempo(double f)
    {
        var parts = new List<string>();
        while (f > 2) { parts.Add("atempo=2"); f /= 2; }
        while (f < 0.5) { parts.Add("atempo=0.5"); f /= 0.5; }
        parts.Add("atempo=" + Inv(f));
        return string.Join(',', parts);
    }

    public static async Task<string> StripMetadataAsync(string input, Action<double>? progress, CancellationToken ct)
    {
        var info = await ProbeAsync(input, ct);
        var ext = FileKinds.Ext(input);
        var output = OutputPaths.For(input, ext, "clean");
        await RunAsync(["-i", input, "-map", "0", "-map", "-0:d?", "-map_metadata", "-1", "-map_chapters", "-1", "-c", "copy", .. MovFlags(ext), output], info.Duration, progress, ct);
        return output;
    }

    // ---------- Audio tools ----------

    public static async Task<string> AudioCompressAsync(string input, Action<double>? progress, CancellationToken ct)
    {
        var info = await ProbeAsync(input, ct);
        var ext = FileKinds.Ext(input);
        // Uncompressed formats become lossless FLAC; lossy ones get a lower bitrate.
        var outExt = ext is "wav" or "aiff" or "aif" ? "flac" : ext is "flac" ? "mp3" : ext;
        var output = OutputPaths.For(input, outExt, "compressed");
        await RunAsync(["-i", input, "-map", "0:a:0", "-vn", .. AudioCodec(outExt, smaller: true), .. MovFlags(outExt), output], info.Duration, progress, ct);
        return output;
    }

    static async Task<string> AudioFilterAsync(string input, string filter, string suffix, Action<double>? progress, CancellationToken ct, params string[] extra)
    {
        var info = await ProbeAsync(input, ct);
        var ext = FileKinds.Ext(input);
        var output = OutputPaths.For(input, ext, suffix);
        await RunAsync(["-i", input, "-map", "0:a:0", "-vn", "-af", filter, .. extra, .. AudioCodec(ext), .. MovFlags(ext), output], info.Duration, progress, ct);
        return output;
    }

    public static Task<string> NormalizeAsync(string input, Action<double>? progress, CancellationToken ct) =>
        AudioFilterAsync(input, "loudnorm=I=-16:TP=-1.5:LRA=11", "normalized", progress, ct, "-ar", "48000");

    public static Task<string> MonoAsync(string input, Action<double>? progress, CancellationToken ct) =>
        AudioFilterAsync(input, "anull", "mono", progress, ct, "-ac", "1");

    /// <summary>Two-second fade in and fade out (shorter for very short clips).</summary>
    public static async Task<string> FadeAsync(string input, Action<double>? progress, CancellationToken ct)
    {
        var info = await ProbeAsync(input, ct);
        var len = Math.Min(2, info.Duration / 4);
        var outStart = Math.Max(0, info.Duration - len);
        return await AudioFilterAsync(input, $"afade=t=in:st=0:d={Inv(len)},afade=t=out:st={Inv(outStart)}:d={Inv(len)}", "faded", progress, ct);
    }
}
