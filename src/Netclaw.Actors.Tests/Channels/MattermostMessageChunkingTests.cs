// -----------------------------------------------------------------------
// <copyright file="MattermostMessageChunkingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels.Mattermost;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class MattermostMessageChunkingTests
{
    [Fact]
    public void ShortMessage_returns_single_chunk()
    {
        var text = new string('a', 100);
        var chunks = MattermostSessionBindingActor.ChunkMessage(text);
        Assert.Single(chunks);
        Assert.Equal(text, chunks[0]);
    }

    [Fact]
    public void LongMessage_splits_at_limit()
    {
        // 32001 chars should produce 3 chunks: 16000 + 16000 + 1
        var text = new string('x', 32_001);
        var chunks = MattermostSessionBindingActor.ChunkMessage(text);
        Assert.Equal(3, chunks.Count);
        Assert.True(chunks.All(c => c.Length <= 16_000));
        Assert.Equal(32_001, chunks.Sum(c => c.Length));
    }

    [Fact]
    public void Splits_at_newline_when_available()
    {
        // Place a newline near the boundary so the split prefers it
        var before = new string('a', 15_990);
        var after = new string('b', 5_000);
        var text = before + "\n" + after;

        var chunks = MattermostSessionBindingActor.ChunkMessage(text);
        Assert.Equal(2, chunks.Count);

        // First chunk should end with the newline (inclusive)
        Assert.Equal(before.Length + 1, chunks[0].Length);
        Assert.EndsWith("\n", chunks[0]);
        Assert.Equal(after, chunks[1]);
    }

    [Fact]
    public void Handles_text_with_no_newlines()
    {
        // Continuous text with no newlines splits at exactly MaxMattermostPostLength
        var text = new string('z', 40_000);
        var chunks = MattermostSessionBindingActor.ChunkMessage(text);
        Assert.Equal(3, chunks.Count);
        Assert.Equal(16_000, chunks[0].Length);
        Assert.Equal(16_000, chunks[1].Length);
        Assert.Equal(8_000, chunks[2].Length);
        Assert.Equal(40_000, chunks.Sum(c => c.Length));
    }
}
