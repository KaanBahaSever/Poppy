using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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
                var bmp = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                // GDI hands back top-down bitmaps with premultiplied alpha; WPF reads them correctly as Pbgra32.
                var converted = new FormatConvertedBitmap(bmp, System.Windows.Media.PixelFormats.Pbgra32, null, 0);
                converted.Freeze();
                return converted;
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
}
