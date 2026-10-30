using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Poppy;

/// <summary>Poppy's colours, following the Windows light/dark app setting.</summary>
static class Theme
{
    public static bool Dark { get; private set; } = ReadDark();

    public static readonly Color Accent = Color.FromRgb(0xFF, 0x5A, 0x36);      // poppy red-orange
    public static readonly Color AccentDeep = Color.FromRgb(0xE8, 0x3A, 0x1E);
    // The logo's colours: petal gradient and seed-head plum.
    public static readonly Color PetalLight = Color.FromRgb(0xFF, 0x86, 0x58);
    public static readonly Color PetalDeep = Color.FromRgb(0xD6, 0x2C, 0x18);
    public static readonly Color Plum = Color.FromRgb(0x2B, 0x16, 0x30);
    public static readonly Color Disabled = Color.FromRgb(0x9A, 0x9A, 0x9A);

    public static Color SegmentTop => Dark ? Color.FromRgb(0x3A, 0x3A, 0x3E) : Color.FromRgb(0xFF, 0xFF, 0xFF);
    public static Color SegmentBottom => Dark ? Color.FromRgb(0x2C, 0x2C, 0x30) : Color.FromRgb(0xEE, 0xEF, 0xF1);
    public static Color SegmentEdge => Dark ? Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x30, 0x00, 0x00, 0x00);
    public static Color Glass => Dark ? Color.FromArgb(0xD8, 0x22, 0x22, 0x26) : Color.FromArgb(0xC8, 0xF4, 0xF5, 0xF7);
    public static Color Text => Dark ? Color.FromRgb(0xF2, 0xF2, 0xF2) : Color.FromRgb(0x14, 0x14, 0x14);
    public static Color SubText => Dark ? Color.FromRgb(0xA8, 0xA8, 0xAD) : Color.FromRgb(0x6B, 0x6B, 0x70);
    public static Color Window => Dark ? Color.FromRgb(0x1C, 0x1C, 0x1F) : Color.FromRgb(0xF5, 0xF5, 0xF6);
    public static Color Card => Dark ? Color.FromRgb(0x27, 0x27, 0x2B) : Color.FromRgb(0xFF, 0xFF, 0xFF);
    public static Color CardEdge => Dark ? Color.FromRgb(0x36, 0x36, 0x3B) : Color.FromRgb(0xE6, 0xE6, 0xE8);

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly FontFamily Ui = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily UiDisplay = new("Segoe UI Variable Display, Segoe UI");

    static bool ReadDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static void Refresh() => Dark = ReadDark();

    /// <summary>Resources shared by every Poppy window (buttons, inputs) for the current theme.</summary>
    public static ResourceDictionary Resources()
    {
        var d = new ResourceDictionary();
        d["Accent"] = Brush(Accent);
        d["Text"] = Brush(Text);
        d["SubText"] = Brush(SubText);
        d["WindowBg"] = Brush(Window);
        d["Card"] = Brush(Card);
        d["CardEdge"] = Brush(CardEdge);
        return d;
    }
}
