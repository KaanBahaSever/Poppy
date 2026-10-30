using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Threading;
using Poppy.Core;
using Poppy.Core.Ops;
using Forms = System.Windows.Forms;

namespace Poppy;

public partial class App : Application
{
    // POPPY_INSTANCE gives test and development copies their own instance, so they never talk to
    // (or get mistaken for) the Poppy the user has installed.
    static readonly string InstanceSuffix = Environment.GetEnvironmentVariable("POPPY_INSTANCE") is { Length: > 0 } i ? "." + i : "";
    static readonly string MutexName = @"Local\Poppy.SingleInstance" + InstanceSuffix;
    static readonly string ShowEventName = @"Local\Poppy.ShowSettings" + InstanceSuffix;

    internal static AppSettings Settings { get; private set; } = new();
    internal static App Current2 => (App)Current;

    Mutex? mutex;
    EventWaitHandle? showEvent;
    DragGesture gesture = null!;
    InputHooks hooks = null!;
    RingWindow ring = null!;
    JobsWindow jobs = null!;
    ShelfWindow? shelf;
    MainWindow? main;
    Forms.NotifyIcon? tray;
    readonly DispatcherTimer releaseTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            // Already running: ask that instance to show its window.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (showEvent.WaitOne()) Dispatcher.BeginInvoke(ShowMain);
        }) { IsBackground = true }.Start();

        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = true;
        };

        Settings = AppSettings.Load();
        Settings.Apply();
        foreach (var (k, v) in Theme.Resources().Cast<System.Collections.DictionaryEntry>().Select(x => ((string)x.Key, x.Value)))
            Resources[k] = v;

        // Load codecs and probe engines now, so the first ring opens without a hitch mid-drag.
        Task.Run(() =>
        {
            try
            {
                foreach (var sample in new[] { "a.png", "a.heic", "a.mp4", "a.mp3", "a.pdf", "a.docx", "a.pptx", "a.xlsx", "a.md", "a.srt", "a.zip" })
                    foreach (var mode in Enum.GetValues<RingMode>()) Catalog.Items(mode, [sample]);
            }
            catch (Exception ex) { Log(ex); }
        });

        jobs = new JobsWindow();
        ring = new RingWindow(Settings);
        ring.ItemDropped += OnItemDropped;
        ring.Prewarm();

        gesture = new DragGesture
        {
            KeyToMode = Settings.ModeForKey,
            SwallowKeys = InputHooks.ShellDragImageVisible,
        };
        gesture.Show += (mode, x, y) =>
        {
            var ghost = !InputHooks.ShellDragImageVisible();
            Dispatcher.BeginInvoke(() => ring.Open(mode, x, y, ghost));
        };
        gesture.Switch += mode => Dispatcher.BeginInvoke(() => ring.SetMode(mode));
        gesture.Cancelled += () => Dispatcher.BeginInvoke(() => ring.Close());
        gesture.Released += () => Dispatcher.BeginInvoke(() =>
        {
            // The drop (if any) arrives just after the button-up; give it time before closing.
            releaseTimer.Stop();
            releaseTimer.Start();
        });
        releaseTimer.Tick += (_, _) =>
        {
            releaseTimer.Stop();
            if (ring.IsOpen) ring.Close();
        };
        ring.IsVisibleChanged += (_, _) => { if (!ring.IsVisible) gesture.Reset(); };

        hooks = new InputHooks(gesture);
        hooks.Start();
        CreateTray();

        if (!AutoStart.IsPackaged)
            _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => Program.CheckForUpdatesAsync(), TaskScheduler.Default);

        var background = e.Args.Contains("--background");
        if (!Settings.FirstRunDone)
        {
            Settings.FirstRunDone = true;
            Settings.Save();
            if (Settings.StartWithWindows) _ = SafeStartup(true);
            ShowMain();
        }
        else if (!background)
        {
            ShowMain();
        }
    }

    static async Task SafeStartup(bool on)
    {
        try { await AutoStart.SetAsync(on); } catch (Exception e) { Log(e); }
    }

    // ---------- Running items ----------

    async void OnItemDropped(RingItem item, string[] files)
    {
        try
        {
            if (!item.Enabled && item.Need != null)
            {
                var what = (files.Length == 1 ? Path.GetFileName(files[0].TrimEnd('\\')) : L.F("job.files", files.Length)) + " → " + item.Label;
                if (!await EnsureEngineAsync(item.Need, what)) return;
                var mode = item.IsConvert ? RingMode.Convert : item.IsAction ? RingMode.Actions : RingMode.Tools;
                var fresh = Catalog.Items(mode, files).FirstOrDefault(i => i.Id == item.Id);
                if (fresh is not { Enabled: true })
                {
                    jobs.Add(item.Label, null).Fail(fresh?.Reason ?? L.T("job.failed"));
                    return;
                }
                item = fresh;
            }
            if (item.IsAction && item.Id != "action.zip")
            {
                Actions.Run(item.Id, files, jobs, GetShelf);
                return;
            }
            var options = item.Input == InputKind.None ? new OpOptions() : Ask.For(item, files);
            if (options == null) return;
            await RunJob(item, files, options);
        }
        catch (Exception e)
        {
            Log(e);
            jobs.Add(item.Label, null).Fail(e.Message);
        }
    }

    internal async Task RunJob(RingItem item, string[] files, OpOptions options)
    {
        var name = files.Length == 1 ? Path.GetFileName(files[0].TrimEnd('\\')) : L.F("job.files", files.Length);
        var card = jobs.Add($"{name}  →  {item.Label}", files.Length == 1 && File.Exists(files[0]) ? files[0] : null);
        using var cts = new CancellationTokenSource();
        card.CancelRequested += cts.Cancel;
        var progress = new Progress<double>(card.SetProgress);
        try
        {
            var ctx = new OpContext { Inputs = files, Options = options, Progress = progress, Ct = cts.Token };
            var result = await Task.Run(() => Runner.RunAsync(item.Id, ctx));
            var message = result.Message ?? (result.Outputs.Count == 1 ? Path.GetFileName(result.Outputs[0]) : L.F("job.files", result.Outputs.Count));
            card.Finish(result.Outputs.Count > 0 ? $"{L.T("job.done")} · {message}" : message, result.Outputs);
            if (Settings.PlaySound) SystemSounds.Asterisk.Play();
            if (Settings.RevealWhenDone && result.Outputs.Count > 0) JobCard.Reveal(result.Outputs[0]);
        }
        catch (OperationCanceledException)
        {
            card.Fail(L.T("job.cancelled"));
        }
        catch (Exception e)
        {
            if (e is not OpException) Log(e);
            card.Fail(e is OpException ? e.Message : e.GetBaseException().Message);
        }
        finally
        {
            if (options.Get("rendered") is { Length: > 0 } tmp) try { File.Delete(tmp); } catch { }
        }
    }

    ShelfWindow GetShelf() => shelf ??= new ShelfWindow();

    // ---------- Engines ----------

    /// <summary>Makes sure an engine is there, asking first. True when the work can go on.</summary>
    internal async Task<bool> EnsureEngineAsync(string id, string forWhat)
    {
        var engine = Poppy.Core.Engines.EngineInstaller.Get(id);
        if (engine == null) return false;
        if (engine.IsInstalled()) return true;
        if (new InstallPrompt(engine, forWhat).ShowDialog() != true) return false;
        return await InstallEngineAsync(engine);
    }

    /// <summary>
    /// Engines Poppy can install by itself that are not here yet. LibreOffice only counts for
    /// people without Microsoft Office; the paid HEVC extension never does.
    /// </summary>
    internal static List<Poppy.Core.Engines.EngineInfo> MissingEngines()
    {
        var office = Poppy.Core.Engines.Engines.Word || Poppy.Core.Engines.Engines.PowerPoint || Poppy.Core.Engines.Engines.Excel;
        return Poppy.Core.Engines.EngineInstaller.All
            .Where(e => e.CanAutoInstall && !e.IsInstalled() && !(e.Id == "libreoffice" && office))
            .ToList();
    }

    bool installingAll;

    /// <summary>One click: installs every missing engine, one after another.</summary>
    internal async Task InstallMissingAsync()
    {
        if (installingAll) return;
        installingAll = true;
        try
        {
            foreach (var e in MissingEngines()) await InstallEngineAsync(e);
        }
        finally
        {
            installingAll = false;
            RebuildTrayMenu();
            main?.Refresh();
        }
    }

    internal async Task<bool> InstallEngineAsync(Poppy.Core.Engines.EngineInfo engine)
    {
        var card = jobs.Add(engine.Name, null);
        card.SetStatus(L.T("inst.prepare"));
        using var cts = new CancellationTokenSource();
        card.CancelRequested += cts.Cancel;
        try
        {
            var r = await Poppy.Core.Engines.EngineInstaller.InstallAsync(engine, s => Dispatcher.BeginInvoke(() => card.SetStatus(s)), cts.Token);
            if (r.Ok) card.Finish(r.Message, []);
            else card.Fail(r.Message);
            return r.Ok;
        }
        catch (OperationCanceledException)
        {
            card.Fail(L.T("job.cancelled"));
            return false;
        }
        catch (Exception e)
        {
            Log(e);
            card.Fail(e.Message);
            return false;
        }
    }

    // ---------- Windows & tray ----------

    internal void ShowMain()
    {
        if (main == null)
        {
            main = new MainWindow(Settings, OnSettingsChanged);
            main.Closed += (_, _) => main = null;
        }
        main.Show();
        if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
        main.Activate();
    }

    void OnSettingsChanged()
    {
        Settings.Apply();
        Settings.Save();
        gesture.KeyToMode = Settings.ModeForKey;
        RebuildTrayMenu();
    }

    void CreateTray()
    {
        using var stream = GetResourceStream(new Uri("pack://application:,,,/Assets/Poppy.ico"))!.Stream;
        tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
            Text = "Poppy",
            Visible = true,
        };
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ShowMain();
        };
        RebuildTrayMenu();
    }

    void RebuildTrayMenu()
    {
        if (tray == null) return;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(L.T("tray.open"), null, (_, _) => ShowMain());
        menu.Items.Add(L.T("tray.shelf"), null, (_, _) => GetShelf().ShowNearCursor());
        var missing = MissingEngines();
        if (missing.Count > 0)
            menu.Items.Add(L.F("tray.engines", missing.Count), null, async (_, _) => await InstallMissingAsync());
        var pause = new Forms.ToolStripMenuItem(L.T("tray.pause")) { Checked = hooks.Paused, CheckOnClick = true };
        pause.CheckedChanged += (_, _) => hooks.Paused = pause.Checked;
        menu.Items.Add(pause);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(L.T("tray.quit"), null, (_, _) => Quit());
        tray.ContextMenuStrip = menu;
    }

    internal void Quit()
    {
        hooks.Dispose();
        if (tray != null) { tray.Visible = false; tray.Dispose(); }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        hooks?.Dispose();
        tray?.Dispose();
        mutex?.Dispose();
        base.OnExit(e);
    }

    internal static void Log(Exception e)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.AppendAllText(Path.Combine(AppSettings.Folder, "poppy.log"), $"[{DateTime.Now:u}] {e}\r\n\r\n");
        }
        catch { }
    }
}
