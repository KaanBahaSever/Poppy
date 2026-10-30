using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Poppy.Core;
using Poppy.Core.Ops;
using static Poppy.Theme;

namespace Poppy;

/// <summary>Base for Poppy's dialogs: themed, comes to the front after a drop, OK/Cancel footer.</summary>
abstract class PoppyDialog : Window
{
    protected readonly Grid Body = new();
    protected readonly Button OkButton;
    readonly StackPanel footer;

    protected PoppyDialog(string title, double width, double height, bool resizable = false)
    {
        Title = title + " — Poppy";
        Width = width;
        Height = height;
        MinWidth = Math.Min(width, 360);
        MinHeight = Math.Min(height, 220);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = resizable ? ResizeMode.CanResize : ResizeMode.NoResize;
        ShowInTaskbar = true;
        Topmost = true;
        Icon = Logo.Small;
        SetResourceReference(BackgroundProperty, "WindowBg");
        FontFamily = Ui;

        var layout = new DockPanel { Margin = new Thickness(20, 16, 20, 16) };
        var heading = new TextBlock { Text = title, FontFamily = UiDisplay, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        DockPanel.SetDock(heading, Dock.Top);
        layout.Children.Add(heading);

        footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        var cancel = new Button { Content = L.T("cancel"), IsCancel = true, MinWidth = 90 };
        OkButton = new Button { Content = L.T("save"), IsDefault = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), Style = Styles.Primary };
        OkButton.Click += (_, _) =>
        {
            if (Validate()) DialogResult = true;
        };
        footer.Children.Add(cancel);
        footer.Children.Add(OkButton);
        layout.Children.Add(footer);
        layout.Children.Add(Body);
        Content = layout;

        SourceInitialized += (_, _) => Native.StyleWindow(new WindowInteropHelper(this).Handle, Dark);
        Loaded += (_, _) =>
        {
            Native.ForceForeground(new WindowInteropHelper(this).Handle);
            Activate();
            Topmost = false; // only to get in front; do not stay above everything
        };
    }

    protected void AddFooterLeft(UIElement e)
    {
        footer.Children.Insert(0, e);
    }

    protected virtual bool Validate() => true;

    public abstract OpOptions Options { get; }

    protected static TextBlock Label(string text, double top = 0)
    {
        var t = new TextBlock { Text = text, FontSize = 13, Margin = new Thickness(0, top, 0, 6) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "SubText");
        return t;
    }
}

/// <summary>Small forms: resize presets, speed, page range, password, colour, offset.</summary>
sealed class OptionsDialog : PoppyDialog
{
    readonly StackPanel fields = new();
    readonly Dictionary<string, Func<string?>> readers = [];
    readonly OpOptions result = new();
    Func<OpOptions, string?>? validator;

    public OptionsDialog(string title, double height = 330) : base(title, 440, height)
    {
        Body.Children.Add(fields);
    }

    public override OpOptions Options => result;

    public OptionsDialog Choice(string key, string label, IReadOnlyList<(string Value, string Text)> options, string selected)
    {
        fields.Children.Add(Label(label, fields.Children.Count == 0 ? 0 : 12));
        var wrap = new WrapPanel();
        var group = "g" + key;
        string current = selected;
        foreach (var (value, text) in options)
        {
            var b = new ToggleButton { Content = text, Style = Styles.Toggle, IsChecked = value == selected, Margin = new Thickness(0, 0, 6, 6), Tag = group };
            b.Click += (_, _) =>
            {
                current = value;
                foreach (var other in wrap.Children.OfType<ToggleButton>()) other.IsChecked = other == b;
                ChoiceChanged?.Invoke(key, value);
            };
            wrap.Children.Add(b);
        }
        fields.Children.Add(wrap);
        readers[key] = () => current;
        return this;
    }

    public event Action<string, string>? ChoiceChanged;

    public OptionsDialog Number(string key, string label, double value, string? suffix = null)
    {
        var group = new StackPanel { Tag = key };
        fields.Children.Add(group);
        group.Children.Add(Label(label, 12));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var box = new TextBox { Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture), Width = 120, Name = "f_" + key.Replace('.', '_') };
        box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(ch => char.IsDigit(ch) || ch is '-' or '.' or ',');
        row.Children.Add(box);
        if (suffix != null)
        {
            var s = new TextBlock { Text = suffix, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            s.SetResourceReference(TextBlock.ForegroundProperty, "SubText");
            row.Children.Add(s);
        }
        group.Children.Add(row);
        readers[key] = () => box.Text.Replace(',', '.');
        Loaded += (_, _) => { if (group.Visibility == Visibility.Visible) { box.Focus(); box.SelectAll(); } };
        return this;
    }

    public OptionsDialog Text(string key, string label, string value, string? hint = null)
    {
        var group = new StackPanel { Tag = key };
        fields.Children.Add(group);
        group.Children.Add(Label(label, 12));
        var box = new TextBox { Text = value };
        group.Children.Add(box);
        if (hint != null) group.Children.Add(Label(hint, 6));
        readers[key] = () => box.Text;
        Loaded += (_, _) => { if (group.Visibility == Visibility.Visible) { box.Focus(); box.SelectAll(); } };
        return this;
    }

    public OptionsDialog Password(string key)
    {
        fields.Children.Add(Label(L.T("dlg.password"), 0));
        var a = new PasswordBox();
        fields.Children.Add(a);
        fields.Children.Add(Label(L.T("dlg.password.again"), 12));
        var b = new PasswordBox();
        fields.Children.Add(b);
        readers[key] = () => a.Password == b.Password ? a.Password : null;
        validator = o => o.Get(key).Length == 0 ? L.T("dlg.password.mismatch") : null;
        Loaded += (_, _) => a.Focus();
        return this;
    }

    public OptionsDialog Colors(string key, string label, string selected)
    {
        string[] swatches = ["#FFFFFF", "#000000", "#F2F2F2", "#FFF4EE", "#FF5A36", "#2F6FEB", "#22C55E", "#FACC15"];
        fields.Children.Add(Label(label, fields.Children.Count == 0 ? 0 : 12));
        var wrap = new WrapPanel();
        string current = selected;
        var hex = new TextBox { Text = selected, Width = 96, Margin = new Thickness(4, 0, 0, 6), VerticalAlignment = VerticalAlignment.Center };
        foreach (var s in swatches)
        {
            var color = (Color)ColorConverter.ConvertFromString(s);
            var b = new ToggleButton
            {
                Width = 30, Height = 30, Margin = new Thickness(0, 0, 6, 6), IsChecked = s == selected, Cursor = Cursors.Hand,
                Template = SwatchTemplate(), Background = new SolidColorBrush(color), ToolTip = s,
            };
            b.Click += (_, _) =>
            {
                current = s;
                hex.Text = s;
                foreach (var o in wrap.Children.OfType<ToggleButton>()) o.IsChecked = o == b;
            };
            wrap.Children.Add(b);
        }
        wrap.Children.Add(hex);
        hex.TextChanged += (_, _) => current = hex.Text.Trim();
        fields.Children.Add(wrap);
        readers[key] = () => current;
        validator = o =>
        {
            try { ColorConverter.ConvertFromString(o.Get(key)); return null; }
            catch { return L.T("dlg.color.invalid"); }
        };
        return this;
    }

    static ControlTemplate SwatchTemplate()
    {
        var t = new ControlTemplate(typeof(ToggleButton));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(15));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        border.SetValue(Border.BorderBrushProperty, Brush(CardEdge));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.Name = "b";
        t.VisualTree = border;
        var checkedTrigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, Brush(Accent), "b"));
        checkedTrigger.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(3), "b"));
        t.Triggers.Add(checkedTrigger);
        return t;
    }

    public OptionsDialog Hint(string text)
    {
        fields.Children.Add(Label(text, 12));
        return this;
    }

    public void SetVisible(string key, bool visible)
    {
        foreach (var g in fields.Children.OfType<StackPanel>().Where(g => Equals(g.Tag, key)))
            g.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override bool Validate()
    {
        result.Clear();
        foreach (var (k, read) in readers) result[k] = read() ?? "";
        var error = validator?.Invoke(result);
        if (error != null)
        {
            MessageBox.Show(this, error, "Poppy", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }
}

/// <summary>Builds the right dialog for each item and returns its options, or null if cancelled.</summary>
static class Ask
{
    public static OpOptions? For(RingItem item, string[] files)
    {
        Window? dlg = item.Input switch
        {
            InputKind.Resize => ResizeImage(),
            InputKind.VideoResize => new OptionsDialog(L.T("tool.resize"), 250)
                .Choice("height", L.T("dlg.height"), [("2160", "4K"), ("1440", "1440p"), ("1080", "1080p"), ("720", "720p"), ("480", "480p"), ("360", "360p")], "720"),
            InputKind.Background => new OptionsDialog(L.T("tool.background"), 330)
                .Colors("color", L.T("dlg.color"), "#FFFFFF")
                .Number("padding", L.T("dlg.padding"), 0, "%"),
            InputKind.Speed => new OptionsDialog(L.T("tool.speed"), 250)
                .Choice("factor", L.T("dlg.speed"), [("0.5", "0.5×"), ("0.75", "0.75×"), ("1.25", "1.25×"), ("1.5", "1.5×"), ("2", "2×"), ("3", "3×")], "2"),
            InputKind.SplitPdf => SplitPdf(files[0]),
            InputKind.PageRange => new OptionsDialog(L.T("tool.extract"), 280)
                .Text("pages", L.T("dlg.pages"), "1", L.F("dlg.pages.hint", SafePageCount(files[0]))),
            InputKind.Password => new OptionsDialog(L.T("tool.protect"), 320).Password("password"),
            InputKind.Offset => new OptionsDialog(L.T("tool.shift"), 270)
                .Number("ms", L.T("dlg.offset"), 1000, "ms")
                .Hint(L.T("dlg.offset.hint")),
            InputKind.Crop => new CropWindow(files[0]),
            InputKind.Adjust => new AdjustWindow(files[0]),
            InputKind.Annotate => new AnnotateWindow(files[0], redact: false),
            InputKind.Redact => new AnnotateWindow(files[0], redact: true),
            InputKind.Trim => new TrimWindow(files[0]),
            _ => null,
        };
        if (dlg == null) return new OpOptions();
        if (dlg.ShowDialog() != true) return null;
        var o = ((PoppyDialog)dlg).Options;
        if (item.Input == InputKind.Resize)
        {
            var preset = o.Get("preset");
            if (preset != "custom")
            {
                var parts = preset.Split(':');
                o.Set("mode", parts[0]).Set("value", parts[1]);
            }
            else o.Set("mode", "percent");
        }
        return o;
    }

    static OptionsDialog ResizeImage()
    {
        var d = new OptionsDialog(L.T("tool.resize"), 340)
            .Choice("preset", L.T("dlg.size"),
                [("percent:50", "50%"), ("percent:25", "25%"), ("max:3840", "3840 px"), ("max:1920", "1920 px"), ("max:1280", "1280 px"), ("max:800", "800 px"), ("custom", L.T("dlg.custom"))],
                "percent:50")
            .Number("value", L.T("dlg.percent"), 50, "%");
        d.Loaded += (_, _) => d.SetVisible("value", false);
        d.ChoiceChanged += (_, v) => d.SetVisible("value", v == "custom");
        return d;
    }

    static OptionsDialog SplitPdf(string pdf)
    {
        var d = new OptionsDialog(L.T("tool.split"), 360)
            .Choice("mode", L.T("split.how"), [("each", L.T("split.each")), ("every", L.T("split.every")), ("ranges", L.T("split.ranges"))], "each")
            .Number("n", L.T("split.n"), 2, L.T("split.pages"))
            .Text("ranges", L.T("split.rangeslabel"), "1-3, 4-", L.F("split.hint", SafePageCount(pdf)));
        d.Loaded += (_, _) => { d.SetVisible("n", false); d.SetVisible("ranges", false); };
        d.ChoiceChanged += (_, v) => { d.SetVisible("n", v == "every"); d.SetVisible("ranges", v == "ranges"); };
        return d;
    }

    static int SafePageCount(string pdf)
    {
        try { return PdfOps.PageCount(pdf); }
        catch { return 0; }
    }
}

/// <summary>"Poppy needs X for this. Install it?" with what it is, how big, and that it is free.</summary>
sealed class InstallPrompt : PoppyDialog
{
    public InstallPrompt(Poppy.Core.Engines.EngineInfo engine, string forWhat) : base(L.F("inst.title", engine.Name), 540, 310)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Image { Source = Logo.Small, Width = 48, Height = 48, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top });
        var text = new TextBlock
        {
            Text = engine.CanAutoInstall
                ? L.F("inst.body", engine.Name, forWhat, engine.Size)
                : L.F("inst.body.store", engine.Name, forWhat),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            MaxWidth = 410,
            LineHeight = 21,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        row.Children.Add(text);
        Body.Children.Add(row);
        OkButton.Content = engine.CanAutoInstall ? L.T("inst.yes") : L.T("inst.open");
    }

    public override OpOptions Options => new();
}
