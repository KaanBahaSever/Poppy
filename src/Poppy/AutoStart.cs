using Microsoft.Win32;

namespace Poppy;

/// <summary>"Start with Windows": a StartupTask inside the MSIX package, the Run key otherwise.</summary>
static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string TaskId = "PoppyStartup";

    public static bool IsPackaged
    {
        get
        {
            try { return Windows.ApplicationModel.Package.Current != null; }
            catch { return false; }
        }
    }

    public static async Task SetAsync(bool enabled)
    {
        if (IsPackaged)
        {
            var task = await Windows.ApplicationModel.StartupTask.GetAsync(TaskId);
            if (enabled) await task.RequestEnableAsync();
            else task.Disable();
            return;
        }
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue("Poppy", $"\"{Environment.ProcessPath}\" --background");
        else key.DeleteValue("Poppy", throwOnMissingValue: false);
    }
}
