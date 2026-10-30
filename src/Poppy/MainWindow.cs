using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Poppy.Core;
using Poppy.Core.Engines;
using E = Poppy.Core.Engines.Engines;
using static Poppy.Theme;

namespace Poppy;

/// <summary>Welcome, how-to, settings, engines and about, in one window.</summary>
sealed class MainWindow : Window
{
    public const string RepoUrl = "https://github.com/KaanBahaSever/Poppy";

    readonly AppSettings settings;
    readonly Action changed;
    readonly ContentControl page = new();
    readonly StackPanel nav = new() { Margin = new Thickness(0, 18, 0, 0) };
    string current = "how";

    public MainWindow(AppSettings settings, Action changed)
    {
        this.settings = settings;
        this.changed = changed;
        Title = "Poppy";
        Width = 940;
        Height = 660;
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = Logo.Small;
        SetResourceReference(BackgroundProperty, "WindowBg");
        SourceInitialized += (_, _) => Native.StyleWindow(new WindowInteropHelper(this).Handle, Dark);
        Build();
    }

    void Build()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var side = new DockPanel { Margin = new Thickness(18, 22, 10, 18) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal };
        brand.Children.Add(new Image { Source = Logo.Small, Width = 34, Height = 34, Margin = new Thickness(0, 0, 10, 0) });
        brand.Children.Add(new TextBlock { Text = "Poppy", FontFamily = UiDisplay, FontSize = 24, FontWeight = FontWeights.Bold, Foreground = Brush(Theme.Text), VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(brand, Dock.Top);
        side.Children.Add(brand);
        DockPanel.SetDock(nav, Dock.Top);
        side.Children.Add(nav);
        var version = new TextBlock { Text = "v" + typeof(MainWindow).Assembly.GetName().Version?.ToString(3), Foreground = Brush(SubText), FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom };
        side.Children.Add(version);
        grid.Children.Add(side);

        var scroll = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8, 22, 28, 22) };
        Grid.SetColumn(scroll, 1);
        grid.Children.Add(scroll);
        Content = grid;
        Navigate(current);
    }

    void Navigate(string id)
    {
        current = id;
        nav.Children.Clear();
        AddNav("how", "", L.T("nav.how"));
        AddNav("settings", "", L.T("nav.settings"));
        AddNav("engines", "", L.T("nav.engines"));
        AddNav("about", "", L.T("nav.about"));
        page.Content = id switch
        {
            "settings" => SettingsPage(),
            "engines" => EnginesPage(),
            "about" => AboutPage(),
            _ => HowPage(),
        };
    }

    /// <summary>Re-reads engine state (after installs) and redraws the current page.</summary>
    public void Refresh() => Navigate(current);

    /// <summary>"3 engines missing: FFmpeg, Pandoc, 7-Zip. [Install all] [Details]" or null when nothing is.</summary>
    UIElement? MissingBanner()
    {
        var missing = App.MissingEngines();
        if (missing.Count == 0) return null;
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = "\uE896", FontFamily = Icons, FontSize = 20, Foreground = Brush(Accent), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = L.F("banner.title", missing.Count), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brush(Theme.Text) });
        text.Children.Add(new TextBlock { Text = L.F("banner.body", string.Join(", ", missing.Select(e => e.Name))), FontSize = 13, Foreground = Brush(SubText), TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var install = new Button { Content = L.T("banner.install"), Style = Styles.Primary };
        install.Click += async (_, _) =>
        {
            install.IsEnabled = false;
            install.Content = L.T("eng.installing");
            await App.Current2.InstallMissingAsync();
        };
        var more = new Button { Content = L.T("banner.more"), Style = Styles.Ghost, Margin = new Thickness(6, 0, 0, 0) };
        more.Click += (_, _) => Navigate("engines");
        buttons.Children.Add(install);
        buttons.Children.Add(more);
        Grid.SetColumn(buttons, 2);
        row.Children.Add(buttons);
        return new Border
        {
            Child = row,
            Background = Brush(Dark ? Color.FromArgb(0x30, 0xFF, 0x5A, 0x36) : Color.FromRgb(0xFF, 0xF1, 0xEB)),
            BorderBrush = Brush(Dark ? Color.FromArgb(0x60, 0xFF, 0x5A, 0x36) : Color.FromRgb(0xFF, 0xD3, 0xC4)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18, 14, 14, 14),
            Margin = new Thickness(0, 0, 0, 22),
        };
    }

    void AddNav(string id, string glyph, string text)
    {
        var active = id == current;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 15, Width = 28, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush(active ? Accent : SubText) });
        row.Children.Add(new TextBlock { Text = text, FontSize = 14, FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Brush(Theme.Text) });
        var b = new Button
        {
            Content = row,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Style = Styles.Ghost,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 2),
            Background = active ? Brush(Theme.Card) : Brushes.Transparent,
            BorderBrush = active ? Brush(CardEdge) : Brushes.Transparent,
        };
        b.Click += (_, _) => Navigate(id);
        nav.Children.Add(b);
    }

    // ---------- Pages ----------

    static TextBlock H1(string t) => new() { Text = t, FontFamily = UiDisplay, FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = Brush(Theme.Text), Margin = new Thickness(0, 0, 0, 6) };
    static TextBlock P(string t, double top = 0) => new() { Text = t, FontSize = 14, Foreground = Brush(SubText), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0), LineHeight = 21 };
    static TextBlock H2(string t) => new() { Text = t, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Brush(Theme.Text), Margin = new Thickness(0, 0, 0, 8) };

    static Border Card(UIElement child, double top = 14) => new()
    {
        Child = child,
        Background = Brush(Theme.Card),
        BorderBrush = Brush(CardEdge),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(14),
        Padding = new Thickness(20),
        Margin = new Thickness(0, top, 0, 0),
    };

    UIElement HowPage()
    {
        var s = new StackPanel();
        var banner = MissingBanner();
        if (banner != null) s.Children.Add(banner);
        s.Children.Add(H1(L.T("how.title")));
        s.Children.Add(P(L.T("how.lead")));

        var rings = new UniformGrid { Columns = 3, Margin = new Thickness(0, 6, 0, 0) };
        rings.Children.Add(RingCard(settings.ConvertKey, L.T("mode.convert"), L.T("how.convert"), RingMode.Convert, ["photo.jpg"], 0));
        rings.Children.Add(RingCard(settings.ToolsKey, L.T("mode.tools"), L.T("how.tools"), RingMode.Tools, ["clip.mp4"], 1));
        rings.Children.Add(RingCard(settings.ActionsKey, L.T("mode.actions"), L.T("how.actions"), RingMode.Actions, ["report.pdf"], 0));
        s.Children.Add(rings);

        var steps = new StackPanel();
        steps.Children.Add(H2(L.T("how.steps")));
        for (int i = 1; i <= 4; i++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            row.Children.Add(new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Brush(Accent), Margin = new Thickness(0, 0, 12, 0),
                Child = new TextBlock { Text = i.ToString(), Foreground = Brushes.White, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            row.Children.Add(new TextBlock { Text = L.T("how.step" + i), FontSize = 14, Foreground = Brush(Theme.Text), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 });
            steps.Children.Add(row);
        }
        s.Children.Add(Card(steps));
        s.Children.Add(P(L.T("how.tip"), 12));
        return s;
    }

    UIElement RingCard(int key, string title, string text, RingMode mode, string[] sample, int hot)
    {
        var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var cap = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            BorderBrush = Brush(CardEdge), Background = Brush(SegmentBottom), Padding = new Thickness(14, 6, 14, 8), Margin = new Thickness(0, 0, 0, 8),
            Child = new TextBlock { Text = AppSettings.KeyName(key), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brush(Theme.Text) },
        };
        s.Children.Add(cap);
        s.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brush(Theme.Text), HorizontalAlignment = HorizontalAlignment.Center });
        s.Children.Add(new Viewbox { Width = 150, Height = 150, Margin = new Thickness(0, 8, 0, 8), Child = RingWindow.Preview(Catalog.Items(mode, sample), hot) });
        s.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = Brush(SubText), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 170 });
        return Card(s, 10).Also(b => b.Margin = new Thickness(0, 14, 10, 0));
    }

    UIElement SettingsPage()
    {
        var s = new StackPanel();
        s.Children.Add(H1(L.T("nav.settings")));

        var keys = new StackPanel();
        keys.Children.Add(H2(L.T("set.keys")));
        keys.Children.Add(P(L.T("set.keys.hint")));
        keys.Children.Add(KeyRow(L.T("mode.convert"), () => settings.ConvertKey, v => settings.ConvertKey = v));
        keys.Children.Add(KeyRow(L.T("mode.tools"), () => settings.ToolsKey, v => settings.ToolsKey = v));
        keys.Children.Add(KeyRow(L.T("mode.actions"), () => settings.ActionsKey, v => settings.ActionsKey = v));
        s.Children.Add(Card(keys));

        var general = new StackPanel();
        general.Children.Add(H2(L.T("set.general")));
        var lang = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
        foreach (var (v, t) in new[] { ("auto", L.T("set.lang.auto")), ("en", "English"), ("tr", "Türkçe") })
            lang.Items.Add(new ComboBoxItem { Content = t, Tag = v, IsSelected = settings.Language == v });
        lang.SelectionChanged += (_, _) =>
        {
            settings.Language = (string)((ComboBoxItem)lang.SelectedItem).Tag;
            changed();
            Navigate("settings");
        };
        general.Children.Add(Row(L.T("set.lang"), lang));

        var output = new TextBlock { Text = settings.OutputFolder.Length == 0 ? L.T("set.output.same") : settings.OutputFolder, Foreground = Brush(Theme.Text), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300 };
        var pick = new Button { Content = L.T("set.output.pick"), Margin = new Thickness(10, 0, 0, 0) };
        pick.Click += (_, _) =>
        {
            var d = new OpenFolderDialog();
            if (d.ShowDialog(this) == true) { settings.OutputFolder = d.FolderName; changed(); Navigate("settings"); }
        };
        var same = new Button { Content = L.T("set.output.reset"), Margin = new Thickness(6, 0, 0, 0), Style = Styles.Ghost };
        same.Click += (_, _) => { settings.OutputFolder = ""; changed(); Navigate("settings"); };
        general.Children.Add(Row(L.T("set.output"), new StackPanel { Orientation = Orientation.Horizontal, Children = { output, pick, same } }));

        general.Children.Add(Check(L.T("set.reveal"), settings.RevealWhenDone, v => settings.RevealWhenDone = v));
        general.Children.Add(Check(L.T("set.sound"), settings.PlaySound, v => settings.PlaySound = v));
        general.Children.Add(Check(L.T("set.startup"), settings.StartWithWindows, v =>
        {
            settings.StartWithWindows = v;
            _ = AutoStart.SetAsync(v).ContinueWith(t => { if (t.Exception != null) App.Log(t.Exception); });
        }));

        var size = new Slider { Minimum = 0.8, Maximum = 1.4, Value = settings.RingScale, Width = 220, TickFrequency = 0.1, IsSnapToTickEnabled = true };
        size.ValueChanged += (_, _) => { settings.RingScale = Math.Round(size.Value, 1); changed(); };
        general.Children.Add(Row(L.T("set.ringsize"), size));
        s.Children.Add(Card(general));
        return s;
    }

    UIElement KeyRow(string label, Func<int> get, Action<int> set)
    {
        var box = new ComboBox { Width = 160 };
        foreach (var (vk, name) in AppSettings.KeyChoices)
            box.Items.Add(new ComboBoxItem { Content = name, Tag = vk, IsSelected = vk == get() });
        box.SelectionChanged += (_, _) =>
        {
            var vk = (int)((ComboBoxItem)box.SelectedItem).Tag;
            // Two modes on one key would make one unreachable: swap them instead.
            var old = get();
            if (settings.ConvertKey == vk) settings.ConvertKey = old;
            else if (settings.ToolsKey == vk) settings.ToolsKey = old;
            else if (settings.ActionsKey == vk) settings.ActionsKey = old;
            set(vk);
            changed();
            Navigate("settings");
        };
        return Row(label, box);
    }

    static UIElement Row(string label, UIElement control)
    {
        var g = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = label, FontSize = 14, Foreground = Brush(Theme.Text), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    UIElement Check(string label, bool value, Action<bool> set)
    {
        var c = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 8, 0, 0) };
        c.Click += (_, _) => { set(c.IsChecked == true); changed(); };
        return c;
    }

    UIElement EnginesPage()
    {
        var s = new StackPanel();
        s.Children.Add(H1(L.T("nav.engines")));
        s.Children.Add(P(L.T("eng.lead")));

        var office = E.Word || E.PowerPoint || E.Excel;
        var missing = App.MissingEngines();
        if (missing.Count > 0)
        {
            var all = new Button { Content = L.T("eng.installall") + $" ({missing.Count})", Style = Styles.Primary, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
            all.Click += async (_, _) =>
            {
                all.IsEnabled = false;
                all.Content = L.T("eng.installing");
                await App.Current2.InstallMissingAsync();
            };
            s.Children.Add(all);
        }

        var list = new StackPanel();
        void Row(string name, string use, bool ok, string? detail, UIElement? action)
        {
            var g = new Grid { Margin = new Thickness(0, 8, 0, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Text = ok ? "\uE73E" : "\uE711", FontFamily = Icons, FontSize = 16, Foreground = Brush(ok ? Color.FromRgb(0x22, 0xA0, 0x55) : Color.FromRgb(0xC0, 0x40, 0x30)), VerticalAlignment = VerticalAlignment.Center });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brush(Theme.Text) });
            text.Children.Add(new TextBlock { Text = use + (detail != null ? " · " + detail : ""), FontSize = 12, Foreground = Brush(SubText), TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            if (action != null)
            {
                Grid.SetColumn(action, 2);
                g.Children.Add(action);
            }
            list.Children.Add(g);
        }

        Row("Magick.NET · PDFsharp", L.T("eng.builtin"), true, L.T("eng.included"), null);
        Row("Microsoft Office", L.T("eng.office"), office,
            string.Join(", ", new[] { E.Word ? "Word" : null, E.PowerPoint ? "PowerPoint" : null, E.Excel ? "Excel" : null }.Where(x => x != null)), null);
        foreach (var e in EngineInstaller.All)
        {
            var ok = e.IsInstalled();
            Button? b = null;
            if (!ok)
            {
                var label = e.CanAutoInstall ? L.T("eng.install") : e.StoreId != null ? L.T("eng.store") : L.T("eng.page");
                var button = new Button { Content = label, ToolTip = e.Size };
                if (e.CanAutoInstall) button.Style = Styles.Primary;
                var engine = e;
                button.Click += async (_, _) =>
                {
                    button.IsEnabled = false;
                    button.Content = L.T("eng.installing");
                    await App.Current2.InstallEngineAsync(engine);
                    Navigate("engines");
                };
                b = button;
            }
            Row(e.Name, e.Purpose, ok, ok ? L.T("eng.ready") : e.Size, b);
        }
        Row("Microsoft Edge", L.T("eng.edge"), E.Edge != null, null, null);
        s.Children.Add(Card(list));
        return s;
    }

    UIElement AboutPage()
    {
        var s = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Image { Source = Logo.Small, Width = 72, Height = 72, Margin = new Thickness(0, 0, 18, 0) });
        var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        t.Children.Add(H1("Poppy"));
        t.Children.Add(P(L.T("about.tag")));
        head.Children.Add(t);
        s.Children.Add(head);

        var body = new StackPanel();
        body.Children.Add(P(L.T("about.oss")));
        var links = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        links.Children.Add(Link(L.T("about.github"), RepoUrl, primary: true));
        links.Children.Add(Link(L.T("about.issues"), RepoUrl + "/issues"));
        links.Children.Add(Link(L.T("about.license"), RepoUrl + "/blob/main/LICENSE"));
        body.Children.Add(links);
        s.Children.Add(Card(body, 20));

        var credits = new StackPanel();
        credits.Children.Add(H2(L.T("about.credits")));
        credits.Children.Add(P("FFmpeg (LGPL), Magick.NET / ImageMagick (Apache 2.0), PDFsharp (MIT), ClosedXML (MIT), SharpCompress (MIT), Pandoc (GPL, optional), LibreOffice (MPL, optional)."));
        s.Children.Add(Card(credits));
        var quit = new Button { Content = L.T("tray.quit"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
        quit.Click += (_, _) => App.Current2.Quit();
        s.Children.Add(quit);
        return s;
    }

    static Button Link(string text, string url, bool primary = false)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8) };
        if (primary) b.Style = Styles.Primary;
        b.Click += (_, _) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return b;
    }
}

static class Fluent
{
    public static T Also<T>(this T o, Action<T> a)
    {
        a(o);
        return o;
    }
}
