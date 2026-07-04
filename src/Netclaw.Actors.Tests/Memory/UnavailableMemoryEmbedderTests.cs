// -----------------------------------------------------------------------
// <copyright file="UnavailableMemoryEmbedderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Memory;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public sealed class UnavailableMemoryEmbedderTests
{
    [Fact]
    public void IsAvailable_is_always_false()
    {
        IMemoryEmbedder embedder = new UnavailableMemoryEmbedder("snowflake-arctic-embed-m", "model not provisioned");

        Assert.False(embedder.IsAvailable);
        Assert.Equal(0, embedder.Dimensions);
        Assert.Equal("snowflake-arctic-embed-m", embedder.ModelId);
    }

    [Fact]
    public async Task EmbedAsync_throws_with_remediation_text_instead_of_returning_a_vector()
    {
        IMemoryEmbedder embedder = new UnavailableMemoryEmbedder("snowflake-arctic-embed-m", "hash verification failed");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await embedder.EmbedAsync("some text", CancellationToken.None));

        Assert.Contains("hash verification failed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("snowflake-arctic-embed-m", ex.Message, StringComparison.Ordinal);
        Assert.Contains("IsAvailable", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbedBatchAsync_throws_instead_of_returning_garbage_vectors()
    {
        IMemoryEmbedder embedder = new UnavailableMemoryEmbedder("snowflake-arctic-embed-m", "runtime load error");

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await embedder.EmbedBatchAsync(["a", "b"], CancellationToken.None));
    }
}
