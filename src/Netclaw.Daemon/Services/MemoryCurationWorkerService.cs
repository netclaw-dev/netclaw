// -----------------------------------------------------------------------
// <copyright file="MemoryCurationWorkerService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Telemetry;

namespace Netclaw.Daemon.Services;

internal sealed class MemoryCurationWorkerService(
    SQLiteMemoryStore store,
    MemoryCurationEngine engine,
    TimeProvider timeProvider,
    ILogger<MemoryCurationWorkerService> logger,
    ISessionMetrics? metrics = null,
    MemoryEmbedderHolder? embedderHolder = null) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _worker;
    private bool _disposed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _worker = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
            return;

        _cts.Cancel();
        if (_worker is not null)
            await _worker;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
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
                    var started = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                    var operations = await engine.CurateAsync(leased, ct);
                    var writtenDocs = await store.ApplyCurationBatchAsync(leased.CheckpointId, operations, ct);

                    // Embed-on-write (memory-core-redesign Slice 2, task 2.8): runs after the
                    // checkpoint's write has already committed. Vectors are derived data — a
                    // failure here must never fail or retry this checkpoint;
                    // MemoryEmbedOnWriteCoordinator isolates and logs per-item failures.
                    await MemoryEmbedOnWriteCoordinator.EmbedWrittenDocumentsAsync(
                        embedderHolder, store, writtenDocs, logger, ct);

                    var ended = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                    logger.LogInformation(
                        "Memory checkpoint curation completed for {CheckpointId} (trigger={TriggerType}, operations={OperationCount}, durationMs={DurationMs})",
                        leased.CheckpointId,
                        leased.TriggerType,
                        operations.Count,
                        ended - started);

                    if (operations.Count > 0)
                    {
                        metrics?.RecordMemoriesFormed(operations.Count);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Memory checkpoint curation failed for {CheckpointId} (trigger={TriggerType}); scheduling retry",
                        leased.CheckpointId,
                        leased.TriggerType);

                    if (string.Equals(leased.TriggerType, "subagent-findings", StringComparison.OrdinalIgnoreCase))
                    {
                        logger.LogWarning(
                            "Subagent-originated memory candidate retry scheduled for checkpoint {CheckpointId}",
                            leased.CheckpointId);
                    }

                    await store.MarkCheckpointRetryAsync(leased.CheckpointId, maxRetries: 5, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogDebug("Memory curation worker stopped.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Memory curation worker terminated due to an unexpected exception.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (!_cts.IsCancellationRequested)
            _cts.Cancel();
        _cts.Dispose();
    }
}
