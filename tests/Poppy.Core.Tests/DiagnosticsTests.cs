using ImageMagick;
using Xunit.Abstractions;

namespace Poppy.Core.Tests;

public class DiagnosticsTests(ITestOutputHelper log)
{
    [Fact]
    public void ReportImageCodecs()
    {
        log.WriteLine("Delegates: " + MagickNET.Delegates);
        log.WriteLine("Features: " + MagickNET.Features);
        foreach (var f in MagickNET.SupportedFormats.Where(f => f.Format is MagickFormat.Heic or MagickFormat.Heif or MagickFormat.Avif or MagickFormat.WebP or MagickFormat.Jxl))
            log.WriteLine($"{f.Format}: read={f.SupportsReading} write={f.SupportsWriting} {f.Description}");
    }
}
