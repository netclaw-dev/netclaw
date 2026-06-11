// -----------------------------------------------------------------------
// <copyright file="ProactiveOutboundFakes.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Channels.Discord;
using Netclaw.Channels.Mattermost;
using Netclaw.Channels.Slack;

namespace Netclaw.Actors.Tests.Channels.TestHelpers;

/// <summary>
/// Minimal non-null <see cref="IActorRef"/> stand-in for the gateway in tests
/// that don't need the session-wiring ask to succeed. NOTE: <c>Ask</c> against
/// this ref always faults (a <see cref="MinimalActorRef"/> has no provider to
/// create the promise ref), so a proactive client talking to it returns the
/// sent-but-pipeline-failed fallback. For genuine ack/nack coverage use
/// <see cref="ProactiveGatewayResponderActor"/> inside a TestKit actor system
/// (see <c>ProactiveOutboundClientContractTests</c>).
/// </summary>
internal sealed class FakeProactiveGateway(Func<object, object?> respond) : MinimalActorRef
{
    public override ActorPath Path { get; } =
        new RootActorPath(Address.AllSystems) / "fake-proactive-gateway";

    public override IActorRefProvider Provider =>
        throw new NotSupportedException("Not needed for proactive client tests");

    protected override void TellInternal(object message, IActorRef sender)
    {
        if (respond(message) is { } reply)
            sender.Tell(reply);
    }
}

/// <summary>
/// Real actor that plays the gateway in proactive-send tests: the responder
/// maps the asked message to a reply (the channel's proactive-thread ack, or
/// <c>Status.Failure</c> to nack the ask); returning <c>null</c> leaves the
/// ask unanswered.
/// </summary>
internal sealed class ProactiveGatewayResponderActor : ReceiveActor
{
    public ProactiveGatewayResponderActor(Func<object, object?> respond)
    {
        ReceiveAny(message =>
        {
            if (respond(message) is { } reply)
                Sender.Tell(reply);
        });
    }
}

internal sealed class FakeSlackOutboundClient : ISlackOutboundClient
{
    public bool ShouldThrow { get; init; }
    public List<SlackUserId> OpenedDms { get; } = [];
    public List<(SlackChannelId ChannelId, string Text)> PostedThreads { get; } = [];

    public Task<SlackChannelId> OpenDmChannelAsync(SlackUserId userId, CancellationToken ct = default)
    {
        if (ShouldThrow) throw new InvalidOperationException("Slack API error");
        OpenedDms.Add(userId);
        return Task.FromResult(new SlackChannelId($"D{userId.Value}"));
    }

    public Task<SlackNewThread> PostNewThreadAsync(SlackChannelId channelId, string text, CancellationToken ct = default)
    {
        if (ShouldThrow) throw new InvalidOperationException("Slack API error");
        PostedThreads.Add((channelId, text));
        return Task.FromResult(new SlackNewThread(channelId, new SlackThreadTs("1234567890.000001")));
    }
}

internal sealed class FakeDiscordOutboundClient : IDiscordOutboundClient
{
    public bool ShouldThrow { get; init; }
    public bool ThrowThreadCreationFailure { get; init; }
    public List<(DiscordChannelId ChannelId, string Text, string ThreadName)> Posts { get; } = [];
    public List<(DiscordUserId UserId, string Text)> DirectMessages { get; } = [];

    public Task<DiscordNewThread> PostNewThreadAsync(
        DiscordChannelId channelId, string text, string threadName, CancellationToken ct = default)
    {
        if (ShouldThrow) throw new InvalidOperationException("Discord API error");
        if (ThrowThreadCreationFailure)
        {
            throw new DiscordThreadCreationFailedException(
                channelId,
                new DiscordMessageId($"root-{channelId.Value}"),
                "Root message posted but thread creation failed.",
                new InvalidOperationException("Missing Create Public Threads permission"));
        }
        Posts.Add((channelId, text, threadName));
        // Discord convention: a thread created from a message shares its id.
        var threadId = $"thread-{channelId.Value}";
        return Task.FromResult(new DiscordNewThread(
            channelId,
            new DiscordReplyChannelId(threadId),
            new DiscordThreadOrMessageId(threadId)));
    }

    public Task<DiscordNewDirectMessage> PostDirectMessageAsync(
        DiscordUserId userId,
        string text,
        CancellationToken ct = default)
    {
        if (ShouldThrow) throw new InvalidOperationException("Discord API error");
        DirectMessages.Add((userId, text));
        var dmChannelId = $"dm-{userId.Value}";
        var rootMessageId = $"root-{userId.Value}";
        return Task.FromResult(new DiscordNewDirectMessage(
            new DiscordChannelId(dmChannelId),
            new DiscordReplyChannelId(dmChannelId),
            new DiscordThreadOrMessageId(rootMessageId),
            new DiscordMessageId(rootMessageId),
            userId));
    }
}

internal sealed class FakeMattermostOutboundClient : IMattermostOutboundClient
{
    public List<MattermostUserId> OpenedDms { get; } = [];
    public List<(MattermostChannelId ChannelId, string Text)> PostedThreads { get; } = [];

    public Task<MattermostChannelId> OpenDmChannelAsync(MattermostUserId userId, CancellationToken ct = default)
    {
        OpenedDms.Add(userId);
        return Task.FromResult(new MattermostChannelId($"dm-{userId.Value}"));
    }

    public Task<MattermostNewThread> PostNewThreadAsync(
        MattermostChannelId channelId,
        string text,
        CancellationToken ct = default)
    {
        PostedThreads.Add((channelId, text));
        return Task.FromResult(new MattermostNewThread(channelId, new MattermostRootPostId($"root-{channelId.Value}")));
    }
}
