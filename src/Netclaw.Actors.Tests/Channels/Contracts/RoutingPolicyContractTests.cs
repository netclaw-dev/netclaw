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
/// reasons (e.g. Slack's <c>HiddenMessage</c>/<c>UnsupportedSubtype</c>/<c>WrongKind</c>
/// or Telegram's <c>GroupMentionRequired</c>) map onto these when the meaning
/// matches and have no mapping here otherwise; those stay covered by the
/// standalone per-channel tests.
/// </summary>
public enum RoutingIgnoreReason
{
    NoContent,
    DmNotAllowed,
    DmMentionRequired,
    ThreadMentionRequired,
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
/// Universal behavioral contract for channel routing policies: the routing
/// behavior every chat channel shares — the mention gate, explicit-mention
/// ingress, allowed-DM ingress, empty-content filtering, and the
/// mention-only opt-out. The policies are pure static functions, so no
/// TestKit is needed. Each fixture constructs a plain text-only inbound
/// message for its channel (no files/attachments) and normalizes the channel
/// decision into a <see cref="RoutingVerdict"/>.
/// <para>
/// Platform-specific routing axes live in capability layers that derive from
/// this base: <see cref="ThreadReplyRoutingPolicyContractTests"/> (threads)
/// and <see cref="DmGatedRoutingPolicyContractTests"/> (policy-level DM
/// gating). Channels enroll in the layers whose interaction model they have.
/// </para>
/// </summary>
public abstract class RoutingPolicyContractTests
{
    /// <summary>
    /// Evaluates the channel's routing policy for a plain text-only user
    /// message with no thread context.
    /// </summary>
    protected abstract RoutingVerdict EvaluateRouting(
        bool mentionOnly,
        bool isDm,
        bool containsMention,
        string text);

    [Fact]
    public void MessageWithoutMention_Ignored_WhenMentionOnly()
    {
        var verdict = EvaluateRouting(
            mentionOnly: true,
            isDm: false,
            containsMention: false,
            text: "hello");

        Assert.Equal(RoutingVerdict.Ignore(RoutingIgnoreReason.ChannelMentionRequired), verdict);
    }

    [Fact]
    public void MessageWithMention_Routes_WhenMentionOnly()
    {
        var verdict = EvaluateRouting(
            mentionOnly: true,
            isDm: false,
            containsMention: true,
            text: "@bot hello");

        Assert.Equal(RoutingVerdict.Route, verdict);
    }

    [Fact]
    public void AllowedDirectMessage_Routes()
    {
        var verdict = EvaluateRouting(
            mentionOnly: true,
            isDm: true,
            containsMention: false,
            text: "hey");

        Assert.Equal(RoutingVerdict.Route, verdict);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyContent_Ignored(string text)
    {
        var verdict = EvaluateRouting(
            mentionOnly: false,
            isDm: false,
            containsMention: false,
            text: text);

        Assert.Equal(RoutingVerdict.Ignore(RoutingIgnoreReason.NoContent), verdict);
    }

    [Fact]
    public void MentionOnlyDisabled_RoutesWithoutMention()
    {
        var verdict = EvaluateRouting(
            mentionOnly: false,
            isDm: false,
            containsMention: false,
            text: "hello");

        Assert.Equal(RoutingVerdict.Route, verdict);
    }
}
