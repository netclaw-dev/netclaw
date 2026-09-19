// -----------------------------------------------------------------------
// <copyright file="DaemonRestartWaiter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

namespace Netclaw.Cli.Daemon;

/// <summary>Waits until a config change produces a healthy daemon generation.</summary>
internal sealed class DaemonRestartWaiter(DaemonApi daemonApi, TimeProvider timeProvider)
{
    internal static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public async Task<bool> WaitAsync(
        int priorGeneration,
        CancellationToken cancellationToken)
    {
        var deadline = timeProvider.GetUtcNow() + ReadyTimeout;
        while (timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var readiness = await daemonApi.ProbeReadinessAsync(cancellationToken);
                if (readiness.Healthy && readiness.Generation > priorGeneration)
                    return true;
            }
            catch (Exception ex) when (
                (ex is HttpRequestException or OperationCanceledException)
                && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, timeProvider, cancellationToken);
                continue;
            }

            await Task.Delay(PollInterval, timeProvider, cancellationToken);
        }

        return false;
    }
}
