// -----------------------------------------------------------------------
// <copyright file="ProbeProgressReporter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli.Model;

/// <summary>
/// Emits a live "probing … (Ns)" elapsed-time ticker while a provider probe is in
/// flight, so a slow self-hosted server reads as "working, just slow" instead of a
/// hang (#1292). The ticker goes to <b>stderr</b> and is suppressed entirely when
/// stderr is redirected, so piped stdout (the discovered-model table) stays clean and
/// machine-parseable. Start it, run the probe inside the <c>await using</c> scope, and
/// disposal stops the ticker and erases its line. Best-effort by design: terminal I/O
/// faults (window/SSH closed mid-probe) are swallowed so the ticker can never break the
/// command or mask the probe's real result.
/// </summary>
internal sealed class ProbeProgressReporter : IAsyncDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    // Widest line drawn so far; read only after _loop completes (so no synchronization
    // needed) to erase exactly what was written regardless of endpoint length.
    private int _lastLineWidth;

    private ProbeProgressReporter(string endpoint)
        => _loop = RunAsync(endpoint, _cts.Token);

    internal static ProbeProgressReporter Start(string endpoint) => new(endpoint);

    private async Task RunAsync(string endpoint, CancellationToken ct)
    {
        if (System.Console.IsErrorRedirected)
            return;

        var start = TimeProvider.System.GetTimestamp();
        try
        {
            while (true)
            {
                await Task.Delay(TickInterval, ct);
                var seconds = (int)TimeProvider.System.GetElapsedTime(start).TotalSeconds;
                var line = $"  probing {endpoint} ... {seconds}s";
                _lastLineWidth = Math.Max(_lastLineWidth, line.Length);
                await System.Console.Error.WriteAsync("\r" + line + " ");
                await System.Console.Error.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { return; } // probe completed — stop ticking
        catch (IOException) { return; }                // terminal gone — ticker must not throw
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _loop; // RunAsync swallows its own OCE/IO faults, so this won't throw.
            EraseLine();
        }
        finally
        {
            _cts.Dispose();
        }
    }

    private void EraseLine()
    {
        if (System.Console.IsErrorRedirected || _lastLineWidth == 0)
            return;
        try
        {
            // Blank exactly the width we drew (+1 for the trailing space) so the result
            // row prints clean no matter how long the endpoint was.
            System.Console.Error.Write("\r" + new string(' ', _lastLineWidth + 1) + "\r");
        }
        catch (IOException) { return; } // terminal gone — nothing left to erase
    }
}
