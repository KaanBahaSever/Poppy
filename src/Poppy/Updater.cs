using Poppy.Core;
using Velopack;
using Velopack.Sources;

namespace Poppy;

/// <summary>
/// Updates for the GitHub (Setup.exe) edition. Checks right after start and every few hours,
/// downloads in the background (only the changed parts when a delta package exists), then offers
/// "Restart to update". If the user just quits instead, the update is applied on the way out.
/// The Store edition is updated by the Store and never gets here.
/// </summary>
sealed class Updater
{
    public enum State { Unsupported, Idle, Checking, Downloading, Ready, UpToDate, Failed }

    readonly UpdateManager? mgr;
    UpdateInfo? ready;
    int percent;

    public State Current { get; private set; }
    public event Action? Changed;

    public Updater()
    {
        try
        {
            var m = new UpdateManager(new GithubSource(MainWindow.RepoUrl, null, false));
            if (m.IsInstalled) mgr = m;
        }
        catch (Exception e)
        {
            App.Log(e);
        }
        Current = mgr == null ? State.Unsupported : State.Idle;
    }

    public string CurrentVersion => mgr?.CurrentVersion?.ToString() ?? typeof(Updater).Assembly.GetName().Version?.ToString(3) ?? "?";
    public string? ReadyVersion => ready?.TargetFullRelease.Version.ToString();

    /// <summary>One line for the About page.</summary>
    public string Describe() => Current switch
    {
        State.Unsupported => AutoStart.IsPackaged ? L.T("upd.store") : L.T("upd.dev"),
        State.Checking => L.T("upd.checking"),
        State.Downloading => L.F("upd.downloading", ReadyVersionOrNext, percent),
        State.Ready => L.F("upd.ready", ReadyVersion ?? ""),
        State.UpToDate => L.T("upd.latest"),
        State.Failed => L.T("upd.failed"),
        _ => "",
    };

    string ReadyVersionOrNext = "";

    public async Task CheckAsync()
    {
        if (mgr == null || Current is State.Checking or State.Downloading or State.Ready) return;
        Set(State.Checking);
        try
        {
            var update = await mgr.CheckForUpdatesAsync();
            if (update == null)
            {
                Set(State.UpToDate);
                return;
            }
            ReadyVersionOrNext = update.TargetFullRelease.Version.ToString();
            percent = 0;
            Set(State.Downloading);
            await mgr.DownloadUpdatesAsync(update, p =>
            {
                if (p / 10 == percent / 10) return;
                percent = p;
                App.Current.Dispatcher.BeginInvoke(() => Changed?.Invoke());
            });
            ready = update;
            Set(State.Ready);
        }
        catch (Exception e)
        {
            App.Log(e);
            Set(State.Failed);
        }
    }

    /// <summary>Applies the downloaded update now and starts the new version.</summary>
    public void RestartNow()
    {
        if (mgr == null || ready == null || applying) return;
        applying = true;
        mgr.ApplyUpdatesAndRestart(ready.TargetFullRelease);
    }

    /// <summary>Called when Poppy exits normally: a downloaded update is applied after it closes.</summary>
    bool applying;

    public void ApplyOnExit()
    {
        if (mgr == null || ready == null || applying) return;
        applying = true;
        try { mgr.WaitExitThenApplyUpdates(ready.TargetFullRelease, silent: true, restart: false); }
        catch (Exception e) { App.Log(e); }
    }

    void Set(State s)
    {
        Current = s;
        Changed?.Invoke();
    }
}
