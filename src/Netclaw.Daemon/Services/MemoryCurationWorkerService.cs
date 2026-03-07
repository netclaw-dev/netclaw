using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Memory;

namespace Netclaw.Daemon.Services;

internal sealed class MemoryCurationWorkerService(
    SQLiteMemoryStore store,
    MemoryCurationEngine engine,
    ILogger<MemoryCurationWorkerService> logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _worker;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _worker = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        if (_worker is not null)
            await _worker;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await store.ResetProcessingCheckpointsAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            var leased = await store.LeaseNextPendingCheckpointAsync(ct);
            if (leased is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
                continue;
            }

            try
            {
                var operations = await engine.CurateAsync(leased, ct);
                await store.ApplyCurationBatchAsync(leased.CheckpointId, operations, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Memory checkpoint curation failed for {CheckpointId}; scheduling retry",
                    leased.CheckpointId);
                await store.MarkCheckpointRetryAsync(leased.CheckpointId, maxRetries: 5, ct);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
