using System.Diagnostics;
using System.Text;

namespace Poppy.Core.Engines;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;

    /// <summary>Last few lines of stderr, for error messages.</summary>
    public string Tail(int lines = 4) =>
        string.Join(Environment.NewLine, StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(lines)).Trim();
}

public static class ProcessRunner
{
    public static ProcessResult Run(string exe, IEnumerable<string> args, TimeSpan timeout) =>
        RunAsync(exe, args, null, CancellationToken.None, timeout).GetAwaiter().GetResult();

    /// <summary>Runs a console program with no window. onStdOutLine sees stdout line by line.</summary>
    public static async Task<ProcessResult> RunAsync(
        string exe, IEnumerable<string> args, Action<string>? onStdOutLine, CancellationToken ct,
        TimeSpan? timeout = null, string? workingDir = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workingDir != null) psi.WorkingDirectory = workingDir;
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (stdout) stdout.AppendLine(e.Data);
            onStdOutLine?.Invoke(e.Data);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (stderr)
            {
                // ffmpeg can be chatty; keep the tail only.
                if (stderr.Length > 64_000) stderr.Remove(0, 32_000);
                stderr.AppendLine(e.Data);
            }
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var timeoutCts = timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await p.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            return new ProcessResult(-1, stdout.ToString(), "Timed out. " + stderr);
        }
        p.WaitForExit(); // flush async readers
        return new ProcessResult(p.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
