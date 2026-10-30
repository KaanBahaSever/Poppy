using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using Poppy.Core;
using Poppy.Core.Ops;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Poppy;

/// <summary>The Actions ring: things that do not create a new file.</summary>
static class Actions
{
    public static void Run(string id, string[] files, JobsWindow jobs, Func<ShelfWindow> shelf)
    {
        switch (id)
        {
            case "action.copy":
                var list = new StringCollection();
                list.AddRange(files);
                var data = new DataObject();
                data.SetFileDropList(list);
                // DROPEFFECT_COPY so Explorer pastes a copy rather than moving.
                data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));
                System.Windows.Clipboard.SetDataObject(data, true);
                jobs.Flash(Name(files), L.T("copied"));
                break;
            case "action.copypath":
                System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, files));
                jobs.Flash(Name(files), L.T("pathcopied"));
                break;
            case "action.move":
                Move(files, jobs);
                break;
            case "action.terminal":
                OpenTerminal(files[0]);
                break;
            case "action.share":
                Share.Show(files);
                break;
            case "action.shelf":
                var s = shelf();
                s.AddFiles(files);
                s.ShowNearCursor();
                break;
        }
    }

    static string Name(string[] files) =>
        files.Length == 1 ? Path.GetFileName(files[0].TrimEnd('\\')) : L.F("job.files", files.Length);

    static void Move(string[] files, JobsWindow jobs)
    {
        var dlg = new OpenFolderDialog { Title = L.T("action.move"), InitialDirectory = Path.GetDirectoryName(files[0].TrimEnd('\\')) };
        if (dlg.ShowDialog() != true) return;
        var target = dlg.FolderName;
        var moved = new List<string>();
        try
        {
            foreach (var f in files)
            {
                var path = f.TrimEnd('\\');
                var name = Path.GetFileName(path);
                var isDir = Directory.Exists(path);
                if (string.Equals(Path.GetDirectoryName(path), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                var dest = OutputPaths.Unique(target, isDir ? name : Path.GetFileNameWithoutExtension(name), isDir ? "" : Path.GetExtension(name));
                if (isDir) MoveDirectory(path, dest);
                else File.Move(path, dest);
                moved.Add(dest);
            }
            jobs.Flash(Name(files), L.T("moved"), moved.FirstOrDefault());
        }
        catch (Exception e)
        {
            jobs.Add(Name(files), null).Fail(e.Message);
        }
    }

    static void MoveDirectory(string src, string dest)
    {
        if (string.Equals(Path.GetPathRoot(src), Path.GetPathRoot(dest), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(src, dest);
            return;
        }
        // Across drives Directory.Move fails: copy everything, then remove the original.
        CopyDirectory(src, dest);
        Directory.Delete(src, true);
    }

    static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dest, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(src)) CopyDirectory(d, Path.Combine(dest, Path.GetFileName(d)));
    }

    public static void OpenTerminal(string path)
    {
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
        var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\wt.exe");
        try
        {
            if (File.Exists(wt))
                Process.Start(new ProcessStartInfo(wt) { ArgumentList = { "-d", dir }, UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoExit" }, WorkingDirectory = dir, UseShellExecute = true });
        }
        catch
        {
            Process.Start(new ProcessStartInfo("cmd.exe") { WorkingDirectory = dir, UseShellExecute = true });
        }
    }
}

/// <summary>The Windows share sheet (Nearby Sharing, Mail, Teams...) for desktop apps.</summary>
static class Share
{
    [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }

    static readonly Guid DataTransferManagerIid = new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

    public static void Show(string[] files)
    {
        // The sheet needs a window of ours to anchor to; a tiny invisible one at the cursor will do.
        Native.GetCursorPos(out var pt);
        var anchor = new Window
        {
            Width = 1, Height = 1, WindowStyle = WindowStyle.None, AllowsTransparency = true, Opacity = 0.01,
            ShowInTaskbar = false, Topmost = true, Left = -10000, Top = -10000, Title = "Poppy Share",
        };
        anchor.Show();
        var hwnd = new WindowInteropHelper(anchor).Handle;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, pt.X, pt.Y, 1, 1, Native.SWP_NOACTIVATE);
        Native.ForceForeground(hwnd);

        var interop = DataTransferManager.As<IDataTransferManagerInterop>();
        var iid = DataTransferManagerIid;
        var manager = WinRT.MarshalInterface<DataTransferManager>.FromAbi(interop.GetForWindow(hwnd, ref iid));
        manager.DataRequested += async (_, args) =>
        {
            var deferral = args.Request.GetDeferral();
            try
            {
                args.Request.Data.Properties.Title = files.Length == 1 ? Path.GetFileName(files[0].TrimEnd('\\')) : L.F("job.files", files.Length);
                var items = new List<IStorageItem>();
                foreach (var f in files)
                {
                    if (Directory.Exists(f)) items.Add(await StorageFolder.GetFolderFromPathAsync(f));
                    else items.Add(await StorageFile.GetFileFromPathAsync(f));
                }
                args.Request.Data.SetStorageItems(items);
            }
            catch (Exception e)
            {
                args.Request.FailWithDisplayText(e.Message);
            }
            finally
            {
                deferral.Complete();
            }
        };
        manager.TargetApplicationChosen += (_, _) => anchor.Dispatcher.BeginInvoke(anchor.Close);
        interop.ShowShareUIForWindow(hwnd);
        // The sheet has no "dismissed" event; tidy the anchor up after a while either way.
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        timer.Tick += (_, _) => { timer.Stop(); if (anchor.IsLoaded) anchor.Close(); };
        timer.Start();
    }
}
