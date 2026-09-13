// -----------------------------------------------------------------------
// <copyright file="EmbedQueryLatencyBudgetTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Netclaw.Actors.Memory;
using Xunit;

namespace Netclaw.Embeddings.Tests;

/// <summary>
/// Latency budget test for the per-turn query-embedding sub-budget (memory-core-redesign Slice
/// 4, task 4.8): <c>SQLiteMemoryRecallCoordinator</c>'s <c>VectorEmbedSubBudgetMs</c> gives each
/// turn's query-embedding call 150ms before degrading to the lexical-only path. This lives in
/// <c>Netclaw.Embeddings.Tests</c>, not <c>Netclaw.Actors.Tests</c>, because
/// <c>Netclaw.Actors</c> must never reference <c>Netclaw.Embeddings</c> (design D1 seam rule) —
/// <see cref="OnnxMemoryEmbedder"/> is only visible from this project.
///
/// <para>
/// Uses the same tiny fixture ONNX model as <see cref="OnnxMemoryEmbedderTests"/> (no network
/// access, no real allowlisted model download). A tiny fixture graph is drastically faster than
/// any real embedding model, so this is NOT a measurement of the sub-budget's real-world margin
/// (that measurement lives in design.md, task 2.13, <c>tools/embed-latency-bench</c>, against the
/// real allowlisted model: p50 19.0ms / p95 20.9ms on the reference box). It is a regression
/// guard against something making even a trivial model's <c>EmbedAsync</c> call pathologically
/// slow (a synchronization bug serializing every call through a lock, a leaked debug delay, a
/// broken bucketing path re-padding to the full 512-token scratch buffer). The 150ms bound is
/// intentionally generous for a model this tiny; median (not max) across repeated calls avoids
/// flaking on one slow first-call cost, which the explicit warm-up call below already absorbs.
/// </para>
/// </summary>
public sealed class EmbedQueryLatencyBudgetTests : IAsyncLifetime
{
    private const string ModelId = "tiny-fixture";
    private const int Dimensions = 8;
    private const int SampleCount = 10;
    private const double MedianBudgetMs = 150.0;

    private OnnxMemoryEmbedder _embedder = null!;

    public async ValueTask InitializeAsync()
    {
        var fixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        _embedder = await OnnxMemoryEmbedder.LoadAsync(
            modelPath: Path.Combine(fixturesDir, "tiny-embedder.onnx"),
            vocabPath: Path.Combine(fixturesDir, "tiny-vocab.txt"),
            modelId: ModelId,
            dimensions: Dimensions,
            queryPrefix: "",
            maxConcurrency: 2);
    }

    public ValueTask DisposeAsync()
    {
        _embedder.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Median_short_query_embed_latency_is_within_the_150ms_sub_budget_once_warm()
    {
        var ct = TestContext.Current.CancellationToken;

        // Warm-up call: absorbs first-call session/JIT costs the real
        // EmbeddingWarmupHostedService pays once at startup, outside the per-turn budget.
        await _embedder.EmbedAsync("warm up the inference session", EmbeddingPurpose.RetrievalQuery, ct);

        var samples = new double[SampleCount];
        for (var i = 0; i < SampleCount; i++)
        {
            var sw = Stopwatch.StartNew();
            await _embedder.EmbedAsync("What's our Sev2 response time for commercial support?", EmbeddingPurpose.RetrievalQuery, ct);
            sw.Stop();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        var median = SampleCount % 2 == 0
            ? (samples[(SampleCount / 2) - 1] + samples[SampleCount / 2]) / 2.0
            : samples[SampleCount / 2];

        Assert.True(
            median < MedianBudgetMs,
            $"median short-query embed latency {median:F2}ms exceeded the {MedianBudgetMs}ms sub-budget " +
            $"across samples [{string.Join(", ", samples.Select(s => s.ToString("F2")))}]");
    }
}
