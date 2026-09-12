// -----------------------------------------------------------------------
// <copyright file="UnavailableRelevanceScorerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Memory;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

public sealed class UnavailableRelevanceScorerTests
{
    [Fact]
    public void IsAvailable_is_always_false()
    {
        IRelevanceScorer scorer = new UnavailableRelevanceScorer("ms-marco-minilm-l-6-v2", "model not provisioned");

        Assert.False(scorer.IsAvailable);
        Assert.Equal("ms-marco-minilm-l-6-v2", scorer.ModelId);
    }

    [Fact]
    public async Task ScoreAsync_throws_with_remediation_text_instead_of_returning_a_score()
    {
        IRelevanceScorer scorer = new UnavailableRelevanceScorer("ms-marco-minilm-l-6-v2", "hash verification failed");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await scorer.ScoreAsync("query", ["candidate"], CancellationToken.None));

        Assert.Contains("hash verification failed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ms-marco-minilm-l-6-v2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("IsAvailable", ex.Message, StringComparison.Ordinal);
    }
}
