using Netclaw.Channels.Discord;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class DiscordMessageChunkingTests
{
    [Fact]
    public void Short_message_returns_single_chunk()
    {
        var chunks = DiscordSessionBindingActor.ChunkMessage("hello");
        Assert.Single(chunks);
        Assert.Equal("hello", chunks[0]);
    }

    [Fact]
    public void Message_at_limit_returns_single_chunk()
    {
        var text = new string('a', 2000);
        var chunks = DiscordSessionBindingActor.ChunkMessage(text);
        Assert.Single(chunks);
        Assert.Equal(2000, chunks[0].Length);
    }

    [Fact]
    public void Long_message_splits_at_newline_boundary()
    {
        var firstPart = new string('a', 1995);
        var text = firstPart + "\nremainder";
        var chunks = DiscordSessionBindingActor.ChunkMessage(text);
        Assert.Equal(2, chunks.Count);
        Assert.Equal(firstPart + "\n", chunks[0]);
        Assert.Equal("remainder", chunks[1]);
    }

    [Fact]
    public void Long_message_without_newlines_splits_at_hard_limit()
    {
        var text = new string('x', 4500);
        var chunks = DiscordSessionBindingActor.ChunkMessage(text);
        Assert.Equal(3, chunks.Count);
        Assert.Equal(2000, chunks[0].Length);
        Assert.Equal(2000, chunks[1].Length);
        Assert.Equal(500, chunks[2].Length);
    }

    [Fact]
    public void Empty_string_returns_single_chunk()
    {
        var chunks = DiscordSessionBindingActor.ChunkMessage("");
        Assert.Single(chunks);
        Assert.Equal("", chunks[0]);
    }
}
