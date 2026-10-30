using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Poppy.Core;
using static Poppy.Theme;

namespace Poppy;

/// <summary>Progress cards stacked in the bottom-right corner, one per running job.</summary>
sealed class JobsWindow : Window
{
    readonly StackPanel stack = new() { VerticalAlignment = VerticalAlignment.Bottom };

    public JobsWindow()
    {
        Title = "Poppy Jobs";
        AllowsTransparency = true;
        WindowStyle = WindowStyle.None;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Width = 360;
        SizeToContent = SizeToContent.Height;
        Content = stack;
        SourceInitialized += (_, _) => Native.MakeOverlay(new WindowInteropHelper(this).Handle);
        SizeChanged += (_, _) => PlaceInCorner();
    }

    void PlaceInCorner()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Right - Width - 12;
        Top = work.Bottom - ActualHeight - 12;
    }

    public JobCard Add(string title, string? thumbPath)
    {
        var card = new JobCard(title, thumbPath, this);
        stack.Children.Add(card.Root);
        if (!IsVisible) Show();
        PlaceInCorner();
        return card;
    }

    public void Remove(JobCard card)
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) =>
        {
            stack.Children.Remove(card.Root);
            if (stack.Children.Count == 0) Hide();
        };
        card.Root.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>"Done" toast for instant actions (copy, copy path...).</summary>
    public void Flash(string title, string message, string? reveal = null)
    {
        var card = Add(title, null);
        card.Finish(message, reveal == null ? [] : [reveal]);
    }
}

sealed class JobCard
{
    public readonly Border Root;
    readonly TextBlock status;
    readonly ProgressBar bar;
    readonly Button show;
    readonly Button open;
    readonly Button close;
    bool running = true;
    readonly JobsWindow owner;
    IReadOnlyList<string> outputs = [];
    DispatcherTimer? autoClose;

    public event Action? CancelRequested;

    public JobCard(string title, string? thumbPath, JobsWindow owner)
    {
        this.owner = owner;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(9), Background = Brush(Accent), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        var thumb = thumbPath != null ? ShellThumbnail.Get(thumbPath, 64) : null;
        if (thumb != null)
        {
            icon.Background = new ImageBrush(thumb) { Stretch = Stretch.UniformToFill };
        }
        else
        {
            icon.Child = new Image { Source = Logo.Small, Width = 26, Height = 26 };
        }
        grid.Children.Add(icon);

        var text = new StackPanel();
        Grid.SetColumn(text, 1);
        text.Children.Add(new TextBlock { Text = title, FontFamily = Ui, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brush(Text), TextTrimming = TextTrimming.CharacterEllipsis });
        status = new TextBlock { Text = L.T("job.working"), FontFamily = Ui, FontSize = 12, Foreground = Brush(SubText), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 6), TextWrapping = TextWrapping.Wrap, MaxHeight = 52 };
        text.Children.Add(status);
        bar = new ProgressBar { Height = 4, Minimum = 0, Maximum = 1, IsIndeterminate = true, Foreground = Brush(Accent), Background = Brush(CardEdge), BorderThickness = new Thickness(0) };
        text.Children.Add(bar);
        grid.Children.Add(text);

        // Round icon buttons, the same quiet style as the close button: show in folder, open, close.
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, -4, 0) };
        Grid.SetColumn(buttons, 2);
        show = IconButton("\uE838", L.T("job.show"));
        show.Click += (_, _) => { if (outputs.Count > 0) Reveal(outputs[0]); };
        open = IconButton("\uE8A7", L.T("job.open"));
        open.Click += (_, _) =>
        {
            if (outputs.Count == 1) Open(outputs[0]);
            else if (outputs.Count > 1) Reveal(outputs[0]);
        };
        close = IconButton("\uE711", L.T("cancel"));
        close.Click += (_, _) =>
        {
            if (running) CancelRequested?.Invoke();
            else owner.Remove(this);
        };
        show.Visibility = open.Visibility = Visibility.Collapsed;
        buttons.Children.Add(show);
        buttons.Children.Add(open);
        buttons.Children.Add(close);
        grid.Children.Add(buttons);

        Root = new Border
        {
            Child = grid,
            Background = Brush(Card),
            BorderBrush = Brush(CardEdge),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12),
            Margin = new Thickness(8, 4, 8, 4),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.16, Color = Colors.Black },
        };
    }

    // The glyph is an explicit TextBlock: the app-wide TextBlock style would otherwise replace the
    // icon font with Segoe UI and draw an empty box.
    static Button IconButton(string glyph, string tip)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 13 },
            ToolTip = tip,
            Style = Styles.Icon,
            Margin = new Thickness(2, 0, 0, 0),
        };
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        return b;
    }

    public void SetStatus(string text) => status.Text = text;

    public void SetProgress(double v)
    {
        if (v <= 0) return;
        bar.IsIndeterminate = false;
        bar.Value = v;
    }

    public void Finish(string message, IReadOnlyList<string> results)
    {
        outputs = results;
        bar.IsIndeterminate = false;
        bar.Value = 1;
        bar.Visibility = Visibility.Collapsed;
        status.Text = message;
        status.Foreground = Brush(SubText);
        running = false;
        show.Visibility = open.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        close.ToolTip = L.T("job.close");
        System.Windows.Automation.AutomationProperties.SetName(close, L.T("job.close"));
        CloseAfter(TimeSpan.FromSeconds(5));
    }

    /// <summary>A message with one action button ("Restart"); stays until used or closed.</summary>
    public void Offer(string message, string action, Action onClick)
    {
        running = false;
        bar.Visibility = Visibility.Collapsed;
        status.Text = message;
        var b = new Button
        {
            Content = action,
            Style = Styles.Primary,
            Padding = new Thickness(12, 4, 12, 5),
            FontSize = 12,
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.Click += (_, _) => onClick();
        ((Panel)close.Parent).Children.Insert(0, b);
        close.ToolTip = L.T("job.close");
    }

    public void Fail(string message)
    {
        bar.Visibility = Visibility.Collapsed;
        status.Text = L.T("job.failed") + ": " + message;
        status.Foreground = Brush(Color.FromRgb(0xD9, 0x30, 0x25));
        running = false;
        show.Visibility = open.Visibility = Visibility.Collapsed;
        close.ToolTip = L.T("job.close");
        CloseAfter(TimeSpan.FromSeconds(15));
    }

    void CloseAfter(TimeSpan t)
    {
        autoClose?.Stop();
        autoClose = new DispatcherTimer { Interval = t };
        autoClose.Tick += (_, _) =>
        {
            autoClose.Stop();
            if (!Root.IsMouseOver) owner.Remove(this);
            else CloseAfter(TimeSpan.FromSeconds(2));
        };
        autoClose.Start();
    }

    /// <summary>Opens a result in its default app (a folder opens in File Explorer).</summary>
    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { Reveal(path); }
    }

    public static void Reveal(string path)
    {
        try
        {
            if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
            else Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch { }
    }
}
