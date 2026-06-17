// -----------------------------------------------------------------------
// <copyright file="SingleThreadSynchronizationContext.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;

namespace Netclaw.Cli.Tests.Tui;

/// <summary>
/// A <see cref="SynchronizationContext"/> backed by exactly ONE worker thread. Reproduces the
/// bounded-worker condition of xunit v3's <c>MaxConcurrencySyncContext</c> on a low-core CI runner —
/// the environment in which the <c>netclaw config</c> TUI deadlocked on macOS.
/// <para/>
/// When code posts a continuation to this context while the single worker is blocked
/// (sync-over-async, e.g. <c>SomethingAsync().GetAwaiter().GetResult()</c>), the continuation can
/// never run and the operation deadlocks. Code that awaits all the way through (never blocking the
/// worker) completes here without hanging. Tests run an operation on this context and assert it
/// finishes within a bounded timeout, so a regression back to a blocking bridge fails deterministically
/// instead of only flaking on a specific runner.
/// </summary>
internal sealed class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly Thread _worker;

    public SingleThreadSynchronizationContext()
    {
        _worker = new Thread(Pump) { IsBackground = true, Name = "single-worker-sync-context" };
        _worker.Start();
    }

    public override void Post(SendOrPostCallback d, object? state)
        => _queue.Add((d, state));

    // Send (synchronous dispatch) is intentionally unsupported: a single-worker context cannot run a
    // Send from its own worker without deadlocking, and the tests only ever Post.
    public override void Send(SendOrPostCallback d, object? state)
        => throw new NotSupportedException("SingleThreadSynchronizationContext does not support Send.");

    /// <summary>
    /// Schedules an async method on the single worker (under this context) and returns a Task that
    /// completes — observable from any thread — when the method finishes or faults. The method's awaits
    /// resume on this same worker, so a sync-over-async block anywhere in its call chain self-deadlocks.
    /// </summary>
    public Task Run(Func<Task> asyncMethod)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async _ =>
        {
            try
            {
                await asyncMethod();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }, null);
        return done.Task;
    }

    private void Pump()
    {
        SetSynchronizationContext(this);
        foreach (var (callback, state) in _queue.GetConsumingEnumerable())
        {
            try
            {
                callback(state);
            }
            catch (Exception ex)
            {
                // Keep the single worker alive if a posted continuation throws. The Run() entry point
                // already funnels its scenario's exceptions to a TaskCompletionSource (so the test sees
                // the real failure); letting one stray continuation kill the worker here would drain no
                // further callbacks and make every awaiting test look like a generic deadlock instead.
                _lastError ??= ex;
            }
        }
    }

    /// <summary>The first exception thrown by a posted continuation, if any — for test diagnostics.</summary>
    public Exception? LastError => _lastError;

    private volatile Exception? _lastError;

    public void Dispose() => _queue.CompleteAdding();
}
