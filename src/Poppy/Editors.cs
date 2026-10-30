using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Poppy.Core;
using Poppy.Core.Ops;
using static Poppy.Theme;
using Path = System.Windows.Shapes.Path;

namespace Poppy;

static class Bitmaps
{
    public static BitmapSource FromPng(byte[] png)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.StreamSource = new MemoryStream(png);
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        return img;
    }
}

/// <summary>Drag a rectangle over the photo; corner handles resize, inside drags move.</summary>
sealed class CropWindow : PoppyDialog
{
    readonly int fullW, fullH;
    readonly double pw, ph; // preview pixel size (canvas units)
    readonly Canvas canvas;
    readonly Rectangle frame = new() { Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
    readonly Path shade = new() { Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), IsHitTestVisible = false };
    readonly List<Rectangle> handles = [];
    readonly List<Line> thirds = [];
    readonly TextBlock sizeLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
    Rect sel;
    double? aspect;
    Point dragStart;
    Rect selStart;
    int dragMode; // 0 none, 1 new, 2 move, 3..6 corners (tl, tr, br, bl)

    public CropWindow(string path) : base(L.T("tool.crop"), 920, 700, resizable: true)
    {
        var (png, w, h) = ImageOps.LoadOriented(path, 1600);
        fullW = w;
        fullH = h;
        var bmp = Bitmaps.FromPng(png);
        pw = bmp.PixelWidth;
        ph = bmp.PixelHeight;

        canvas = new Canvas { Width = pw, Height = ph, Background = Brushes.Transparent, Cursor = Cursors.Cross, ClipToBounds = true };
        canvas.Children.Add(new Image { Source = bmp, Width = pw, Height = ph });
        canvas.Children.Add(shade);
        for (int i = 0; i < 4; i++)
        {
            var l = new Line { Stroke = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1, IsHitTestVisible = false };
            thirds.Add(l);
            canvas.Children.Add(l);
        }
        canvas.Children.Add(frame);
        for (int i = 0; i < 4; i++)
        {
            var r = new Rectangle { Fill = Brushes.White, Stroke = Brush(Accent), StrokeThickness = 2, IsHitTestVisible = false };
            handles.Add(r);
            canvas.Children.Add(r);
        }
        canvas.MouseLeftButtonDown += OnDown;
        canvas.MouseMove += OnMove;
        canvas.MouseLeftButtonUp += (_, _) => { dragMode = 0; canvas.ReleaseMouseCapture(); };

        Body.Children.Add(new Viewbox { Child = canvas, Stretch = Stretch.Uniform });

        var ratios = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, value) in new (string, double?)[] { (L.T("crop.free"), null), ("1:1", 1), ("4:3", 4 / 3.0), ("3:2", 1.5), ("16:9", 16 / 9.0), ("9:16", 9 / 16.0) })
        {
            var b = new ToggleButton { Content = label, Style = Styles.Toggle, IsChecked = value == null };
            b.Click += (_, _) =>
            {
                aspect = value;
                foreach (var o in ratios.Children.OfType<ToggleButton>()) o.IsChecked = o == b;
                if (aspect != null) sel = FitAspect(sel);
                Redraw();
            };
            ratios.Children.Add(b);
        }
        sizeLabel.SetResourceReference(TextBlock.ForegroundProperty, "SubText");
        AddFooterLeft(ratios);
        AddFooterLeft(sizeLabel);

        sel = new Rect(pw * 0.1, ph * 0.1, pw * 0.8, ph * 0.8);
        Redraw();
    }

    double Scale => fullW / pw;
    double HandleSize => Math.Max(10, pw / 60);

    Rect FitAspect(Rect r)
    {
        if (aspect is not { } a) return r;
        var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        double w = r.Width, h = w / a;
        if (h > r.Height) { h = r.Height; w = h * a; }
        if (w > pw) { w = pw; h = w / a; }
        if (h > ph) { h = ph; w = h * a; }
        return Clamp(new Rect(c.X - w / 2, c.Y - h / 2, w, h));
    }

    Rect Clamp(Rect r)
    {
        var w = Math.Min(r.Width, pw);
        var h = Math.Min(r.Height, ph);
        return new Rect(Math.Clamp(r.X, 0, pw - w), Math.Clamp(r.Y, 0, ph - h), w, h);
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(canvas);
        dragStart = p;
        selStart = sel;
        var corners = new[] { sel.TopLeft, sel.TopRight, sel.BottomRight, sel.BottomLeft };
        dragMode = 1;
        for (int i = 0; i < 4; i++)
            if ((corners[i] - p).Length < HandleSize * 1.6) dragMode = 3 + i;
        if (dragMode == 1 && sel.Contains(p)) dragMode = 2;
        canvas.CaptureMouse();
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(canvas);
        if (dragMode == 0)
        {
            var corners = new[] { sel.TopLeft, sel.TopRight, sel.BottomRight, sel.BottomLeft };
            canvas.Cursor = corners.Any(c => (c - p).Length < HandleSize * 1.6) ? Cursors.SizeNWSE : sel.Contains(p) ? Cursors.SizeAll : Cursors.Cross;
            return;
        }
        p = new Point(Math.Clamp(p.X, 0, pw), Math.Clamp(p.Y, 0, ph));
        var d = p - dragStart;
        switch (dragMode)
        {
            case 1: sel = Normalize(dragStart, p); break;
            case 2: sel = Clamp(new Rect(selStart.X + d.X, selStart.Y + d.Y, selStart.Width, selStart.Height)); break;
            default:
                // Opposite corner stays fixed.
                var fixedCorner = dragMode switch { 3 => selStart.BottomRight, 4 => selStart.BottomLeft, 5 => selStart.TopLeft, _ => selStart.TopRight };
                sel = Normalize(fixedCorner, p);
                break;
        }
        if (aspect is { } a && dragMode != 2)
        {
            var anchor = dragMode == 1 ? dragStart : dragMode switch { 3 => selStart.BottomRight, 4 => selStart.BottomLeft, 5 => selStart.TopLeft, _ => selStart.TopRight };
            double w = sel.Width, h = w / a;
            if (h > sel.Height && sel.Height > 0) { h = sel.Height; w = h * a; }
            double x = p.X < anchor.X ? anchor.X - w : anchor.X;
            double y = p.Y < anchor.Y ? anchor.Y - h : anchor.Y;
            sel = Clamp(new Rect(x, y, w, h));
        }
        Redraw();
    }

    static Rect Normalize(Point a, Point b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    void Redraw()
    {
        Canvas.SetLeft(frame, sel.X);
        Canvas.SetTop(frame, sel.Y);
        frame.Width = sel.Width;
        frame.Height = sel.Height;
        frame.StrokeThickness = Math.Max(2, pw / 500);
        shade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, pw, ph)), new RectangleGeometry(sel));
        var corners = new[] { sel.TopLeft, sel.TopRight, sel.BottomRight, sel.BottomLeft };
        for (int i = 0; i < 4; i++)
        {
            handles[i].Width = handles[i].Height = HandleSize;
            Canvas.SetLeft(handles[i], corners[i].X - HandleSize / 2);
            Canvas.SetTop(handles[i], corners[i].Y - HandleSize / 2);
        }
        for (int i = 0; i < 2; i++)
        {
            var x = sel.X + sel.Width * (i + 1) / 3;
            var y = sel.Y + sel.Height * (i + 1) / 3;
            (thirds[i].X1, thirds[i].Y1, thirds[i].X2, thirds[i].Y2) = (x, sel.Y, x, sel.Bottom);
            (thirds[i + 2].X1, thirds[i + 2].Y1, thirds[i + 2].X2, thirds[i + 2].Y2) = (sel.X, y, sel.Right, y);
        }
        var (_, _, w, h) = Pixels();
        sizeLabel.Text = $"{w} × {h}";
        OkButton.IsEnabled = w >= 1 && h >= 1;
    }

    (int X, int Y, int W, int H) Pixels() =>
        ((int)Math.Round(sel.X * Scale), (int)Math.Round(sel.Y * Scale), (int)Math.Round(sel.Width * Scale), (int)Math.Round(sel.Height * Scale));

    public override OpOptions Options
    {
        get
        {
            var (x, y, w, h) = Pixels();
            return new OpOptions().Set("x", x).Set("y", y).Set("w", Math.Min(w, fullW - x)).Set("h", Math.Min(h, fullH - y));
        }
    }
}

/// <summary>Brightness / contrast / saturation sliders, rotate, flip and black &amp; white, with live preview.</summary>
sealed class AdjustWindow : PoppyDialog
{
    readonly byte[] previewPng;
    readonly Image preview = new() { Stretch = Stretch.Uniform };
    readonly Slider brightness, contrast, saturation;
    readonly ToggleButton gray;
    readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(70) };
    int rotate;
    bool flipH, flipV;
    int version;

    public AdjustWindow(string path) : base(L.T("tool.edit"), 980, 680, resizable: true)
    {
        previewPng = ImageOps.LoadOriented(path, 1000).Png;
        preview.Source = Bitmaps.FromPng(previewPng);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        var frame = new Border { Child = preview, CornerRadius = new CornerRadius(8), Background = Brush(Color.FromArgb(0x10, 0, 0, 0)), Padding = new Thickness(8) };
        grid.Children.Add(frame);

        var side = new StackPanel { Margin = new Thickness(18, 0, 0, 0) };
        Grid.SetColumn(side, 1);
        brightness = AddSlider(side, L.T("adj.brightness"));
        contrast = AddSlider(side, L.T("adj.contrast"));
        saturation = AddSlider(side, L.T("adj.saturation"));
        gray = new ToggleButton { Content = L.T("adj.grayscale"), Style = Styles.Toggle, Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        gray.Click += (_, _) => Changed();
        side.Children.Add(gray);

        var tools = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        tools.Children.Add(IconButton("", L.T("adj.rotl"), () => { rotate = (rotate + 270) % 360; Changed(); }, mirror: true));
        tools.Children.Add(IconButton("", L.T("adj.rotr"), () => { rotate = (rotate + 90) % 360; Changed(); }));
        tools.Children.Add(IconButton("", L.T("adj.fliph"), () => { flipH = !flipH; Changed(); }));
        tools.Children.Add(IconButton("", L.T("adj.flipv"), () => { flipV = !flipV; Changed(); }, vertical: true));
        side.Children.Add(tools);
        var reset = new Button { Content = L.T("reset"), Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) =>
        {
            brightness.Value = contrast.Value = saturation.Value = 0;
            gray.IsChecked = false;
            rotate = 0;
            flipH = flipV = false;
            Changed();
        };
        side.Children.Add(reset);
        grid.Children.Add(side);
        Body.Children.Add(grid);

        debounce.Tick += async (_, _) =>
        {
            debounce.Stop();
            var v = ++version;
            var o = Options;
            var png = await Task.Run(() => ImageOps.PreviewAdjust(previewPng, o));
            if (v == version) preview.Source = Bitmaps.FromPng(png);
        };
    }

    Slider AddSlider(Panel parent, string label)
    {
        parent.Children.Add(Label(label, parent.Children.Count == 0 ? 0 : 14));
        var s = new Slider { Minimum = -100, Maximum = 100, Value = 0, IsSnapToTickEnabled = false, SmallChange = 1, LargeChange = 10 };
        s.ValueChanged += (_, _) => Changed();
        s.MouseDoubleClick += (_, _) => s.Value = 0;
        parent.Children.Add(s);
        return s;
    }

    static Button IconButton(string glyph, string tip, Action click, bool mirror = false, bool vertical = false)
    {
        var text = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 16 };
        if (mirror) text.RenderTransform = new ScaleTransform(-1, 1, 8, 8);
        if (vertical) text.RenderTransform = new RotateTransform(90, 8, 8);
        var b = new Button { Content = text, ToolTip = tip, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 7, 10, 7) };
        b.Click += (_, _) => click();
        return b;
    }

    void Changed()
    {
        debounce.Stop();
        debounce.Start();
    }

    public override OpOptions Options => new OpOptions()
        .Set("brightness", Math.Round(brightness.Value))
        .Set("contrast", Math.Round(contrast.Value))
        .Set("saturation", Math.Round(saturation.Value))
        .Set("grayscale", gray.IsChecked == true)
        .Set("rotate", rotate)
        .Set("flipH", flipH)
        .Set("flipV", flipV);
}

/// <summary>Pen, highlighter, boxes and arrows on top of the photo. Redact mode draws solid black boxes.</summary>
sealed class AnnotateWindow : PoppyDialog
{
    readonly bool redact;
    readonly Grid surface;
    readonly InkCanvas ink;
    readonly Canvas shapes = new() { IsHitTestVisible = false };
    readonly int w, h;
    readonly Stack<UIElement> history = new();
    string tool;
    Color color = Color.FromRgb(0xFF, 0x3B, 0x30);
    Point start;
    Shape? drawing;
    string? rendered;

    public AnnotateWindow(string path, bool redact) : base(L.T(redact ? "tool.redact" : "tool.annotate"), 1000, 720, resizable: true)
    {
        this.redact = redact;
        tool = redact ? "box" : "pen";
        if (redact) color = Colors.Black;
        var (png, ow, oh) = ImageOps.LoadOriented(path, 0);
        w = ow;
        h = oh;
        var bmp = Bitmaps.FromPng(png);

        ink = new InkCanvas { Width = w, Height = h, Background = Brushes.Transparent, EditingMode = InkCanvasEditingMode.Ink };
        ink.StrokeCollected += (_, e) => history.Push(new StrokeMarker(e.Stroke));
        surface = new Grid { Width = w, Height = h };
        surface.Children.Add(new Image { Source = bmp, Width = w, Height = h, Stretch = Stretch.Fill });
        surface.Children.Add(shapes);
        surface.Children.Add(ink);
        ink.PreviewMouseLeftButtonDown += ShapeDown;
        ink.PreviewMouseMove += ShapeMove;
        ink.PreviewMouseLeftButtonUp += ShapeUp;

        var dock = new DockPanel();
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(bar, Dock.Top);
        var toolButtons = new List<ToggleButton>();
        void AddTool(string id, string glyph, string tip)
        {
            var b = new ToggleButton { Content = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 15 }, ToolTip = tip, Style = Styles.Toggle, IsChecked = id == tool, Margin = new Thickness(0, 0, 6, 6) };
            b.Click += (_, _) =>
            {
                tool = id;
                foreach (var o in toolButtons) o.IsChecked = o == b;
                ApplyTool();
            };
            toolButtons.Add(b);
            bar.Children.Add(b);
        }
        AddTool("pen", "", L.T("ann.pen"));
        AddTool("marker", "", L.T("ann.marker"));
        AddTool("box", "", L.T("ann.box"));
        if (!redact) AddTool("arrow", "", L.T("ann.arrow"));
        AddTool("eraser", "", L.T("ann.eraser"));

        bar.Children.Add(new Border { Width = 14 });
        var swatches = new List<ToggleButton>();
        Color[] palette = redact
            ? [Colors.Black, Colors.White, Color.FromRgb(0x80, 0x80, 0x80)]
            : [Color.FromRgb(0xFF, 0x3B, 0x30), Accent, Color.FromRgb(0xFF, 0xCC, 0x00), Color.FromRgb(0x34, 0xC7, 0x59), Color.FromRgb(0x2F, 0x6F, 0xEB), Colors.Black, Colors.White];
        foreach (var c in palette)
        {
            var dot = new Ellipse { Width = 18, Height = 18, Fill = new SolidColorBrush(c), Stroke = Brush(CardEdge), StrokeThickness = 1 };
            var b = new ToggleButton { Content = dot, Style = Styles.Toggle, IsChecked = c == color, Padding = new Thickness(6), Margin = new Thickness(0, 0, 4, 6) };
            b.Click += (_, _) =>
            {
                color = c;
                foreach (var o in swatches) o.IsChecked = o == b;
                ApplyTool();
            };
            swatches.Add(b);
            bar.Children.Add(b);
        }
        bar.Children.Add(new Border { Width = 14 });
        var undo = new Button { Content = new TextBlock { Text = "", FontFamily = Icons, FontSize = 15 }, ToolTip = L.T("undo"), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 6, 6) };
        undo.Click += (_, _) => Undo();
        bar.Children.Add(undo);
        var clear = new Button { Content = L.T("ann.clear"), Margin = new Thickness(0, 0, 6, 6) };
        clear.Click += (_, _) =>
        {
            ink.Strokes.Clear();
            shapes.Children.Clear();
            history.Clear();
        };
        bar.Children.Add(clear);
        dock.Children.Add(bar);
        if (redact)
        {
            var hint = Label(L.T("ann.redact.hint"));
            DockPanel.SetDock(hint, Dock.Bottom);
            dock.Children.Add(hint);
        }
        dock.Children.Add(new Viewbox { Child = surface, Stretch = Stretch.Uniform });
        Body.Children.Add(dock);

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { Undo(); e.Handled = true; }
        };
        ApplyTool();
        Closed += (_, _) => { if (DialogResult != true && rendered != null) TryDelete(rendered); };
    }

    double Thickness => Math.Max(3, Math.Max(w, h) / 300.0);

    void ApplyTool()
    {
        switch (tool)
        {
            case "pen":
                ink.EditingMode = InkCanvasEditingMode.Ink;
                ink.DefaultDrawingAttributes = new DrawingAttributes { Color = color, Width = Thickness, Height = Thickness, FitToCurve = true };
                break;
            case "marker":
                ink.EditingMode = InkCanvasEditingMode.Ink;
                ink.DefaultDrawingAttributes = new DrawingAttributes
                {
                    Color = color, Width = Thickness * 6, Height = Thickness * 6, IsHighlighter = true, StylusTip = StylusTip.Rectangle, FitToCurve = true,
                };
                break;
            case "eraser":
                ink.EditingMode = InkCanvasEditingMode.EraseByStroke;
                break;
            default:
                ink.EditingMode = InkCanvasEditingMode.None;
                break;
        }
        ink.Cursor = tool is "box" or "arrow" ? Cursors.Cross : null;
    }

    void ShapeDown(object sender, MouseButtonEventArgs e)
    {
        if (tool is not ("box" or "arrow")) return;
        start = e.GetPosition(ink);
        drawing = tool == "box"
            ? new Rectangle
            {
                Fill = redact ? new SolidColorBrush(color) : Brushes.Transparent,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = redact ? 0 : Thickness,
                RadiusX = redact ? 0 : Thickness,
                RadiusY = redact ? 0 : Thickness,
            }
            : new Path { Stroke = new SolidColorBrush(color), StrokeThickness = Thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        shapes.Children.Add(drawing);
        ink.CaptureMouse();
        e.Handled = true;
        ShapeMove(sender, e);
    }

    void ShapeMove(object sender, MouseEventArgs e)
    {
        if (drawing == null) return;
        var p = e.GetPosition(ink);
        p = new Point(Math.Clamp(p.X, 0, w), Math.Clamp(p.Y, 0, h));
        if (drawing is Rectangle r)
        {
            Canvas.SetLeft(r, Math.Min(start.X, p.X));
            Canvas.SetTop(r, Math.Min(start.Y, p.Y));
            r.Width = Math.Abs(p.X - start.X);
            r.Height = Math.Abs(p.Y - start.Y);
        }
        else if (drawing is Path path)
        {
            path.Data = Arrow(start, p, Thickness * 4);
        }
        e.Handled = true;
    }

    static Geometry Arrow(Point a, Point b, double head)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(a, false, false);
            c.LineTo(b, true, true);
            var v = a - b;
            if (v.Length > 1)
            {
                v.Normalize();
                var left = new Matrix(); left.Rotate(28);
                var right = new Matrix(); right.Rotate(-28);
                c.BeginFigure(b + left.Transform(v) * head, false, false);
                c.LineTo(b, true, true);
                c.LineTo(b + right.Transform(v) * head, true, true);
            }
        }
        g.Freeze();
        return g;
    }

    void ShapeUp(object sender, MouseButtonEventArgs e)
    {
        if (drawing == null) return;
        ink.ReleaseMouseCapture();
        if (drawing is Rectangle { Width: < 2 } or Rectangle { Height: < 2 }) shapes.Children.Remove(drawing);
        else history.Push(drawing);
        drawing = null;
        e.Handled = true;
    }

    void Undo()
    {
        if (history.Count == 0) return;
        var last = history.Pop();
        if (last is StrokeMarker s) ink.Strokes.Remove(s.Stroke);
        else shapes.Children.Remove(last);
    }

    sealed class StrokeMarker(Stroke stroke) : UIElement
    {
        public Stroke Stroke { get; } = stroke;
    }

    protected override bool Validate()
    {
        // Render at the photo's full resolution, independent of the window's zoom.
        surface.Measure(new Size(w, h));
        surface.Arrange(new Rect(0, 0, w, h));
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(surface);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Poppy", "edit-" + Guid.NewGuid().ToString("N")[..8] + ".png");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        using (var fs = File.Create(file)) enc.Save(fs);
        rendered = file;
        return true;
    }

    static void TryDelete(string f)
    {
        try { File.Delete(f); } catch { }
    }

    public override OpOptions Options => new OpOptions().Set("rendered", rendered ?? "");
}

/// <summary>Pick start and end on a waveform timeline, with a playable preview.</summary>
sealed class TrimWindow : PoppyDialog
{
    readonly string path;
    readonly MediaElement media = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Stop, ScrubbingEnabled = true, Stretch = Stretch.Uniform };
    readonly Image still = new() { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
    readonly Canvas timeline = new() { Height = 86, Background = Brushes.Transparent, ClipToBounds = true, Cursor = Cursors.Hand };
    readonly Image wave = new() { Stretch = Stretch.Fill, Height = 86, Opacity = 0.9 };
    readonly Rectangle range = new() { Fill = new SolidColorBrush(Color.FromArgb(0x33, Accent.R, Accent.G, Accent.B)), Stroke = Brush(Accent), StrokeThickness = 2, RadiusX = 6, RadiusY = 6, IsHitTestVisible = false };
    readonly Rectangle head = new() { Width = 2, Fill = Brush(Color.FromRgb(0x2F, 0x6F, 0xEB)), IsHitTestVisible = false };
    readonly TextBox startBox = new() { Width = 96 }, endBox = new() { Width = 96 };
    readonly TextBlock lengthLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    readonly Button play;
    readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromMilliseconds(50) };
    readonly bool isVideo;
    double duration, start, end, position;
    int dragEdge; // 0 seek, 1 start, 2 end
    bool playing, mediaOk;
    CancellationTokenSource? frameCts;

    public TrimWindow(string path) : base(L.T("tool.trim"), 900, 640, resizable: true)
    {
        this.path = path;
        isVideo = FileKinds.Detect(path) == FileCategory.Video;

        var layout = new DockPanel();
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(controls, Dock.Bottom);
        play = new Button { Content = new TextBlock { Text = "", FontFamily = Icons, FontSize = 14 }, Padding = new Thickness(12, 7, 12, 7) };
        play.Click += (_, _) => TogglePlay();
        controls.Children.Add(play);
        controls.Children.Add(Small(L.T("trim.start"), 14));
        controls.Children.Add(startBox);
        var setStart = new Button { Content = "[", ToolTip = L.T("trim.setstart"), Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(10, 5, 10, 5) };
        setStart.Click += (_, _) => { start = Math.Min(position, end - 0.1); Sync(); };
        controls.Children.Add(setStart);
        controls.Children.Add(Small(L.T("trim.end"), 14));
        controls.Children.Add(endBox);
        var setEnd = new Button { Content = "]", ToolTip = L.T("trim.setend"), Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(10, 5, 10, 5) };
        setEnd.Click += (_, _) => { end = Math.Max(position, start + 0.1); Sync(); };
        controls.Children.Add(setEnd);
        lengthLabel.SetResourceReference(TextBlock.ForegroundProperty, "SubText");
        controls.Children.Add(lengthLabel);
        layout.Children.Add(controls);

        var strip = new Border { CornerRadius = new CornerRadius(8), Background = Brush(Color.FromArgb(0x14, 0, 0, 0)), Child = timeline, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(strip, Dock.Bottom);
        timeline.Children.Add(wave);
        timeline.Children.Add(range);
        timeline.Children.Add(head);
        timeline.SizeChanged += (_, _) => Sync();
        timeline.MouseLeftButtonDown += TimelineDown;
        timeline.MouseMove += TimelineMove;
        timeline.MouseLeftButtonUp += (_, _) => { dragEdge = -1; timeline.ReleaseMouseCapture(); };
        layout.Children.Add(strip);

        var screen = new Grid { Background = Brushes.Black };
        screen.Children.Add(media);
        screen.Children.Add(still);
        if (!isVideo)
        {
            screen.Background = Brushes.Transparent;
            screen.Children.Add(new Image { Source = Logo.Small, Width = 96, Height = 96, Opacity = 0.9 });
        }
        layout.Children.Add(new Border { Child = screen, CornerRadius = new CornerRadius(8), ClipToBounds = true });
        Body.Children.Add(layout);

        startBox.LostFocus += (_, _) => { if (TryParse(startBox.Text, out var v)) start = Math.Clamp(v, 0, end - 0.1); Sync(); };
        endBox.LostFocus += (_, _) => { if (TryParse(endBox.Text, out var v)) end = Math.Clamp(v, start + 0.1, duration); Sync(); };
        media.MediaOpened += (_, _) => { mediaOk = true; };
        media.MediaFailed += (_, _) => { mediaOk = false; ShowStill(position); };
        tick.Tick += (_, _) =>
        {
            if (!playing) return;
            position = media.Position.TotalSeconds;
            if (position >= end) { media.Pause(); playing = false; SetPlayIcon(); position = end; }
            Sync();
        };
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => { media.Stop(); media.Close(); tick.Stop(); };
        dragEdge = -1;
    }

    static TextBlock Small(string t, double left) => new() { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 6, 0) };

    async Task LoadAsync()
    {
        try
        {
            var info = await MediaOps.ProbeAsync(path);
            duration = Math.Max(0.1, info.Duration);
            start = 0;
            end = duration;
            Sync();
            if (info.HasAudio)
            {
                var png = await MediaOps.WaveformAsync(path, 1600, 172, "#FF5A36", CancellationToken.None);
                wave.Source = Bitmaps.FromPng(await File.ReadAllBytesAsync(png));
                File.Delete(png);
            }
        }
        catch (Exception e)
        {
            lengthLabel.Text = e.Message;
        }
        media.Source = new Uri(path);
        media.Play();
        media.Pause();
        media.Position = TimeSpan.Zero;
        tick.Start();
    }

    void TogglePlay()
    {
        if (!mediaOk) return;
        if (playing) media.Pause();
        else
        {
            if (position >= end - 0.05 || position < start) position = start;
            media.Position = TimeSpan.FromSeconds(position);
            media.Play();
        }
        playing = !playing;
        SetPlayIcon();
    }

    void SetPlayIcon() => ((TextBlock)play.Content).Text = playing ? "" : "";

    double X(double t) => duration <= 0 ? 0 : t / duration * timeline.ActualWidth;
    double T(double x) => Math.Clamp(x / Math.Max(1, timeline.ActualWidth) * duration, 0, duration);

    void TimelineDown(object sender, MouseButtonEventArgs e)
    {
        var x = e.GetPosition(timeline).X;
        dragEdge = Math.Abs(x - X(start)) < 10 ? 1 : Math.Abs(x - X(end)) < 10 ? 2 : 0;
        timeline.CaptureMouse();
        TimelineMove(sender, e);
    }

    void TimelineMove(object sender, MouseEventArgs e)
    {
        var x = e.GetPosition(timeline).X;
        if (dragEdge < 0)
        {
            timeline.Cursor = Math.Abs(x - X(start)) < 10 || Math.Abs(x - X(end)) < 10 ? Cursors.SizeWE : Cursors.Hand;
            return;
        }
        var t = T(x);
        switch (dragEdge)
        {
            case 1: start = Math.Min(t, end - 0.1); Seek(start); break;
            case 2: end = Math.Max(t, start + 0.1); Seek(end); break;
            default: Seek(t); break;
        }
        Sync();
    }

    void Seek(double t)
    {
        position = t;
        if (mediaOk) media.Position = TimeSpan.FromSeconds(t);
        else if (isVideo) ShowStill(t);
    }

    /// <summary>When Windows cannot play the file (some MKV/WebM), show FFmpeg-extracted stills instead.</summary>
    async void ShowStill(double t)
    {
        if (!isVideo) return;
        media.Visibility = Visibility.Collapsed;
        still.Visibility = Visibility.Visible;
        frameCts?.Cancel();
        var cts = frameCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(120, cts.Token);
            var png = await MediaOps.FrameBytesAsync(path, t, 960, cts.Token);
            if (!cts.IsCancellationRequested) still.Source = Bitmaps.FromPng(png);
        }
        catch { }
    }

    void Sync()
    {
        var hgt = timeline.ActualHeight;
        wave.Width = timeline.ActualWidth;
        Canvas.SetLeft(range, X(start));
        range.Width = Math.Max(2, X(end) - X(start));
        range.Height = hgt;
        Canvas.SetLeft(head, X(position) - 1);
        head.Height = hgt;
        if (!startBox.IsKeyboardFocused) startBox.Text = Format(start);
        if (!endBox.IsKeyboardFocused) endBox.Text = Format(end);
        lengthLabel.Text = L.F("trim.length", Format(end - start));
    }

    static string Format(double s)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, s));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f") : t.ToString(@"m\:ss\.f");
    }

    static bool TryParse(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Trim().Replace(',', '.').Split(':');
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return false;
            total = total * 60 + v;
        }
        seconds = total;
        return true;
    }

    protected override bool Validate()
    {
        if (end - start < 0.05)
        {
            MessageBox.Show(this, L.T("trim.invalid"), "Poppy");
            return false;
        }
        return true;
    }

    public override OpOptions Options => new OpOptions().Set("start", Math.Round(start, 3)).Set("end", Math.Round(end, 3));
}
