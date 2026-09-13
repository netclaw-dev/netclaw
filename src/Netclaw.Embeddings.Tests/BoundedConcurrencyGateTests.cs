// -----------------------------------------------------------------------
// <copyright file="BoundedConcurrencyGateTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Embeddings.Tests;

/// <summary>
/// Proves the concurrency bound <see cref="OnnxMemoryEmbedder"/> relies on
/// (<c>BoundedConcurrencyGate</c>) is actually enforced under real contention, without racing
/// on wall-clock sleeps in test orchestration. The tiny fixture ONNX model runs in
/// microseconds, so a test that fired concurrent real inferences could never reliably observe
/// overlap; testing the gate in isolation with a controlled fake unit of work (a
/// <c>Task.Delay</c> inside the fake work item — legitimate per the constitution's testing
/// guidelines, since the delay lives in the fake, not in test orchestration logic) is the
/// deterministic way to prove the bound holds.
/// </summary>
public sealed class BoundedConcurrencyGateTests
{
    [Fact]
    public async Task RunAsync_never_exceeds_the_configured_max_concurrency()
    {
        var gate = new BoundedConcurrencyGate(maxConcurrency: 2);
        var tasks = new Task<int>[6];

        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = gate.RunAsync(async ct =>
            {
                await Task.Delay(20, ct);
                return 0;
            }, TestContext.Current.CancellationToken);
        }

        await Task.WhenAll(tasks);

        Assert.True(gate.PeakObservedConcurrency <= 2, $"expected peak <= 2, observed {gate.PeakObservedConcurrency}");
        // With 6 tasks racing for 2 slots and a real (non-zero) delay inside each, contention
        // is all but guaranteed — assert it actually happened so this test cannot pass
        // vacuously (e.g. if the gate silently stopped gating and everything just ran serially
        // one at a time, peak would still be 1 and the <= 2 assertion above would be
        // meaningless on its own).
        Assert.True(gate.PeakObservedConcurrency >= 2, $"expected genuine contention (peak >= 2), observed {gate.PeakObservedConcurrency}");
    }

    [Fact]
    public async Task RunAsync_lets_all_queued_work_complete()
    {
        var gate = new BoundedConcurrencyGate(maxConcurrency: 2);
        var completed = 0;

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => gate.RunAsync(async ct =>
            {
                await Task.Delay(5, ct);
                return Interlocked.Increment(ref completed);
            }, TestContext.Current.CancellationToken))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(10, completed);
    }

    [Fact]
    public void Constructor_rejects_non_positive_concurrency()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedConcurrencyGate(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedConcurrencyGate(-1));
    }
}
