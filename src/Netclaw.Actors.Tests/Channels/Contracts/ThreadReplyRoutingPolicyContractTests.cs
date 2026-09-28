// -----------------------------------------------------------------------
// <copyright file="ThreadReplyRoutingPolicyContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

/// <summary>
/// Capability layer for channels whose platform has operator-visible thread
/// objects (Slack threads, Discord threads, Mattermost root-post chains).
/// Covers thread continuation on a live session, daemon-restart rehydration
/// from a thread reply, and the per-channel <c>MentionRequiredInThread</c>
/// tap that gates both paths. Channels without thread-reply routing (e.g.
/// Telegram, whose topics key sessions but are not addressing signals)
/// enroll in <see cref="RoutingPolicyContractTests"/> only.
/// <para>
/// Derived from <see cref="RoutingPolicyContractTests"/> so channels in this
/// layer run the universal cases too; the layer adds these cases exactly
/// once per concrete fixture.
/// </para>
/// </summary>
public abstract class ThreadReplyRoutingPolicyContractTests : RoutingPolicyContractTests
{
    /// <summary>
    /// Evaluates the channel's routing policy for a group (non-DM) thread
    /// message under an enabled mention gate.
    /// </summary>
    protected abstract RoutingVerdict EvaluateThreadReply(
        bool mentionRequiredInThread,
        bool containsMention,
        bool threadExists,
        bool isThreadReply,
        string text);

    [Fact]
    public void ExistingThread_ContinuesWithoutMention()
    {
        var verdict = EvaluateThreadReply(
            mentionRequiredInThread: false,
            containsMention: false,
            threadExists: true,
            isThreadReply: true,
            text: "follow up");

        Assert.Equal(RoutingVerdict.ContinueOnly, verdict);
    }

    [Fact]
    public void ThreadReply_RehydratesSession_WhenNoActorExists()
    {
        // Reply in an existing platform thread, but the session actor was lost
        // (e.g. daemon restart). The policy must route so the persisted session
        // can be rehydrated — the mention-only gate must not block this path.
        var verdict = EvaluateThreadReply(
            mentionRequiredInThread: false,
            containsMention: false,
            threadExists: false,
            isThreadReply: true,
            text: "follow up");

        Assert.Equal(RoutingVerdict.Route, verdict);
    }

    [Theory]
    [InlineData(true, RoutingVerdictKind.ContinueOnly, null)]
    [InlineData(false, RoutingVerdictKind.Ignore, RoutingIgnoreReason.ThreadMentionRequired)]
    public void ExistingThread_HonorsMentionRequiredInThread(
        bool containsMention,
        RoutingVerdictKind expectedKind,
        RoutingIgnoreReason? expectedReason)
    {
        // With MentionRequiredInThread enabled, follow-ups in a thread with an
        // active session still need a mention — the active-session bypass is off.
        var verdict = EvaluateThreadReply(
            mentionRequiredInThread: true,
            containsMention: containsMention,
            threadExists: true,
            isThreadReply: true,
            text: "follow up");

        Assert.Equal(new RoutingVerdict(expectedKind, expectedReason), verdict);
    }

    [Theory]
    [InlineData(true, RoutingVerdictKind.Route, null)]
    [InlineData(false, RoutingVerdictKind.Ignore, RoutingIgnoreReason.ThreadMentionRequired)]
    public void ThreadReplyRehydration_HonorsMentionRequiredInThread(
        bool containsMention,
        RoutingVerdictKind expectedKind,
        RoutingIgnoreReason? expectedReason)
    {
        // The daemon-restart rehydration path is gated the same way: an
        // un-mentioned thread reply must not re-create the session.
        var verdict = EvaluateThreadReply(
            mentionRequiredInThread: true,
            containsMention: containsMention,
            threadExists: false,
            isThreadReply: true,
            text: "follow up");

        Assert.Equal(new RoutingVerdict(expectedKind, expectedReason), verdict);
    }
}
