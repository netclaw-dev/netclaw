// -----------------------------------------------------------------------
// <copyright file="EmbeddingHolderLifetimeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Memory;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public sealed class EmbeddingHolderLifetimeTests
{
    [Fact]
    public void MemoryEmbedderHolder_disposes_replaced_and_current_embedders_once()
    {
        var first = new DisposableEmbedder("first");
        var second = new DisposableEmbedder("second");
        var holder = new MemoryEmbedderHolder(first, string.Empty, null);

        holder.Set(second, string.Empty, null);
        holder.Dispose();
        holder.Dispose();

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public void RelevanceScorerHolder_disposes_replaced_and_current_scorers_once()
    {
        var first = new DisposableScorer("first");
        var second = new DisposableScorer("second");
        var holder = new RelevanceScorerHolder(first, 0.5);

        holder.Set(second, 0.6);
        holder.Dispose();
        holder.Dispose();

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    private sealed class DisposableEmbedder(string modelId) : IMemoryEmbedder, IDisposable
    {
        public int DisposeCount { get; private set; }

        public string ModelId => modelId;

        public int Dimensions => 1;

        public bool IsAvailable => true;

        public ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text, EmbeddingPurpose purpose, CancellationToken ct)
            => ValueTask.FromResult<ReadOnlyMemory<float>>(new float[] { 1f });

        public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchAsync(
            IReadOnlyList<string> texts,
            EmbeddingPurpose purpose,
            CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(
                texts.Select(_ => (ReadOnlyMemory<float>)new float[] { 1f }).ToArray());

        public void Dispose() => DisposeCount++;
    }

    private sealed class DisposableScorer(string modelId) : IRelevanceScorer, IDisposable
    {
        public int DisposeCount { get; private set; }

        public string ModelId => modelId;

        public bool IsAvailable => true;

        public ValueTask<IReadOnlyList<double>> ScoreAsync(
            string query,
            IReadOnlyList<string> candidates,
            CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<double>>(candidates.Select(_ => 1.0).ToArray());

        public void Dispose() => DisposeCount++;
    }
}
