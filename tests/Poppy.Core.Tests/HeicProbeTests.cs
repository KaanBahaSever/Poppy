using ImageMagick;
using Poppy.Core.Engines;
using Poppy.Core.Ops;

namespace Poppy.Core.Tests;

public class HeicProbeTests
{
    /// <summary>
    /// When the HEVC extension is installed, saving HEIC must work. (A disposed-stream bug once
    /// made the probe fail silently, so every HEIC test skipped itself.)
    /// </summary>
    [Fact]
    public async Task HeicEncoderWorksWhenTheExtensionIsInstalled()
    {
        var r = await ProcessRunner.RunAsync("powershell.exe",
            ["-NoProfile", "-Command", "(Get-AppxPackage -Name 'Microsoft.HEVCVideoExtension*').Count"], null, default, TimeSpan.FromMinutes(1));
        if (!int.TryParse(r.StdOut.Trim(), out var installed) || installed == 0) return;

        using var img = new MagickImage(MagickColors.Orange, 16, 16);
        var bytes = await WicImaging.EncodeHeic(img.ToByteArray(MagickFormat.Png), 0.8);
        Assert.True(bytes.Length > 100);
        WicImaging.Reset();
        Assert.True(WicImaging.CanEncodeHeic);
        Assert.Null(Converter.Unavailable("photo.png", "heic"));
    }
}
