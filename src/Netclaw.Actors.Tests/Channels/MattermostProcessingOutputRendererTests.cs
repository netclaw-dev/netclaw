// -----------------------------------------------------------------------
// <copyright file="MattermostProcessingOutputRendererTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;
using Netclaw.Channels.Mattermost;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Channels;

public sealed class MattermostProcessingOutputRendererTests
{
    [Fact]
    public async Task Processing_true_renders_channel_and_thread_pulse()
    {
        var ct = TestContext.Current.CancellationToken;
        var replyClient = new RecordingMattermostReplyClient();
        var registry = TestChannelRegistries.MattermostWithProcessingRenderer(replyClient);
        var channelKey = ChannelDescriptorKey.FromChannelType(ChannelType.Mattermost);

        var request = BuildRequest(
            channelKey,
            channelId: "ch-1",
            rootPostId: "root-1",
            isProcessing: true);

        var result = await registry.RenderOutputAsync(request, ct);

        Assert.Equal(ChannelOutputRenderStatus.Rendered, result.Status);
        var pulse = Assert.Single(replyClient.TypingPulses);
        Assert.Equal("ch-1", pulse.ChannelId.Value);
        Assert.Equal("root-1", pulse.RootPostId);
    }

    [Fact]
    public async Task Processing_false_renders_no_pulse()
    {
        var ct = TestContext.Current.CancellationToken;
        var replyClient = new RecordingMattermostReplyClient();
        var registry = TestChannelRegistries.MattermostWithProcessingRenderer(replyClient);
        var channelKey = ChannelDescriptorKey.FromChannelType(ChannelType.Mattermost);

        var request = BuildRequest(
            channelKey,
            channelId: "ch-1",
            rootPostId: "root-1",
            isProcessing: false);

        var result = await registry.RenderOutputAsync(request, ct);

        // The renderer ignores IsProcessing:false; the pulse is a one-shot
        // event that only fires on the start of a processing phase.
        Assert.Equal(ChannelOutputRenderStatus.Rendered, result.Status);
        Assert.Empty(replyClient.TypingPulses);
    }

    [Fact]
    public async Task Processing_true_with_no_thread_root_pulses_the_channel()
    {
        var ct = TestContext.Current.CancellationToken;
        var replyClient = new RecordingMattermostReplyClient();
        var registry = TestChannelRegistries.MattermostWithProcessingRenderer(replyClient);
        var channelKey = ChannelDescriptorKey.FromChannelType(ChannelType.Mattermost);

        var request = BuildRequest(
            channelKey,
            channelId: "ch-1",
            rootPostId: null,
            isProcessing: true);

        await registry.RenderOutputAsync(request, ct);

        var pulse = Assert.Single(replyClient.TypingPulses);
        Assert.Equal("ch-1", pulse.ChannelId.Value);
        Assert.Null(pulse.RootPostId);
    }

    private static ChannelOutputRenderRequest BuildRequest(
        ChannelDescriptorKey channelKey,
        string channelId,
        string? rootPostId,
        bool isProcessing)
    {
        var target = new ChannelDeliveryTarget(
            channelKey,
            new ResolvedChannelAddress(
                channelKey,
                ChannelAddressKind.Destination,
                channelId,
                channelId),
            rootPostId);

        return new ChannelOutputRenderRequest(
            target,
            new ProcessingStateOutput(isProcessing)
            {
                SessionId = new SessionId("test-session")
            },
            ChannelOutputEffectKind.ProcessingIndicator,
            ChannelOutputRequirement.Optional);
    }
}
