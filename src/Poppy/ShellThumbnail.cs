using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Poppy;

/// <summary>The thumbnail (or icon) Explorer shows for a file, via IShellItemImageFactory.</summary>
static class ShellThumbnail
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int cx, cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory item);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr h);

    const int SIIGBF_BIGGERSIZEOK = 0x1;

    public static BitmapSource? Get(string path, int size)
    {
        IShellItemImageFactory? factory = null;
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out factory);
            if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_BIGGERSIZEOK, out var hbm) != 0) return null;
            try
            {
                return FromHBitmap(hbm);
            }
            finally
            {
                DeleteObject(hbm);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (factory != null) Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>
    /// GDI gets the orientation right (thumbnails and icons arrive in different row orders), but
    /// photo thumbnails come with an all-zero alpha channel and would be drawn invisible. Keep the
    /// pixels, and make them opaque when the bitmap has no real alpha.
    /// </summary>
    static BitmapSource FromHBitmap(IntPtr hbm)
    {
        BitmapSource src = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        if (src.Format != PixelFormats.Bgra32 && src.Format != PixelFormats.Pbgra32)
            src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        src.CopyPixels(pixels, stride, 0);

        bool anyAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha)
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        // The shell's alpha is premultiplied.
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Diagnostics: <c>Poppy.exe --diag-thumb file out.png</c> writes what the ring and job cards show.</summary>
    public static void Save(string path, string png)
    {
        var bmp = Get(path, 96) ?? throw new InvalidOperationException("no thumbnail");
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = System.IO.File.Create(png);
        enc.Save(fs);
    }
}
