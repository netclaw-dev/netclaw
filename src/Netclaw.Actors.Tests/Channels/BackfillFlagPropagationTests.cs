using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class BackfillFlagPropagationTests
{
    [Fact]
    public void MapToCommand_propagates_IsBackfill_true()
    {
        var input = new ChannelInput
        {
            SenderId = "U1",
            ChannelId = "C1",
            Contents = [new TextContent("hello")],
            IsBackfill = true
        };

        var options = new SessionPipelineOptions
        {
            ChannelType = ChannelType.Slack
        };

        // Use reflection to call the private static MapToCommand method
        var method = typeof(SessionPipeline).GetMethod(
            "MapToCommand",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        var cmd = (SendUserMessage)method!.Invoke(null,
            [input, new SessionId("C1/T1"), options, null])!;

        Assert.True(cmd.IsBackfill);
    }

    [Fact]
    public void MapToCommand_defaults_IsBackfill_false()
    {
        var input = new ChannelInput
        {
            SenderId = "U1",
            ChannelId = "C1",
            Contents = [new TextContent("hello")]
        };

        var options = new SessionPipelineOptions
        {
            ChannelType = ChannelType.Slack
        };

        var method = typeof(SessionPipeline).GetMethod(
            "MapToCommand",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        var cmd = (SendUserMessage)method!.Invoke(null,
            [input, new SessionId("C1/T1"), options, null])!;

        Assert.False(cmd.IsBackfill);
    }
}
