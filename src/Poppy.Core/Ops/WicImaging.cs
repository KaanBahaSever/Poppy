using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Poppy.Core.Ops;

/// <summary>
/// The Windows Imaging Component, for HEIC. Encoding HEIC needs HEVC, which is patent-licensed:
/// Windows provides it through the "HEVC Video Extensions" / "HEIF Image Extensions" from the
/// Microsoft Store, so Poppy uses the system codec instead of shipping one.
/// </summary>
public static class WicImaging
{
    static Lazy<bool> canEncodeHeic = NewProbe();

    public static void Reset() => canEncodeHeic = NewProbe();

    static Lazy<bool> NewProbe() => new(() =>
    {
        try
        {
            using var img = new ImageMagick.MagickImage(ImageMagick.MagickColors.Orange, 16, 16);
            EncodeHeic(img.ToByteArray(ImageMagick.MagickFormat.Png), 0.8).GetAwaiter().GetResult();
            return true;
        }
        catch { return false; }
    });

    public static bool CanEncodeHeic => canEncodeHeic.Value;

    /// <summary>Encodes PNG bytes as HEIC (quality 0-1).</summary>
    public static async Task<byte[]> EncodeHeic(byte[] png, double quality)
    {
        using var input = await ToStream(png);
        var decoder = await BitmapDecoder.CreateAsync(input);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        using var output = new InMemoryRandomAccessStream();
        var props = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(quality, PropertyType.Single) };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.HeifEncoderId, output, props);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        return await ToBytes(output);
    }

    /// <summary>Decodes any image Windows can open (HEIC with the HEIF extension, JPEG XR...) to PNG bytes.</summary>
    public static async Task<byte[]> DecodeToPng(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        using var input = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(input);
        // Apply EXIF orientation the way Photos does.
        var transform = new BitmapTransform();
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        return await ToBytes(output);
    }

    static async Task<InMemoryRandomAccessStream> ToStream(byte[] data)
    {
        // Not AsStreamForWrite(): disposing that wrapper also disposes the stream we hand on.
        var s = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(s))
        {
            writer.WriteBytes(data);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        s.Seek(0);
        return s;
    }

    static async Task<byte[]> ToBytes(IRandomAccessStream s)
    {
        s.Seek(0);
        using var net = s.AsStreamForRead();
        using var ms = new MemoryStream();
        await net.CopyToAsync(ms);
        return ms.ToArray();
    }
}
