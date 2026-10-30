// Draws the Poppy logo (a ring of rounded poppy petals around a dark seed head) as vectors
// and writes the app icon, PNGs and Microsoft Store tiles.
//   dotnet run --project tools/AssetGen -- <repo root>
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
var appAssets = Path.Combine(root, "src", "Poppy", "Assets");
var storeAssets = Path.Combine(root, "packaging", "msix", "Assets");
var docs = Path.Combine(root, "docs");
Directory.CreateDirectory(appAssets);
Directory.CreateDirectory(storeAssets);
Directory.CreateDirectory(docs);

var thread = new Thread(() =>
{
    // App icon: every size Windows asks for, PNG-compressed inside one .ico.
    int[] iconSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
    var pngs = iconSizes.Select(s => (s, Png(Render(s, s, 0, false)))).ToList();
    WriteIco(Path.Combine(appAssets, "Poppy.ico"), pngs);
    File.WriteAllBytes(Path.Combine(appAssets, "Poppy-256.png"), Png(Render(256, 256, 0, false)));
    File.WriteAllBytes(Path.Combine(docs, "logo.png"), Png(Render(512, 512, 0, false)));
    File.WriteAllBytes(Path.Combine(docs, "banner.png"), Png(Banner(1280, 400)));

    // MSIX / Store tiles (scale-100 and scale-200).
    foreach (var scale in new[] { 100, 200 })
    {
        double k = scale / 100.0;
        void Tile(string name, int w, int h, double pad)
        {
            var px = (int)(w * k);
            var py = (int)(h * k);
            File.WriteAllBytes(Path.Combine(storeAssets, $"{name}.scale-{scale}.png"), Png(Render(px, py, pad, false)));
        }
        Tile("Square44x44Logo", 44, 44, 0.04);
        Tile("Square71x71Logo", 71, 71, 0.12);
        Tile("Square150x150Logo", 150, 150, 0.2);
        Tile("Wide310x150Logo", 310, 150, 0.18);
        Tile("StoreLogo", 50, 50, 0.04);
        Tile("SplashScreen", 620, 300, 0.25);
    }
    foreach (var s in new[] { 16, 24, 32, 48, 256 })
        File.WriteAllBytes(Path.Combine(storeAssets, $"Square44x44Logo.targetsize-{s}_altform-unplated.png"), Png(Render(s, s, 0, false)));
    Console.WriteLine("Assets written to " + root);
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();

// ---------- Drawing ----------

static BitmapSource Render(int w, int h, double padFraction, bool background)
{
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        if (background) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xEE)), null, new Rect(0, 0, w, h));
        var size = Math.Min(w, h) * (1 - 2 * padFraction);
        DrawFlower(dc, new Point(w / 2.0, h / 2.0), size / 2, Math.Min(w, h) <= 24);
    }
    var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
    bmp.Render(visual);
    bmp.Freeze();
    return bmp;
}

static BitmapSource Banner(int w, int h)
{
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0xFF, 0xF6, 0xF1), Color.FromRgb(0xFF, 0xE6, 0xDB), 0), null, new Rect(0, 0, w, h));
        DrawFlower(dc, new Point(h * 0.62, h / 2.0), h * 0.36, false);
        var title = new FormattedText("Poppy", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), h * 0.30,
            new SolidColorBrush(Color.FromRgb(0x2B, 0x16, 0x30)), 1.0);
        dc.DrawText(title, new Point(h * 1.12, h * 0.20));
        var tag = new FormattedText("Drag a file. Tap a key. Pick from the ring.", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), h * 0.085,
            new SolidColorBrush(Color.FromRgb(0x6B, 0x4A, 0x55)), 1.0);
        dc.DrawText(tag, new Point(h * 1.14, h * 0.58));
    }
    var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
    bmp.Render(visual);
    return bmp;
}

/// <summary>Six petals as rounded ring segments (the same shape as Poppy's ring), seed head in the middle.</summary>
static void DrawFlower(DrawingContext dc, Point c, double r, bool tiny)
{
    const int petals = 6;
    double outer = r * 0.98, inner = r * 0.33;
    double round = tiny ? r * 0.16 : r * 0.26;   // big rounding turns ring segments into soft petals
    double gap = tiny ? r * 0.07 : r * 0.05;
    double ro = outer - round / 2, ri = inner + round / 2;
    double sweep = 360.0 / petals;

    for (int i = 0; i < petals; i++)
    {
        double start = i * sweep - sweep / 2, end = i * sweep + sweep / 2;
        double half = round / 2 + gap / 2;
        double ao = half / ro * 180 / Math.PI, ai = half / ri * 180 / Math.PI;
        var fig = new PathFigure { StartPoint = P(c, start + ao, ro), IsClosed = true, IsFilled = true };
        fig.Segments.Add(new ArcSegment(P(c, end - ao, ro), new Size(ro, ro), 0, false, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(P(c, end - ai, ri), true));
        fig.Segments.Add(new ArcSegment(P(c, start + ai, ri), new Size(ri, ri), 0, false, SweepDirection.Counterclockwise, true));
        var core = new PathGeometry([fig]);
        // One solid shape (fill + rounded outline), so the gradient has no visible stroke band.
        var petal = new CombinedGeometry(GeometryCombineMode.Union, core,
            core.GetWidenedPathGeometry(new Pen(Brushes.Black, round) { LineJoin = PenLineJoin.Round }));

        bool lit = i is 0 or 4 or 5; // light from the top-left
        var outerColor = lit ? Color.FromRgb(0xFF, 0x86, 0x58) : Color.FromRgb(0xFF, 0x6C, 0x40);
        var innerColor = lit ? Color.FromRgb(0xE8, 0x3C, 0x1E) : Color.FromRgb(0xD6, 0x2C, 0x18);
        var brush = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = c,
            GradientOrigin = c,
            RadiusX = outer,
            RadiusY = outer,
            GradientStops = [new GradientStop(innerColor, 0.32), new GradientStop(outerColor, 0.92)],
        };
        dc.DrawGeometry(brush, null, petal);

        if (!tiny)
        {
            // Soft highlight near the outer rim of each petal.
            var hp = P(c, i * sweep, ro * 0.86);
            var sheen = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                Center = hp,
                GradientOrigin = hp,
                RadiusX = r * 0.24,
                RadiusY = r * 0.24,
                GradientStops = [new GradientStop(Color.FromArgb(lit ? (byte)0x50 : (byte)0x30, 0xFF, 0xFF, 0xFF), 0), new GradientStop(Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 1)],
            };
            dc.DrawGeometry(sheen, null, petal);
        }
    }

    // Seed head: deep plum disc, ring of golden stamens, darker crown.
    var plum = Color.FromRgb(0x2B, 0x16, 0x30);
    dc.DrawEllipse(new SolidColorBrush(plum), null, c, inner * 0.80, inner * 0.80);
    if (!tiny)
    {
        int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            var p = P(c, i * 360.0 / dots + 18, inner * 0.56);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0xC9, 0x4D)), null, p, r * 0.035, r * 0.035);
        }
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x46, 0x22, 0x4E)), null, c, inner * 0.30, inner * 0.30);
    }
    else
    {
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0xC9, 0x4D)), null, c, inner * 0.22, inner * 0.22);
    }
}

static Point P(Point c, double deg, double radius)
{
    var a = deg * Math.PI / 180;
    return new Point(c.X + Math.Sin(a) * radius, c.Y - Math.Cos(a) * radius);
}

static byte[] Png(BitmapSource src)
{
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(src));
    using var ms = new MemoryStream();
    enc.Save(ms);
    return ms.ToArray();
}

static void WriteIco(string path, List<(int Size, byte[] Png)> images)
{
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);
    w.Write((short)0);
    w.Write((short)1);
    w.Write((short)images.Count);
    int offset = 6 + 16 * images.Count;
    foreach (var (size, png) in images)
    {
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((short)1);
        w.Write((short)32);
        w.Write(png.Length);
        w.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in images) w.Write(png);
}
