using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Poppy.Core;
using static Poppy.Theme;

namespace Poppy;

/// <summary>
/// The ring. A transparent, never-activated, topmost window that is an OLE drop target:
/// Windows hands it the dragged files on DragEnter, so it knows what to offer, and the
/// segment under the cursor on Drop is what runs.
/// </summary>
sealed class RingWindow : Window
{
    public const string WindowTitle = "Poppy Ring";
    const double Outer = 132, Inner = 54, Pad = 28, BarHeight = 46;
    const double Size = (Outer + Pad) * 2;
    const double C = Outer + Pad; // ring centre inside the ring canvas

    readonly AppSettings settings;
    readonly Grid root;
    readonly Canvas ringCanvas;
    readonly Canvas segmentLayer;
    readonly Image thumb;
    readonly Border pill;
    readonly TextBlock pillText;
    readonly StackPanel bar;
    readonly ScaleTransform zoom = new(1, 1);

    readonly List<Segment> segments = [];
    RingLayout layout = new(0, Outer, Inner);
    IReadOnlyList<RingItem> items = [];
    string[]? files;
    RingMode mode;
    int hover = RingLayout.Outside;
    bool ghost;
    IntPtr hwnd;

    public bool IsOpen { get; private set; }
    public RingMode Mode => mode;

    /// <summary>Raised after a drop on an enabled segment; the ring is already closing.</summary>
    public event Action<RingItem, string[]>? ItemDropped;

    public RingWindow(AppSettings settings)
    {
        this.settings = settings;
        Title = WindowTitle;
        AllowsTransparency = true;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        AllowDrop = true;
        Width = Size;
        Height = Size + BarHeight;

        root = new Grid { RenderTransform = zoom, RenderTransformOrigin = new Point(0.5, C / (Size + BarHeight)), Opacity = 0 };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Size) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(BarHeight) });

        bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        // Mode switcher under the ring, in the plum of Poppy's seed head.
        var barBorder = new Border
        {
            Child = bar,
            CornerRadius = new CornerRadius(17),
            Padding = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
            Background = Brush(Plum),
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.25, Color = Colors.Black },
        };
        Grid.SetRow(barBorder, 1);
        root.Children.Add(barBorder);

        ringCanvas = new Canvas { Width = Size, Height = Size };
        root.Children.Add(ringCanvas);

        // Frosted backdrop disc.
        var disc = new Ellipse
        {
            Width = (Outer + 12) * 2,
            Height = (Outer + 12) * 2,
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 4, Opacity = 0.22, Color = Colors.Black },
        };
        disc.SetResourceReference(Shape.FillProperty, "DiscBg");
        disc.SetResourceReference(Shape.StrokeProperty, "DiscEdge");
        disc.StrokeThickness = 1;
        Canvas.SetLeft(disc, C - Outer - 12);
        Canvas.SetTop(disc, C - Outer - 12);
        ringCanvas.Children.Add(disc);

        segmentLayer = new Canvas { Width = Size, Height = Size };
        ringCanvas.Children.Add(segmentLayer);

        // Centre: hub, thumbnail and the name/label pill.
        var hub = new Ellipse { Width = (Inner - 7) * 2, Height = (Inner - 7) * 2 };
        hub.SetResourceReference(Shape.FillProperty, "HubBg");
        Canvas.SetLeft(hub, C - Inner + 7);
        Canvas.SetTop(hub, C - Inner + 7);
        ringCanvas.Children.Add(hub);

        thumb = new Image { Width = 50, Height = 50, Stretch = Stretch.UniformToFill, Clip = new RectangleGeometry(new Rect(0, 0, 50, 50), 9, 9) };
        RenderOptions.SetBitmapScalingMode(thumb, BitmapScalingMode.HighQuality);
        Canvas.SetLeft(thumb, C - 25);
        Canvas.SetTop(thumb, C - 31);
        ringCanvas.Children.Add(thumb);

        pillText = new TextBlock { FontFamily = Ui, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 };
        pill = new Border { Child = pillText, CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 2, 9, 3), Background = Brush(Plum) };
        ringCanvas.Children.Add(pill);

        Content = root;
        ApplyTheme();

        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += (_, _) => SetHover(RingLayout.Outside);
        Drop += OnDrop;
        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeOverlay(hwnd);
        };
    }

    public void ApplyTheme()
    {
        Resources["BarBg"] = Brush(Dark ? Color.FromArgb(0xF0, 0x2A, 0x2A, 0x2E) : Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF));
        Resources["DiscBg"] = new RadialGradientBrush(Glass, Color.FromArgb((byte)(Glass.A - 30), Glass.R, Glass.G, Glass.B));
        Resources["DiscEdge"] = Brush(Dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
        Resources["HubBg"] = Brush(Dark ? Color.FromArgb(0xFF, 0x33, 0x33, 0x37) : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
    }

    /// <summary>Creates the window off-screen once so the first real open is instant.</summary>
    public void Prewarm()
    {
        Left = -10000;
        Top = -10000;
        Show();
        Hide();
    }

    // ---------- Open / close ----------

    /// <summary>Opens centred on the cursor. In ghost mode the ring stays invisible until files arrive.</summary>
    public void Open(RingMode m, int screenX, int screenY, bool ghostMode)
    {
        mode = m;
        files = null;
        ghost = ghostMode;
        hover = RingLayout.Outside;
        items = [];
        thumb.Source = null;
        BuildBar();
        BuildSegments(skeleton: true);
        SetPill(null);

        root.BeginAnimation(OpacityProperty, null);
        root.Opacity = 0;
        Opacity = ghost ? 0.01 : 1; // 1% keeps it hit-testable for the drop loop
        if (!IsVisible) Show();
        Place(screenX, screenY);
        IsOpen = true;
        Animate(appear: true);
        // The drag loop only re-checks the window under the cursor when the mouse moves.
        Native.NudgeMouse();
    }

    void Place(int x, int y)
    {
        var (work, dpi) = Native.MonitorAt(x, y);
        Width = Size * settings.RingScale;
        Height = (Size + BarHeight) * settings.RingScale;
        var s = settings.RingScale * dpi;
        int w = (int)Math.Round(Size * s), h = (int)Math.Round((Size + BarHeight) * s);
        int cy = (int)Math.Round(C * s);
        int left = Math.Clamp(x - w / 2, work.Left, Math.Max(work.Left, work.Right - w));
        int top = Math.Clamp(y - cy, work.Top, Math.Max(work.Top, work.Bottom - h));
        root.LayoutTransform = new ScaleTransform(settings.RingScale, settings.RingScale);
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, left, top, w, h, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    public void SetMode(RingMode m)
    {
        if (!IsOpen || m == mode) return;
        mode = m;
        BuildBar();
        Populate();
    }

    public void Close(bool immediately = false)
    {
        if (!IsOpen) return;
        IsOpen = false;
        files = null;
        if (immediately)
        {
            Hide();
            return;
        }
        Animate(appear: false);
    }

    void Animate(bool appear)
    {
        var dur = TimeSpan.FromMilliseconds(appear ? 130 : 90);
        var ease = new CubicEase { EasingMode = appear ? EasingMode.EaseOut : EasingMode.EaseIn };
        var fade = new DoubleAnimation(appear ? 0 : root.Opacity, appear ? 1 : 0, dur) { EasingFunction = ease };
        var grow = new DoubleAnimation(appear ? 0.86 : 1, appear ? 1 : 0.92, dur) { EasingFunction = ease };
        if (!appear)
            fade.Completed += (_, _) =>
            {
                if (!IsOpen) Hide();
            };
        root.BeginAnimation(OpacityProperty, fade);
        zoom.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        zoom.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    // ---------- Drag & drop ----------

    void OnDragEnter(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (files == null)
        {
            files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files is not { Length: > 0 })
            {
                files = null;
                if (ghost)
                {
                    // Not a file drag after all: step aside so the drop reaches its real target.
                    Close(immediately: true);
                    e.Effects = DragDropEffects.None;
                    return;
                }
            }
            else
            {
                if (ghost)
                {
                    ghost = false;
                    Opacity = 1;
                }
                Populate();
                LoadThumbnail(files[0]);
            }
        }
        OnDragOver(sender, e);
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var p = e.GetPosition(ringCanvas);
        var hit = layout.HitTest(p.X - C, p.Y - C);
        SetHover(hit);
        e.Effects = hit >= 0 && hit < items.Count && items[hit].Enabled && files != null
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var p = e.GetPosition(ringCanvas);
        var hit = layout.HitTest(p.X - C, p.Y - C);
        var dropped = files ?? e.Data.GetData(DataFormats.FileDrop) as string[];
        if (hit >= 0 && hit < items.Count && items[hit].Enabled && dropped is { Length: > 0 })
        {
            e.Effects = DragDropEffects.Copy;
            var item = items[hit];
            Close();
            // Never do real work inside Drop: the drag source (Explorer) is blocked until it returns.
            Dispatcher.BeginInvoke(() => ItemDropped?.Invoke(item, dropped));
        }
        else
        {
            e.Effects = DragDropEffects.None;
            Close();
        }
    }

    // ---------- Building the ring ----------

    void Populate()
    {
        items = files == null ? [] : Catalog.Items(mode, files);
        BuildSegments(skeleton: false);
        SetHover(RingLayout.Outside, force: true);
    }

    void BuildBar()
    {
        bar.Children.Clear();
        AddBarEntry(AppSettings.KeyName(settings.ConvertKey), L.T("mode.convert"), mode == RingMode.Convert);
        AddBarEntry(AppSettings.KeyName(settings.ToolsKey), L.T("mode.tools"), mode == RingMode.Tools);
        AddBarEntry(AppSettings.KeyName(settings.ActionsKey), L.T("mode.actions"), mode == RingMode.Actions);
    }

    void AddBarEntry(string key, string label, bool active)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontFamily = Ui,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = active ? Brushes.White : Brush(Color.FromRgb(0xE8, 0xD9, 0xE6)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock
        {
            Text = key,
            FontFamily = Ui,
            FontSize = 10.5,
            Foreground = active ? Brush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)) : Brush(Color.FromRgb(0x9C, 0x86, 0x9E)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 1, 0, 0),
        });
        bar.Children.Add(new Border
        {
            Child = row,
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(11, 4, 11, 5),
            Background = active ? new LinearGradientBrush(PetalLight, PetalDeep, 90) : Brushes.Transparent,
        });
    }

    void BuildSegments(bool skeleton)
    {
        segmentLayer.Children.Clear();
        segments.Clear();
        var count = skeleton ? 6 : items.Count;
        layout = new RingLayout(count, Outer, Inner);
        if (count == 0) return;
        for (int i = 0; i < count; i++)
        {
            var seg = new Segment(layout, i, skeleton ? null : items[i]);
            segments.Add(seg);
            segmentLayer.Children.Add(seg.Root);
        }
    }

    void SetHover(int index, bool force = false)
    {
        if (index == hover && !force) return;
        hover = index;
        for (int i = 0; i < segments.Count; i++) segments[i].SetHot(i == index);
        SetPill(index >= 0 && index < items.Count ? items[index] : null);
    }

    void SetPill(RingItem? item)
    {
        string text;
        Color color;
        if (item != null)
        {
            text = item.Enabled ? item.Label : item.Reason ?? item.Label;
            color = item.Enabled ? PetalDeep : Color.FromRgb(0x6B, 0x6B, 0x70);
        }
        else if (files == null)
        {
            text = "Poppy";
            color = Plum;
        }
        else if (items.Count == 0)
        {
            text = FileKinds.Common(files) == null ? L.T("mixed") : L.T("nothing");
            color = Color.FromRgb(0x6B, 0x6B, 0x70);
        }
        else
        {
            text = files.Length == 1 ? System.IO.Path.GetFileName(files[0].TrimEnd('\\')) : L.F("job.files", files.Length);
            color = Plum;
        }
        pillText.Text = text;
        pill.Background = Brush(color);
        pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(pill, C - pill.DesiredSize.Width / 2);
        Canvas.SetTop(pill, C + 20);
    }

    void LoadThumbnail(string path)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (files == null || files[0] != path) return;
            thumb.Source = ShellThumbnail.Get(path, 96);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>A static picture of a ring (for the how-to page), with one segment highlighted.</summary>
    public static FrameworkElement Preview(IReadOnlyList<RingItem> items, int hot)
    {
        var canvas = new Canvas { Width = Size, Height = Size };
        var disc = new Ellipse { Width = (Outer + 12) * 2, Height = (Outer + 12) * 2, Fill = Brush(Dark ? Color.FromRgb(0x2A, 0x2A, 0x2E) : Color.FromRgb(0xF1, 0xF2, 0xF4)) };
        Canvas.SetLeft(disc, C - Outer - 12);
        Canvas.SetTop(disc, C - Outer - 12);
        canvas.Children.Add(disc);
        var layout = new RingLayout(items.Count, Outer, Inner);
        for (int i = 0; i < items.Count; i++)
        {
            var seg = new Segment(layout, i, items[i]);
            if (i == hot) seg.SetColorsOnly(true);
            canvas.Children.Add(seg.Root);
        }
        var hub = new Ellipse { Width = (Inner - 7) * 2, Height = (Inner - 7) * 2, Fill = Brush(Dark ? Color.FromRgb(0x33, 0x33, 0x37) : Colors.White) };
        Canvas.SetLeft(hub, C - Inner + 7);
        Canvas.SetTop(hub, C - Inner + 7);
        canvas.Children.Add(hub);
        var label = new TextBlock { Text = hot < items.Count ? items[hot].Label : "", FontFamily = Ui, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brush(Text) };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(label, C - label.DesiredSize.Width / 2);
        Canvas.SetTop(label, C - label.DesiredSize.Height / 2);
        canvas.Children.Add(label);
        return canvas;
    }

    /// <summary>One petal: rounded annular sector with icon and label.</summary>
    sealed class Segment
    {
        public readonly Canvas Root = new() { Width = Size, Height = Size };
        readonly Path shape, edge;
        readonly TextBlock? glyph;
        readonly TextBlock? label;
        readonly RingItem? item;
        readonly ScaleTransform pop = new(1, 1);
        readonly Brush normalFill;

        const double Round = 12;   // stroke width that rounds the corners
        const double Gap = 3.5;    // visual gap between petals

        public Segment(RingLayout layout, int index, RingItem? item)
        {
            this.item = item;
            var geometry = Geometry(layout, index);
            normalFill = new LinearGradientBrush(SegmentTop, SegmentBottom, 90);
            edge = new Path { Data = geometry, Stroke = Brush(SegmentEdge), StrokeThickness = Round + 1.5, StrokeLineJoin = PenLineJoin.Round, Fill = Brush(SegmentEdge) };
            shape = new Path { Data = geometry, Fill = normalFill, Stroke = normalFill, StrokeThickness = Round, StrokeLineJoin = PenLineJoin.Round };
            Root.Children.Add(edge);
            Root.Children.Add(shape);
            Root.RenderTransform = pop;
            Root.RenderTransformOrigin = new Point(0.5, 0.5);
            if (item == null)
            {
                Root.Opacity = 0.55;
                return;
            }

            var (x, y) = layout.SegmentCenter(index);
            var stack = new StackPanel { Width = 86, HorizontalAlignment = HorizontalAlignment.Center };
            if (item.Glyph.Length > 0)
            {
                glyph = new TextBlock { Text = item.Glyph, FontFamily = Icons, FontSize = 17, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 3) };
                stack.Children.Add(glyph);
            }
            label = new TextBlock
            {
                Text = item.Label,
                FontFamily = item.IsConvert ? UiDisplay : Ui,
                FontSize = item.IsConvert ? 15 : 11.5,
                FontWeight = item.IsConvert ? FontWeights.Bold : FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 84,
            };
            stack.Children.Add(label);
            stack.Measure(new Size(86, double.PositiveInfinity));
            Canvas.SetLeft(stack, C + x - 43);
            Canvas.SetTop(stack, C + y - stack.DesiredSize.Height / 2);
            Root.Children.Add(stack);
            SetColors(false);
            if (!item.Enabled) stack.Opacity = 0.4;
        }

        static Geometry Geometry(RingLayout layout, int i)
        {
            if (layout.Count == 1)
            {
                var ring = new CombinedGeometry(GeometryCombineMode.Exclude,
                    new EllipseGeometry(new Point(C, C), layout.OuterRadius - Round / 2, layout.OuterRadius - Round / 2),
                    new EllipseGeometry(new Point(C, C), layout.InnerRadius + Round / 2, layout.InnerRadius + Round / 2));
                ring.Freeze();
                return ring;
            }
            var (start, end) = layout.Angles(i);
            double ro = layout.OuterRadius - Round / 2, ri = layout.InnerRadius + Round / 2;
            double half = Round / 2 + Gap / 2;
            // Angular inset so the gap has the same width at both radii.
            double ao = half / ro * 180 / Math.PI, ai = half / ri * 180 / Math.PI;
            var p1 = P(start + ao, ro);
            var p2 = P(end - ao, ro);
            var p3 = P(end - ai, ri);
            var p4 = P(start + ai, ri);
            var fig = new PathFigure { StartPoint = p1, IsClosed = true, IsFilled = true };
            var large = end - start - 2 * ao > 180;
            fig.Segments.Add(new ArcSegment(p2, new Size(ro, ro), 0, large, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(p3, true));
            fig.Segments.Add(new ArcSegment(p4, new Size(ri, ri), 0, large, SweepDirection.Counterclockwise, true));
            var g = new PathGeometry([fig]);
            g.Freeze();
            return g;
        }

        static Point P(double deg, double r)
        {
            var (x, y) = RingLayout.Polar(deg, r);
            return new Point(C + x, C + y);
        }

        public void SetHot(bool hot)
        {
            if (item == null) return;
            var on = hot && item.Enabled;
            SetColors(on);
            var to = on ? 1.035 : 1.0;
            var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(110)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            pop.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            pop.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }

        public void SetColorsOnly(bool hot) => SetColors(hot);

        void SetColors(bool hot)
        {
            // Hot petal: the logo's radial coral-to-red gradient, with a soft glow.
            var fill = hot ? new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                Center = new Point(C, C),
                GradientOrigin = new Point(C, C),
                RadiusX = Outer,
                RadiusY = Outer,
                GradientStops = [new GradientStop(PetalDeep, 0.38), new GradientStop(PetalLight, 1.0)],
            } : normalFill;
            shape.Fill = fill;
            shape.Stroke = fill;
            edge.Stroke = edge.Fill = hot ? Brush(PetalDeep) : Brush(SegmentEdge);
            Root.Effect = hot ? new DropShadowEffect { Color = PetalLight, BlurRadius = 18, ShadowDepth = 0, Opacity = 0.55 } : null;
            if (glyph != null) glyph.Foreground = hot ? Brushes.White : Brush(Accent);
            if (label != null) label.Foreground = hot ? Brushes.White : Brush(Text);
        }
    }
}
