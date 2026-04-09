using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class ThreadHistoryContextBuilderTests
{
    [Fact]
    public void Build_produces_delimited_context_block_with_sender_attribution()
    {
        var messages = new List<SendUserMessage>
        {
            new()
            {
                SessionId = new SessionId("C1/T1"),
                Content = "Has anyone seen the latency spike?",
                Source = new MessageSource { ChannelType = Netclaw.Actors.Channels.ChannelType.Slack, SenderId ="alice" }
            },
            new()
            {
                SessionId = new SessionId("C1/T1"),
                Content = "I think it's the new query.",
                Source = new MessageSource { ChannelType = Netclaw.Actors.Channels.ChannelType.Slack, SenderId ="bob" }
            }
        };

        var result = ThreadHistoryContextBuilder.Build(messages, null);

        Assert.Contains("[thread history", result.Text);
        Assert.Contains("[end thread history]", result.Text);
        Assert.Contains("<user: alice>", result.Text);
        Assert.Contains("<user: bob>", result.Text);
        Assert.Contains("Has anyone seen the latency spike?", result.Text);
        Assert.Contains("I think it's the new query.", result.Text);
        Assert.Null(result.MediaReferences);
    }

    [Fact]
    public void Build_collects_media_references_from_all_messages()
    {
        var messages = new List<SendUserMessage>
        {
            new()
            {
                SessionId = new SessionId("C1/T1"),
                Content = "Look at this",
                MediaReferences = [new SerializableMediaReference
                {
                    RelativePath = "abc123.png",
                    MimeType = "image/png",
                    Modality = (int)MediaModality.Image
                }],
                Source = new MessageSource { ChannelType = Netclaw.Actors.Channels.ChannelType.Slack, SenderId ="alice" }
            },
            new()
            {
                SessionId = new SessionId("C1/T1"),
                Content = "And this",
                MediaReferences = [new SerializableMediaReference
                {
                    RelativePath = "def456.jpg",
                    MimeType = "image/jpeg",
                    Modality = (int)MediaModality.Image
                }],
                Source = new MessageSource { ChannelType = Netclaw.Actors.Channels.ChannelType.Slack, SenderId ="bob" }
            }
        };

        var result = ThreadHistoryContextBuilder.Build(messages, null);

        Assert.NotNull(result.MediaReferences);
        Assert.Equal(2, result.MediaReferences.Count);
        Assert.Contains("[image: abc123.png]", result.Text);
        Assert.Contains("[image: def456.jpg]", result.Text);
    }

    [Fact]
    public void Build_handles_empty_list()
    {
        var result = ThreadHistoryContextBuilder.Build([], null);

        Assert.Contains("[thread history", result.Text);
        Assert.Contains("[end thread history]", result.Text);
        Assert.Null(result.MediaReferences);
    }
}
