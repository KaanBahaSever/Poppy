using Velopack;
using Velopack.Sources;

namespace Poppy;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Must run first: handles install/uninstall/update hooks of the GitHub (Setup.exe) edition.
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    /// <summary>
    /// The GitHub edition updates itself from GitHub Releases; the update is applied the next time
    /// Poppy exits, never while you are using it. The Store edition is updated by the Store.
    /// </summary>
    public static async Task CheckForUpdatesAsync()
    {
        try
        {
            var mgr = new UpdateManager(new GithubSource(MainWindow.RepoUrl, null, false));
            if (!mgr.IsInstalled) return;
            var update = await mgr.CheckForUpdatesAsync();
            if (update == null) return;
            await mgr.DownloadUpdatesAsync(update);
            mgr.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: false);
        }
        catch (Exception e)
        {
            App.Log(e);
        }
    }
}
