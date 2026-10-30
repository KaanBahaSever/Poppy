// End-to-end test: starts Poppy.exe, performs real OLE drags with synthetic mouse and
// keyboard input, and checks that the ring opens, switches, closes and runs the right item.
//   Poppy.E2E.exe <path to Poppy.exe> [screenshot folder]
// It moves the real mouse, so do not touch the mouse or keyboard while it runs.
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Poppy.Core;

namespace Poppy.E2E;

static class Program
{
    static string poppyExe = "";
    static string shots = "";
    static string work = "";
    static Form source = null!;
    static string[] dragFiles = [];
    static bool useDragImage = true; // like Explorer; one scenario turns it off
    static readonly List<string> failures = [];
    static readonly StreamWriter log = new(Path.Combine(Path.GetTempPath(), "poppy-e2e.log")) { AutoFlush = true };

    [STAThread]
    static int Main(string[] args)
    {
        poppyExe = Path.GetFullPath(args[0]);
        shots = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "poppy-e2e-shots"));
        Directory.CreateDirectory(shots);
        work = Path.Combine(Path.GetTempPath(), "poppy-e2e", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        source = new Form
        {
            Text = "Poppy E2E source",
            StartPosition = FormStartPosition.Manual,
            Location = new Point(40, 40),
            Size = new Size(260, 160),
            TopMost = true,
            BackColor = Color.FromArgb(255, 244, 238),
        };
        source.Controls.Add(new Label { Text = "drag source", AutoSize = true, Location = new Point(16, 16) });
        var downAt = Point.Empty;
        source.MouseDown += (_, e) => downAt = e.Location;
        source.MouseMove += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || dragFiles.Length == 0) return;
            if (Math.Abs(e.X - downAt.X) < 4 && Math.Abs(e.Y - downAt.Y) < 4) return;
            var data = new DataObject(DataFormats.FileDrop, dragFiles);
            if (useDragImage)
            {
                // Same path Explorer takes: the shell drag-image helper (creates SysDragImage).
                using var img = new Bitmap(64, 64);
                using (var g = Graphics.FromImage(img)) g.Clear(Color.FromArgb(255, 90, 54));
                source.DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move, img, new Point(32, 32), false);
            }
            else
            {
                source.DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move);
            }
        };
        // A neutral backdrop behind the ring, so screenshots never contain what is on the user's screen.
        var backdrop = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(320, 160),
            Size = new Size(600, 640),
            TopMost = true,
            ShowInTaskbar = false,
        };
        backdrop.Paint += (_, e) =>
        {
            using var bg = new System.Drawing.Drawing2D.LinearGradientBrush(backdrop.ClientRectangle, Color.FromArgb(255, 232, 218), Color.FromArgb(214, 228, 248), 60f);
            e.Graphics.FillRectangle(bg, backdrop.ClientRectangle);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var b1 = new SolidBrush(Color.FromArgb(70, 255, 140, 100));
            using var b2 = new SolidBrush(Color.FromArgb(60, 120, 150, 255));
            e.Graphics.FillEllipse(b1, 40, 60, 260, 260);
            e.Graphics.FillEllipse(b2, 300, 330, 300, 300);
        };
        // Same for the corner where job cards appear.
        var area = Screen.PrimaryScreen!.WorkingArea;
        var cornerBackdrop = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(area.Right - 460, area.Bottom - 300),
            Size = new Size(460, 300),
            TopMost = true,
            ShowInTaskbar = false,
            BackColor = Color.FromArgb(232, 228, 240),
        };
        source.Shown += (_, _) => { backdrop.Show(); cornerBackdrop.Show(); source.BringToFront(); };
        int exit = 1;
        source.Shown += (_, _) => new Thread(() =>
        {
            try { exit = RunAll(); }
            catch (Exception e) { Log("CRASH " + e); exit = 2; }
            source.BeginInvoke(source.Close);
        }) { IsBackground = true }.Start();
        Application.Run(source);
        return exit;
    }

    static void Log(string s)
    {
        Console.WriteLine(s);
        log.WriteLine(s);
    }

    static int RunAll()
    {
        var settingsDir = Path.Combine(work, "settings");
        Directory.CreateDirectory(settingsDir);
        File.WriteAllText(Path.Combine(settingsDir, "settings.json"),
            """{ "FirstRunDone": true, "StartWithWindows": false, "PlaySound": false, "Language": "en" }""");
        foreach (var p in Process.GetProcessesByName("Poppy")) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
        var psi = new ProcessStartInfo(poppyExe, "--background") { UseShellExecute = false };
        psi.Environment["POPPY_SETTINGS_DIR"] = settingsDir;
        using var app = Process.Start(psi)!;
        try
        {
            if (!WaitFor(() => FindWindow(null, "Poppy Ring") != IntPtr.Zero, 15000)) throw new Exception("Poppy did not start");
            Thread.Sleep(1200);
            L.Language = "en";

            var png = MakeImage("photo.png");
            Scenario("convert ring: PNG -> JPG", () => DragTo(png, [VK_SHIFT], RingMode.Convert, "convert:jpg", "convert-ring.png"),
                () => WaitFile(Path.Combine(work, "photo.jpg")));

            Scenario("job card: Show button opens Explorer (real click)", ClickShowOnJobCard, () => true);

            var png2 = MakeImage("tools.png");
            Scenario("tools ring: strip metadata", () => DragTo(png2, [VK_CONTROL], RingMode.Tools, "image.metadata", "tools-ring.png"),
                () => WaitFile(Path.Combine(work, "tools-clean.png")));

            var png3 = MakeImage("switch.png");
            Scenario("switch Tools -> Convert -> Tools -> Convert, PNG -> BMP", () => DragTo(png3, [VK_CONTROL, VK_SHIFT, VK_CONTROL, VK_SHIFT], RingMode.Convert, "convert:bmp", null),
                () => WaitFile(Path.Combine(work, "switch.bmp")));

            var png4 = MakeImage("actions.png");
            Scenario("actions ring: copy path", () => DragTo(png4, [VK_MENU], RingMode.Actions, "action.copypath", "actions-ring.png"),
                () => WaitFor(() => ClipboardText() == png4, 5000));

            var png5 = MakeImage("plain-drag.png");
            useDragImage = false;
            Scenario("drag without shell drag image (ghost ring): PNG -> WEBP", () => DragTo(png5, [VK_SHIFT], RingMode.Convert, "convert:webp", null),
                () => WaitFile(Path.Combine(work, "plain-drag.webp")));
            useDragImage = true;

            var png6 = MakeImage("cancel.png");
            Scenario("Esc closes the ring, nothing runs", () =>
            {
                StartDrag(png6);
                Tap(VK_SHIFT);
                if (!WaitFor(RingVisible, 3000)) throw new Exception("ring did not open");
                Tap(VK_ESCAPE);
                if (!WaitFor(() => !RingVisible(), 3000)) throw new Exception("ring still open after Esc");
                MouseUp();
            }, () =>
            {
                Thread.Sleep(1500);
                return Directory.GetFiles(work, "cancel*").Length == 1;
            });

            var png7 = MakeImage("center.png");
            Scenario("drop on the centre does nothing", () =>
            {
                StartDrag(png7);
                Tap(VK_SHIFT);
                if (!WaitFor(RingVisible, 3000)) throw new Exception("ring did not open");
                Thread.Sleep(300);
                var (cx, cy, _) = RingGeometry();
                MoveSmooth(cx + 3, cy + 3);
                Thread.Sleep(250);
                MouseUp();
                if (!WaitFor(() => !RingVisible(), 3000)) throw new Exception("ring did not close after drop");
            }, () =>
            {
                Thread.Sleep(1500);
                return Directory.GetFiles(work, "center*").Length == 1;
            });

            if (!Poppy.Core.Ops.ImageOps.CanWrite("heic"))
            {
                var png8 = MakeImage("needs-engine.png");
                Scenario("drop on a segment that needs an engine offers to install it", () =>
                {
                    DragTo(png8, [VK_SHIFT], RingMode.Convert, "convert:heic", null);
                    IntPtr dlg = IntPtr.Zero;
                    if (!WaitFor(() => (dlg = WindowWithTitle("Install HEVC")) != IntPtr.Zero, 5000)) throw new Exception("no install prompt");
                    source.Invoke(() => { foreach (Form f in Application.OpenForms) if (f != source) f.Hide(); });
                    Thread.Sleep(600);
                    Capture2(dlg, "install-prompt.png");
                    source.Invoke(() => { foreach (Form f in Application.OpenForms) if (f != source) f.Show(); });
                    Tap(VK_ESCAPE);
                    if (!WaitFor(() => WindowWithTitle("Install HEVC") == IntPtr.Zero, 3000)) throw new Exception("prompt did not close on Esc");
                }, () => !File.Exists(Path.Combine(work, "needs-engine.heic")));
            }

            Scenario("tapping a key without a drag does nothing", () =>
            {
                MoveSmooth(600, 500);
                Tap(VK_SHIFT);
                Thread.Sleep(600);
                if (RingVisible()) throw new Exception("ring opened without a drag");
            }, () => true);
        }
        finally
        {
            try { app.Kill(); } catch { }
        }

        Log(failures.Count == 0 ? "E2E: ALL PASSED" : $"E2E: {failures.Count} FAILED: {string.Join("; ", failures)}");
        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Finds the "Show" button on the finished job card with UI Automation, then clicks it with the
    /// real mouse (a UIA Invoke would bypass hit-testing, which is what broke on the layered window).
    /// </summary>
    static void ClickShowOnJobCard()
    {
        var jobs = FindWindow(null, "Poppy Jobs");
        if (jobs == IntPtr.Zero || !WaitFor(() => IsWindowVisible(jobs), 5000)) throw new Exception("no job card");
        System.Windows.Automation.AutomationElement? show = null;
        WaitFor(() =>
        {
            var win = System.Windows.Automation.AutomationElement.FromHandle(jobs);
            show = win.FindFirst(System.Windows.Automation.TreeScope.Descendants,
                new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.NameProperty, "Show in folder"));
            return show != null && !show.Current.IsOffscreen;
        }, 8000);
        if (show == null) throw new Exception("Show button not found");
        Capture2(jobs, "job-card.png");
        var before = ExplorerWindows();
        var r = show.Current.BoundingRectangle;
        MoveSmooth((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
        Thread.Sleep(200);
        MouseDown();
        Thread.Sleep(60);
        MouseUp();
        IntPtr opened = IntPtr.Zero;
        if (!WaitFor(() => (opened = ExplorerWindows().Except(before).FirstOrDefault()) != IntPtr.Zero, 8000))
            throw new Exception("clicking Show did not open File Explorer");
        Thread.Sleep(800);
        PostMessage(opened, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
    }

    static IntPtr WindowWithTitle(string prefix)
    {
        var found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            var t = new System.Text.StringBuilder(256);
            GetWindowText(h, t, 256);
            if (IsWindowVisible(h) && t.ToString().StartsWith(prefix)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);

    static List<IntPtr> ExplorerWindows()
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            var c = new System.Text.StringBuilder(64);
            GetClassName(h, c, 64);
            if (c.ToString() == "CabinetWClass" && IsWindowVisible(h)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static void Capture2(IntPtr h, string name)
    {
        GetWindowRect(h, out var r);
        using var bmp = new Bitmap(r.Right - r.Left, r.Bottom - r.Top);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size);
        bmp.Save(Path.Combine(shots, name), ImageFormat.Png);
    }

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    static void Scenario(string name, Action act, Func<bool> check)
    {
        try
        {
            act();
            if (!check()) throw new Exception("expected result not found");
            Log("PASS " + name);
        }
        catch (Exception e)
        {
            failures.Add(name);
            Log("FAIL " + name + ": " + e.Message);
            try { MouseUp(); } catch { }
        }
        Thread.Sleep(700);
    }

    static string MakeImage(string name)
    {
        var path = Path.Combine(work, name);
        using var bmp = new Bitmap(320, 200);
        using (var g = Graphics.FromImage(bmp))
        {
            using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, 320, 200), Color.FromArgb(255, 122, 69), Color.FromArgb(124, 58, 237), 30f);
            g.FillRectangle(brush, 0, 0, 320, 200);
        }
        bmp.Save(path, ImageFormat.Png);
        return path;
    }

    static bool WaitFile(string path) => WaitFor(() => File.Exists(path) && new FileInfo(path).Length > 0, 20000);

    static bool WaitFor(Func<bool> f, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (f()) return true;
            Thread.Sleep(50);
        }
        return f();
    }

    // ---------- Drag choreography ----------

    static void StartDrag(string file)
    {
        dragFiles = [file];
        var r = new Rectangle();
        source.Invoke(() => r = source.RectangleToScreen(source.ClientRectangle));
        int x = r.Left + r.Width / 2, y = r.Top + r.Height / 2;
        MoveSmooth(x, y);
        Thread.Sleep(150);
        MouseDown();
        Thread.Sleep(80);
        // Leave the source window so the drag loop is running over the desktop.
        MoveSmooth(x + 30, y + 10, 6);
        Thread.Sleep(150);
        MoveSmooth(620, 480, 18);
        Thread.Sleep(250);
    }

    static void DragTo(string file, int[] keys, RingMode mode, string itemId, string? screenshot)
    {
        StartDrag(file);
        foreach (var k in keys)
        {
            Tap(k);
            Thread.Sleep(250);
        }
        if (!WaitFor(RingVisible, 3000)) throw new Exception("ring did not open");
        Thread.Sleep(350);
        var items = Catalog.Items(mode, [file]);
        var index = items.ToList().FindIndex(i => i.Id == itemId);
        if (index < 0) throw new Exception(itemId + " is not in the ring");
        var (cx, cy, scale) = RingGeometry();
        var (dx, dy) = new RingLayout(items.Count).SegmentCenter(index);
        MoveSmooth((int)(cx + dx * scale), (int)(cy + dy * scale), 16);
        Thread.Sleep(400);
        if (screenshot != null) Capture(screenshot);
        MouseUp();
        if (!WaitFor(() => !RingVisible(), 3000)) throw new Exception("ring did not close after drop");
    }

    /// <summary>Ring centre in screen pixels and the DIP-to-pixel scale, from the ring window's rectangle.</summary>
    static (int X, int Y, double Scale) RingGeometry()
    {
        var h = FindWindow(null, "Poppy Ring");
        GetWindowRect(h, out var r);
        const double size = 320, c = 160;
        var scale = (r.Right - r.Left) / size;
        return (r.Left + (r.Right - r.Left) / 2, r.Top + (int)Math.Round(c * scale), scale);
    }

    static bool RingVisible()
    {
        var h = FindWindow(null, "Poppy Ring");
        return h != IntPtr.Zero && IsWindowVisible(h);
    }

    static void Capture(string name)
    {
        var h = FindWindow(null, "Poppy Ring");
        GetWindowRect(h, out var r);
        int pad = 30;
        using var bmp = new Bitmap(r.Right - r.Left + 2 * pad, r.Bottom - r.Top + 2 * pad);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Left - pad, r.Top - pad, 0, 0, bmp.Size);
        bmp.Save(Path.Combine(shots, name), ImageFormat.Png);
    }

    static string? ClipboardText()
    {
        string? text = null;
        source.Invoke(() => { try { text = Clipboard.GetText(); } catch { } });
        return text;
    }

    // ---------- Input ----------

    const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_ESCAPE = 0x1B;

    static void MoveSmooth(int x, int y, int steps = 10)
    {
        GetCursorPos(out var p);
        for (int i = 1; i <= steps; i++)
        {
            Move(p.X + (x - p.X) * i / steps, p.Y + (y - p.Y) * i / steps);
            Thread.Sleep(12);
        }
    }

    static void Move(int x, int y)
    {
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        var input = new INPUT
        {
            type = 0,
            u = new InputUnion { mi = new MOUSEINPUT { dx = (x - vx) * 65535 / (vw - 1), dy = (y - vy) * 65535 / (vh - 1), dwFlags = 0x0001 | 0x8000 | 0x4000 } },
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    static void MouseDown() => Mouse(0x0002);
    static void MouseUp() => Mouse(0x0004);

    static void Mouse(uint flags)
    {
        var input = new INPUT { type = 0, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags } } };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    static void Tap(int vk)
    {
        Key(vk, false);
        Thread.Sleep(60);
        Key(vk, true);
    }

    static void Key(int vk, bool up)
    {
        var input = new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = up ? 0x0002u : 0 } } };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
}
