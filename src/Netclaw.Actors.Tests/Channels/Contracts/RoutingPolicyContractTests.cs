// -----------------------------------------------------------------------
// <copyright file="RoutingPolicyContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

/// <summary>
/// Channel-neutral routing outcome. Each fixture maps its channel's
/// <c>*RoutingDecisionKind</c> onto these values.
/// </summary>
public enum RoutingVerdictKind
{
    /// <summary>Message is dropped (maps to <c>Ignore</c>).</summary>
    Ignore,

    /// <summary>Deliver only to an already-running session actor (maps to <c>ContinueOnly</c>).</summary>
    ContinueOnly,

    /// <summary>Start a new session actor or continue/rehydrate an existing one (maps to <c>StartOrContinue</c>).</summary>
    Route
}

/// <summary>
/// The ignore reasons shared by all channel routing policies. Channel-specific
/// reasons (e.g. Slack's <c>HiddenMessage</c>/<c>UnsupportedSubtype</c>/<c>WrongKind</c>)
/// have no mapping here and are covered by the standalone per-channel tests.
/// </summary>
public enum RoutingIgnoreReason
{
    NoContent,
    DmNotAllowed,
    DmMentionRequired,
    ChannelMentionRequired
}

public sealed record RoutingVerdict(RoutingVerdictKind Kind, RoutingIgnoreReason? IgnoreReason)
{
    public static readonly RoutingVerdict Route = new(RoutingVerdictKind.Route, null);

    public static readonly RoutingVerdict ContinueOnly = new(RoutingVerdictKind.ContinueOnly, null);

    public static RoutingVerdict Ignore(RoutingIgnoreReason reason) =>
        new(RoutingVerdictKind.Ignore, reason);
}

/// <summary>
/// Behavioral contract for channel routing policies (<c>SlackRoutingPolicy</c>,
/// <c>DiscordRoutingPolicy</c>, <c>MattermostRoutingPolicy</c>). The policies are
/// pure static functions, so no TestKit is needed. Each fixture constructs a plain
/// text-only inbound message for its channel (no files/attachments, no subtype,
/// not hidden) and normalizes the channel decision into a <see cref="RoutingVerdict"/>.
/// </summary>
public abstract class RoutingPolicyContractTests
{
    /// <summary>
    /// Evaluates the channel's routing policy for a plain user message.
    /// <paramref name="isThreadReply"/> means the message itself is a reply inside
    /// an existing platform thread (Slack: <c>ThreadTs</c> differs from <c>EventTs</c>;
    /// Discord: <c>IsInThread</c>; Mattermost: non-empty <c>RootPostId</c>), while
    /// <paramref name="threadExists"/> means a live session actor exists for that thread.
    /// </summary>
    protected abstract RoutingVerdict Evaluate(
        bool mentionOnly,
        bool allowDm,
        bool mentionRequiredInDm,
        bool isDm,
        bool containsMention,
        bool threadExists,
        bool isThreadReply,
        string text);

    [Fact]
    public void MessageWithoutMention_Ignored_WhenMentionOnly()
    {
        var verdict = Evaluate(
            mentionOnly: true,
            allowDm: true,
            mentionRequiredInDm: false,
            isDm: false,
            containsMention: false,
            threadExists: false,
            isThreadReply: false,
            text: "hello");

        Assert.Equal(RoutingVerdict.Ignore(RoutingIgnoreReason.ChannelMentionRequired), verdict);
    }

    [Fact]
    public void MessageWithMention_Routes_WhenMentionOnly()
    {
        var verdict = Evaluate(
            mentionOnly: true,
            allowDm: true,
            mentionRequiredInDm: false,
            isDm: false,
            containsMention: true,
            threadExists: false,
            isThreadReply: false,
            text: "@bot hello");

        Assert.Equal(RoutingVerdict.Route, verdict);
    }

    [Fact]
    public void ExistingThread_ContinuesWithoutMention()
    {
        var verdict = Evaluate(
            mentionOnly: true,
            allowDm: true,
            mentionRequiredInDm: false,
            isDm: false,
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
        var verdict = Evaluate(
            mentionOnly: true,
            allowDm: true,
            mentionRequiredInDm: false,
            isDm: false,
            containsMention: false,
            threadExists: false,
            isThreadReply: true,
            text: "follow up");

        Assert.Equal(RoutingVerdict.Route, verdict);
    }

    [Theory]
    [InlineData(true, false, false, RoutingVerdictKind.Route, null)]
    [InlineData(false, false, false, RoutingVerdictKind.Ignore, RoutingIgnoreReason.DmNotAllowed)]
    [InlineData(true, true, false, RoutingVerdictKind.Ignore, RoutingIgnoreReason.DmMentionRequired)]
    [InlineData(true, true, true, RoutingVerdictKind.Route, null)]
    public void DirectMessage_routing_decision(
        bool allowDm,
        bool mentionRequiredInDm,
        bool containsMention,
        RoutingVerdictKind expectedKind,
        RoutingIgnoreReason? expectedReason)
    {
        var verdict = Evaluate(
            mentionOnly: true,
            allowDm: allowDm,
            mentionRequiredInDm: mentionRequiredInDm,
            isDm: true,
            containsMention: containsMention,
            threadExists: false,
            isThreadReply: false,
            text: "hey");

        Assert.Equal(new RoutingVerdict(expectedKind, expectedReason), verdict);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyContent_Ignored(string text)
    {
        var verdict = Evaluate(
            mentionOnly: false,
            allowDm: false,
            mentionRequiredInDm: false,
            isDm: false,
            containsMention: false,
            threadExists: false,
            isThreadReply: false,
            text: text);

        Assert.Equal(RoutingVerdict.Ignore(RoutingIgnoreReason.NoContent), verdict);
    }

    [Fact]
    public void MentionOnlyDisabled_RoutesWithoutMention()
    {
        var verdict = Evaluate(
            mentionOnly: false,
            allowDm: true,
            mentionRequiredInDm: false,
            isDm: false,
            containsMention: false,
            threadExists: false,
            isThreadReply: false,
            text: "hello");

        Assert.Equal(RoutingVerdict.Route, verdict);
    }
}
