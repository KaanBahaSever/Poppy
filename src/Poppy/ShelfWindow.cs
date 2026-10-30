using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Poppy.Core;
using static Poppy.Theme;

namespace Poppy;

/// <summary>
/// A floating shelf that holds files while you find where they go. Drop files on it,
/// drag them (one or all) out into any app. Snaps to a screen edge when moved close to one.
/// </summary>
sealed class ShelfWindow : Window
{
    readonly ObservableCollection<string> files = [];
    readonly StackPanel list = new();
    readonly TextBlock title = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    Point? pressAt;
    string[]? pressFiles;

    public ShelfWindow()
    {
        Title = "Poppy Shelf";
        AllowsTransparency = true;
        WindowStyle = WindowStyle.None;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Width = 250;
        SizeToContent = SizeToContent.Height;
        AllowDrop = true;

        title.Foreground = Brush(Theme.Text);
        var header = new DockPanel { Margin = new Thickness(12, 8, 6, 6), Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
        var close = new Button { Content = new TextBlock { Text = "", FontFamily = Icons, FontSize = 10 }, Style = Styles.Ghost, Padding = new Thickness(7, 5, 7, 5), ToolTip = L.T("cancel") };
        close.Click += (_, _) => { files.Clear(); Hide(); };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var all = new Button { Content = new TextBlock { Text = "", FontFamily = Icons, FontSize = 13 }, Style = Styles.Ghost, Padding = new Thickness(7, 4, 7, 4), ToolTip = L.T("shelf.dragall"), Cursor = Cursors.Hand };
        all.PreviewMouseLeftButtonDown += (_, e) => StartPress(e, [.. files]);
        DockPanel.SetDock(all, Dock.Right);
        header.Children.Add(all);
        header.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { new Image { Source = Logo.Small, Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0) }, title },
        });
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindButton(d) != null) return;
            DragMove();
            SnapToEdge();
        };

        var scroll = new ScrollViewer { Content = list, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(6, 0, 6, 8) };
        var hint = new TextBlock { Text = L.T("shelf.hint"), FontSize = 11.5, Foreground = Brush(SubText), Margin = new Thickness(12, 0, 12, 10), TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Children = { header, scroll, hint } };
        Content = new Border
        {
            Child = panel,
            Background = Brush(Card),
            BorderBrush = Brush(CardEdge),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(10),
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 3, Opacity = 0.2, Color = Colors.Black },
        };

        files.CollectionChanged += (_, _) => Rebuild();
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped) AddFiles(dropped);
            e.Handled = true;
        };
        PreviewMouseMove += OnMouseMove;
        PreviewMouseLeftButtonUp += (_, _) => pressAt = null;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW);
        };
        Rebuild();
    }

    static Button? FindButton(DependencyObject d)
    {
        for (var x = d; x != null; x = VisualTreeHelper.GetParent(x))
            if (x is Button b) return b;
        return null;
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        foreach (var p in paths)
            if (!files.Contains(p, StringComparer.OrdinalIgnoreCase) && (File.Exists(p) || Directory.Exists(p))) files.Add(p);
    }

    public void ShowNearCursor()
    {
        if (!IsVisible)
        {
            var work = SystemParameters.WorkArea;
            Left = work.Right - Width - 24;
            Top = work.Top + 120;
            Show();
        }
        Activate();
    }

    void Rebuild()
    {
        title.Text = files.Count == 0 ? L.T("action.shelf") : $"{L.T("action.shelf")} · {files.Count}";
        list.Children.Clear();
        foreach (var f in files)
        {
            var row = new DockPanel { Margin = new Thickness(2), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = f };
            var remove = new Button { Content = new TextBlock { Text = "", FontFamily = Icons, FontSize = 9 }, Style = Styles.Ghost, Padding = new Thickness(6, 4, 6, 4), Opacity = 0.6 };
            var path = f;
            remove.Click += (_, _) => files.Remove(path);
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(new Image { Source = ShellThumbnail.Get(f, 48), Width = 32, Height = 32, Margin = new Thickness(4, 2, 10, 2), Stretch = Stretch.Uniform });
            row.Children.Add(new TextBlock { Text = Path.GetFileName(f.TrimEnd('\\')), VerticalAlignment = VerticalAlignment.Center, Foreground = Brush(Theme.Text), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12.5 });
            row.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject d && FindButton(d) != null) return;
                StartPress(e, [path]);
            };
            var border = new Border { Child = row, CornerRadius = new CornerRadius(8) };
            border.MouseEnter += (_, _) => border.Background = Brush(Dark ? Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0, 0, 0));
            border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
            list.Children.Add(border);
        }
    }

    void StartPress(MouseButtonEventArgs e, string[] what)
    {
        if (what.Length == 0) return;
        pressAt = e.GetPosition(this);
        pressFiles = what;
        e.Handled = true;
    }

    void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (pressAt is not { } start || pressFiles == null || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - start;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        pressAt = null;
        var data = new DataObject(DataFormats.FileDrop, pressFiles);
        var result = DragDrop.DoDragDrop(this, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        // A move means the files left their old place; they no longer belong on the shelf.
        if (result == DragDropEffects.Move)
            foreach (var f in pressFiles) files.Remove(f);
    }

    /// <summary>Pins the shelf flush to the nearest screen edge when it is dropped close to one.</summary>
    void SnapToEdge()
    {
        const double reach = 48;
        var work = SystemParameters.WorkArea;
        if (Left - work.Left < reach) Left = work.Left;
        else if (work.Right - (Left + ActualWidth) < reach) Left = work.Right - ActualWidth;
        if (Top - work.Top < reach) Top = work.Top;
        else if (work.Bottom - (Top + ActualHeight) < reach) Top = work.Bottom - ActualHeight;
    }
}
